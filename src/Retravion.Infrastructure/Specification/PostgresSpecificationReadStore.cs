using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Product;

namespace Retravion.Infrastructure.Specification;

public sealed class PostgresSpecificationReadStore(string connectionString) : ISpecificationReadStore
{
    // btrim uses the same whitespace characters as .NET Trim; ICU case comparison matches Product IDs.
    private const string Whitespace = """U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000'""";
    private const string Setup = $"""
        SELECT pg_advisory_xact_lock(821749620119);
        CREATE SCHEMA IF NOT EXISTS retravion_specification;
        CREATE COLLATION IF NOT EXISTS retravion_specification.specification_case_fold (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
        CREATE TABLE IF NOT EXISTS retravion_specification.specification (
            internal_id uuid PRIMARY KEY CHECK (internal_id <> '00000000-0000-0000-0000-000000000000'),
            product_internal_id uuid NOT NULL REFERENCES retravion_product.product(internal_id),
            id text NOT NULL CHECK (length(btrim(id, {Whitespace})) > 0),
            title text NOT NULL CHECK (length(btrim(title, {Whitespace})) > 0),
            description text NOT NULL DEFAULT '' CHECK (octet_length(description) <= 1048576),
            status text NOT NULL CHECK (status = 'Draft')
        );
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint
                WHERE conrelid = 'retravion_specification.specification'::regclass
                AND conname = 'specification_review_description_check') THEN
                ALTER TABLE retravion_specification.specification
                    DROP CONSTRAINT specification_status_check,
                    ADD CONSTRAINT specification_status_check CHECK (status IN ('Draft', 'In Review')),
                    ADD CONSTRAINT specification_review_description_check
                        CHECK (status <> 'In Review' OR length(btrim(description, {Whitespace})) > 0);
            END IF;
        END $$;
        CREATE UNIQUE INDEX IF NOT EXISTS specification_product_id_unique ON retravion_specification.specification
            (product_internal_id, (btrim(id, {Whitespace})) COLLATE retravion_specification.specification_case_fold);
        CREATE TABLE IF NOT EXISTS retravion_specification.link (
            specification_internal_id uuid NOT NULL REFERENCES retravion_specification.specification(internal_id) ON DELETE CASCADE,
            kind smallint NOT NULL CHECK (kind BETWEEN 0 AND 2),
            version_id uuid NOT NULL CHECK (version_id <> '00000000-0000-0000-0000-000000000000'),
            PRIMARY KEY (specification_internal_id, kind, version_id)
        );
        CREATE INDEX IF NOT EXISTS specification_link_target ON retravion_specification.link (kind, version_id);
        """;
    internal const string Select = """
        SELECT internal_id, product_internal_id, id, title, description, status,
            ARRAY(SELECT kind FROM retravion_specification.link WHERE specification_internal_id = s.internal_id ORDER BY kind, version_id),
            ARRAY(SELECT version_id FROM retravion_specification.link WHERE specification_internal_id = s.internal_id ORDER BY kind, version_id)
        FROM retravion_specification.specification s WHERE product_internal_id = $1
        """;
    private const string InvalidRecord = "The persisted Specification is invalid.";

    public Task<Result<IReadOnlyList<DevelopmentSpecification>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Read(productInternalId, null, async reader =>
        {
            var specifications = new List<DevelopmentSpecification>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var mapped = Map(reader);
                if (mapped.IsFailure) return Result.Failure<IReadOnlyList<DevelopmentSpecification>>(mapped.Error);
                specifications.Add(mapped.Value);
            }
            return Result.Success<IReadOnlyList<DevelopmentSpecification>>(specifications);
        }, cancellationToken);

    public Task<Result<Maybe<DevelopmentSpecification>>> Find(Guid productInternalId, Guid internalId, CancellationToken cancellationToken = default) =>
        Read(productInternalId, internalId, async reader =>
        {
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<DevelopmentSpecification>.None);
            return Map(reader).Map(Maybe<DevelopmentSpecification>.From);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<SpecificationVersionChoice>>> ListVersions(Guid productInternalId, SpecificationLinkKind kind,
        CancellationToken cancellationToken = default) => Enum.IsDefined(kind)
        ? ReadVersions(connection => PostgresSpecificationVersionSource.For(kind).List(connection, productInternalId, cancellationToken),
            cancellationToken)
        : Task.FromResult(Result.Failure<IReadOnlyList<SpecificationVersionChoice>>("Unknown Specification link type."));

    public Task<Result<Maybe<SpecificationVersionChoice>>> ResolveVersion(Guid productInternalId, SpecificationLink target,
        CancellationToken cancellationToken = default) => Enum.IsDefined(target.Kind) && target.VersionId != Guid.Empty
        ? ReadVersions(connection => PostgresSpecificationVersionSource.For(target.Kind)
            .Resolve(connection, null, productInternalId, target.VersionId, cancellationToken), cancellationToken)
        : Task.FromResult(Result.Failure<Maybe<SpecificationVersionChoice>>("Select an exact version for the Specification link."));

    private async Task<Result<T>> ReadVersions<T>(Func<NpgsqlConnection, Task<T>> read, CancellationToken token)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            return Result.Success(await read(connection));
        }
        catch (NpgsqlException exception) when (exception.IsTransient && !token.IsCancellationRequested)
        {
            return Result.Failure<T>("The Specification link versions could not be retrieved.");
        }
    }

    internal static Result<DevelopmentSpecification> Map(NpgsqlDataReader reader)
    {
        var links = SpecificationLinks.Create(reader.GetFieldValue<short[]>(6).Zip(reader.GetFieldValue<Guid[]>(7),
            (kind, versionId) => new SpecificationLink((SpecificationLinkKind)kind, versionId)));
        var draft = links.Bind(value => DraftSpecification.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), value));
        if (draft.IsFailure) return Result.Failure<DevelopmentSpecification>(InvalidRecord);
        return reader.GetString(5) switch
        {
            "Draft" => Result.Success<DevelopmentSpecification>(draft.Value),
            "In Review" when draft.Value.SubmitForReview().TryGetValue(out var review) => Result.Success<DevelopmentSpecification>(review),
            _ => Result.Failure<DevelopmentSpecification>(InvalidRecord)
        };
    }

    private async Task<Result<T>> Read<T>(Guid productInternalId, Guid? internalId, Func<NpgsqlDataReader, Task<Result<T>>> read,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchema(connection, cancellationToken);
            await using var command = new NpgsqlCommand(Select + (internalId.HasValue
                ? " AND internal_id = $2" : " ORDER BY id, internal_id"), connection);
            command.Parameters.AddWithValue(productInternalId);
            if (internalId is { } identity) command.Parameters.AddWithValue(identity);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await read(reader);
        }
        catch (NpgsqlException exception) when (exception.IsTransient && !cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<T>("The Specifications could not be retrieved.");
        }
    }

    private static async Task EnsureSchema(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (await SchemaExists(connection, null, cancellationToken)) return;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchema(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task EnsureSchema(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await SchemaExists(connection, transaction, cancellationToken)) return;
        await using var productExists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", connection, transaction);
        if (await productExists.ExecuteScalarAsync(cancellationToken) is not true)
            await PostgresProductStore.EnsureSchema(connection, transaction, cancellationToken);
        await using var setup = new NpgsqlCommand(Setup, connection, transaction);
        await setup.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> SchemaExists(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand("""
            SELECT to_regclass('retravion_specification.link') IS NOT NULL AND EXISTS (
                SELECT 1 FROM pg_constraint WHERE conrelid = to_regclass('retravion_specification.specification')
                AND conname = 'specification_review_description_check')
            """, connection, transaction);
        return await exists.ExecuteScalarAsync(cancellationToken) is true;
    }
}
