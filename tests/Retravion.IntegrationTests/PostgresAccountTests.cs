using Npgsql;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class PostgresAccountTests
{
    [PostgresFact]
    public async Task LegacyMigrationPreservesStateAndSupportsConcurrentUniqueAccountsIsolatedAuthenticationAndInitialRecovery()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_accounts_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server)) await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnection) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var hasher = new Pbkdf2PasswordHasher();
            var hash = hasher.Hash("OriginalPass123!");
            var clock = new Clock();
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var legacy = new NpgsqlCommand("""
                CREATE SCHEMA retravion_identity;
                CREATE TABLE retravion_identity.administrator (
                    singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton),
                    username text NOT NULL CHECK (length(btrim(username)) > 0),
                    password_hash text NOT NULL CHECK (length(btrim(password_hash)) > 0),
                    requires_password_replacement boolean NOT NULL DEFAULT TRUE,
                    failed_sign_ins integer NOT NULL DEFAULT 0 CHECK (failed_sign_ins BETWEEN 0 AND 10),
                    retry_at timestamptz NOT NULL DEFAULT '-infinity'
                );
                """, connection))
                await legacy.ExecuteNonQueryAsync();
            await using (var seed = new NpgsqlCommand(
                "INSERT INTO retravion_identity.administrator VALUES (TRUE, 'Legacy.Admin', $1, FALSE, 3, $2)", connection))
            {
                seed.Parameters.AddWithValue(hash);
                seed.Parameters.AddWithValue(clock.Now.AddSeconds(5));
                await seed.ExecuteNonQueryAsync();
            }
            var store = new PostgresAdministratorStore(connectionString);
            var migrated = (await store.Load()).Value.Value;
            Assert.Equal("Legacy.Admin", migrated.Username);
            Assert.Equal("Legacy.Admin", migrated.DisplayName);
            Assert.Equal(hash, migrated.PasswordHash);
            Assert.False(migrated.RequiresPasswordReplacement);
            Assert.True(migrated.IsActive);
            Assert.True(migrated.IsInitialAdministrator);
            Assert.Equal(AccountType.Administrator, migrated.AccountType);
            Assert.Equal(3, migrated.Failures.Count);
            Assert.Equal(clock.Now.AddSeconds(5), migrated.Failures.RetryAt);
            Assert.True((await new AdministratorInitializer(store, hasher).Initialize(null, null)).IsSuccess);
            var creation = new AccountCreation(store, hasher);
            Assert.True((await creation.Create("LEGACY.ADMIN", "Duplicate", AccountType.Ordinary)).IsFailure);
            var racers = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
                new AccountCreation(new PostgresAdministratorStore(connectionString), hasher)
                    .Create(index % 2 == 0 ? "New.User" : "NEW.USER", "Shared 😀", AccountType.Ordinary)));
            Assert.Single(racers, result => result.IsSuccess);
            Assert.Equal(7, racers.Count(result => result.IsFailure));
            var ordinary = racers.Single(result => result.IsSuccess).Value;
            var admin = (await creation.Create("another-admin", "Shared 😀", AccountType.Administrator)).Value;
            Assert.Equal(3, (await store.List()).Value.Count);
            var ordinaryState = (await store.LoadForSignIn("new.user")).Value.Value;
            Assert.False(ordinaryState.IsInitialAdministrator);
            Assert.True(ordinaryState.RequiresPasswordReplacement);
            Assert.Equal(AccountType.Ordinary, ordinaryState.AccountType);
            Assert.True(hasher.Verify(ordinary.TemporaryPassword, ordinaryState.PasswordHash));
            var signIn = new PasswordSignIn(store, hasher, clock);
            Assert.True((await signIn.Authenticate("legacy.admin", "OriginalPass123!")).IsFailure);
            Assert.True((await signIn.Authenticate("NEW.USER", ordinary.TemporaryPassword)).IsSuccess);
            foreach (var name in new[] { "new.user", "NEW.USER", "New.User" })
                Assert.True((await new PasswordSignIn(store, hasher, clock).Authenticate(name, "wrong")).IsFailure);
            Assert.True((await new PasswordSignIn(store, hasher, clock).Authenticate("NEW.USER", ordinary.TemporaryPassword)).IsFailure);
            Assert.True((await signIn.Authenticate("ANOTHER-ADMIN", admin.TemporaryPassword)).IsSuccess);
            Assert.Equal(SignInFailures.None, (await store.LoadForSignIn("another-admin")).Value.Value.Failures);
            clock.Now = clock.Now.AddSeconds(5);
            Assert.True((await signIn.Authenticate("NEW.USER", ordinary.TemporaryPassword)).IsSuccess);
            Assert.True((await new PasswordReplacement(store, hasher).Replace("new.user", ordinary.TemporaryPassword, "Replacement123!")).IsSuccess);
            var beforeRecovery = (await store.LoadForSignIn("new.user")).Value.Value;
            Assert.False(beforeRecovery.RequiresPasswordReplacement);
            Assert.Equal(AccountType.Ordinary, beforeRecovery.AccountType);
            Assert.True((await new AdministratorRecovery(store, hasher).Recover("RecoveredPass123!")).IsSuccess);
            var recovered = (await new PostgresAdministratorStore(connectionString).Load()).Value.Value;
            Assert.Equal("Legacy.Admin", recovered.Username);
            Assert.True(recovered.RequiresPasswordReplacement);
            Assert.Equal(SignInFailures.None, recovered.Failures);
            Assert.True(hasher.Verify("RecoveredPass123!", recovered.PasswordHash));
            Assert.Equal(beforeRecovery.PasswordHash, (await store.LoadForSignIn("new.user")).Value.Value.PasswordHash);
            Assert.True((await signIn.Authenticate("NEW.USER", "Replacement123!")).IsSuccess);
            await using var raw = new NpgsqlCommand("SELECT password_hash FROM retravion_identity.administrator WHERE singleton IS NULL", connection);
            await using var reader = await raw.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                Assert.DoesNotContain(ordinary.TemporaryPassword, reader.GetString(0));
                Assert.DoesNotContain(admin.TemporaryPassword, reader.GetString(0));
            }
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task AvailabilityPersistsAcrossStoresRejectsStaleSignInAndPreservesConcurrentCredentialState()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_availability_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server)) await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnection) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var store = new PostgresAdministratorStore(connectionString);
            var other = new PostgresAdministratorStore(connectionString);
            var hasher = new Pbkdf2PasswordHasher();
            Assert.True((await new AdministratorInitializer(store, hasher).Initialize("actor", "InitialPass123!")).IsSuccess);
            foreach (var type in new[] { AccountType.Ordinary, AccountType.Administrator })
            {
                var name = $"target-{type}";
                var created = (await new AccountCreation(store, hasher).Create(name, "Target", type)).Value;
                var original = (await store.LoadForSignIn(name)).Value.Value;
                // A password replacement committed after the availability read must not be overwritten.
                var replacement = original.ReplacePassword("Replacement123!", hasher.Hash("Replacement123!")).Value;
                Assert.True((await other.ReplacePassword(original, replacement)).IsSuccess);
                Assert.True((await store.ChangeAvailability(original, original.Inactivate("actor").Value)).IsSuccess);
                Assert.False(await other.TryRecordSignIn(original, name, true, DateTimeOffset.UtcNow));
                Assert.True((await other.ChangeAvailability(original, original.Inactivate("actor").Value)).IsFailure);
                var inactive = (await other.LoadForSignIn(name)).Value.Value;
                Assert.False(inactive.IsActive);
                Assert.Equal(replacement.PasswordHash, inactive.PasswordHash);
                Assert.False(inactive.RequiresPasswordReplacement);
                Assert.True((await new PasswordSignIn(other, hasher).Authenticate(name, "Replacement123!")).IsFailure);
                Assert.True((await AccountAvailability.Change(store, "actor", name, true)).IsSuccess);
                Assert.True((await new PasswordSignIn(other, hasher).Authenticate(name, "Replacement123!")).IsSuccess);
                // Locked and mandatory-replacement snapshots survive availability-only writes.
                var pending = (await new AccountCreation(store, hasher).Create($"pending-{type}", "Pending", type)).Value;
                var clock = new Clock();
                var signIn = new PasswordSignIn(store, hasher, clock);
                for (var i = 0; i < 10; i++)
                {
                    Assert.True((await signIn.Authenticate(pending.Account.Username, "wrong")).IsFailure);
                    clock.Now = clock.Now.AddSeconds(5);
                }
                var locked = (await store.LoadForSignIn(pending.Account.Username)).Value.Value;
                Assert.True(locked.Failures.IsLocked);
                Assert.True((await AccountAvailability.Change(store, "actor", locked.Username, false)).IsSuccess);
                Assert.True((await AccountAvailability.Change(other, "actor", locked.Username, true)).IsSuccess);
                var reactivated = (await other.LoadForSignIn(locked.Username)).Value.Value;
                Assert.Equal(locked.Failures, reactivated.Failures);
                Assert.True(reactivated.RequiresPasswordReplacement);
                Assert.True((await new PasswordSignIn(other, hasher).Authenticate(locked.Username, pending.TemporaryPassword)).IsFailure);
            }
            Assert.True((await AccountAvailability.Change(store, "ACTOR", "actor", false)).IsFailure);
            Assert.True((await other.Load()).Value.Value.IsActive);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
