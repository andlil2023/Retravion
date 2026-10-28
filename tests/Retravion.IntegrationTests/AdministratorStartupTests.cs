using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class AdministratorStartupTests
{
    [Fact]
    public void ValidConfigurationInitializesBeforeServingRequests()
    {
        var store = new StartupAdministratorStore();
        using var factory = new AdministratorWebFactory(store, "supplied-user", "ValidPass123!");
        using var client = factory.CreateClient();
        Assert.Equal("supplied-user", store.Account.Value.Username);
        Assert.True(new Pbkdf2PasswordHasher().Verify("ValidPass123!", store.Account.Value.PasswordHash));
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(null, "ValidPass123!", "Username")]
    [InlineData("", "ValidPass123!", "Username")]
    [InlineData("private-user", null, "Password")]
    [InlineData("private-user", "", "Password")]
    [InlineData("private-user", "private-short", "Password")]
    public void InvalidConfigurationPreventsHostStartupWithoutDisclosure(string? username, string? password, string field)
    {
        var store = new StartupAdministratorStore();
        using var factory = new AdministratorWebFactory(store, username, password);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(field, exception.Message);
        if (!string.IsNullOrEmpty(username)) Assert.DoesNotContain(username, exception.ToString());
        if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, exception.ToString());
        Assert.True(store.Account.HasNoValue);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void RestartWithoutConfigurationPreservesCredentials()
    {
        var store = new StartupAdministratorStore();
        using (var first = new AdministratorWebFactory(store, "original", "ValidPass123!"))
        using (first.CreateClient()) { }
        var account = store.Account.Value;
        using var restart = new AdministratorWebFactory(store);
        using var client = restart.CreateClient();
        Assert.Same(account, store.Account.Value);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public void DatabaseFaultsFailStartupWithoutEchoingProviderDetails()
    {
        using var factory = new AdministratorWebFactory(new StartupAdministratorStore { Fail = true });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Check database configuration", exception.Message);
        Assert.DoesNotContain("sensitive-provider-details", exception.ToString());
    }
}

internal sealed class AdministratorWebFactory(
    StartupAdministratorStore store, string? username = null, string? password = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["InitialAdmin:Username"] = username,
                ["InitialAdmin:Password"] = password
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAdministratorStore>();
            services.AddSingleton<IAdministratorStore>(store);
            services.RemoveAll<IAuditSubmission>();
            services.AddSingleton<IAuditSubmission, SuccessfulAuditSubmission>();
        });
    }
}

internal sealed class SuccessfulAuditSubmission : IAuditSubmission
{
    public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default) =>
        Task.FromResult(AuditSubmissionResult.Success);
}

internal sealed class StartupAdministratorStore : IAdministratorStore
{
    public Maybe<Administrator> Account { get; set; }
    private readonly Dictionary<string, Administrator> additional = new(StringComparer.OrdinalIgnoreCase);
    public int Writes { get; private set; }

    public async Task<Result<Maybe<Administrator>>> LoadForSignIn(string? username, CancellationToken cancellationToken = default)
    {
        var initial = await Load(cancellationToken);
        lock (this) return additional.TryGetValue(username ?? "", out var account)
            ? Result.Success(Maybe<Administrator>.From(account)) : initial;
    }

    public Task<Result<IReadOnlyList<Administrator>>> List(CancellationToken cancellationToken = default)
    {
        lock (this) return Task.FromResult(Result.Success<IReadOnlyList<Administrator>>(
            (Account.HasValue ? new[] { Account.Value } : []).Concat(additional.Values).ToArray()));
    }

    public Task<Result> CreateAccount(Administrator account, CancellationToken cancellationToken = default)
    {
        lock (this)
        {
            if ((Account.HasValue && string.Equals(Account.Value.Username, account.Username, StringComparison.OrdinalIgnoreCase)) ||
                !additional.TryAdd(account.Username, account)) return Task.FromResult(Result.Failure("That username is already in use."));
            Writes++;
            return Task.FromResult(Result.Success());
        }
    }
    public bool Fail { get; init; }
    public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
    {
        if (Fail) throw new NpgsqlException("sensitive-provider-details");
        return Task.FromResult(Result.Success(Account));
    }

    public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(username, expected.Username, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(true);
        lock (this)
        {
            var current = expected.IsInitialAdministrator ? Account : additional.GetValueOrDefault(expected.Username);
            if (current.HasNoValue || !ReferenceEquals(current.Value, expected) || !expected.Failures.CanAttempt(now))
                return Task.FromResult(false);
            var updated = expected.RecordSignIn(succeeded, now);
            if (expected.IsInitialAdministrator) Account = updated;
            else additional[expected.Username] = updated;
            return Task.FromResult(true);
        }
    }

    public Task<Result> RecoverPassword(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default)
    {
        lock (this)
        {
            var current = expected.IsInitialAdministrator ? Account : additional.GetValueOrDefault(expected.Username);
            if (current.HasNoValue || !ReferenceEquals(current.Value, expected))
                return Task.FromResult(Result.Failure("Stale account"));
            if (expected.IsInitialAdministrator) Account = replacement;
            else additional[expected.Username] = replacement;
            Writes++;
            return Task.FromResult(Result.Success());
        }
    }

    public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default)
    {
        if (Account.HasNoValue) { Account = administrator; Writes++; }
        return Task.CompletedTask;
    }
}
