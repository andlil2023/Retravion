using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Domain.Product;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.Infrastructure.Requirement;

public sealed partial class PostgresRequirementStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : IRequirementStore, IRequirementChangeTransaction, IRequirementLinkStore
{
    // Keep the entity row as the default-version projection and Product-scoped ID reservation.
    // Legacy decision snapshots are evidence, not a source for invented historical versions.
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620112);
        CREATE SCHEMA IF NOT EXISTS retravion_requirement;
        CREATE TABLE IF NOT EXISTS retravion_requirement.requirement (
            internal_id uuid PRIMARY KEY,
            product_internal_id uuid NOT NULL REFERENCES retravion_product.product(internal_id),
            id text NOT NULL CHECK (length(btrim(id)) > 0 AND char_length(id) <= 100),
            title text NOT NULL CHECK (length(btrim(title)) > 0),
            statement text NOT NULL CHECK (length(btrim(statement)) > 0),
            type integer NOT NULL CHECK (type BETWEEN 0 AND 5), rationale text,
            status integer NOT NULL CHECK (status BETWEEN 0 AND 2),
            revision bigint NOT NULL CHECK (revision > 0)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS requirement_product_id_unique ON retravion_requirement.requirement
            (product_internal_id, id COLLATE retravion_product.product_case_fold);
        CREATE TABLE IF NOT EXISTS retravion_requirement.review (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            requirement_internal_id uuid NOT NULL REFERENCES retravion_requirement.requirement(internal_id),
            actor_identifier text NOT NULL, occurred_at_utc_ticks bigint NOT NULL,
            decision text NOT NULL, comment text, manual_id text NOT NULL,
            title text NOT NULL, statement text NOT NULL,
            type integer NOT NULL CHECK (type BETWEEN 0 AND 5), rationale text
        );
        ALTER TABLE retravion_requirement.requirement ADD COLUMN IF NOT EXISTS version_id uuid NOT NULL DEFAULT gen_random_uuid();
        ALTER TABLE retravion_requirement.requirement ADD COLUMN IF NOT EXISTS version_number integer NOT NULL DEFAULT 1 CHECK (version_number > 0);
        ALTER TABLE retravion_requirement.requirement ADD COLUMN IF NOT EXISTS ever_approved boolean NOT NULL DEFAULT false;
        ALTER TABLE retravion_requirement.requirement ADD COLUMN IF NOT EXISTS creation_reason text;
        ALTER TABLE retravion_requirement.requirement ADD COLUMN IF NOT EXISTS source_version_id uuid;
        UPDATE retravion_requirement.requirement r SET ever_approved=true WHERE NOT ever_approved AND
            (status=2 OR EXISTS (SELECT 1 FROM retravion_requirement.review h WHERE h.requirement_internal_id=r.internal_id AND h.decision='Approved'));
        CREATE TABLE IF NOT EXISTS retravion_requirement.requirement_version (
            internal_id uuid NOT NULL REFERENCES retravion_requirement.requirement(internal_id),
            product_internal_id uuid NOT NULL REFERENCES retravion_product.product(internal_id),
            id text NOT NULL CHECK (length(btrim(id)) > 0 AND char_length(id) <= 100),
            title text NOT NULL CHECK (length(btrim(title)) > 0),
            statement text NOT NULL CHECK (length(btrim(statement)) > 0),
            type integer NOT NULL CHECK (type BETWEEN 0 AND 5), rationale text,
            status integer NOT NULL CHECK (status BETWEEN 0 AND 2),
            revision bigint NOT NULL CHECK (revision > 0),
            version_id uuid PRIMARY KEY,
            version_number integer NOT NULL CHECK (version_number > 0),
            ever_approved boolean NOT NULL,
            creation_reason text,
            source_version_id uuid REFERENCES retravion_requirement.requirement_version(version_id),
            UNIQUE (internal_id, version_number),
            CHECK (version_number=1 OR ever_approved),
            CHECK ((version_number=1 AND creation_reason IS NULL AND source_version_id IS NULL) OR
                (version_number>1 AND creation_reason IS NOT NULL AND length(btrim(creation_reason))>0 AND source_version_id IS NOT NULL))
        );
        ALTER TABLE retravion_requirement.requirement_version ADD COLUMN IF NOT EXISTS release_protected boolean NOT NULL DEFAULT false;
        CREATE UNIQUE INDEX IF NOT EXISTS requirement_one_working_version ON retravion_requirement.requirement_version(internal_id) WHERE status<>2;
        ALTER TABLE retravion_requirement.requirement ADD COLUMN IF NOT EXISTS area_id uuid REFERENCES retravion_product.area(internal_id);
        ALTER TABLE retravion_requirement.requirement_version ADD COLUMN IF NOT EXISTS area_id uuid REFERENCES retravion_product.area(internal_id);
        INSERT INTO retravion_requirement.requirement_version (internal_id,product_internal_id,id,title,statement,type,rationale,status,revision,version_id,version_number,ever_approved,creation_reason,source_version_id) SELECT internal_id,product_internal_id,id,title,statement,type,rationale,status,revision,
            version_id,version_number,ever_approved,creation_reason,source_version_id FROM retravion_requirement.requirement
            ON CONFLICT (version_id) DO NOTHING;
        CREATE TABLE IF NOT EXISTS retravion_requirement.provenance (
            requirement_internal_id uuid PRIMARY KEY REFERENCES retravion_requirement.requirement(internal_id),
            source_snapshot jsonb NOT NULL
        );
        CREATE INDEX IF NOT EXISTS requirement_provenance_source_version ON retravion_requirement.provenance ((source_snapshot->>'SourceVersionId'));
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS version_id uuid;
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS version_number integer;
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS source_version_id uuid;
        CREATE TABLE IF NOT EXISTS retravion_requirement.user_need_link (
            owner_version_id uuid NOT NULL REFERENCES retravion_requirement.requirement_version(version_id),
            target_internal_id uuid NOT NULL, target_version_id uuid NOT NULL,
            PRIMARY KEY (owner_version_id, target_version_id)
        );
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS before_links jsonb;
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS after_links jsonb;
        CREATE TABLE IF NOT EXISTS retravion_requirement.parent_link (
            owner_version_id uuid PRIMARY KEY REFERENCES retravion_requirement.requirement_version(version_id),
            target_internal_id uuid NOT NULL REFERENCES retravion_requirement.requirement(internal_id),
            target_version_id uuid NOT NULL REFERENCES retravion_requirement.requirement_version(version_id)
        );
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS parent_relationship_type text;
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS before_parent jsonb;
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS after_parent jsonb;
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS before_area jsonb;
        ALTER TABLE retravion_requirement.review ADD COLUMN IF NOT EXISTS after_area jsonb;
        CREATE TABLE IF NOT EXISTS retravion_requirement.operation_history (
            sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            product_internal_id uuid NOT NULL, internal_id uuid NOT NULL,
            operation_id uuid UNIQUE, actor_identifier text NOT NULL, occurred_at_utc_ticks bigint NOT NULL,
            action text NOT NULL, change jsonb NOT NULL
        );
        """;

    private const string Columns = "r.internal_id,r.product_internal_id,r.id,r.title,r.statement,r.type,r.rationale,r.status,r.revision,r.version_id,r.version_number,r.ever_approved,r.creation_reason,r.source_version_id,(SELECT max(v.version_number) FROM retravion_requirement.requirement_version v WHERE v.internal_id=r.internal_id AND v.status=2),r.area_id";

    public Task<Result<Maybe<RequirementAuditDetails>>> Outcome(Guid productInternalId, Guid operationId, CancellationToken cancellationToken = default) =>
        Execute(async (c, t) =>
        {
            await using var command = new NpgsqlCommand("SELECT change FROM retravion_requirement.operation_history WHERE product_internal_id=$1 AND operation_id=$2", c, t);
            command.Parameters.AddWithValue(productInternalId); command.Parameters.AddWithValue(operationId);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (json is null) return Result.Success(Maybe<RequirementAuditDetails>.None);
            var change = JsonSerializer.Deserialize<RequirementAuditDetails>(json);
            return change is null ? Result.Failure<Maybe<RequirementAuditDetails>>("Invalid persisted Requirement operation history.") :
                Result.Success(Maybe<RequirementAuditDetails>.From(change));
        }, cancellationToken, Result.Success(Maybe<RequirementAuditDetails>.None));

    public Task<Result<IReadOnlyList<RequirementSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_requirement.requirement r WHERE product_internal_id=$1 ORDER BY id", productInternalId, cancellationToken);

    public Task<Result<IReadOnlyList<RequirementSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_requirement.requirement_version r WHERE internal_id=$1 ORDER BY version_number", internalId, cancellationToken);

    public Task<Result<IReadOnlyList<RequirementSnapshot>>> CopiesOf(Guid sourceVersionId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_requirement.requirement_version r JOIN retravion_requirement.provenance p ON p.requirement_internal_id=r.internal_id WHERE r.version_number=1 AND p.source_snapshot->>'SourceVersionId'=$1::text ORDER BY r.id", sourceVersionId, cancellationToken);

    private Task<Result<IReadOnlyList<RequirementSnapshot>>> Rows(string sql, Guid id, CancellationToken token) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(id);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<RequirementSnapshot>();
            while (await reader.ReadAsync(token))
            {
                var restored = Read(reader);
                if (restored.IsFailure) return Result.Failure<IReadOnlyList<RequirementSnapshot>>(restored.Error);
                rows.Add(restored.Value);
            }
            return Result.Success<IReadOnlyList<RequirementSnapshot>>(rows);
        }, token, Result.Success<IReadOnlyList<RequirementSnapshot>>([]));

    public Task<Result<Maybe<RequirementSnapshot>>> Find(Guid internalId, CancellationToken cancellationToken = default, Guid? versionId = null) =>
        Execute(async (connection, transaction) =>
        {
            var sql = versionId is null ? $"SELECT {Columns} FROM retravion_requirement.requirement r WHERE internal_id=$1" :
                $"SELECT {Columns} FROM retravion_requirement.requirement_version r WHERE internal_id=$1 AND version_id=$2";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(internalId);
            if (versionId is not null) command.Parameters.AddWithValue(versionId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<RequirementSnapshot>.None);
            var restored = Read(reader);
            return restored.IsSuccess ? Result.Success(Maybe<RequirementSnapshot>.From(restored.Value)) : Result.Failure<Maybe<RequirementSnapshot>>(restored.Error);
        }, cancellationToken, Result.Success(Maybe<RequirementSnapshot>.None));

    public Task<Result<IReadOnlyList<RequirementReview>>> History(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT actor_identifier,occurred_at_utc_ticks,decision,comment,manual_id,title,statement,type,rationale,version_id,version_number,source_version_id,before_links,after_links,parent_relationship_type,before_parent,after_parent,before_area,after_area FROM retravion_requirement.review WHERE requirement_internal_id=$1 ORDER BY id", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<RequirementReview>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = (RequirementType)reader.GetInt32(7);
                if (!Enum.IsDefined(type)) return Result.Failure<IReadOnlyList<RequirementReview>>("The persisted Requirement review is invalid.");
                rows.Add(new(internalId, new AuditActor.Identified(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    reader.GetString(2), Text(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetString(6), type, Text(reader, 8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetGuid(11),
                    reader.IsDBNull(13) ? null : "Requirement–User Need", ReadLinks(reader, 12), ReadLinks(reader, 13),
                    Text(reader, 14), ReadParent(reader, 15), ReadParent(reader, 16),
                    reader.IsDBNull(17) ? null : JsonSerializer.Deserialize<AreaEvidence>(reader.GetString(17)),
                    reader.IsDBNull(18) ? null : JsonSerializer.Deserialize<AreaEvidence>(reader.GetString(18))));
            }
            return Result.Success<IReadOnlyList<RequirementReview>>(rows);
        }, cancellationToken, Result.Success<IReadOnlyList<RequirementReview>>([]));

    public Task<Result<Maybe<RequirementProvenance>>> Provenance(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT source_snapshot::text FROM retravion_requirement.provenance WHERE requirement_internal_id=$1", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (json is null) return Result.Success(Maybe<RequirementProvenance>.None);
            var value = JsonSerializer.Deserialize<RequirementProvenance>(json);
            return value is null || value.SourceProductId == Guid.Empty || value.SourceRequirementId == Guid.Empty ||
                value.SourceVersionId == Guid.Empty || value.SourceVersionNumber < 1 || !Enum.IsDefined(value.SourceType) ||
                !Enum.TryParse<RequirementStatus>(value.SourceStatus, out var status) || !Enum.IsDefined(status) ||
                string.IsNullOrWhiteSpace(value.SourceManualId) || string.IsNullOrWhiteSpace(value.SourceTitle) ||
                string.IsNullOrWhiteSpace(value.SourceStatement)
                ? Result.Failure<Maybe<RequirementProvenance>>("The persisted Requirement provenance is invalid.") :
                Result.Success(Maybe<RequirementProvenance>.From(value));
        }, cancellationToken, Result.Success(Maybe<RequirementProvenance>.None));

    public Task<Result<RequirementSnapshot>> Derive(RequirementSnapshot expectedSource, RequirementState destination,
        AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        var source = expectedSource.Requirement;
        if (actor is not AuditActor.Identified || expectedSource.Revision <= 0 || destination.Status != RequirementStatus.Draft ||
            destination.VersionNumber != 1 || destination.HasBeenApproved || destination.AreaId is not null || destination.ProductInternalId == source.ProductInternalId)
            return Task.FromResult(Result.Failure<RequirementSnapshot>("Invalid Requirement derivation."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, destination.ProductInternalId, cancellationToken)) return Inactive();
            await using (var check = new NpgsqlCommand($"SELECT {Columns} FROM retravion_requirement.requirement_version r WHERE internal_id=$1 AND version_id=$2 FOR UPDATE", connection, transaction))
            {
                check.Parameters.AddWithValue(source.InternalId); check.Parameters.AddWithValue(source.VersionId);
                await using var reader = await check.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return Result.Failure<RequirementSnapshot>("The source changed or is unavailable. Reload and review it.");
                var current = Read(reader);
                if (current.IsFailure || current.Value.Revision != expectedSource.Revision || current.Value.Requirement != source)
                    return Result.Failure<RequirementSnapshot>("The source changed. Reload and review it.");
            }
            var provenance = new RequirementProvenance(source.ProductInternalId, source.InternalId, source.VersionId,
                source.VersionNumber, source.Status.ToString(), source.Id, source.Title, source.Statement, source.Type, source.Rationale);
            await using var command = new NpgsqlCommand("INSERT INTO retravion_requirement.requirement (internal_id,product_internal_id,id,title,statement,type,rationale,status,revision,version_id) VALUES ($1,$2,$3,$4,$5,$6,$7,0,1,$8) ON CONFLICT DO NOTHING", connection, transaction);
            command.Parameters.AddWithValue(destination.InternalId); command.Parameters.AddWithValue(destination.ProductInternalId);
            command.Parameters.AddWithValue(destination.Id); command.Parameters.AddWithValue(destination.Title); command.Parameters.AddWithValue(destination.Statement);
            command.Parameters.AddWithValue((int)destination.Type); command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)destination.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue(destination.VersionId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return Result.Failure<RequirementSnapshot>("That Requirement ID is already in use for this Product.");
            if (!await SaveVersion(connection, transaction, destination.InternalId, cancellationToken))
                return Result.Failure<RequirementSnapshot>("The Requirement version identity is already in use.");
            await using var evidence = new NpgsqlCommand("INSERT INTO retravion_requirement.provenance(requirement_internal_id,source_snapshot) VALUES ($1,$2)", connection, transaction);
            evidence.Parameters.AddWithValue(destination.InternalId);
            evidence.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(provenance));
            await evidence.ExecuteNonQueryAsync(cancellationToken);
            await Audit(connection, transaction, actor, occurredAt, "requirement.derive", null, destination, null, null, cancellationToken, provenance);
            return Result.Success(new RequirementSnapshot(destination, 1));
        }, cancellationToken);
    }

    public Task<Result<RequirementSnapshot>> Create(RequirementState requirement, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default,
        Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        if (requirement.Status != RequirementStatus.Draft || requirement.VersionNumber != 1 || requirement.HasBeenApproved || actor is not AuditActor.Identified || operationId == Guid.Empty)
            return Task.FromResult(Result.Failure<RequirementSnapshot>("Invalid Requirement creation."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, requirement.ProductInternalId, cancellationToken)) return Inactive();
            if (!await PostgresProductAreaStore.CanSelect(connection, transaction, requirement.ProductInternalId, null, requirement.AreaId, cancellationToken))
                return Result.Failure<RequirementSnapshot>("Select an active area from this Product.");
            await using var command = new NpgsqlCommand("INSERT INTO retravion_requirement.requirement (internal_id,product_internal_id,id,title,statement,type,rationale,status,revision,version_id,area_id) VALUES ($1,$2,$3,$4,$5,$6,$7,0,1,$8,$9) ON CONFLICT DO NOTHING", connection, transaction);
            command.Parameters.AddWithValue(requirement.InternalId); command.Parameters.AddWithValue(requirement.ProductInternalId);
            command.Parameters.AddWithValue(requirement.Id); command.Parameters.AddWithValue(requirement.Title); command.Parameters.AddWithValue(requirement.Statement);
            command.Parameters.AddWithValue((int)requirement.Type); command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)requirement.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue(requirement.VersionId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)requirement.AreaId ?? DBNull.Value);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return Result.Failure<RequirementSnapshot>("That Requirement ID is already in use for this Product.");
            if (!await SaveVersion(connection, transaction, requirement.InternalId, cancellationToken))
                return Result.Failure<RequirementSnapshot>("The Requirement version identity is already in use.");
            await Audit(connection, transaction, actor, occurredAt, "requirement.create", null, requirement, null, null, cancellationToken, operationId: operationId);
            return Result.Success(new RequirementSnapshot(requirement, 1));
        }, cancellationToken, authorized: authorized);
    }

    public Task<Result<RequirementSnapshot>> CreateSuccessor(RequirementSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        var successor = expected.Requirement.CreateSuccessor(reason);
        return successor.IsFailure ? Task.FromResult(Result.Failure<RequirementSnapshot>(successor.Error)) :
            Mutate(expected, successor.Value, actor, occurredAt, "Successor", reason, cancellationToken, operationId, authorized);
    }

    public Task<Result<RequirementSnapshot>> Update(RequirementSnapshot expected, RequirementState replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision = null, string? comment = null, CancellationToken cancellationToken = default,
        Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        var before = expected.Requirement;
        var valid = decision switch
        {
            "InReview" => before.SubmitForReview(), "Approved" => before.Approve(), "Draft" => before.ReturnToDraft(),
            "Reopened" => before.Reopen(comment),
            null when comment is null => before.Edit(replacement.Id, replacement.Title, replacement.Statement, replacement.Type, replacement.Rationale)
                .Bind(r => r.Classify(replacement.AreaId)),
            _ => Result.Failure<RequirementState>("Invalid review decision.")
        };
        if (valid.IsFailure || valid.Value != replacement) return Task.FromResult(Result.Failure<RequirementSnapshot>(valid.IsFailure ? valid.Error : "Invalid Requirement update."));
        return Mutate(expected, replacement, actor, occurredAt, decision, comment, cancellationToken, operationId, authorized);
    }

    private Task<Result<RequirementSnapshot>> Mutate(RequirementSnapshot expected, RequirementState replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision, string? comment, CancellationToken token, Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        var before = expected.Requirement;
        if (actor is not AuditActor.Identified identified || expected.Revision <= 0 || operationId == Guid.Empty)
            return Task.FromResult(Result.Failure<RequirementSnapshot>("Invalid Requirement update."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, before.ProductInternalId, token)) return Inactive();
            if (!await PostgresProductAreaStore.CanSelect(connection, transaction, before.ProductInternalId, before.AreaId, replacement.AreaId, token))
                return Result.Failure<RequirementSnapshot>("Select an active area from this Product.");
            if (replacement.VersionId == before.VersionId && replacement != before &&
                await Protected(connection, transaction, before.VersionId, token))
                return Result.Failure<RequirementSnapshot>("This exact version is permanently protected by a Released revision. Create a successor for changes.");
            if (decision == "Approved")
            {
                var approval = await ValidateTargets(connection, transaction, before.VersionId, token);
                if (approval.IsFailure) return Result.Failure<RequirementSnapshot>(approval.Error);
                var parentApproval = await ValidateParent(connection, transaction, before.VersionId, token);
                if (parentApproval.IsFailure) return Result.Failure<RequirementSnapshot>(parentApproval.Error);
            }
            // The projection is always the newest version. Compare the full snapshot, including no-ops,
            // to block older reopening and races between successor creation, reopening and editing.
            await using var command = new NpgsqlCommand("""
                UPDATE retravion_requirement.requirement SET id=$1,title=$2,statement=$3,type=$4,rationale=$5,status=$6,
                    revision=revision+CASE WHEN $7 THEN 1 ELSE 0 END,version_id=$8,version_number=$9,ever_approved=$10,creation_reason=$11,source_version_id=$12,area_id=$27
                WHERE internal_id=$13 AND product_internal_id=$14 AND revision=$15 AND version_id=$16
                    AND id=$17 AND title=$18 AND statement=$19 AND type=$20 AND rationale IS NOT DISTINCT FROM $21
                    AND status=$22 AND version_number=$23 AND ever_approved=$24
                    AND creation_reason IS NOT DISTINCT FROM $25 AND source_version_id IS NOT DISTINCT FROM $26 AND area_id IS NOT DISTINCT FROM $28
                RETURNING revision
                """, connection, transaction);
            var changed = before != replacement;
            command.Parameters.AddWithValue(replacement.Id); command.Parameters.AddWithValue(replacement.Title); command.Parameters.AddWithValue(replacement.Statement);
            command.Parameters.AddWithValue((int)replacement.Type); command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)replacement.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue((int)replacement.Status); command.Parameters.AddWithValue(changed);
            command.Parameters.AddWithValue(replacement.VersionId); command.Parameters.AddWithValue(replacement.VersionNumber); command.Parameters.AddWithValue(replacement.HasBeenApproved);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)replacement.CreationReason ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)replacement.SourceVersionId ?? DBNull.Value);
            command.Parameters.AddWithValue(before.InternalId); command.Parameters.AddWithValue(before.ProductInternalId); command.Parameters.AddWithValue(expected.Revision);
            command.Parameters.AddWithValue(before.VersionId); command.Parameters.AddWithValue(before.Id); command.Parameters.AddWithValue(before.Title); command.Parameters.AddWithValue(before.Statement);
            command.Parameters.AddWithValue((int)before.Type); command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)before.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue((int)before.Status); command.Parameters.AddWithValue(before.VersionNumber); command.Parameters.AddWithValue(before.HasBeenApproved);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)before.CreationReason ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)before.SourceVersionId ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)replacement.AreaId ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)before.AreaId ?? DBNull.Value);
            var revision = await command.ExecuteScalarAsync(token);
            if (revision is null) return Result.Failure<RequirementSnapshot>("The Requirement changed or a newer version exists. Retrieve it again.");
            if (!await SaveVersion(connection, transaction, before.InternalId, token))
                return Result.Failure<RequirementSnapshot>("The Requirement version identity is already in use.");
            if (decision == "Successor")
            {
                await using var copy = new NpgsqlCommand("INSERT INTO retravion_requirement.user_need_link SELECT $1,target_internal_id,target_version_id FROM retravion_requirement.user_need_link WHERE owner_version_id=$2", connection, transaction);
                copy.Parameters.AddWithValue(replacement.VersionId); copy.Parameters.AddWithValue(before.VersionId);
                await copy.ExecuteNonQueryAsync(token);
                await using var parentCopy = new NpgsqlCommand("INSERT INTO retravion_requirement.parent_link SELECT $1,target_internal_id,target_version_id FROM retravion_requirement.parent_link WHERE owner_version_id=$2", connection, transaction);
                parentCopy.Parameters.AddWithValue(replacement.VersionId); parentCopy.Parameters.AddWithValue(before.VersionId);
                await parentCopy.ExecuteNonQueryAsync(token);
            }
            if (decision is not null)
            {
                await using var history = new NpgsqlCommand("INSERT INTO retravion_requirement.review (requirement_internal_id,actor_identifier,occurred_at_utc_ticks,decision,comment,manual_id,title,statement,type,rationale,version_id,version_number,source_version_id) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)", connection, transaction);
                history.Parameters.AddWithValue(before.InternalId); history.Parameters.AddWithValue(identified.Identifier); history.Parameters.AddWithValue(occurredAt.UtcTicks);
                history.Parameters.AddWithValue(decision); history.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)comment ?? DBNull.Value);
                history.Parameters.AddWithValue(replacement.Id); history.Parameters.AddWithValue(replacement.Title); history.Parameters.AddWithValue(replacement.Statement);
                history.Parameters.AddWithValue((int)replacement.Type); history.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)replacement.Rationale ?? DBNull.Value);
                history.Parameters.AddWithValue(replacement.VersionId); history.Parameters.AddWithValue(replacement.VersionNumber);
                history.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)replacement.SourceVersionId ?? DBNull.Value);
                await history.ExecuteNonQueryAsync(token);
                var ownedLinks = await LoadLinks(connection, transaction, replacement.VersionId, token);
                if (ownedLinks.Count > 0)
                    await SetHistoryLinks(connection, transaction, replacement, decision == "Successor" ? null : ownedLinks,
                        ownedLinks, token);
                var parent = await LoadParent(connection, transaction, replacement.VersionId, token);
                if (parent.HasValue)
                    await SetHistoryParent(connection, transaction, replacement, decision == "Successor" ? Maybe<RequirementParentLink>.None : parent,
                        parent, token);
            }
            if (changed) await Audit(connection, transaction, actor, occurredAt, decision switch
            {
                "Successor" => "requirement.version.create", "Reopened" => "requirement.reopen", null => "requirement.edit", _ => "requirement.review." + decision
            }, before, replacement, decision, comment, token, operationId: operationId);
            await using var latest = new NpgsqlCommand("SELECT max(version_number) FROM retravion_requirement.requirement_version WHERE internal_id=$1 AND status=2", connection, transaction);
            latest.Parameters.AddWithValue(before.InternalId);
            var approved = await latest.ExecuteScalarAsync(token);
            return Result.Success(new RequirementSnapshot(replacement, (long)revision, approved is int number ? number : null));
        }, token, authorized: authorized);
    }

    private static async Task<bool> SaveVersion(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO retravion_requirement.requirement_version (internal_id,product_internal_id,id,title,statement,type,rationale,status,revision,version_id,version_number,ever_approved,creation_reason,source_version_id,area_id) SELECT internal_id,product_internal_id,id,title,statement,type,rationale,status,revision,
                version_id,version_number,ever_approved,creation_reason,source_version_id,area_id FROM retravion_requirement.requirement WHERE internal_id=$1
            ON CONFLICT (version_id) DO UPDATE SET id=EXCLUDED.id,title=EXCLUDED.title,statement=EXCLUDED.statement,type=EXCLUDED.type,rationale=EXCLUDED.rationale,
                status=EXCLUDED.status,revision=EXCLUDED.revision,ever_approved=EXCLUDED.ever_approved,area_id=EXCLUDED.area_id
            WHERE requirement_version.internal_id=EXCLUDED.internal_id
                AND requirement_version.product_internal_id=EXCLUDED.product_internal_id
                AND requirement_version.version_number=EXCLUDED.version_number
                AND requirement_version.creation_reason IS NOT DISTINCT FROM EXCLUDED.creation_reason
                AND requirement_version.source_version_id IS NOT DISTINCT FROM EXCLUDED.source_version_id
            """, connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    private static async Task<bool> Protected(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid version, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT release_protected FROM retravion_requirement.requirement_version WHERE version_id=$1", connection, transaction);
        command.Parameters.AddWithValue(version);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Result<RequirementSnapshot> Read(NpgsqlDataReader reader)
    {
        var requirement = RequirementState.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            (RequirementType)reader.GetInt32(5), Text(reader, 6), (RequirementStatus)reader.GetInt32(7), reader.GetGuid(9), reader.GetInt32(10),
            reader.GetBoolean(11), Text(reader, 12), reader.IsDBNull(13) ? null : reader.GetGuid(13), reader.IsDBNull(15) ? null : reader.GetGuid(15));
        return requirement.IsSuccess && reader.GetInt64(8) > 0 ? Result.Success(new RequirementSnapshot(requirement.Value, reader.GetInt64(8), reader.IsDBNull(14) ? null : reader.GetInt32(14)))
            : Result.Failure<RequirementSnapshot>("The persisted Requirement is invalid.");
    }

    private static Result<RequirementSnapshot> Inactive() => Result.Failure<RequirementSnapshot>("The responsible Product is retired or no longer exists.");
    private static async Task<bool> ProductActive(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static async Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor actor,
        DateTimeOffset occurredAt, string action, RequirementState? before, RequirementState after, string? decision, string? comment, CancellationToken token,
        RequirementProvenance? provenance = null, Guid? operationId = null)
    {
        var beforeArea = before is null ? null : await PostgresProductAreaStore.Evidence(connection, transaction, before.ProductInternalId, before.AreaId, token);
        var afterArea = await PostgresProductAreaStore.Evidence(connection, transaction, after.ProductInternalId, after.AreaId, token);
        RequirementAuditValues Values(RequirementState r, AreaEvidence? area) => new(r.ProductInternalId, r.Id, r.Title, r.Statement, r.Type.ToString(), r.Rationale, r.Status.ToString(), area);
        if (decision is null && (before?.AreaId != after.AreaId))
        {
            await using var history = new NpgsqlCommand("INSERT INTO retravion_requirement.review(requirement_internal_id,actor_identifier,occurred_at_utc_ticks,decision,manual_id,title,statement,type,rationale,version_id,version_number,source_version_id) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)", connection, transaction);
            history.Parameters.AddWithValue(after.InternalId); history.Parameters.AddWithValue(((AuditActor.Identified)actor).Identifier);
            history.Parameters.AddWithValue(occurredAt.UtcTicks); history.Parameters.AddWithValue(action);
            history.Parameters.AddWithValue(after.Id); history.Parameters.AddWithValue(after.Title); history.Parameters.AddWithValue(after.Statement);
            history.Parameters.AddWithValue((int)after.Type); history.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)after.Rationale ?? DBNull.Value);
            history.Parameters.AddWithValue(after.VersionId); history.Parameters.AddWithValue(after.VersionNumber);
            history.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)after.SourceVersionId ?? DBNull.Value);
            await history.ExecuteNonQueryAsync(token);
        }
        if (decision is not null || before?.AreaId != after.AreaId)
        {
            await using var evidence = new NpgsqlCommand("UPDATE retravion_requirement.review SET before_area=$1,after_area=$2 WHERE id=(SELECT max(id) FROM retravion_requirement.review WHERE requirement_internal_id=$3)", connection, transaction);
            evidence.Parameters.AddWithValue(NpgsqlDbType.Jsonb, beforeArea is null ? DBNull.Value : JsonSerializer.Serialize(beforeArea));
            evidence.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(afterArea));
            evidence.Parameters.AddWithValue(after.InternalId);
            await evidence.ExecuteNonQueryAsync(token);
        }
        var afterLinks = await LoadLinks(connection, transaction, after.VersionId, token);
        var beforeLinks = before is null || decision == "Successor" ? null : await LoadLinks(connection, transaction, before.VersionId, token);
        var afterParent = await LoadParent(connection, transaction, after.VersionId, token);
        var beforeParent = before is null || decision == "Successor" ? Maybe<RequirementParentLink>.None : await LoadParent(connection, transaction, before.VersionId, token);
        var change = new RequirementAuditDetails(after.InternalId, before is null ? null : Values(before, beforeArea), Values(after, afterArea), decision, comment,
                    after.VersionId, after.VersionNumber, after.SourceVersionId, decision is "Successor" or "Reopened" ? comment : null,
                    afterLinks.Count == 0 ? null : "Requirement–User Need", beforeLinks is { Count: > 0 } ? beforeLinks : null,
                    afterLinks.Count == 0 ? null : afterLinks,
                    afterParent.HasValue ? ParentRelationship : null, beforeParent.HasValue ? beforeParent.Value : null,
                    afterParent.HasValue ? afterParent.Value : null, provenance, operationId);
        await using var receipt = new NpgsqlCommand("INSERT INTO retravion_requirement.operation_history(product_internal_id,internal_id,operation_id,actor_identifier,occurred_at_utc_ticks,action,change) VALUES($1,$2,$3,$4,$5,$6,$7)", connection, transaction);
        receipt.Parameters.AddWithValue(after.ProductInternalId); receipt.Parameters.AddWithValue(after.InternalId);
        receipt.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)operationId ?? DBNull.Value);
        receipt.Parameters.AddWithValue(((AuditActor.Identified)actor).Identifier); receipt.Parameters.AddWithValue(occurredAt.UtcTicks);
        receipt.Parameters.AddWithValue(action); receipt.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(change));
        await receipt.ExecuteNonQueryAsync(token);
        await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success,
                requirementChange: change), token);
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token,
        Result<T>? whenProductStorageMissing = null, Func<Task<bool>>? authorized = null)
    {
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            // Product owns its lazy storage; reads on fresh installations must not initialize it here.
            await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", connection, transaction))
                if (await exists.ExecuteScalarAsync(token) is not true)
                    return whenProductStorageMissing ?? Result.Failure<T>("The responsible Product is retired or no longer exists.");
            // Same lock as User Need transactions, acquired first: target validation and approval
            // cannot race target edits/reopening. No User Need storage is initialized here.
            await using (var targetLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111)", connection, transaction))
                await targetLock.ExecuteNonQueryAsync(token);
            await using (var areas = new NpgsqlCommand(PostgresProductAreaStore.Setup, connection, transaction)) await areas.ExecuteNonQueryAsync(token);
            await using (var setup = new NpgsqlCommand(Setup, connection, transaction)) await setup.ExecuteNonQueryAsync(token);
            if (authorized is not null && !await authorized()) return Result.Failure<T>("Access denied.");
            var result = await operation(connection, transaction);
            if (result.IsSuccess)
            {
                if (authorized is not null && !await authorized()) return Result.Failure<T>("Access denied.");
                commitStarted = true;
                if (commit is null) await transaction.CommitAsync(token); else await commit(transaction, token);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        { return Result.Failure<T>("The Requirement operation outcome could not be confirmed. Retrieve the Requirement before retrying."); }
        catch (PostgresException exception) when (!token.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Result.Failure<T>("That Requirement ID or working version is already in use. Retrieve it again."); }
        catch (NpgsqlException exception) when (!token.IsCancellationRequested && (exception.IsTransient || exception is PostgresException
        { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction }))
        { return Result.Failure<T>("The Requirement operation could not be completed."); }
    }
}
