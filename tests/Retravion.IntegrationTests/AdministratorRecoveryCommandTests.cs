using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class AdministratorRecoveryCommandTests
{
    [PostgresFact]
    public async Task OfflineCommandRecoversExistingAdministratorInPostgres()
    {
        var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
        var database = $"retravion_recovery_{Guid.NewGuid():N}";
        await using var server = new NpgsqlConnection(serverConnection);
        await server.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", server))
            await create.ExecuteNonQueryAsync();
        var connection = new NpgsqlConnectionStringBuilder(serverConnection) { Database = database, Pooling = false }.ConnectionString;
        try
        {
            var store = new PostgresAdministratorStore(connection);
            var hasher = new Pbkdf2PasswordHasher();
            Assert.True((await new AdministratorInitializer(store, hasher).Initialize("admin", "InitialPass123!")).IsSuccess);
            Assert.True((await new PasswordReplacement(store, hasher).Replace("admin", "InitialPass123!", "PreviousPass123!")).IsSuccess);
            string oldCookie;
            await using (var host = CreateHost(connection))
            using (var client = CreateClient(host))
            {
                using var login = await Login(client, "PreviousPass123!");
                Assert.Equal("/home", login.Headers.Location?.OriginalString);
                oldCookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("Retravion.Identity=" )).Split(';')[0];
                using var home = await client.GetAsync("/home");
                Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            }
            await using (var db = new NpgsqlConnection(connection))
            {
                await db.OpenAsync();
                await using var lockout = new NpgsqlCommand("UPDATE retravion_identity.administrator SET failed_sign_ins = 10, requires_password_replacement = false", db);
                await lockout.ExecuteNonQueryAsync();
            }
            var original = (await store.Load()).Value.Value;
            var rejected = await RunCommand("short", connection);
            Assert.NotEqual(0, rejected.ExitCode);
            var unchanged = (await store.Load()).Value.Value;
            Assert.Equal(original.PasswordHash, unchanged.PasswordHash);
            Assert.Equal(original.Failures, unchanged.Failures);
            var result = await RunCommand("RecoveryPass123!", connection);
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("RecoveryPass123!", result.Output);
            var recovered = (await store.Load()).Value.Value;
            Assert.Equal("admin", recovered.Username);
            Assert.NotEqual("RecoveryPass123!", recovered.PasswordHash);
            Assert.True(hasher.Verify("RecoveryPass123!", recovered.PasswordHash));
            Assert.Equal(SignInFailures.None, recovered.Failures);
            Assert.True(recovered.RequiresPasswordReplacement);

            await using var restarted = CreateHost(connection);
            using var replay = CreateClient(restarted, false);
            replay.DefaultRequestHeaders.Add("Cookie", oldCookie);
            using var denied = await replay.GetAsync("/home");
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
            using var clientAfter = CreateClient(restarted);
            using var recoveredLogin = await Login(clientAfter, "RecoveryPass123!");
            Assert.Equal("/password-replacement", recoveredLogin.Headers.Location?.OriginalString);
            using var restricted = await clientAfter.GetAsync("/home");
            Assert.Equal("/password-replacement", restricted.Headers.Location?.OriginalString);
            foreach (var confirmation in new string?[] { null, "", "newPassword123!", "NewPassword123! " })
            {
                using var invalid = await Replace(clientAfter, "RecoveryPass123!", "NewPassword123!", confirmation);
                Assert.Equal("/password-replacement?confirmationFailed=true", invalid.Headers.Location?.OriginalString);
                var html = await clientAfter.GetStringAsync(invalid.Headers.Location);
                Assert.Contains("role=\"alert\"", html);
                Assert.DoesNotContain("RecoveryPass123!", html);
                Assert.DoesNotContain("NewPassword123!", html);
                var stillRecovered = (await store.Load()).Value.Value;
                Assert.Equal(recovered.PasswordHash, stillRecovered.PasswordHash);
                Assert.True(stillRecovered.RequiresPasswordReplacement);
                using var stillRestricted = await clientAfter.GetAsync("/home");
                Assert.Equal("/password-replacement", stillRestricted.Headers.Location?.OriginalString);
            }
            using var same = await Replace(clientAfter, "RecoveryPass123!", "RecoveryPass123!", "RecoveryPass123!");
            Assert.Equal("/password-replacement?failed=true", same.Headers.Location?.OriginalString);
            using var changed = await Replace(clientAfter, "RecoveryPass123!", "NewPassword123!", "NewPassword123!");
            Assert.Equal("/home", changed.Headers.Location?.OriginalString);
            using var full = await clientAfter.GetAsync("/home");
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            using var later = CreateClient(restarted);
            using var laterLogin = await Login(later, "NewPassword123!");
            Assert.Equal("/home", laterLogin.Headers.Location?.OriginalString);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("private-short")]
    public async Task InvalidRecoveryConfigurationFailsWithoutServingOrDisclosingPassword(string password)
    {
        var result = await RunCommand(password, "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=private-db-secret");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Administrator recovery failed", result.Output);
        if (password.Length > 0) Assert.DoesNotContain(password, result.Output);
        Assert.DoesNotContain("private-db-secret", result.Output);
        Assert.DoesNotContain("Now listening", result.Output);
    }

    private static WebApplicationFactory<Program> CreateHost(string connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connection })));

    private static HttpClient CreateClient(WebApplicationFactory<Program> host, bool cookies = true) =>
        host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = cookies, BaseAddress = new Uri("https://localhost")
        });

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string password) =>
        await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = password,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));

    private static async Task<HttpResponseMessage> Replace(HttpClient client, string current, string replacement, string? confirmation)
    {
        var form = new Dictionary<string, string>
        {
            ["currentPassword"] = current, ["newPassword"] = replacement,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/password-replacement"))
        };
        if (confirmation is not null) form["confirmNewPassword"] = confirmation;
        return await client.PostAsync("/identity/password-replacement", new FormUrlEncodedContent(form));
    }

    private static async Task<(int ExitCode, string Output)> RunCommand(string password, string connection)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--recover-initial-administrator");
        start.Environment["InitialAdmin__Password"] = password;
        start.Environment["ConnectionStrings__DefaultConnection"] = connection;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await stdout + await stderr);
    }
}
