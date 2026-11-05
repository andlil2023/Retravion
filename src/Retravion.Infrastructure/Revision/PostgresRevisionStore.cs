using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;

namespace Retravion.Infrastructure.Revision;

public sealed partial class PostgresRevisionStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : IRevisionStore, IRevisionChangeTransaction
{
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620115);
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
    private const string Columns = "internal_id,product_internal_id,identifier,description,state,selections,revision,deleted";

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
        if (reader.GetString(4) != "Draft" || reader.GetInt64(6) <= 0) return Result.Failure<RevisionSnapshot>("Invalid persisted revision state.");
        var selections = JsonSerializer.Deserialize<RevisionSelection[]>(reader.GetString(5));
        return DraftRevision.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), selections)
            .Map(d => new RevisionSnapshot(d, reader.GetInt64(6), reader.GetBoolean(7)));
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
            if (expected?.Draft is null || expected.Deleted || expected.Revision <= 0) return Stale();
            if (!await ProductActive(c, t, expected.Draft.ProductInternalId, token)) return Inactive();
            var found = await Find(c, t, expected.Draft.ProductInternalId, expected.Draft.InternalId, token);
            if (found.IsFailure) return Result.Failure<RevisionSnapshot>(found.Error);
            if (found.Value.HasNoValue || found.Value.Value.Deleted || found.Value.Value.Revision != expected.Revision ||
                !found.Value.Value.Draft.SameValues(expected.Draft)) return Stale();
            var next = transform(found.Value.Value.Draft);
            if (next.IsFailure) return Result.Failure<RevisionSnapshot>(next.Error);
            if (!delete && next.Value.SameValues(expected.Draft)) return found.Value.Value;
            foreach (var endpoint in next.Value.Selections)
            {
                var target = await LoadEndpoint(c, t, expected.Draft.ProductInternalId, endpoint, token);
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
            await Evidence(c, t, operation, actor, time, action, expected.Draft, next.Value, delete, token);
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
        string action, DraftRevision? before, DraftRevision after, bool delete, CancellationToken token)
    {
        static RevisionAuditValues Values(DraftRevision d) => new(d.Identifier, d.Description, d.State, d.Selections);
        var change = new RevisionAuditDetails(after.ProductInternalId, after.InternalId, operation, before is null ? null : Values(before), delete ? null : Values(after));
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
            // Match User Need -> Requirement -> Test Case order, before the revision lock and audit lock.
            await using (var locks = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111); SELECT pg_advisory_xact_lock(821749620112); SELECT pg_advisory_xact_lock(821749620114)", c, t))
                await locks.ExecuteNonQueryAsync(token);
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
