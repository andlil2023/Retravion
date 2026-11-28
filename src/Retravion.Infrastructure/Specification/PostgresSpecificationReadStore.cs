using System.Text.Json;
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
            IF NOT EXISTS (SELECT 1 FROM pg_constraint
                WHERE conrelid = 'retravion_specification.specification'::regclass
                AND conname = 'specification_approved_description_check') THEN
                ALTER TABLE retravion_specification.specification
                    DROP CONSTRAINT specification_status_check,
                    ADD CONSTRAINT specification_status_check CHECK (status IN ('Draft', 'In Review', 'Approved')),
                    ADD CONSTRAINT specification_approved_description_check
                        CHECK (status <> 'Approved' OR length(btrim(description, {Whitespace})) > 0);
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_constraint
                WHERE conrelid = 'retravion_specification.specification'::regclass
                AND conname = 'specification_implementation_check') THEN
                ALTER TABLE retravion_specification.specification
                    DROP CONSTRAINT specification_status_check,
                    ADD COLUMN merge_attested_by text,
                    ADD COLUMN merge_attested_at_utc_ticks bigint,
                    ADD CONSTRAINT specification_status_check CHECK (status IN ('Draft', 'In Review', 'Approved', 'Implemented')),
                    ADD CONSTRAINT specification_implementation_check CHECK (
                        (status = 'Implemented' AND length(btrim(description, {Whitespace})) > 0
                            AND merge_attested_by IS NOT NULL AND length(btrim(merge_attested_by, {Whitespace})) > 0
                            AND merge_attested_at_utc_ticks IS NOT NULL AND merge_attested_at_utc_ticks BETWEEN 0 AND 3155378975999999999)
                        OR (status <> 'Implemented' AND merge_attested_by IS NULL AND merge_attested_at_utc_ticks IS NULL));
            END IF;
        END $$;
        ALTER TABLE retravion_specification.specification ADD COLUMN IF NOT EXISTS review_basis_revision bigint NOT NULL DEFAULT 0;
        CREATE UNIQUE INDEX IF NOT EXISTS specification_product_id_unique ON retravion_specification.specification
            (product_internal_id, (btrim(id, {Whitespace})) COLLATE retravion_specification.specification_case_fold);
        CREATE TABLE IF NOT EXISTS retravion_specification.link (
            specification_internal_id uuid NOT NULL REFERENCES retravion_specification.specification(internal_id) ON DELETE CASCADE,
            kind smallint NOT NULL CHECK (kind BETWEEN 0 AND 2),
            version_id uuid NOT NULL CHECK (version_id <> '00000000-0000-0000-0000-000000000000'),
            PRIMARY KEY (specification_internal_id, kind, version_id)
        );
        CREATE INDEX IF NOT EXISTS specification_link_target ON retravion_specification.link (kind, version_id);
        CREATE TABLE IF NOT EXISTS retravion_specification.assignment_history (
            specification_internal_id uuid NOT NULL REFERENCES retravion_specification.specification(internal_id) ON DELETE CASCADE,
            sequence bigint GENERATED ALWAYS AS IDENTITY,
            actor text NOT NULL CHECK (length(btrim(actor)) > 0),
            occurred_at_utc_ticks bigint NOT NULL CHECK (occurred_at_utc_ticks BETWEEN 0 AND 3155378975999999999),
            old_assignee text CHECK (length(btrim(old_assignee)) > 0),
            new_assignee text CHECK (length(btrim(new_assignee)) > 0),
            CHECK (old_assignee IS DISTINCT FROM new_assignee),
            PRIMARY KEY (specification_internal_id, sequence)
        );
        CREATE TABLE IF NOT EXISTS retravion_specification.ai_review (
            specification_internal_id uuid PRIMARY KEY REFERENCES retravion_specification.specification(internal_id) ON DELETE CASCADE,
            markdown text NOT NULL CHECK (octet_length(markdown) <= 1048576 AND length(btrim(markdown, {Whitespace})) > 0),
            is_outdated boolean NOT NULL
        );
        CREATE TABLE IF NOT EXISTS retravion_specification.evidence (
            specification_internal_id uuid PRIMARY KEY REFERENCES retravion_specification.specification(internal_id) ON DELETE CASCADE,
            markdown text NOT NULL CHECK (octet_length(markdown) <= 1048576 AND length(btrim(markdown, {Whitespace})) > 0),
            pull_request_url text,
            is_outdated boolean NOT NULL
        );
        """;
    private const string SelectFields = """
        SELECT s.internal_id, s.product_internal_id, s.id, s.title, s.description, s.status,
            ARRAY(SELECT kind FROM retravion_specification.link WHERE specification_internal_id = s.internal_id ORDER BY kind, version_id),
            ARRAY(SELECT version_id FROM retravion_specification.link WHERE specification_internal_id = s.internal_id ORDER BY kind, version_id),
            s.merge_attested_by, s.merge_attested_at_utc_ticks,
            e.specification_internal_id IS NOT NULL, e.markdown, e.pull_request_url, e.is_outdated
        """;
    internal const string Select = SelectFields + """
         FROM retravion_specification.specification s
         LEFT JOIN retravion_specification.evidence e ON e.specification_internal_id = s.internal_id AND s.status = 'Implemented'
         WHERE s.product_internal_id = $1
        """;
    private const string SelectDetail = SelectFields + """
        , (SELECT coalesce(jsonb_agg(jsonb_build_object('Actor', h.actor, 'UtcTicks', h.occurred_at_utc_ticks,
              'OldAssignee', h.old_assignee, 'NewAssignee', h.new_assignee) ORDER BY h.sequence), '[]'::jsonb)
           FROM retravion_specification.assignment_history h WHERE h.specification_internal_id = s.internal_id),
          r.specification_internal_id IS NOT NULL, r.markdown, r.is_outdated, s.review_basis_revision
         FROM retravion_specification.specification s
         LEFT JOIN retravion_specification.ai_review r ON r.specification_internal_id = s.internal_id
         LEFT JOIN retravion_specification.evidence e ON e.specification_internal_id = s.internal_id
         WHERE s.product_internal_id = $1
        """;
    private const int AttesterIndex = 8;
    private const int AttestedAtIndex = 9;
    private const int EvidencePresentIndex = 10;
    private const int EvidenceMarkdownIndex = 11;
    private const int AssignmentHistoryIndex = 14;
    private const int ReviewPresentIndex = 15;
    private const int ReviewMarkdownIndex = 16;

    private sealed record AssignmentRow(string Actor, long UtcTicks, string? OldAssignee, string? NewAssignee)
    {
        public SpecificationAssignmentEntry ToEntry() => new(Actor, new DateTimeOffset(UtcTicks, TimeSpan.Zero), OldAssignee, NewAssignee);
    }
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

    public Task<Result<Maybe<SpecificationDetail>>> FindDetail(Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default) => Read(productInternalId, internalId, async reader =>
        {
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<SpecificationDetail>.None);
            var mapped = Map(reader);
            if (mapped.IsFailure) return Result.Failure<Maybe<SpecificationDetail>>(mapped.Error);
            var history = JsonSerializer.Deserialize<AssignmentRow[]>(reader.GetString(AssignmentHistoryIndex))!;
            SpecificationAiReview? review = null;
            if (reader.GetBoolean(ReviewPresentIndex))
            {
                var mappedReview = MapAiReview(reader, ReviewMarkdownIndex);
                if (mappedReview.IsFailure) return Result.Failure<Maybe<SpecificationDetail>>(mappedReview.Error);
                review = mappedReview.Value;
            }
            var evidence = (mapped.Value as ImplementedSpecification)?.Evidence;
            if (evidence is null && reader.GetBoolean(EvidencePresentIndex))
            {
                var mappedEvidence = MapEvidence(reader, EvidenceMarkdownIndex);
                if (mappedEvidence.IsFailure ||
                    (mapped.Value is DraftSpecification or InReviewSpecification && !mappedEvidence.Value.IsOutdated))
                    return Result.Failure<Maybe<SpecificationDetail>>(InvalidRecord);
                evidence = mappedEvidence.Value;
            }
            var detail = new SpecificationDetail(mapped.Value, Array.AsReadOnly(history.Select(row => row.ToEntry()).ToArray()), review)
            {
                ReviewBasisRevision = reader.GetInt64(18),
                Evidence = evidence
            };
            return mapped.Value is ImplementedSpecification && detail.Assignee is not null
                ? Result.Failure<Maybe<SpecificationDetail>>(InvalidRecord)
                : Result.Success(Maybe<SpecificationDetail>.From(detail));
        }, cancellationToken, SelectDetail);

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
        var status = reader.GetString(5);
        if (status != "Implemented" && (!reader.IsDBNull(AttesterIndex) || !reader.IsDBNull(AttestedAtIndex)))
            return Result.Failure<DevelopmentSpecification>(InvalidRecord);
        if (status == "Draft") return Result.Success<DevelopmentSpecification>(draft.Value);
        if (!draft.Value.SubmitForReview().TryGetValue(out var review))
            return Result.Failure<DevelopmentSpecification>(InvalidRecord);
        if (status == "In Review") return Result.Success<DevelopmentSpecification>(review);
        var approved = review.Approve();
        return status switch
        {
            "Approved" => Result.Success<DevelopmentSpecification>(approved),
            "Implemented" => MapImplemented(reader, approved),
            _ => Result.Failure<DevelopmentSpecification>(InvalidRecord)
        };
    }

    private static Result<DevelopmentSpecification> MapImplemented(NpgsqlDataReader reader, ApprovedSpecification approved)
    {
        if (reader.IsDBNull(AttesterIndex) || reader.IsDBNull(AttestedAtIndex) || !reader.GetBoolean(EvidencePresentIndex))
            return Result.Failure<DevelopmentSpecification>(InvalidRecord);
        var ticks = reader.GetInt64(AttestedAtIndex);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ||
            !MapEvidence(reader, EvidenceMarkdownIndex).TryGetValue(out var evidence))
            return Result.Failure<DevelopmentSpecification>(InvalidRecord);
        var implemented = approved.Implement(evidence, mergeAttested: true, reader.GetString(AttesterIndex),
            new DateTimeOffset(ticks, TimeSpan.Zero));
        return implemented.IsSuccess
            ? Result.Success<DevelopmentSpecification>(implemented.Value)
            : Result.Failure<DevelopmentSpecification>(InvalidRecord);
    }

    internal static Result<SpecificationAiReview> MapAiReview(NpgsqlDataReader reader, int markdownIndex)
    {
        if (!reader.IsDBNull(markdownIndex) && !reader.IsDBNull(markdownIndex + 1))
        {
            var review = SpecificationAiReview.Restore(reader.GetString(markdownIndex), reader.GetBoolean(markdownIndex + 1));
            if (review.IsSuccess) return review;
        }
        return Result.Failure<SpecificationAiReview>(InvalidRecord);
    }

    internal static Result<SpecificationEvidence> MapEvidence(NpgsqlDataReader reader, int markdownIndex)
    {
        if (!reader.IsDBNull(markdownIndex) && !reader.IsDBNull(markdownIndex + 2))
        {
            var url = reader.IsDBNull(markdownIndex + 1) ? null : reader.GetString(markdownIndex + 1);
            var evidence = SpecificationEvidence.Restore(reader.GetString(markdownIndex), url, reader.GetBoolean(markdownIndex + 2));
            if (evidence.IsSuccess) return evidence;
        }
        return Result.Failure<SpecificationEvidence>(InvalidRecord);
    }

    private async Task<Result<T>> Read<T>(Guid productInternalId, Guid? internalId, Func<NpgsqlDataReader, Task<Result<T>>> read,
        CancellationToken cancellationToken, string select = Select)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await EnsureSchema(connection, cancellationToken);
            await using var command = new NpgsqlCommand(select + (internalId.HasValue
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
            SELECT to_regclass('retravion_specification.link') IS NOT NULL
            AND to_regclass('retravion_specification.assignment_history') IS NOT NULL
            AND to_regclass('retravion_specification.ai_review') IS NOT NULL
            AND to_regclass('retravion_specification.evidence') IS NOT NULL AND EXISTS (
                SELECT 1 FROM pg_constraint WHERE conrelid = to_regclass('retravion_specification.specification')
                AND conname = 'specification_implementation_check')
            AND EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass('retravion_specification.specification')
                AND attname = 'review_basis_revision' AND NOT attisdropped)
            """, connection, transaction);
        return await exists.ExecuteScalarAsync(cancellationToken) is true;
    }
}
