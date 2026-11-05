using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.TestCase;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.AuditTrail;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.Infrastructure.TestCase;

public sealed class PostgresTestCaseStore(string connectionString,
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
            version_number integer NOT NULL CHECK (version_number = 1),
            status text NOT NULL CHECK (status = 'Draft'),
            title text NOT NULL,
            description text NOT NULL,
            preconditions text NOT NULL,
            steps jsonb NOT NULL CHECK (jsonb_typeof(steps) = 'array'),
            revision bigint NOT NULL CHECK (revision > 0),
            UNIQUE(product_internal_id, number)
        );
        """;
    private const string Columns = "internal_id,product_internal_id,number,version_id,version_number,status,title,description,preconditions,steps,revision";

    public Task<Result<IReadOnlyList<TestCaseSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Execute<IReadOnlyList<TestCaseSnapshot>>(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_test_case.test_case WHERE product_internal_id=$1 ORDER BY number", connection, transaction);
            command.Parameters.AddWithValue(productInternalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<TestCaseSnapshot>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = Read(reader);
                if (row.IsFailure) return Result.Failure<IReadOnlyList<TestCaseSnapshot>>(row.Error);
                rows.Add(row.Value);
            }
            return rows;
        }, cancellationToken);

    public Task<Result<Maybe<TestCaseSnapshot>>> Find(Guid internalId, Guid? versionId = null, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_test_case.test_case WHERE internal_id=$1 AND ($2::uuid IS NULL OR version_id=$2)", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)versionId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<TestCaseSnapshot>.None);
            return Read(reader).Map(value => Maybe<TestCaseSnapshot>.From(value));
        }, cancellationToken);

    public Task<Result<TestCaseSnapshot>> Create(Guid productInternalId, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        if (productInternalId == Guid.Empty || content is null || actor is not AuditActor.Identified)
            return Task.FromResult(Result.Failure<TestCaseSnapshot>("Invalid Test Case creation."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, productInternalId, cancellationToken)) return Inactive();
            await using var allocate = new NpgsqlCommand("""
                INSERT INTO retravion_test_case.counter AS counter(product_internal_id,last_number) VALUES($1,1)
                ON CONFLICT(product_internal_id) DO UPDATE SET last_number=counter.last_number+1 RETURNING last_number
                """, connection, transaction);
            allocate.Parameters.AddWithValue(productInternalId);
            var number = (long)(await allocate.ExecuteScalarAsync(cancellationToken))!;
            var value = Case.Create(productInternalId, number, content);
            if (value.IsFailure) return Result.Failure<TestCaseSnapshot>(value.Error);
            var testCase = value.Value;
            await using var command = new NpgsqlCommand("""
                INSERT INTO retravion_test_case.test_case(internal_id,product_internal_id,number,version_id,version_number,status,title,description,preconditions,steps,revision)
                VALUES($1,$2,$3,$4,1,'Draft',$5,$6,$7,$8,1)
                """, connection, transaction);
            command.Parameters.AddWithValue(testCase.InternalId);
            command.Parameters.AddWithValue(productInternalId);
            command.Parameters.AddWithValue(number);
            command.Parameters.AddWithValue(testCase.VersionId);
            ContentParameters(command, content);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await Audit(connection, transaction, actor, occurredAt, null, testCase, cancellationToken);
            return new TestCaseSnapshot(testCase, 1);
        }, cancellationToken);
    }

    public Task<Result<TestCaseSnapshot>> Update(TestCaseSnapshot expected, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        if (expected is null || expected.TestCase is null || expected.Revision <= 0 || content is null || actor is not AuditActor.Identified)
            return Task.FromResult(Result.Failure<TestCaseSnapshot>("Invalid Test Case update."));
        return Execute(async (connection, transaction) =>
        {
            var before = expected.TestCase;
            if (!await ProductActive(connection, transaction, before.ProductInternalId, cancellationToken)) return Inactive();
            TestCaseSnapshot current;
            await using (var read = new NpgsqlCommand($"SELECT {Columns} FROM retravion_test_case.test_case WHERE internal_id=$1 FOR UPDATE", connection, transaction))
            {
                read.Parameters.AddWithValue(before.InternalId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return Stale();
                var restored = Read(reader);
                if (restored.IsFailure) return Result.Failure<TestCaseSnapshot>(restored.Error);
                current = restored.Value;
            }
            if (current.Revision != expected.Revision || current.TestCase.ProductInternalId != before.ProductInternalId ||
                current.TestCase.VersionId != before.VersionId || current.TestCase.Number != before.Number ||
                !current.TestCase.Content.SameContent(before.Content)) return Stale();
            if (current.TestCase.Content.SameContent(content)) return current;
            await using var update = new NpgsqlCommand("""
                UPDATE retravion_test_case.test_case SET title=$1,description=$2,preconditions=$3,steps=$4,revision=revision+1
                WHERE internal_id=$5 RETURNING revision
                """, connection, transaction);
            ContentParameters(update, content);
            update.Parameters.AddWithValue(before.InternalId);
            var revision = (long)(await update.ExecuteScalarAsync(cancellationToken))!;
            var after = current.TestCase.Edit(content);
            await Audit(connection, transaction, actor, occurredAt, current.TestCase, after, cancellationToken);
            return new TestCaseSnapshot(after, revision);
        }, cancellationToken);
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
        if (reader.GetInt32(4) != 1 || reader.GetString(5) != "Draft" || reader.GetInt64(10) <= 0)
            return Result.Failure<TestCaseSnapshot>("The persisted Test Case state is invalid.");
        try
        {
            var steps = JsonSerializer.Deserialize<TestCaseStep[]>(reader.GetString(9));
            if (steps is null) return Result.Failure<TestCaseSnapshot>("The persisted Test Case steps are invalid.");
            var content = TestCaseContent.Create(reader.GetString(6), reader.GetString(7), reader.GetString(8), steps);
            return content.Bind(value => Case.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(3), reader.GetInt64(2), value))
                .Map(value => new TestCaseSnapshot(value, reader.GetInt64(10)));
        }
        catch (JsonException)
        { return Result.Failure<TestCaseSnapshot>("The persisted Test Case steps are invalid."); }
    }

    private static Result<TestCaseSnapshot> Stale() => Result.Failure<TestCaseSnapshot>("The Test Case changed. Retrieve it again before editing.");
    private static Result<TestCaseSnapshot> Inactive() => Result.Failure<TestCaseSnapshot>("The responsible Product is retired or no longer exists.");

    private static async Task<bool> ProductActive(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", connection, transaction))
            if (!(bool)(await exists.ExecuteScalarAsync(token))!) return false;
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor actor, DateTimeOffset occurredAt,
        Case? before, Case after, CancellationToken token)
    {
        static TestCaseAuditValues Values(Case value) => new(value.ProductInternalId, value.Id, value.Content.Title,
            value.Content.Description, value.Content.Preconditions, value.Status, value.Content.Steps);
        return PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, before is null ? "test-case.create" : "test-case.edit",
                new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success,
                testCaseChange: new TestCaseAuditDetails(after.InternalId, after.VersionId, after.VersionNumber,
                    before is null ? null : Values(before), Values(after))), token);
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token)
    {
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using (var setup = new NpgsqlCommand(Setup, connection, transaction)) await setup.ExecuteNonQueryAsync(token);
            var result = await operation(connection, transaction);
            if (result.IsSuccess)
            {
                commitStarted = true;
                if (commit is null) await transaction.CommitAsync(token); else await commit(transaction, token);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        { return Result.Failure<T>("The Test Case operation outcome could not be confirmed. Reload before retrying; do not retry automatically."); }
        catch (PostgresException exception) when (!token.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Result.Failure<T>("The Test Case identity is already in use. Retrieve it again."); }
        catch (PostgresException exception) when (!token.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        { return Result.Failure<T>("The Test Case numbering or revision limit has been reached."); }
        catch (NpgsqlException exception) when (!token.IsCancellationRequested && (exception.IsTransient || exception is PostgresException
        { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction }))
        { return Result.Failure<T>("The Test Case operation could not be completed."); }
    }
}
