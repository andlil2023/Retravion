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
        CREATE UNIQUE INDEX IF NOT EXISTS specification_product_id_unique ON retravion_specification.specification
            (product_internal_id, (btrim(id, {Whitespace})) COLLATE retravion_specification.specification_case_fold);
        """;
    private const string Select = """
        SELECT internal_id, product_internal_id, id, title, description, status
        FROM retravion_specification.specification WHERE product_internal_id = $1
        """;
    private const string InvalidRecord = "The persisted Specification is invalid.";

    public Task<Result<IReadOnlyList<DraftSpecification>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Read(productInternalId, null, async reader =>
        {
            var specifications = new List<DraftSpecification>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var mapped = Map(reader);
                if (mapped.IsFailure) return Result.Failure<IReadOnlyList<DraftSpecification>>(mapped.Error);
                specifications.Add(mapped.Value);
            }
            return Result.Success<IReadOnlyList<DraftSpecification>>(specifications);
        }, cancellationToken);

    public Task<Result<Maybe<DraftSpecification>>> Find(Guid productInternalId, Guid internalId, CancellationToken cancellationToken = default) =>
        Read(productInternalId, internalId, async reader =>
        {
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<DraftSpecification>.None);
            return Map(reader).Map(Maybe<DraftSpecification>.From);
        }, cancellationToken);

    private static Result<DraftSpecification> Map(NpgsqlDataReader reader)
    {
        if (reader.GetString(5) != "Draft") return Result.Failure<DraftSpecification>(InvalidRecord);
        var draft = DraftSpecification.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4));
        return draft.IsSuccess ? draft : Result.Failure<DraftSpecification>(InvalidRecord);
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
        await using var exists = new NpgsqlCommand("SELECT to_regclass('retravion_specification.specification') IS NOT NULL", connection);
        if (await exists.ExecuteScalarAsync(cancellationToken) is true) return;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var productExists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", connection, transaction);
        if (await productExists.ExecuteScalarAsync(cancellationToken) is not true)
            await PostgresProductStore.EnsureSchema(connection, transaction, cancellationToken);
        await using var setup = new NpgsqlCommand(Setup, connection, transaction);
        await setup.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
