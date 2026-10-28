using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Web.Features.Identity.Users;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class AccountAuditTransactionTests
{
    [PostgresFact]
    public async Task AuditWriteFailureRollsBackEveryAccountOperationAndSuccessCommitsBoth()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_account_atomic_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server)) await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnection) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var store = new PostgresAdministratorStore(connectionString);
            await store.Load();
            var hasher = new Pbkdf2PasswordHasher();
            var creation = new AccountCreation(store, hasher);
            Assert.True((await creation.Create("active", "Active", AccountType.Ordinary)).IsSuccess);
            Assert.True((await creation.Create("inactive", "Inactive", AccountType.Ordinary)).IsSuccess);
            Assert.True((await AccountAvailability.Change(store, "admin", "inactive", false)).IsSuccess);
            var before = (await store.LoadForSignIn("active")).Value.Value.PasswordHash;
            var submission = new PostgresAuditSubmission(connectionString);
            AuditInformation Event(string action, string target) => new(new AuditActor.Identified("admin"), DateTimeOffset.UtcNow,
                action, new AuditTarget.Identified(target), AuditActionOutcome.Success);
            Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(Event("setup", "active")));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var trigger = new NpgsqlCommand("""
                CREATE FUNCTION retravion_audit.reject_event() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected audit failure' USING ERRCODE = '42501'; END $$;
                CREATE TRIGGER reject_event BEFORE INSERT ON retravion_audit.event
                FOR EACH ROW EXECUTE FUNCTION retravion_audit.reject_event();
                """, connection)) await trigger.ExecuteNonQueryAsync();
            var coordinator = new PostgresAccountAuditTransaction(connectionString);
            async Task<AccountTransactionResult<bool>> Availability(string target, bool active) => await coordinator.Execute(async s =>
            {
                var result = await AccountAvailability.Change(s, "admin", target, active);
                return result.IsSuccess ? CSharpFunctionalExtensions.Result.Success(true) : CSharpFunctionalExtensions.Result.Failure<bool>(result.Error);
            }, Event(active ? "account.reactivate" : "account.inactivate", target), CancellationToken.None);
            async Task<AccountTransactionResult<CreatedAccount>> Create() => await coordinator.Execute(
                s => new AccountCreation(s, hasher).Create("new-user", "New", AccountType.Ordinary),
                Event("account.create", "new-user"), CancellationToken.None);
            async Task<AccountTransactionResult<CreatedAccount>> Reset() => await coordinator.Execute(
                s => AccountPasswordReset.Reset(s, hasher, "admin", "active"),
                Event("account.password-reset", "active"), CancellationToken.None);
            Assert.True((await Create()).Result.IsFailure);
            Assert.True((await Availability("active", false)).Result.IsFailure);
            Assert.True((await Availability("inactive", true)).Result.IsFailure);
            Assert.True((await Reset()).Result.IsFailure);
            var accounts = (await store.List()).Value;
            Assert.DoesNotContain(accounts, a => a.Username == "new-user");
            Assert.True(accounts.Single(a => a.Username == "active").IsActive);
            Assert.False(accounts.Single(a => a.Username == "inactive").IsActive);
            Assert.Equal(before, accounts.Single(a => a.Username == "active").PasswordHash);
            await using (var remove = new NpgsqlCommand("DROP TRIGGER reject_event ON retravion_audit.event", connection)) await remove.ExecuteNonQueryAsync();
            Assert.True((await Create()).Result.IsSuccess);
            Assert.True((await Availability("active", false)).Result.IsSuccess);
            Assert.True((await Availability("inactive", true)).Result.IsSuccess);
            Assert.True((await Reset()).Result.IsSuccess);
            await using (var read = new NpgsqlCommand("SELECT actor_kind, actor_identifier, occurred_at_utc_ticks, action, target_kind, target_identifier, outcome FROM retravion_audit.event WHERE action <> 'setup' ORDER BY id", connection))
            await using (var reader = await read.ExecuteReaderAsync())
            {
                foreach (var (action, target) in new[] { ("account.create", "new-user"), ("account.inactivate", "active"),
                    ("account.reactivate", "inactive"), ("account.password-reset", "active") })
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal("identified", reader.GetString(0));
                    Assert.Equal("admin", reader.GetString(1));
                    Assert.InRange(reader.GetInt64(2), DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                    Assert.Equal(action, reader.GetString(3));
                    Assert.Equal("identified", reader.GetString(4));
                    Assert.Equal(target, reader.GetString(5));
                    Assert.Equal("success", reader.GetString(6));
                }
                Assert.False(await reader.ReadAsync());
            }
            var lostAcknowledgement = new PostgresAccountAuditTransaction(connectionString, async (tx, token) =>
            {
                await tx.CommitAsync(token);
                throw new IOException("Injected lost commit acknowledgement");
            });
            var uncertain = await lostAcknowledgement.Execute(
                s => new AccountCreation(s, hasher).Create("uncertain", "Uncertain", AccountType.Ordinary),
                Event("account.create", "uncertain"), CancellationToken.None);
            Assert.True(uncertain.Uncertain);
            Assert.True(uncertain.Result.IsFailure);
            Assert.Contains("could not be confirmed", uncertain.Result.Error);
            Assert.Contains((await store.List()).Value, a => a.Username == "uncertain");
            await using var count = new NpgsqlCommand("SELECT count(*) FROM retravion_audit.event WHERE target_identifier = 'uncertain' AND outcome = 'success'", connection);
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task SharedTransactionCommitsOrRollsBackAccountAndAuditTogether()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_account_audit_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server))
            await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnection) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var store = new PostgresAdministratorStore(connectionString);
            await store.Load(); // Initialize the identity schema.
            var hasher = new Pbkdf2PasswordHasher();
            foreach (var commit in new[] { false, true })
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                var transactionalStore = new PostgresAdministratorStore(connection);
                var username = commit ? "committed" : "rolledback";
                var result = await new AccountCreation(transactionalStore, hasher)
                    .Create(username, "Example", AccountType.Ordinary);
                Assert.True(result.IsSuccess);
                var audit = new AuditInformation(new AuditActor.Identified("admin"), DateTimeOffset.UtcNow,
                    "account.create", new AuditTarget.Identified(username), AuditActionOutcome.Success);
                await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction, audit);
                if (commit) await transaction.CommitAsync();
                else await transaction.RollbackAsync();
            }

            Assert.Equal(new[] { "committed" }, (await store.List()).Value.Select(x => x.Username));
            await using var check = new NpgsqlConnection(connectionString);
            await check.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT target_identifier FROM retravion_audit.event", check);
            Assert.Equal("committed", await command.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
