using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Identity.Domain;
using Npgsql;
using Retravion.Identity.Application;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class PostgresAdministratorTests
{
    [PostgresFact]
    public async Task RealDatabaseEnforcesInitializationConcurrencyAndRestartPreservation()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_test_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server))
            await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnection) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var store = new PostgresAdministratorStore(connectionString);
            var initializer = new AdministratorInitializer(store, hasher);
            foreach (var (username, password) in new (string?, string?)[]
            {
                (null, "ValidPass123!"), ("", "ValidPass123!"), (" \t", "ValidPass123!"),
                ("private-user", null), ("private-user", ""), ("private-user", "short"),
                ("private-user", "abcdefghijk"), ("private-user", new string('A', 31))
            })
            {
                var rejected = await initializer.Initialize(username, password);
                Assert.True(rejected.IsFailure);
                if (!string.IsNullOrEmpty(username)) Assert.DoesNotContain(username, rejected.Error);
                if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, rejected.Error);
                Assert.True((await store.Load()).Value.HasNoValue);
            }

            // Exercise concurrent schema creation as well as concurrent inserts.
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var drop = new NpgsqlCommand("DROP SCHEMA retravion_identity CASCADE", connection))
                await drop.ExecuteNonQueryAsync();
            var attempts = Enumerable.Range(0, 6).Select(async index =>
                await new AdministratorInitializer(new PostgresAdministratorStore(connectionString), hasher)
                    .Initialize($"supplied-{index}", $"ValidPass{index}123!"));
            Assert.All(await Task.WhenAll(attempts), result => Assert.True(result.IsSuccess));
            var original = (await store.Load()).Value.Value;
            var winner = int.Parse(original.Username["supplied-".Length..]);
            Assert.True(hasher.Verify($"ValidPass{winner}123!", original.PasswordHash));
            Assert.DoesNotContain("ValidPass", original.PasswordHash);
            Assert.True(original.RequiresPasswordReplacement);

            // Simulate the pre-replacement/lockout schema: migration must restrict the existing account,
            // preserve credentials, initialize failure tracking, and remain idempotent on later loads.
            await using (var legacy = new NpgsqlCommand(
                "ALTER TABLE retravion_identity.administrator DROP COLUMN requires_password_replacement, DROP COLUMN failed_sign_ins, DROP COLUMN retry_at", connection))
                await legacy.ExecuteNonQueryAsync();
            var migrated = (await new PostgresAdministratorStore(connectionString).Load()).Value.Value;
            Assert.True(migrated.RequiresPasswordReplacement);
            Assert.Equal(original.Username, migrated.Username);
            Assert.Equal(original.PasswordHash, migrated.PasswordHash);
            Assert.Equal(SignInFailures.None, migrated.Failures);
            await using (var count = new NpgsqlCommand("SELECT count(*) FROM retravion_identity.administrator", connection))
                Assert.Equal(1L, await count.ExecuteScalarAsync());

            // A new actual web host must read persisted state, ignoring missing/changed provisioning credentials.
            foreach (var (username, password) in new (string?, string?)[] { (null, null), ("replacement", "Replacement123!") })
            {
                await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:DefaultConnection"] = connectionString,
                            ["InitialAdmin:Username"] = username,
                            ["InitialAdmin:Password"] = password
                        })));
                using var client = factory.CreateClient();
                Assert.True((await client.GetAsync("/")).IsSuccessStatusCode);
                var persisted = (await new PostgresAdministratorStore(connectionString).Load()).Value.Value;
                Assert.Equal(original.Username, persisted.Username);
                Assert.Equal(original.PasswordHash, persisted.PasswordHash);
                Assert.False(hasher.Verify("Replacement123!", persisted.PasswordHash));
            }

            var replacement = new PasswordReplacement(store, hasher);
            Assert.True((await replacement.Replace(original.Username, $"ValidPass{winner}123!", "short")).IsFailure);
            Assert.True((await replacement.Replace(original.Username, $"ValidPass{winner}123!", $"ValidPass{winner}123!")).IsFailure);
            Assert.Equal(original.PasswordHash, (await store.Load()).Value.Value.PasswordHash);

            // Competing completions from the same snapshot: exactly one wins, without overwriting it.
            var first = original.ReplacePassword("ChangedPass123!", hasher.Hash("ChangedPass123!")).Value;
            var second = original.ReplacePassword("OtherChanged123!", hasher.Hash("OtherChanged123!")).Value;
            var completions = await Task.WhenAll(
                store.ReplacePassword(original, first),
                new PostgresAdministratorStore(connectionString).ReplacePassword(original, second));
            Assert.Single(completions, result => result.IsSuccess);
            Assert.Single(completions, result => result.IsFailure);
            var completed = completions[0].IsSuccess ? first : second;
            var changedPassword = completions[0].IsSuccess ? "ChangedPass123!" : "OtherChanged123!";
            var restarted = new PostgresAdministratorStore(connectionString);
            Assert.True((await new AdministratorInitializer(restarted, hasher).Initialize(null, null)).IsSuccess);
            var signIn = new PasswordSignIn(restarted, hasher);
            Assert.True((await signIn.Authenticate(original.Username, changedPassword)).IsSuccess);
            Assert.True((await signIn.Authenticate(original.Username, $"ValidPass{winner}123!")).IsFailure);
            var persistedReplacement = (await restarted.Load()).Value.Value;
            Assert.False(persistedReplacement.RequiresPasswordReplacement);
            Assert.Equal(completed.PasswordHash, persistedReplacement.PasswordHash);
            Assert.True((await restarted.ReplacePassword(original, second)).IsFailure);
            Assert.True((await replacement.Replace(original.Username, changedPassword, "ThirdPassword123!")).IsFailure);

            // The initial-account marker still rejects FALSE; additional accounts use NULL.
            await using (var invalidInsert = new NpgsqlCommand("""
                INSERT INTO retravion_identity.administrator (singleton, username, password_hash, display_name, normalized_username)
                VALUES (FALSE, 'another', 'hash', 'Another', 'ANOTHER')
                """, connection))
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => invalidInsert.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            }

            // Persisted data must pass domain validation before being returned.
            await using (var corrupt = new NpgsqlCommand("UPDATE retravion_identity.administrator SET username = E'\\t'", connection))
                await corrupt.ExecuteNonQueryAsync();
            var invalid = await store.Load();
            Assert.True(invalid.IsFailure);
            Assert.Equal("The persisted administrator is invalid.", invalid.Error);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task FailureTrackingAndLockoutSurviveHostsAndRejectStaleSuccess()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_test_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server))
            await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnection)
            { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var store = new PostgresAdministratorStore(connectionString);
            Assert.True((await new AdministratorInitializer(store, hasher).Initialize("admin", "ValidPass123!")).IsSuccess);
            var clock = new Clock();
            for (var i = 0; i < 3; i++)
                Assert.True((await new PasswordSignIn(new PostgresAdministratorStore(connectionString), hasher, clock)
                    .Authenticate("admin", "wrong")).IsFailure);
            Assert.Equal(3, (await store.Load()).Value.Value.Failures.Count);
            Assert.True((await new PasswordSignIn(new PostgresAdministratorStore(connectionString), hasher, clock)
                .Authenticate("admin", "ValidPass123!")).IsFailure);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True((await new PasswordSignIn(new PostgresAdministratorStore(connectionString), hasher, clock)
                .Authenticate("admin", "ValidPass123!")).IsSuccess);
            Assert.Equal(SignInFailures.None, (await store.Load()).Value.Value.Failures);

            for (var i = 0; i < 9; i++)
            {
                Assert.True((await new PasswordSignIn(new PostgresAdministratorStore(connectionString), hasher, clock)
                    .Authenticate("admin", "wrong")).IsFailure);
                Assert.Equal(i + 1, (await store.Load()).Value.Value.Failures.Count);
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            var ninth = (await store.Load()).Value.Value;
            var competingFailures = await Task.WhenAll(
                store.TryRecordSignIn(ninth, "admin", false, clock.GetUtcNow()),
                new PostgresAdministratorStore(connectionString).TryRecordSignIn(ninth, "admin", false, clock.GetUtcNow()));
            Assert.Single(competingFailures, succeeded => succeeded);
            Assert.Single(competingFailures, succeeded => !succeeded);
            Assert.False(await store.TryRecordSignIn(ninth, "admin", true, clock.GetUtcNow()));
            var locked = (await store.Load()).Value.Value;
            Assert.True(locked.Failures.IsLocked);
            clock.Advance(TimeSpan.FromDays(3650));
            Assert.False(await store.TryRecordSignIn(locked, "admin", true, clock.GetUtcNow()));
            Assert.True(await store.TryRecordSignIn(locked, "unknown", false, clock.GetUtcNow()));
            Assert.Equal(locked.Failures, (await store.Load()).Value.Value.Failures);

            // Password replacement is not recovery and must leave the lockout intact.
            var replacement = locked.ReplacePassword("Replacement123!", hasher.Hash("Replacement123!")).Value;
            Assert.True((await store.ReplacePassword(locked, replacement)).IsSuccess);
            Assert.True((await store.Load()).Value.Value.Failures.IsLocked);

            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = connectionString,
                        ["InitialAdmin:Username"] = "different-admin",
                        ["InitialAdmin:Password"] = "DifferentPass123!"
                    })));
            using var client = factory.CreateClient();
            Assert.True((await client.GetAsync("/")).IsSuccessStatusCode);
            Assert.Equal(PasswordSignIn.FailureMessage,
                (await factory.Services.GetRequiredService<PasswordSignIn>()
                    .Authenticate("admin", "Replacement123!")).Error);
            Assert.True((await store.Load()).Value.Value.Failures.IsLocked);
            Assert.Equal(locked.Username, (await store.Load()).Value.Value.Username);

            // Even data that passes SQL's count constraint must pass domain validation.
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var corrupt = new NpgsqlCommand(
                "UPDATE retravion_identity.administrator SET failed_sign_ins = 3, retry_at = '-infinity'", connection);
            await corrupt.ExecuteNonQueryAsync();
            Assert.Equal("The persisted administrator is invalid.", (await store.Load()).Error);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task RecoveryIsAtomicRejectsStaleSnapshotsAndNeverCreatesAnAccount()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_test_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server))
            await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnection)
            { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var store = new PostgresAdministratorStore(connectionString);
            var recovery = new AdministratorRecovery(store, hasher);
            Assert.True((await recovery.Recover("RecoveryPass123!")).IsFailure);
            Assert.True((await store.Load()).Value.HasNoValue);
            Assert.True((await new AdministratorInitializer(store, hasher).Initialize("admin", "OriginalPass123!")).IsSuccess);
            var initial = (await store.Load()).Value.Value;
            Assert.True(await store.TryRecordSignIn(initial, "admin", false, DateTimeOffset.UnixEpoch));
            var changed = (await store.Load()).Value.Value;
            var staleReplacement = initial.RecoverPassword("RecoveryPass123!", hasher.Hash("RecoveryPass123!")).Value;
            Assert.True((await store.RecoverPassword(initial, staleReplacement)).IsFailure);
            Assert.Equal(changed.Failures, (await store.Load()).Value.Value.Failures);
            Assert.True((await recovery.Recover("short")).IsFailure);
            Assert.Equal(changed.PasswordHash, (await store.Load()).Value.Value.PasswordHash);
            Assert.Equal(changed.Failures, (await store.Load()).Value.Value.Failures);

            // Competing recoveries match the same credential and failure snapshot; only one may commit.
            var first = changed.RecoverPassword("RecoveryPass123!", hasher.Hash("RecoveryPass123!")).Value;
            var second = changed.RecoverPassword("OtherRecovery123!", hasher.Hash("OtherRecovery123!")).Value;
            var results = await Task.WhenAll(store.RecoverPassword(changed, first),
                new PostgresAdministratorStore(connectionString).RecoverPassword(changed, second));
            Assert.Single(results, result => result.IsSuccess);
            Assert.Single(results, result => result.IsFailure);
            var persisted = (await new PostgresAdministratorStore(connectionString).Load()).Value.Value;
            Assert.Equal("admin", persisted.Username);
            Assert.True(persisted.RequiresPasswordReplacement);
            Assert.Equal(SignInFailures.None, persisted.Failures);
            Assert.Equal(results[0].IsSuccess ? first.PasswordHash : second.PasswordHash, persisted.PasswordHash);
            Assert.True(hasher.Verify(results[0].IsSuccess ? "RecoveryPass123!" : "OtherRecovery123!", persisted.PasswordHash));
            Assert.False(hasher.Verify("OriginalPass123!", persisted.PasswordHash));

            // A terminal lockout is cleared by the public recovery operation as one persisted transition.
            var locked = Administrator.Create(persisted.Username, persisted.PasswordHash, false,
                SignInFailures.Restore(10, DateTimeOffset.MinValue).Value).Value;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var command = new NpgsqlCommand("UPDATE retravion_identity.administrator SET requires_password_replacement = FALSE, failed_sign_ins = 10, retry_at = '-infinity'", connection))
                await command.ExecuteNonQueryAsync();
            Assert.Equal(locked.Failures, (await store.Load()).Value.Value.Failures);
            Assert.True((await recovery.Recover("FinalRecovery123!")).IsSuccess);
            var recovered = (await store.Load()).Value.Value;
            Assert.Equal(SignInFailures.None, recovered.Failures);
            Assert.True(recovered.RequiresPasswordReplacement);
            Assert.True(hasher.Verify("FinalRecovery123!", recovered.PasswordHash));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")))
            Skip = "Set RETRAVION_TEST_POSTGRES to a PostgreSQL test server connection (CREATE DATABASE permission required).";
    }
}
