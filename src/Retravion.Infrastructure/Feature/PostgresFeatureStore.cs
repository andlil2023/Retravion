using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Infrastructure.AuditTrail;
using FeatureRecord = Retravion.Domain.Feature.Feature;

namespace Retravion.Infrastructure.Feature;

public sealed partial class PostgresFeatureStore(string connectionString, Func<NpgsqlTransaction, CancellationToken, Task>? commit = null)
    : IFeatureStore, IFeatureChangeTransaction
{
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620118);
        CREATE SCHEMA IF NOT EXISTS retravion_feature;
        CREATE TABLE IF NOT EXISTS retravion_feature.counter (
            product_internal_id uuid PRIMARY KEY, last_number bigint NOT NULL CHECK(last_number > 0));
        CREATE TABLE IF NOT EXISTS retravion_feature.feature (
            internal_id uuid PRIMARY KEY, product_internal_id uuid NOT NULL, number bigint NOT NULL CHECK(number > 0),
            version_id uuid NOT NULL, version_number integer NOT NULL CHECK(version_number > 0), status text NOT NULL,
            title text NOT NULL, description text NOT NULL, revision bigint NOT NULL CHECK(revision > 0),
            creation_reason text, source_version_id uuid, UNIQUE(product_internal_id, number));
        CREATE TABLE IF NOT EXISTS retravion_feature.feature_version (
            internal_id uuid NOT NULL REFERENCES retravion_feature.feature(internal_id), product_internal_id uuid NOT NULL,
            number bigint NOT NULL, version_id uuid PRIMARY KEY, version_number integer NOT NULL, status text NOT NULL,
            title text NOT NULL, description text NOT NULL, revision bigint NOT NULL, creation_reason text, source_version_id uuid,
            release_protected boolean NOT NULL DEFAULT false, UNIQUE(internal_id,version_number));
        ALTER TABLE retravion_feature.feature ADD COLUMN IF NOT EXISTS links jsonb NOT NULL DEFAULT '[]';
        ALTER TABLE retravion_feature.feature_version ADD COLUMN IF NOT EXISTS links jsonb NOT NULL DEFAULT '[]';
        CREATE UNIQUE INDEX IF NOT EXISTS feature_one_working_version ON retravion_feature.feature_version(internal_id) WHERE status <> 'Approved';
        CREATE TABLE IF NOT EXISTS retravion_feature.history (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, internal_id uuid NOT NULL REFERENCES retravion_feature.feature(internal_id),
            actor_identifier text NOT NULL, occurred_at_utc_ticks bigint NOT NULL, action text NOT NULL, change jsonb NOT NULL, operation_id uuid UNIQUE);
        """;
    private const string Columns = "r.internal_id,r.product_internal_id,r.number,r.version_id,r.version_number,r.status,r.title,r.description,r.revision,r.creation_reason,r.source_version_id,r.links,(SELECT max(v.version_number) FROM retravion_feature.feature_version v WHERE v.internal_id=r.internal_id AND v.status='Approved')";
    private const string StoredColumns = "internal_id,product_internal_id,number,version_id,version_number,status,title,description,revision,creation_reason,source_version_id,links";

    public Task<Result<IReadOnlyList<FeatureSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_feature.feature r WHERE product_internal_id=$1 ORDER BY number", productInternalId, cancellationToken);
    public Task<Result<IReadOnlyList<FeatureSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_feature.feature_version r WHERE internal_id=$1 ORDER BY version_number", internalId, cancellationToken);
    private Task<Result<IReadOnlyList<FeatureSnapshot>>> Rows(string sql, Guid id, CancellationToken token) => Execute<IReadOnlyList<FeatureSnapshot>>(async (c, t) =>
    {
        await using var cmd = new NpgsqlCommand(sql, c, t); cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(token); var rows = new List<FeatureSnapshot>();
        while (await reader.ReadAsync(token)) { var row = Read(reader); if (row.IsFailure) return Result.Failure<IReadOnlyList<FeatureSnapshot>>(row.Error); rows.Add(row.Value); }
        return rows;
    }, token);
    public Task<Result<Maybe<FeatureSnapshot>>> Find(Guid internalId, Guid? versionId = null, CancellationToken cancellationToken = default) => Execute<Maybe<FeatureSnapshot>>(async (c, t) =>
    {
        var sql = versionId is null ? $"SELECT {Columns} FROM retravion_feature.feature r WHERE internal_id=$1" : $"SELECT {Columns} FROM retravion_feature.feature_version r WHERE internal_id=$1 AND version_id=$2";
        await using var cmd = new NpgsqlCommand(sql, c, t); cmd.Parameters.AddWithValue(internalId); if (versionId is not null) cmd.Parameters.AddWithValue(versionId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<FeatureSnapshot>.None);
        return Read(reader).Map(Maybe<FeatureSnapshot>.From);
    }, cancellationToken);
    public Task<Result<Maybe<FeatureAuditDetails>>> Outcome(Guid productInternalId, Guid operationId, CancellationToken cancellationToken = default) => Execute<Maybe<FeatureAuditDetails>>(async (c, t) =>
    {
        await using var cmd = new NpgsqlCommand("SELECT h.change FROM retravion_feature.history h JOIN retravion_feature.feature f ON f.internal_id=h.internal_id WHERE f.product_internal_id=$1 AND h.operation_id=$2", c, t);
        cmd.Parameters.AddWithValue(productInternalId); cmd.Parameters.AddWithValue(operationId);
        var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken);
        if (json is null) return Result.Success(Maybe<FeatureAuditDetails>.None);
        var value = JsonSerializer.Deserialize<FeatureAuditDetails>(json);
        return value is null ? Result.Failure<Maybe<FeatureAuditDetails>>("Invalid Feature history.") : Result.Success(Maybe<FeatureAuditDetails>.From(value));
    }, cancellationToken);
    public Task<Result<IReadOnlyList<FeatureHistory>>> History(Guid internalId, CancellationToken cancellationToken = default) => Execute<IReadOnlyList<FeatureHistory>>(async (c, t) =>
    {
        await using var cmd = new NpgsqlCommand("SELECT actor_identifier,occurred_at_utc_ticks,action,change FROM retravion_feature.history WHERE internal_id=$1 ORDER BY id", c, t);
        cmd.Parameters.AddWithValue(internalId); await using var reader = await cmd.ExecuteReaderAsync(cancellationToken); var rows = new List<FeatureHistory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var change = JsonSerializer.Deserialize<FeatureAuditDetails>(reader.GetString(3));
            if (change is null) return Result.Failure<IReadOnlyList<FeatureHistory>>("Invalid Feature history.");
            rows.Add(new(new AuditActor.Identified(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), change));
        }
        return rows;
    }, cancellationToken);

    public Task<Result<FeatureSnapshot>> Create(Guid productInternalId, FeatureContent content, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        if (productInternalId == Guid.Empty || content is null || actor is not AuditActor.Identified || operationId == Guid.Empty)
            return Task.FromResult(Result.Failure<FeatureSnapshot>("Invalid Feature creation."));
        return Execute<FeatureSnapshot>(async (c, t) =>
        {
            if (!await ProductActive(c, t, productInternalId, cancellationToken)) return Inactive();
            await using var allocate = new NpgsqlCommand("INSERT INTO retravion_feature.counter AS counter(product_internal_id,last_number) VALUES($1,1) ON CONFLICT(product_internal_id) DO UPDATE SET last_number=counter.last_number+1 RETURNING last_number", c, t);
            allocate.Parameters.AddWithValue(productInternalId); var number = (long)(await allocate.ExecuteScalarAsync(cancellationToken))!;
            var created = FeatureRecord.Create(productInternalId, number, content); if (created.IsFailure) return Result.Failure<FeatureSnapshot>(created.Error);
            var value = created.Value;
            await using var insert = new NpgsqlCommand("INSERT INTO retravion_feature.feature(internal_id,product_internal_id,number,version_id,version_number,status,title,description,revision) VALUES($1,$2,$3,$4,1,'Draft',$5,$6,1)", c, t);
            insert.Parameters.AddWithValue(value.InternalId); insert.Parameters.AddWithValue(productInternalId); insert.Parameters.AddWithValue(number); insert.Parameters.AddWithValue(value.VersionId);
            insert.Parameters.AddWithValue(content.Title); insert.Parameters.AddWithValue(content.Description); await insert.ExecuteNonQueryAsync(cancellationToken);
            await SaveVersion(c, t, value.InternalId, cancellationToken);
            await Evidence(c, t, actor, occurredAt, "feature.create", null, value, null, null, operationId, cancellationToken);
            return new FeatureSnapshot(value, 1);
        }, cancellationToken, authorized);
    }
    public Task<Result<FeatureSnapshot>> Update(FeatureSnapshot expected, FeatureContent content, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, (f, _, _) => Task.FromResult(f.Edit(content)), actor, occurredAt, "feature.edit", null, null, cancellationToken, operationId, authorized);
    public Task<Result<FeatureSnapshot>> Review(FeatureSnapshot expected, string decision, string? comment, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, async (f, c, t) => decision switch { "InReview" => f.SubmitForReview(), "Approved" => await TargetsApproved(c, t, f, cancellationToken) ? f.Approve() : Result.Failure<FeatureRecord>("Every linked Requirement version must be Approved."), "Draft" => f.ReturnToDraft(), _ => Result.Failure<FeatureRecord>("Invalid review decision.") }, actor, occurredAt, "feature.review." + decision, decision, comment, cancellationToken, authorized: authorized);
    public Task<Result<FeatureSnapshot>> Reopen(FeatureSnapshot expected, string? reason, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, (f, _, _) => Task.FromResult(f.Reopen(reason)), actor, occurredAt, "feature.reopen", "Reopened", reason, cancellationToken, authorized: authorized);
    public Task<Result<FeatureSnapshot>> CreateSuccessor(FeatureSnapshot expected, string? reason, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, (f, _, _) => Task.FromResult(f.CreateSuccessor(reason)), actor, occurredAt, "feature.version.create", "Successor", reason, cancellationToken, operationId, authorized);

    private Task<Result<FeatureSnapshot>> Mutate(FeatureSnapshot expected, Func<FeatureRecord, NpgsqlConnection, NpgsqlTransaction, Task<Result<FeatureRecord>>> change, AuditActor actor,
        DateTimeOffset occurredAt, string action, string? decision, string? comment, CancellationToken token, Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        if (expected is null || expected.Feature is null || expected.Revision <= 0 || actor is not AuditActor.Identified || operationId == Guid.Empty)
            return Task.FromResult(Result.Failure<FeatureSnapshot>("Invalid Feature change."));
        return Execute<FeatureSnapshot>(async (c, t) =>
        {
            var before = expected.Feature;
            if (!await ProductActive(c, t, before.ProductInternalId, token)) return Inactive();
            FeatureSnapshot current;
            await using (var cmd = new NpgsqlCommand($"SELECT {Columns} FROM retravion_feature.feature r WHERE internal_id=$1 FOR UPDATE", c, t))
            {
                cmd.Parameters.AddWithValue(before.InternalId); await using var reader = await cmd.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return Stale();
                var restored = Read(reader); if (restored.IsFailure) return Result.Failure<FeatureSnapshot>(restored.Error); current = restored.Value;
            }
            if (current.Revision != expected.Revision || !current.Feature.SameState(before)) return Stale();
            var replacement = await change(current.Feature, c, t); if (replacement.IsFailure) return Result.Failure<FeatureSnapshot>(replacement.Error);
            var after = replacement.Value; if (after.SameState(before)) return current;
            if (after.VersionId == before.VersionId)
            {
                await using var protectedVersion = new NpgsqlCommand("SELECT release_protected FROM retravion_feature.feature_version WHERE internal_id=$1 AND version_id=$2", c, t);
                protectedVersion.Parameters.AddWithValue(before.InternalId); protectedVersion.Parameters.AddWithValue(before.VersionId);
                if (await protectedVersion.ExecuteScalarAsync(token) is true) return Result.Failure<FeatureSnapshot>("This version is protected by a Released revision. Create a successor for changes.");
            }
            await using var update = new NpgsqlCommand("UPDATE retravion_feature.feature SET title=$1,description=$2,status=$3,version_id=$4,version_number=$5,creation_reason=$6,source_version_id=$7,links=$9,revision=revision+1 WHERE internal_id=$8 RETURNING revision", c, t);
            update.Parameters.AddWithValue(after.Content.Title); update.Parameters.AddWithValue(after.Content.Description); update.Parameters.AddWithValue(after.Status); update.Parameters.AddWithValue(after.VersionId); update.Parameters.AddWithValue(after.VersionNumber);
            update.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)after.CreationReason ?? DBNull.Value); update.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)after.SourceVersionId ?? DBNull.Value); update.Parameters.AddWithValue(before.InternalId); update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(after.Links));
            var revision = (long)(await update.ExecuteScalarAsync(token))!;
            await SaveVersion(c, t, before.InternalId, token); await Evidence(c, t, actor, occurredAt, action, before, after, decision, comment, operationId, token);
            await using var latest = new NpgsqlCommand("SELECT max(version_number) FROM retravion_feature.feature_version WHERE internal_id=$1 AND status='Approved'", c, t);
            latest.Parameters.AddWithValue(before.InternalId); var approved = await latest.ExecuteScalarAsync(token);
            return new FeatureSnapshot(after, revision, approved is int number ? number : null);
        }, token, authorized);
    }
    private static async Task SaveVersion(NpgsqlConnection c, NpgsqlTransaction t, Guid id, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand($"INSERT INTO retravion_feature.feature_version ({StoredColumns}) SELECT {StoredColumns} FROM retravion_feature.feature WHERE internal_id=$1 ON CONFLICT(version_id) DO UPDATE SET title=EXCLUDED.title,description=EXCLUDED.description,status=EXCLUDED.status,revision=EXCLUDED.revision,links=EXCLUDED.links", c, t);
        cmd.Parameters.AddWithValue(id); await cmd.ExecuteNonQueryAsync(token);
    }
    private static Result<FeatureSnapshot> Read(NpgsqlDataReader r)
    {
        if (r.GetInt64(8) <= 0) return Result.Failure<FeatureSnapshot>("Invalid persisted Feature revision.");
        try
        {
            var links = JsonSerializer.Deserialize<FeatureRequirementLink[]>(r.GetString(11));
            if (links is null) return Result.Failure<FeatureSnapshot>("Invalid persisted Feature links.");
            return FeatureRecord.Restore(r.GetGuid(0), r.GetGuid(1), r.GetGuid(3), r.GetInt64(2), r.GetInt32(4), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetGuid(10), FeatureContent.Create(r.GetString(6), r.GetString(7)), r.GetString(5), links)
                .Map(f => new FeatureSnapshot(f, r.GetInt64(8), r.IsDBNull(12) ? null : r.GetInt32(12)));
        }
        catch (JsonException) { return Result.Failure<FeatureSnapshot>("Invalid persisted Feature links."); }
    }
    private static Result<FeatureSnapshot> Stale() => Result.Failure<FeatureSnapshot>("The Feature changed or a newer version exists. Retrieve it again before editing.");
    private static Result<FeatureSnapshot> Inactive() => Result.Failure<FeatureSnapshot>("The responsible Product is retired or no longer exists.");
    private static async Task<bool> ProductActive(NpgsqlConnection c, NpgsqlTransaction t, Guid id, CancellationToken token)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", c, t)) if (!(bool)(await exists.ExecuteScalarAsync(token))!) return false;
        await using var cmd = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", c, t); cmd.Parameters.AddWithValue(id); return await cmd.ExecuteScalarAsync(token) is true;
    }
    private static async Task Evidence(NpgsqlConnection c, NpgsqlTransaction t, AuditActor actor, DateTimeOffset occurredAt, string action,
        FeatureRecord? before, FeatureRecord after, string? decision, string? comment, Guid? operationId, CancellationToken token)
    {
        static FeatureAuditValues Values(FeatureRecord f) => new(f.ProductInternalId, f.Id, f.Content.Title, f.Content.Description, f.Status, f.Links.ToArray());
        var details = new FeatureAuditDetails(after.InternalId, after.VersionId, after.VersionNumber, before is null || decision == "Successor" ? null : Values(before), Values(after), decision,
            decision == "Successor" ? null : comment, after.SourceVersionId, decision == "Reopened" ? comment : after.CreationReason, operationId);
        await using var history = new NpgsqlCommand("INSERT INTO retravion_feature.history(internal_id,actor_identifier,occurred_at_utc_ticks,action,change,operation_id) VALUES($1,$2,$3,$4,$5,$6)", c, t);
        history.Parameters.AddWithValue(after.InternalId); history.Parameters.AddWithValue(((AuditActor.Identified)actor).Identifier); history.Parameters.AddWithValue(occurredAt.UtcTicks); history.Parameters.AddWithValue(action);
        history.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(details)); history.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)operationId ?? DBNull.Value); await history.ExecuteNonQueryAsync(token);
        await PostgresAuditSubmission.SubmitInTransactionAsync(c, t, new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success, featureChange: details), token);
    }
    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token, Func<Task<bool>>? authorized = null)
    {
        var committing = false;
        try
        {
            await using var c = new NpgsqlConnection(connectionString); await c.OpenAsync(token); await using var t = await c.BeginTransactionAsync(token);
            await using (var targets = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111); SELECT pg_advisory_xact_lock(821749620112)", c, t)) await targets.ExecuteNonQueryAsync(token);
            await using (var setup = new NpgsqlCommand(Setup, c, t)) await setup.ExecuteNonQueryAsync(token);
            if (authorized is not null && !await authorized()) return Result.Failure<T>("Access denied.");
            var result = await operation(c, t);
            if (result.IsSuccess)
            {
                if (authorized is not null && !await authorized()) return Result.Failure<T>("Access denied.");
                committing = true; if (commit is null) await t.CommitAsync(token); else await commit(t, token);
            }
            return result;
        }
        catch (Exception) when (committing) { return Result.Failure<T>("The Feature operation outcome could not be confirmed. Reload before retrying; do not retry automatically."); }
        catch (PostgresException e) when (!token.IsCancellationRequested && e.SqlState == PostgresErrorCodes.UniqueViolation) { return Result.Failure<T>("The Feature identity or working version is already in use. Retrieve it again."); }
        catch (PostgresException e) when (!token.IsCancellationRequested && e.SqlState == PostgresErrorCodes.NumericValueOutOfRange) { return Result.Failure<T>("The Feature numbering or revision limit has been reached."); }
        catch (NpgsqlException e) when (!token.IsCancellationRequested && (e.IsTransient || e is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction })) { return Result.Failure<T>("The Feature operation could not be completed."); }
    }
}
