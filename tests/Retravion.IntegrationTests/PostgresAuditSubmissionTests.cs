using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Infrastructure.AuditTrail;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class PostgresAuditSubmissionTests
{
    [PostgresFact]
    public async Task CompositionPersistsAllVariantsAndExactInstantsAcrossWebHostRestarts()
    {
        await using var database = await TestDatabase.Create();
        AuditActor[] actors = [new AuditActor.Identified("user-'42; — 用户"), new AuditActor.Unknown()];
        AuditTarget[] targets =
        [
            new AuditTarget.Identified("target-'17; — 目标"), new AuditTarget.Unknown(), new AuditTarget.NotApplicable()
        ];
        var information = (from actor in actors
                           from target in targets
                           from outcome in Enum.GetValues<AuditActionOutcome>()
                           select new AuditInformation(actor,
                               new DateTimeOffset(2026, 10, 27, 9, 15, 0, TimeSpan.FromHours(2)).AddTicks(1234567),
                               "requirement.'; DROP SCHEMA retravion_audit CASCADE; -- 修改", target, outcome))
            .ToList();
        information.Add(new AuditInformation(new AuditActor.Unknown(), DateTimeOffset.MinValue, "earliest",
            new AuditTarget.NotApplicable(), AuditActionOutcome.Failure));
        information.Add(new AuditInformation(new AuditActor.Unknown(), DateTimeOffset.MaxValue, "latest",
            new AuditTarget.NotApplicable(), AuditActionOutcome.Success));

        await using (var host = CreateHost(database.ConnectionString, provision: true))
        {
            var submission = host.Services.GetRequiredService<IAuditSubmission>();
            foreach (var item in information)
            {
                Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(item));
                // A different connection sees the committed record immediately after success.
                await AssertRows(database.ConnectionString, information.Take(information.IndexOf(item) + 1).ToArray());
            }
        }

        // Restart the actual host with no provisioning credentials, not merely the store object.
        await using (var restarted = CreateHost(database.ConnectionString, provision: false))
        {
            Assert.IsType<PostgresAuditSubmission>(restarted.Services.GetRequiredService<IAuditSubmission>());
            await AssertRows(database.ConnectionString, information);
            var afterRestart = Information("after-restart");
            Assert.Equal(AuditSubmissionResult.Success,
                await restarted.Services.GetRequiredService<IAuditSubmission>().SubmitAsync(afterRestart));
            information.Add(afterRestart);
            await AssertRows(database.ConnectionString, information);
        }
    }

    [PostgresFact]
    public async Task ConcurrentFirstUseAndRepeatedInitializationPreserveEveryRecord()
    {
        await using var database = await TestDatabase.Create();
        var information = Enumerable.Range(0, 8).Select(index => Information($"concurrent-{index}")).ToArray();
        var results = await Task.WhenAll(information.Select(item =>
            new PostgresAuditSubmission(database.ConnectionString).SubmitAsync(item)));
        Assert.All(results, result => Assert.Equal(AuditSubmissionResult.Success, result));

        // Compare by action because commit order between concurrent callers is unspecified.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT action FROM retravion_audit.event ORDER BY action", connection);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            foreach (var item in information.OrderBy(item => item.Action, StringComparer.Ordinal))
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(item.Action, reader.GetString(0));
            }
            Assert.False(await reader.ReadAsync());
        }
        var repeated = Information("repeated-initialization");
        Assert.Equal(AuditSubmissionResult.Success,
            await new PostgresAuditSubmission(database.ConnectionString).SubmitAsync(repeated));
        await using var count = new NpgsqlCommand("SELECT count(*) FROM retravion_audit.event", connection);
        Assert.Equal(9L, await count.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task ReadOnlyStorageReportsFailureForEitherActionOutcomeWithoutChangingExistingRecords()
    {
        await using var database = await TestDatabase.Create();
        var existing = Information("existing");
        Assert.Equal(AuditSubmissionResult.Success,
            await new PostgresAuditSubmission(database.ConnectionString).SubmitAsync(existing));
        var readOnly = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c default_transaction_read_only=on"
        }.ConnectionString;
        foreach (var outcome in Enum.GetValues<AuditActionOutcome>())
        {
            var item = new AuditInformation(new AuditActor.Unknown(), DateTimeOffset.UtcNow, "denied",
                new AuditTarget.Unknown(), outcome);
            Assert.Equal(AuditSubmissionResult.Failure, await new PostgresAuditSubmission(readOnly).SubmitAsync(item));
        }
        await AssertRows(database.ConnectionString, [existing]);
    }

    [PostgresFact]
    public async Task CommitTimeFailureIsNotReportedAsSuccessAndRollsBackTheInsert()
    {
        await using var database = await TestDatabase.Create();
        var submission = new PostgresAuditSubmission(database.ConnectionString);
        var existing = Information("existing");
        Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(existing));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE FUNCTION retravion_audit.reject_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'test commit rejection' USING ERRCODE = '40001';
            END;
            $$;
            CREATE CONSTRAINT TRIGGER reject_commit AFTER INSERT ON retravion_audit.event
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION retravion_audit.reject_commit();
            """, connection))
            await setup.ExecuteNonQueryAsync();

        foreach (var outcome in Enum.GetValues<AuditActionOutcome>())
        {
            var item = new AuditInformation(new AuditActor.Unknown(), DateTimeOffset.UtcNow, "commit-rejected",
                new AuditTarget.NotApplicable(), outcome);
            Assert.Equal(AuditSubmissionResult.Failure, await submission.SubmitAsync(item));
            await AssertRows(database.ConnectionString, [existing]);
        }
    }

    [PostgresFact]
    public async Task UnexpectedSchemaAndConfigurationFaultsRemainExceptions()
    {
        await using var database = await TestDatabase.Create();
        var submission = new PostgresAuditSubmission(database.ConnectionString);
        var existing = Information("existing");
        Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(existing));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var corrupt = new NpgsqlCommand(
            "ALTER TABLE retravion_audit.event RENAME COLUMN action TO unexpected_action", connection))
            await corrupt.ExecuteNonQueryAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(() => submission.SubmitAsync(Information("fault")));
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, exception.SqlState);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new PostgresAuditSubmission("not a connection string").SubmitAsync(Information("fault")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => submission.SubmitAsync(null!));
    }

    [PostgresFact]
    public async Task CancellationWhileWaitingForTheDatabaseRemainsCancellationAndStoresNothing()
    {
        await using var database = await TestDatabase.Create();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        // Hold the schema lock so cancellation occurs during a real database operation.
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(821749620109)", connection))
            await acquire.ExecuteNonQueryAsync();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new PostgresAuditSubmission(database.ConnectionString).SubmitAsync(Information("cancelled"), cancellation.Token));
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(821749620109)", connection);
            await release.ExecuteNonQueryAsync();
        }
        await using (var schema = new NpgsqlCommand("SELECT to_regclass('retravion_audit.event') IS NULL", connection))
            Assert.Equal(true, await schema.ExecuteScalarAsync());

        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PostgresAuditSubmission(database.ConnectionString).SubmitAsync(Information("cancelled"), alreadyCancelled.Token));
    }

    [PostgresFact]
    public async Task SubmissionForcesDurableCommitWhenTheSessionDefaultIsDisabled()
    {
        await using var database = await TestDatabase.Create();
        var submission = new PostgresAuditSubmission(database.ConnectionString);
        var existing = Information("existing");
        Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(existing));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE FUNCTION retravion_audit.require_durable_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF current_setting('synchronous_commit') <> 'on' THEN
                    RAISE EXCEPTION 'synchronous commit must be enabled';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER require_durable_commit BEFORE INSERT ON retravion_audit.event
                FOR EACH ROW EXECUTE FUNCTION retravion_audit.require_durable_commit();
            """, connection))
            await setup.ExecuteNonQueryAsync();
        var asynchronousDefault = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c synchronous_commit=off"
        }.ConnectionString;
        var durable = Information("durable");
        Assert.Equal(AuditSubmissionResult.Success,
            await new PostgresAuditSubmission(asynchronousDefault).SubmitAsync(durable));
        await AssertRows(database.ConnectionString, [existing, durable]);
    }

    private static AuditInformation Information(string action) => new(new AuditActor.Unknown(),
        DateTimeOffset.UtcNow, action, new AuditTarget.NotApplicable(), AuditActionOutcome.Success);

    private static WebApplicationFactory<Program> CreateHost(string connectionString, bool provision) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                    ["InitialAdmin:Username"] = provision ? "audit-test-admin" : null,
                    ["InitialAdmin:Password"] = provision ? "AuditTest123!" : null
                })));

    private static async Task AssertRows(string connectionString, IReadOnlyList<AuditInformation> expected)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT actor_kind, actor_identifier, occurred_at_utc_ticks, action, target_kind, target_identifier, outcome
            FROM retravion_audit.event ORDER BY id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        foreach (var item in expected)
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(item.Actor is AuditActor.Identified ? "identified" : "unknown", reader.GetString(0));
            Assert.Equal((item.Actor as AuditActor.Identified)?.Identifier, reader.IsDBNull(1) ? null : reader.GetString(1));
            Assert.Equal(item.OccurredAt.UtcTicks, reader.GetInt64(2));
            Assert.Equal(item.Action, reader.GetString(3));
            Assert.Equal(item.Target switch
            {
                AuditTarget.Identified => "identified",
                AuditTarget.Unknown => "unknown",
                AuditTarget.NotApplicable => "not_applicable",
                _ => throw new InvalidOperationException()
            }, reader.GetString(4));
            Assert.Equal((item.Target as AuditTarget.Identified)?.Identifier, reader.IsDBNull(5) ? null : reader.GetString(5));
            Assert.Equal(item.Outcome == AuditActionOutcome.Success ? "success" : "failure", reader.GetString(6));
        }
        Assert.False(await reader.ReadAsync());
    }

    private sealed class TestDatabase(string serverConnectionString, string name, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public static async Task<TestDatabase> Create()
        {
            var serverConnectionString = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
            var name = $"retravion_audit_test_{Guid.NewGuid():N}";
            await using var server = new NpgsqlConnection(serverConnectionString);
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", server);
            await create.ExecuteNonQueryAsync();
            return new TestDatabase(serverConnectionString, name,
                new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name, Pooling = false }.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await using var server = new NpgsqlConnection(serverConnectionString);
            await server.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
