using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;

namespace Retravion.Infrastructure.Revision;

public sealed partial class PostgresRevisionStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : IRevisionStore, IRevisionChangeTransaction
{
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620115);
        ALTER TABLE IF EXISTS retravion_need.need_version ADD COLUMN IF NOT EXISTS release_protected boolean NOT NULL DEFAULT false;
        ALTER TABLE IF EXISTS retravion_requirement.requirement_version ADD COLUMN IF NOT EXISTS release_protected boolean NOT NULL DEFAULT false;
        ALTER TABLE IF EXISTS retravion_requirement.requirement_version ADD COLUMN IF NOT EXISTS area_id uuid REFERENCES retravion_product.area(internal_id);
        ALTER TABLE IF EXISTS retravion_test_case.test_case_version ADD COLUMN IF NOT EXISTS release_protected boolean NOT NULL DEFAULT false;
        ALTER TABLE IF EXISTS retravion_test_case.test_case_version ADD COLUMN IF NOT EXISTS area_id uuid REFERENCES retravion_product.area(internal_id);
        CREATE SCHEMA IF NOT EXISTS retravion_revision;
        CREATE COLLATION IF NOT EXISTS retravion_revision.identifier_fold (provider=icu, locale='und-u-ks-level2', deterministic=false);
        CREATE TABLE IF NOT EXISTS retravion_revision.revision (
            internal_id uuid PRIMARY KEY CHECK (internal_id<>'00000000-0000-0000-0000-000000000000'),
            product_internal_id uuid NOT NULL CHECK (product_internal_id<>'00000000-0000-0000-0000-000000000000'),
            identifier text NOT NULL CHECK (length(btrim(identifier))>0 AND char_length(identifier)<=100),
            description text NOT NULL,
            state text NOT NULL CHECK (state='Draft'),
            selections jsonb NOT NULL CHECK (jsonb_typeof(selections)='array'),
            revision bigint NOT NULL CHECK (revision>0),
            deleted boolean NOT NULL DEFAULT false
        );
        ALTER TABLE retravion_revision.revision DROP CONSTRAINT IF EXISTS revision_state_check;
        ALTER TABLE retravion_revision.revision ADD COLUMN IF NOT EXISTS release_evidence jsonb;
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='retravion_revision.revision'::regclass AND conname='revision_valid_state') THEN
                ALTER TABLE retravion_revision.revision ADD CONSTRAINT revision_valid_state CHECK (
                    (state='Draft' AND release_evidence IS NULL) OR
                    (state='Released' AND release_evidence IS NOT NULL AND NOT deleted));
            END IF;
        END $$;
        CREATE UNIQUE INDEX IF NOT EXISTS revision_identifier_unique ON retravion_revision.revision
            (product_internal_id,identifier COLLATE retravion_revision.identifier_fold) WHERE NOT deleted;
        CREATE TABLE IF NOT EXISTS retravion_revision.history (
            sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            internal_id uuid NOT NULL REFERENCES retravion_revision.revision(internal_id),
            operation_id uuid NOT NULL UNIQUE CHECK (operation_id<>'00000000-0000-0000-0000-000000000000'),
            actor_identifier text NOT NULL, occurred_at_utc_ticks bigint NOT NULL,
            action text NOT NULL, change jsonb NOT NULL
        );
        """;
    private const string Columns = "internal_id,product_internal_id,identifier,description,state,selections,revision,deleted,release_evidence";

    public Task<Result<IReadOnlyList<RevisionSnapshot>>> List(Guid product, CancellationToken token = default) =>
        Execute<IReadOnlyList<RevisionSnapshot>>(async (c, t) =>
        {
            await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_revision.revision WHERE product_internal_id=$1 AND NOT deleted ORDER BY identifier", c, t);
            command.Parameters.AddWithValue(product);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<RevisionSnapshot>();
            while (await reader.ReadAsync(token))
            {
                var row = Read(reader);
                if (row.IsFailure) return Result.Failure<IReadOnlyList<RevisionSnapshot>>(row.Error);
                rows.Add(row.Value);
            }
            return rows;
        }, token);

    public Task<Result<RevisionRetrieval>> Retrieve(Guid product, Guid id, Guid? operation = null, CancellationToken token = default) =>
        Execute<RevisionRetrieval>(async (c, t) =>
        {
            var row = await Find(c, t, product, id, token);
            if (row.IsFailure) return Result.Failure<RevisionRetrieval>(row.Error);
            var history = await ReadHistory(c, t, product, id, token);
            if (history.IsFailure) return Result.Failure<RevisionRetrieval>(history.Error);
            return new RevisionRetrieval(row.Value.HasValue ? row.Value.Value : null,
                operation is null ? null : history.Value.SingleOrDefault(h => h.Change.OperationId == operation));
        }, token);

    public Task<Result<IReadOnlyList<RevisionHistory>>> History(Guid product, Guid id, CancellationToken token = default) =>
        Execute((c, t) => ReadHistory(c, t, product, id, token), token);

    private static async Task<Result<IReadOnlyList<RevisionHistory>>> ReadHistory(NpgsqlConnection c, NpgsqlTransaction t, Guid product, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT h.actor_identifier,h.occurred_at_utc_ticks,h.action,h.change FROM retravion_revision.history h
            JOIN retravion_revision.revision r ON r.internal_id=h.internal_id
            WHERE r.product_internal_id=$1 AND r.internal_id=$2 ORDER BY h.sequence
            """, c, t);
        command.Parameters.AddWithValue(product); command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<RevisionHistory>();
        while (await reader.ReadAsync(token))
        {
            var change = JsonSerializer.Deserialize<RevisionAuditDetails>(reader.GetString(3));
            if (change is null) return Result.Failure<IReadOnlyList<RevisionHistory>>("Invalid persisted revision history.");
            rows.Add(new(new AuditActor.Identified(reader.GetString(0)), new(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), change));
        }
        return rows;
    }

    private static async Task<Result<Maybe<RevisionSnapshot>>> Find(NpgsqlConnection c, NpgsqlTransaction t, Guid product, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_revision.revision WHERE product_internal_id=$1 AND internal_id=$2 FOR UPDATE", c, t);
        command.Parameters.AddWithValue(product); command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader).Map(Maybe<RevisionSnapshot>.From) : Result.Success(Maybe<RevisionSnapshot>.None);
    }

    private static Result<RevisionSnapshot> Read(NpgsqlDataReader reader)
    {
        var state = reader.GetString(4);
        if (reader.GetInt64(6) <= 0 || state is not ("Draft" or "Released") ||
            (state == "Released" && (reader.GetBoolean(7) || reader.IsDBNull(8))) ||
            (state == "Draft" && !reader.IsDBNull(8)))
            return Result.Failure<RevisionSnapshot>("Invalid persisted revision state.");
        var selections = JsonSerializer.Deserialize<RevisionSelection[]>(reader.GetString(5));
        var draft = DraftRevision.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), selections);
        if (draft.IsFailure) return Result.Failure<RevisionSnapshot>(draft.Error);
        if (state == "Draft") return new RevisionSnapshot(draft.Value, reader.GetInt64(6), reader.GetBoolean(7));
        var evidence = JsonSerializer.Deserialize<RevisionReleaseEvidence>(reader.GetString(8));
        return evidence is null ? Result.Failure<RevisionSnapshot>("Invalid frozen release evidence.") :
            ReleasedRevision.Restore(draft.Value, evidence.Actor, evidence.OccurredAt, evidence.Members.IsDefault ? null : evidence.Members)
                .Map(released => new RevisionSnapshot(released, reader.GetInt64(6)));
    }

    public Task<Result<RevisionSnapshot>> Create(Guid operation, DraftRevision draft, AuditActor actor, DateTimeOffset time,
        Func<Task<bool>> authorized, CancellationToken token = default) =>
        Write(operation, actor, authorized, async (c, t) =>
        {
            if (draft is null || !draft.Selections.IsEmpty) return Result.Failure<RevisionSnapshot>("Create an empty Draft first.");
            if (!await ProductActive(c, t, draft.ProductInternalId, token)) return Inactive();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO retravion_revision.revision(internal_id,product_internal_id,identifier,description,state,selections,revision)
                VALUES($1,$2,$3,$4,'Draft','[]',1)
                """, c, t);
            insert.Parameters.AddWithValue(draft.InternalId); insert.Parameters.AddWithValue(draft.ProductInternalId);
            insert.Parameters.AddWithValue(draft.Identifier); insert.Parameters.AddWithValue(draft.Description);
            await insert.ExecuteNonQueryAsync(token);
            await Evidence(c, t, operation, actor, time, "revision.create", null, draft, false, token);
            return new RevisionSnapshot(draft, 1);
        }, token);

    public Task<Result<RevisionSnapshot>> Copy(Guid operation, DraftRevision destination, Guid sourceId, AuditActor actor, DateTimeOffset time,
        Func<Task<bool>> authorized, CancellationToken token = default) =>
        Write(operation, actor, authorized, async (c, t) =>
        {
            if (destination is null || !destination.Selections.IsEmpty)
                return Result.Failure<RevisionSnapshot>("Provide new Draft metadata without selections.");
            if (!await ProductActive(c, t, destination.ProductInternalId, token)) return Inactive();
            var found = await Find(c, t, destination.ProductInternalId, sourceId, token);
            if (found.IsFailure) return Result.Failure<RevisionSnapshot>(found.Error);
            if (found.Value.HasNoValue || found.Value.Value is not { Deleted: false, Value: ReleasedRevision source })
                return Result.Failure<RevisionSnapshot>("Released source revision not found.");
            var copied = source.CopyToDraft(destination.InternalId, destination.Identifier, destination.Description);
            if (copied.IsFailure) return Result.Failure<RevisionSnapshot>(copied.Error);
            foreach (var endpoint in copied.Value.Selections)
            {
                var target = await LoadEndpoint(c, t, destination.ProductInternalId, endpoint, token);
                if (target.IsFailure) return Result.Failure<RevisionSnapshot>(target.Error);
            }
            await using var insert = new NpgsqlCommand("""
                INSERT INTO retravion_revision.revision(internal_id,product_internal_id,identifier,description,state,selections,revision)
                VALUES($1,$2,$3,$4,'Draft',$5,1)
                """, c, t);
            insert.Parameters.AddWithValue(copied.Value.InternalId); insert.Parameters.AddWithValue(copied.Value.ProductInternalId);
            insert.Parameters.AddWithValue(copied.Value.Identifier); insert.Parameters.AddWithValue(copied.Value.Description);
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(copied.Value.Selections));
            await insert.ExecuteNonQueryAsync(token);
            await Evidence(c, t, operation, actor, time, "revision.copy", null, copied.Value, false, token, source.InternalId);
            return new RevisionSnapshot(copied.Value, 1);
        }, token);

    public Task<Result<RevisionSnapshot>> Edit(Guid operation, RevisionSnapshot expected, string? identifier, string? description,
        AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) =>
        Mutate(operation, expected, actor, time, authorized, "revision.edit", d => d.Edit(identifier, description), false, token);

    public Task<Result<RevisionSnapshot>> Select(Guid operation, RevisionSnapshot expected, string action, RevisionSelection target,
        RevisionSelection? replacement, AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) =>
        Mutate(operation, expected, actor, time, authorized, SelectionAction(action), d => d.ChangeSelection(action, target, replacement), false, token);

    public Task<Result<RevisionSnapshot>> Delete(Guid operation, RevisionSnapshot expected, AuditActor actor, DateTimeOffset time,
        Func<Task<bool>> authorized, CancellationToken token = default) =>
        Mutate(operation, expected, actor, time, authorized, "revision.delete", Result.Success, true, token);

    private Task<Result<RevisionSnapshot>> Mutate(Guid operation, RevisionSnapshot expected, AuditActor actor, DateTimeOffset time,
        Func<Task<bool>> authorized, string action, Func<DraftRevision, Result<DraftRevision>> transform, bool delete, CancellationToken token) =>
        Write(operation, actor, authorized, async (c, t) =>
        {
            if (expected?.Value is not DraftRevision draft || expected.Deleted || expected.Revision <= 0) return Stale();
            if (!await ProductActive(c, t, draft.ProductInternalId, token)) return Inactive();
            var found = await Find(c, t, draft.ProductInternalId, draft.InternalId, token);
            if (found.IsFailure) return Result.Failure<RevisionSnapshot>(found.Error);
            if (found.Value.HasNoValue || found.Value.Value.Deleted || found.Value.Value.Revision != expected.Revision ||
                found.Value.Value.Value is not DraftRevision current || !current.SameValues(draft)) return Stale();
            var next = transform(current);
            if (next.IsFailure) return Result.Failure<RevisionSnapshot>(next.Error);
            if (!delete && next.Value.SameValues(draft)) return found.Value.Value;
            foreach (var endpoint in next.Value.Selections)
            {
                var target = await LoadEndpoint(c, t, draft.ProductInternalId, endpoint, token);
                if (target.IsFailure) return Result.Failure<RevisionSnapshot>(target.Error);
            }
            await using var update = new NpgsqlCommand("""
                UPDATE retravion_revision.revision SET identifier=$1,description=$2,selections=$3,deleted=$4,revision=revision+1
                WHERE internal_id=$5 RETURNING revision
                """, c, t);
            update.Parameters.AddWithValue(next.Value.Identifier); update.Parameters.AddWithValue(next.Value.Description);
            update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(next.Value.Selections));
            update.Parameters.AddWithValue(delete); update.Parameters.AddWithValue(next.Value.InternalId);
            var revision = (long)(await update.ExecuteScalarAsync(token))!;
            await Evidence(c, t, operation, actor, time, action, draft, next.Value, delete, token);
            return new RevisionSnapshot(next.Value, revision, delete);
        }, token);

    private static string SelectionAction(string action) => action switch
    { "add" => "revision.selection.add", "remove" => "revision.selection.remove", "replace" => "revision.selection.replace", _ => "revision.selection" };

    private Task<Result<RevisionSnapshot>> Write(Guid operation, AuditActor actor, Func<Task<bool>> authorized,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<RevisionSnapshot>>> change, CancellationToken token)
    {
        return Execute(async (c, t) =>
        {
            if (!await authorized() || actor is not AuditActor.Identified) return Result.Failure<RevisionSnapshot>("Access denied.");
            if (operation == Guid.Empty) return Result.Failure<RevisionSnapshot>("An immutable operation identity is required.");
            await using (var previous = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM retravion_revision.history WHERE operation_id=$1)", c, t))
            {
                previous.Parameters.AddWithValue(operation);
                if (await previous.ExecuteScalarAsync(token) is true)
                    return Result.Failure<RevisionSnapshot>("This operation already committed. Retrieve its outcome before any new change.");
            }
            var result = await change(c, t);
            if (result.IsSuccess && !await authorized()) return Result.Failure<RevisionSnapshot>("Access denied.");
            return result;
        }, token);
    }

    private static async Task Evidence(NpgsqlConnection c, NpgsqlTransaction t, Guid operation, AuditActor actor, DateTimeOffset time,
        string action, Retravion.Domain.Revision.Revision? before, Retravion.Domain.Revision.Revision after, bool delete, CancellationToken token, Guid? sourceId = null)
    {
        static RevisionAuditValues Values(Retravion.Domain.Revision.Revision d) => new(d.Identifier, d.Description, d.State, d.Selections,
            d is ReleasedRevision r ? new(r.ReleasedBy, r.ReleasedAt, r.Members) : null);
        var change = new RevisionAuditDetails(after.ProductInternalId, after.InternalId, operation, before is null ? null : Values(before), delete ? null : Values(after), sourceId);
        await using var history = new NpgsqlCommand("INSERT INTO retravion_revision.history(internal_id,operation_id,actor_identifier,occurred_at_utc_ticks,action,change) VALUES($1,$2,$3,$4,$5,$6)", c, t);
        history.Parameters.AddWithValue(after.InternalId); history.Parameters.AddWithValue(operation);
        history.Parameters.AddWithValue(((AuditActor.Identified)actor).Identifier); history.Parameters.AddWithValue(time.UtcTicks);
        history.Parameters.AddWithValue(action); history.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(change));
        await history.ExecuteNonQueryAsync(token);
        await PostgresAuditSubmission.SubmitInTransactionAsync(c, t, new AuditInformation(actor, time, action,
            new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success, revisionChange: change), token);
    }

    private static Result<RevisionSnapshot> Stale() => Result.Failure<RevisionSnapshot>("The revision changed or was deleted. Retrieve it before editing.");
    private static Result<RevisionSnapshot> Inactive() => Result.Failure<RevisionSnapshot>("The responsible Product is retired or no longer exists.");
    private static async Task<bool> ProductActive(NpgsqlConnection c, NpgsqlTransaction t, Guid product, CancellationToken token)
    {
        if (!await Exists(c, t, "retravion_product.product", token)) return false;
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", c, t);
        command.Parameters.AddWithValue(product);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static async Task<bool> Exists(NpgsqlConnection c, NpgsqlTransaction t, string table, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", c, t);
        command.Parameters.AddWithValue(table);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token)
    {
        var commitStarted = false;
        try
        {
            await using var c = new NpgsqlConnection(connectionString);
            await c.OpenAsync(token);
            await using var t = await c.BeginTransactionAsync(token);
            // Match User Need -> Requirement -> Test Case -> Feature order, before revision and audit.
            // Feature writes also lock their Requirement targets before their own storage.
            await using (var locks = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111); SELECT pg_advisory_xact_lock(821749620112); SELECT pg_advisory_xact_lock(821749620114); SELECT pg_advisory_xact_lock(821749620118)", c, t))
                await locks.ExecuteNonQueryAsync(token);
            await using (var areas = new NpgsqlCommand(PostgresProductAreaStore.Setup, c, t)) await areas.ExecuteNonQueryAsync(token);
            await using (var setup = new NpgsqlCommand(Setup, c, t)) await setup.ExecuteNonQueryAsync(token);
            var result = await operation(c, t);
            if (result.IsSuccess)
            {
                commitStarted = true;
                if (commit is null) await t.CommitAsync(token); else await commit(t, token);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        { return Result.Failure<T>("The revision operation outcome could not be confirmed. Retrieve its outcome before retrying; do not retry automatically."); }
        catch (JsonException) { return Result.Failure<T>("Invalid persisted revision evidence."); }
        catch (PostgresException e) when (!token.IsCancellationRequested && e.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Result.Failure<T>("The revision identifier or immutable operation identity is already in use. Retrieve before retrying."); }
        catch (PostgresException e) when (!token.IsCancellationRequested && e.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        { return Result.Failure<T>("The revision limit has been reached."); }
        catch (NpgsqlException e) when (!token.IsCancellationRequested && (e.IsTransient || e is PostgresException
            { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction }))
        { return Result.Failure<T>("The revision operation could not be completed."); }
    }
}
