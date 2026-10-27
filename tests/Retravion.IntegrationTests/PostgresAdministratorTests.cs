using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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

            // The schema, not only the application, prevents a second singleton identity.
            await using (var invalidInsert = new NpgsqlCommand("""
                INSERT INTO retravion_identity.administrator VALUES (FALSE, 'another', 'hash')
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
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")))
            Skip = "Set RETRAVION_TEST_POSTGRES to a PostgreSQL test server connection (CREATE DATABASE permission required).";
    }
}
