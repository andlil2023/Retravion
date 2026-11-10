using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.TestCase;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Domain.Product;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.Infrastructure.TestCase;

public sealed partial class PostgresTestCaseStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : ITestCaseStore, ITestCaseChangeTransaction
{
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620114);
        CREATE SCHEMA IF NOT EXISTS retravion_test_case;
        CREATE TABLE IF NOT EXISTS retravion_test_case.counter (
            product_internal_id uuid PRIMARY KEY,
            last_number bigint NOT NULL CHECK (last_number > 0)
        );
        CREATE TABLE IF NOT EXISTS retravion_test_case.test_case (
            internal_id uuid PRIMARY KEY,
            product_internal_id uuid NOT NULL,
            number bigint NOT NULL CHECK (number > 0),
            version_id uuid NOT NULL UNIQUE,
            version_number integer NOT NULL,
            status text NOT NULL,
            title text NOT NULL,
            description text NOT NULL,
            preconditions text NOT NULL,
            steps jsonb NOT NULL CHECK (jsonb_typeof(steps) = 'array'),
            revision bigint NOT NULL CHECK (revision > 0),
            UNIQUE(product_internal_id, number)
        );
        ALTER TABLE retravion_test_case.test_case DROP CONSTRAINT IF EXISTS test_case_version_number_check;
        ALTER TABLE retravion_test_case.test_case DROP CONSTRAINT IF EXISTS test_case_status_check;
        ALTER TABLE retravion_test_case.test_case ADD COLUMN IF NOT EXISTS creation_reason text;
        ALTER TABLE retravion_test_case.test_case ADD COLUMN IF NOT EXISTS source_version_id uuid;
        ALTER TABLE retravion_test_case.test_case ADD COLUMN IF NOT EXISTS links jsonb NOT NULL DEFAULT '[]';
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='retravion_test_case.test_case'::regclass AND conname='test_case_valid_version') THEN
                ALTER TABLE retravion_test_case.test_case ADD CONSTRAINT test_case_valid_version CHECK (
                    version_number>0 AND status IN ('Draft','InReview','Approved') AND jsonb_typeof(links)='array' AND
                    ((version_number=1 AND creation_reason IS NULL AND source_version_id IS NULL) OR
                     (version_number>1 AND creation_reason IS NOT NULL AND length(btrim(creation_reason))>0 AND source_version_id IS NOT NULL)));
            END IF;
        END $$;
        CREATE TABLE IF NOT EXISTS retravion_test_case.test_case_version (
            internal_id uuid NOT NULL REFERENCES retravion_test_case.test_case(internal_id),
            product_internal_id uuid NOT NULL,
            number bigint NOT NULL CHECK (number>0),
            version_id uuid PRIMARY KEY,
            version_number integer NOT NULL CHECK (version_number>0),
            status text NOT NULL CHECK (status IN ('Draft','InReview','Approved')),
            title text NOT NULL, description text NOT NULL, preconditions text NOT NULL,
            steps jsonb NOT NULL CHECK (jsonb_typeof(steps)='array'),
            revision bigint NOT NULL CHECK (revision>0),
            creation_reason text, source_version_id uuid REFERENCES retravion_test_case.test_case_version(version_id),
            links jsonb NOT NULL CHECK (jsonb_typeof(links)='array'),
            UNIQUE(internal_id,version_number),
            CHECK ((version_number=1 AND creation_reason IS NULL AND source_version_id IS NULL) OR
                (version_number>1 AND creation_reason IS NOT NULL AND length(btrim(creation_reason))>0 AND source_version_id IS NOT NULL))
        );
        ALTER TABLE retravion_test_case.test_case_version ADD COLUMN IF NOT EXISTS release_protected boolean NOT NULL DEFAULT false;
        CREATE UNIQUE INDEX IF NOT EXISTS test_case_one_working_version ON retravion_test_case.test_case_version(internal_id) WHERE status<>'Approved';
        ALTER TABLE retravion_test_case.test_case ADD COLUMN IF NOT EXISTS area_id uuid REFERENCES retravion_product.area(internal_id);
        ALTER TABLE retravion_test_case.test_case_version ADD COLUMN IF NOT EXISTS area_id uuid REFERENCES retravion_product.area(internal_id);
        INSERT INTO retravion_test_case.test_case_version (internal_id,product_internal_id,number,version_id,version_number,status,title,description,preconditions,steps,revision,creation_reason,source_version_id,links) SELECT internal_id,product_internal_id,number,version_id,version_number,status,
            title,description,preconditions,steps,revision,creation_reason,source_version_id,links FROM retravion_test_case.test_case t
            WHERE NOT EXISTS (SELECT 1 FROM retravion_test_case.test_case_version v WHERE v.version_id=t.version_id)
            ON CONFLICT(version_id) DO NOTHING;
        CREATE TABLE IF NOT EXISTS retravion_test_case.history (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            internal_id uuid NOT NULL REFERENCES retravion_test_case.test_case(internal_id),
            actor_identifier text NOT NULL, occurred_at_utc_ticks bigint NOT NULL,
            action text NOT NULL, change jsonb NOT NULL
        );
        ALTER TABLE retravion_test_case.history ADD COLUMN IF NOT EXISTS operation_id uuid UNIQUE;
        """;
    private const string StoredColumns = "internal_id,product_internal_id,number,version_id,version_number,status,title,description,preconditions,steps,revision,creation_reason,source_version_id,links,area_id";
    private const string Columns = "r.internal_id,r.product_internal_id,r.number,r.version_id,r.version_number,r.status,r.title,r.description,r.preconditions,r.steps,r.revision,r.creation_reason,r.source_version_id,r.links,(SELECT max(v.version_number) FROM retravion_test_case.test_case_version v WHERE v.internal_id=r.internal_id AND v.status='Approved'),r.area_id";

    public Task<Result<Maybe<TestCaseAuditDetails>>> Outcome(Guid productInternalId, Guid operationId, CancellationToken cancellationToken = default) =>
        Execute(async (c, t) =>
        {
            await using var command = new NpgsqlCommand("SELECT h.change FROM retravion_test_case.history h JOIN retravion_test_case.test_case r ON r.internal_id=h.internal_id WHERE r.product_internal_id=$1 AND h.operation_id=$2", c, t);
            command.Parameters.AddWithValue(productInternalId); command.Parameters.AddWithValue(operationId);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            if (json is null) return Result.Success(Maybe<TestCaseAuditDetails>.None);
            var change = JsonSerializer.Deserialize<TestCaseAuditDetails>(json);
            return change is null ? Result.Failure<Maybe<TestCaseAuditDetails>>("Invalid persisted Test Case operation history.") :
                Result.Success(Maybe<TestCaseAuditDetails>.From(change));
        }, cancellationToken);

    public Task<Result<IReadOnlyList<TestCaseSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_test_case.test_case r WHERE product_internal_id=$1 ORDER BY number", productInternalId, cancellationToken);

    public Task<Result<IReadOnlyList<TestCaseSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_test_case.test_case_version r WHERE internal_id=$1 ORDER BY version_number", internalId, cancellationToken);

    private Task<Result<IReadOnlyList<TestCaseSnapshot>>> Rows(string sql, Guid id, CancellationToken token) =>
        Execute<IReadOnlyList<TestCaseSnapshot>>(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(id);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<TestCaseSnapshot>();
            while (await reader.ReadAsync(token))
            {
                var row = Read(reader);
                if (row.IsFailure) return Result.Failure<IReadOnlyList<TestCaseSnapshot>>(row.Error);
                rows.Add(row.Value);
            }
            return rows;
        }, token);

    public Task<Result<Maybe<TestCaseSnapshot>>> Find(Guid internalId, Guid? versionId = null, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            var sql = versionId is null ? $"SELECT {Columns} FROM retravion_test_case.test_case r WHERE internal_id=$1" :
                $"SELECT {Columns} FROM retravion_test_case.test_case_version r WHERE internal_id=$1 AND version_id=$2";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(internalId);
            if (versionId is not null) command.Parameters.AddWithValue(versionId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<TestCaseSnapshot>.None);
            return Read(reader).Map(value => Maybe<TestCaseSnapshot>.From(value));
        }, cancellationToken);

    public Task<Result<IReadOnlyList<TestCaseHistory>>> History(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute<IReadOnlyList<TestCaseHistory>>(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT actor_identifier,occurred_at_utc_ticks,action,change FROM retravion_test_case.history WHERE internal_id=$1 ORDER BY id", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<TestCaseHistory>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var change = JsonSerializer.Deserialize<TestCaseAuditDetails>(reader.GetString(3));
                if (change is null) return Result.Failure<IReadOnlyList<TestCaseHistory>>("The persisted Test Case history is invalid.");
                rows.Add(new(new AuditActor.Identified(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), change));
            }
            return rows;
        }, cancellationToken);

    public Task<Result<TestCaseSnapshot>> Create(Guid productInternalId, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Guid? areaId = null,
        Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        if (productInternalId == Guid.Empty || content is null || actor is not AuditActor.Identified || operationId == Guid.Empty)
            return Task.FromResult(Result.Failure<TestCaseSnapshot>("Invalid Test Case creation."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, productInternalId, cancellationToken)) return Inactive();
            if (!await PostgresProductAreaStore.CanSelect(connection, transaction, productInternalId, null, areaId, cancellationToken))
                return Result.Failure<TestCaseSnapshot>("Select an active area from this Product.");
            await using var allocate = new NpgsqlCommand("""
                INSERT INTO retravion_test_case.counter AS counter(product_internal_id,last_number) VALUES($1,1)
                ON CONFLICT(product_internal_id) DO UPDATE SET last_number=counter.last_number+1 RETURNING last_number
                """, connection, transaction);
            allocate.Parameters.AddWithValue(productInternalId);
            var number = (long)(await allocate.ExecuteScalarAsync(cancellationToken))!;
            var value = Case.Create(productInternalId, number, content, areaId);
            if (value.IsFailure) return Result.Failure<TestCaseSnapshot>(value.Error);
            var testCase = value.Value;
            await using var command = new NpgsqlCommand("""
                INSERT INTO retravion_test_case.test_case(internal_id,product_internal_id,number,version_id,version_number,status,title,description,preconditions,steps,revision,links,area_id)
                VALUES($1,$2,$3,$4,1,'Draft',$5,$6,$7,$8,1,'[]',$9)
                """, connection, transaction);
            command.Parameters.AddWithValue(testCase.InternalId);
            command.Parameters.AddWithValue(productInternalId);
            command.Parameters.AddWithValue(number);
            command.Parameters.AddWithValue(testCase.VersionId);
            ContentParameters(command, content);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)areaId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await SaveVersion(connection, transaction, testCase.InternalId, cancellationToken);
            await Evidence(connection, transaction, actor, occurredAt, "test-case.create", null, testCase, null, null, cancellationToken, operationId);
            return new TestCaseSnapshot(testCase, 1);
        }, cancellationToken, authorized);
    }

    public Task<Result<TestCaseSnapshot>> Update(TestCaseSnapshot expected, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, bool changeArea = false, Guid? areaId = null,
        Guid? operationId = null, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, (current, _, _) => Task.FromResult(current.Edit(content)
            .Bind(value => changeArea ? value.Classify(areaId) : Result.Success(value))), actor, occurredAt, "test-case.edit", null, null, cancellationToken, operationId, authorized);

    public Task<Result<TestCaseSnapshot>> Review(TestCaseSnapshot expected, string decision, string? comment, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default) =>
        Mutate(expected, async (current, connection, transaction) => decision switch
        {
            "InReview" => current.SubmitForReview(),
            "Draft" => current.ReturnToDraft(),
            "Approved" => current.Approve(await TargetsApproved(connection, transaction, current, cancellationToken)),
            _ => Result.Failure<Case>("Invalid review decision.")
        }, actor, occurredAt, decision switch
        {
            "InReview" => "test-case.review.InReview", "Approved" => "test-case.review.Approved",
            "Draft" => "test-case.review.Draft", _ => "test-case.review"
        }, decision, comment, cancellationToken);

    public Task<Result<TestCaseSnapshot>> CreateSuccessor(TestCaseSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, (current, _, _) => Task.FromResult(current.CreateSuccessor(reason)), actor, occurredAt,
            "test-case.version.create", "Successor", reason, cancellationToken, operationId, authorized);

    public Task<Result<TestCaseSnapshot>> Reopen(TestCaseSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default) =>
        Mutate(expected, (current, _, _) => Task.FromResult(current.Reopen(reason)), actor, occurredAt,
            "test-case.reopen", "Reopened", reason, cancellationToken);

    private Task<Result<TestCaseSnapshot>> Mutate(TestCaseSnapshot expected,
        Func<Case, NpgsqlConnection, NpgsqlTransaction, Task<Result<Case>>> operation, AuditActor actor, DateTimeOffset occurredAt,
        string action, string? decision, string? comment, CancellationToken token, Guid? operationId = null, Func<Task<bool>>? authorized = null)
    {
        if (expected is null || expected.TestCase is null || expected.Revision <= 0 || actor is not AuditActor.Identified || operationId == Guid.Empty)
            return Task.FromResult(Result.Failure<TestCaseSnapshot>("Invalid Test Case change."));
        return Execute(async (connection, transaction) =>
        {
            var before = expected.TestCase;
            if (!await ProductActive(connection, transaction, before.ProductInternalId, token)) return Inactive();
            TestCaseSnapshot current;
            await using (var read = new NpgsqlCommand($"SELECT {Columns} FROM retravion_test_case.test_case r WHERE internal_id=$1 FOR UPDATE", connection, transaction))
            {
                read.Parameters.AddWithValue(before.InternalId);
                await using var reader = await read.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return Stale();
                var restored = Read(reader);
                if (restored.IsFailure) return Result.Failure<TestCaseSnapshot>(restored.Error);
                current = restored.Value;
            }
            // A repeated successor request opens the already-created working version, without another event.
            if (decision == "Successor" && !string.IsNullOrWhiteSpace(comment) && before.Status == "Approved" &&
                current.TestCase.Status != "Approved" && current.TestCase.SourceVersionId == before.VersionId)
            {
                await using var source = new NpgsqlCommand($"SELECT {Columns} FROM retravion_test_case.test_case_version r WHERE internal_id=$1 AND version_id=$2", connection, transaction);
                source.Parameters.AddWithValue(before.InternalId); source.Parameters.AddWithValue(before.VersionId);
                await using var reader = await source.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    var saved = Read(reader);
                    if (saved.IsSuccess && saved.Value.Revision == expected.Revision && saved.Value.TestCase.SameState(before)) return current;
                }
                return Stale();
            }
            if (current.Revision != expected.Revision || !current.TestCase.SameState(before)) return Stale();
            var replacement = await operation(current.TestCase, connection, transaction);
            if (replacement.IsFailure) return Result.Failure<TestCaseSnapshot>(replacement.Error);
            var after = replacement.Value;
            if (!await PostgresProductAreaStore.CanSelect(connection, transaction, before.ProductInternalId, before.AreaId, after.AreaId, token))
                return Result.Failure<TestCaseSnapshot>("Select an active area from this Product.");
            if (before.SameState(after)) return current;
            if (after.VersionId == before.VersionId)
            {
                await using var protection = new NpgsqlCommand("SELECT release_protected FROM retravion_test_case.test_case_version WHERE internal_id=$1 AND version_id=$2", connection, transaction);
                protection.Parameters.AddWithValue(before.InternalId); protection.Parameters.AddWithValue(before.VersionId);
                if (await protection.ExecuteScalarAsync(token) is true)
                    return Result.Failure<TestCaseSnapshot>("This exact version and its owned links are permanently protected by a Released revision. Create a successor for changes.");
            }
            await using var update = new NpgsqlCommand("""
                UPDATE retravion_test_case.test_case SET title=$1,description=$2,preconditions=$3,steps=$4,
                    status=$5,version_id=$6,version_number=$7,creation_reason=$8,source_version_id=$9,links=$10,area_id=$12,revision=revision+1
                WHERE internal_id=$11 RETURNING revision
                """, connection, transaction);
            ContentParameters(update, after.Content);
            update.Parameters.AddWithValue(after.Status); update.Parameters.AddWithValue(after.VersionId); update.Parameters.AddWithValue(after.VersionNumber);
            update.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)after.CreationReason ?? DBNull.Value);
            update.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)after.SourceVersionId ?? DBNull.Value);
            update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(after.Links));
            update.Parameters.AddWithValue(before.InternalId);
            update.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)after.AreaId ?? DBNull.Value);
            var revision = (long)(await update.ExecuteScalarAsync(token))!;
            await SaveVersion(connection, transaction, before.InternalId, token);
            await Evidence(connection, transaction, actor, occurredAt, action, before, after, decision, comment, token, operationId);
            await using var latest = new NpgsqlCommand("SELECT max(version_number) FROM retravion_test_case.test_case_version WHERE internal_id=$1 AND status='Approved'", connection, transaction);
            latest.Parameters.AddWithValue(before.InternalId);
            var approved = await latest.ExecuteScalarAsync(token);
            return new TestCaseSnapshot(after, revision, approved is int number ? number : null);
        }, token, authorized);
    }

    private static async Task SaveVersion(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO retravion_test_case.test_case_version ({StoredColumns}) SELECT {StoredColumns} FROM retravion_test_case.test_case WHERE internal_id=$1
            ON CONFLICT(version_id) DO UPDATE SET title=EXCLUDED.title,description=EXCLUDED.description,preconditions=EXCLUDED.preconditions,
                steps=EXCLUDED.steps,status=EXCLUDED.status,revision=EXCLUDED.revision,links=EXCLUDED.links,area_id=EXCLUDED.area_id
            """, connection, transaction);
        command.Parameters.AddWithValue(id); await command.ExecuteNonQueryAsync(token);
    }

    private static void ContentParameters(NpgsqlCommand command, TestCaseContent content)
    {
        command.Parameters.AddWithValue(content.Title);
        command.Parameters.AddWithValue(content.Description);
        command.Parameters.AddWithValue(content.Preconditions);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(content.Steps));
    }

    private static Result<TestCaseSnapshot> Read(NpgsqlDataReader reader)
    {
        if (reader.GetInt64(10) <= 0) return Result.Failure<TestCaseSnapshot>("The persisted Test Case state is invalid.");
        try
        {
            var steps = JsonSerializer.Deserialize<TestCaseStep[]>(reader.GetString(9));
            var links = JsonSerializer.Deserialize<TestCaseRequirementLink[]>(reader.GetString(13));
            if (steps is null || links is null) return Result.Failure<TestCaseSnapshot>("The persisted Test Case steps or links are invalid.");
            var content = TestCaseContent.Create(reader.GetString(6), reader.GetString(7), reader.GetString(8), steps);
            return content.Bind(value => Case.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(3), reader.GetInt64(2), value,
                    reader.GetString(5), reader.GetInt32(4), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetGuid(12), links, reader.IsDBNull(15) ? null : reader.GetGuid(15)))
                .Map(value => new TestCaseSnapshot(value, reader.GetInt64(10), reader.IsDBNull(14) ? null : reader.GetInt32(14)));
        }
        catch (JsonException)
        { return Result.Failure<TestCaseSnapshot>("The persisted Test Case steps or links are invalid."); }
    }

    private static Result<TestCaseSnapshot> Stale() => Result.Failure<TestCaseSnapshot>("The Test Case changed or a newer version exists. Retrieve it again before editing.");
    private static Result<TestCaseSnapshot> Inactive() => Result.Failure<TestCaseSnapshot>("The responsible Product is retired or no longer exists.");

    private static async Task<bool> ProductActive(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", connection, transaction))
            if (!(bool)(await exists.ExecuteScalarAsync(token))!) return false;
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static async Task Evidence(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor actor, DateTimeOffset occurredAt,
        string action, Case? before, Case after, string? decision, string? comment, CancellationToken token, Guid? operationId = null)
    {
        var beforeArea = before is null ? null : await PostgresProductAreaStore.Evidence(connection, transaction, before.ProductInternalId, before.AreaId, token);
        var afterArea = await PostgresProductAreaStore.Evidence(connection, transaction, after.ProductInternalId, after.AreaId, token);
        TestCaseAuditValues Values(Case value, AreaEvidence? area) => new(value.ProductInternalId, value.Id, value.Content.Title,
            value.Content.Description, value.Content.Preconditions, value.Status, value.Content.Steps, value.Links.ToArray(), area);
        var change = new TestCaseAuditDetails(after.InternalId, after.VersionId, after.VersionNumber,
            before is null || decision == "Successor" ? null : Values(before, beforeArea), Values(after, afterArea), decision,
            decision == "Successor" ? null : comment, after.SourceVersionId, decision == "Reopened" ? comment : after.CreationReason, operationId);
        await using var history = new NpgsqlCommand("INSERT INTO retravion_test_case.history(internal_id,actor_identifier,occurred_at_utc_ticks,action,change,operation_id) VALUES($1,$2,$3,$4,$5,$6)", connection, transaction);
        history.Parameters.AddWithValue(after.InternalId); history.Parameters.AddWithValue(((AuditActor.Identified)actor).Identifier);
        history.Parameters.AddWithValue(occurredAt.UtcTicks); history.Parameters.AddWithValue(action);
        history.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(change));
        history.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)operationId ?? DBNull.Value);
        await history.ExecuteNonQueryAsync(token);
        await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success,
                testCaseChange: change), token);
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token,
        Func<Task<bool>>? authorized = null)
    {
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            // Same lock order as Requirement -> User Need operations. Exact target checks cannot race their mutations.
            await using (var targets = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111); SELECT pg_advisory_xact_lock(821749620112)", connection, transaction))
                await targets.ExecuteNonQueryAsync(token);
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
        { return Result.Failure<T>("The Test Case operation outcome could not be confirmed. Reload before retrying; do not retry automatically."); }
        catch (PostgresException exception) when (!token.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Result.Failure<T>("The Test Case identity or working version is already in use. Retrieve it again."); }
        catch (PostgresException exception) when (!token.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        { return Result.Failure<T>("The Test Case numbering or revision limit has been reached."); }
        catch (NpgsqlException exception) when (!token.IsCancellationRequested && (exception.IsTransient || exception is PostgresException
        { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction }))
        { return Result.Failure<T>("The Test Case operation could not be completed."); }
    }
}
