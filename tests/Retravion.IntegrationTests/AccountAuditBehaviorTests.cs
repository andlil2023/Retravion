using System.Net;
using System.Net.Http.Json;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Web.Features.Identity.Users;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task UncertainResetWithholdsPasswordAndRevokesExistingSessionsEvenIfChangeCommitted()
    {
        var store = new StartupAdministratorStore();
        var transaction = new UncertainTransaction(store);
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddSingleton<IAccountAuditTransaction>(transaction)));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "target", "Target", AccountType.Ordinary);
        var temporary = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var target = Client(factory);
        await Login(target, "target", temporary);
        await Replace(target, temporary, Replacement);
        var oldHash = (await store.LoadForSignIn("target")).Value.Value.PasswordHash;
        transaction.Uncertain = true;
        using var response = await Reset(admin, "target");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("could not be confirmed", body);
        Assert.DoesNotContain("temporaryPassword", body);
        Assert.DoesNotContain(temporary, body);
        Assert.NotEqual(oldHash, (await store.LoadForSignIn("target")).Value.Value.PasswordHash);
        using var denied = await target.GetAsync("/home");
        Assert.Equal("/", denied.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task UnauthorizedAndInvalidRequestsAreAuditedBeforeUserOperations()
    {
        var store = new StartupAdministratorStore();
        var audit = new RecordingAccountAudit();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddSingleton<IAuditSubmission>(audit)));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "ordinary", "Ordinary", AccountType.Ordinary);
        var temporary = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var user = Client(factory);
        await Login(user, "ordinary", temporary);
        await Replace(user, temporary, Replacement);
        audit.Events.Clear();
        using (var denied = await Create(user, "denied", "Denied", AccountType.Ordinary))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await Availability(user, "admin", false))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await Availability(user, "admin", true))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await Reset(user, "admin"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var events = audit.Events.Where(e => e.Action.StartsWith("account.")).ToArray();
        Assert.Equal(new[] { "account.create", "account.inactivate", "account.reactivate", "account.password-reset" }, events.Select(e => e.Action));
        Assert.All(events, e =>
        {
            Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
            Assert.Equal("ordinary", Assert.IsType<AuditActor.Identified>(e.Actor).Identifier);
            Assert.DoesNotContain(temporary, e.ToString());
        });
        Assert.IsType<AuditTarget.Unknown>(events[0].Target);
        Assert.All(events.Skip(1), e => Assert.Equal("admin", Assert.IsType<AuditTarget.Identified>(e.Target).Identifier));
    }

    [Fact]
    public async Task UnexpectedPreCommitFaultIsAuditedAndRethrown()
    {
        var store = new StartupAdministratorStore();
        var audit = new RecordingAccountAudit();
        var transaction = new FaultingTransaction();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAuditSubmission>(audit);
                services.AddSingleton<IAccountAuditTransaction>(transaction);
            }));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        audit.Events.Clear();
        using var response = await Create(admin, "target", "Target", AccountType.Ordinary);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var entry = Assert.Single(audit.Events, e => e.Action == "account.create");
        Assert.Equal(AuditActionOutcome.Failure, entry.Outcome);
        Assert.IsType<AuditTarget.Unknown>(entry.Target);
        Assert.DoesNotContain((await store.List()).Value, a => a.Username == "target");
    }

    private sealed class FaultingTransaction : IAccountAuditTransaction
    {
        public Task<AccountTransactionResult<T>> Execute<T>(Func<IAdministratorStore, Task<Result<T>>> change,
            AuditInformation success, CancellationToken cancellationToken) => throw new InvalidOperationException("injected fault");
    }

    [Fact]
    public async Task EachFailedAdministratorActionProducesFailureEvent()
    {
        var store = new StartupAdministratorStore();
        var audit = new RecordingAccountAudit();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IAuditSubmission>(audit)));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        audit.Events.Clear();
        using (var duplicate = await Create(admin, "admin", "Duplicate", AccountType.Ordinary)) Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using (var inactive = await Availability(admin, "missing", false)) Assert.Equal(HttpStatusCode.BadRequest, inactive.StatusCode);
        using (var active = await Availability(admin, "missing", true)) Assert.Equal(HttpStatusCode.BadRequest, active.StatusCode);
        using (var reset = await Reset(admin, "missing")) Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        var events = audit.Events.Where(e => e.Action.StartsWith("account.")).ToArray();
        Assert.Equal(new[] { "account.create", "account.inactivate", "account.reactivate", "account.password-reset" }, events.Select(e => e.Action));
        Assert.All(events, e =>
        {
            Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
            Assert.Equal("admin", Assert.IsType<AuditActor.Identified>(e.Actor).Identifier);
        });
        Assert.Equal("admin", Assert.IsType<AuditTarget.Identified>(events[0].Target).Identifier);
        Assert.All(events.Skip(1), e => Assert.IsType<AuditTarget.Unknown>(e.Target));
    }

    [Fact]
    public async Task CredentialShapedUnverifiedUsernameIsNeverRecorded()
    {
        var store = new StartupAdministratorStore();
        var audit = new RecordingAccountAudit();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IAuditSubmission>(audit)));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        const string credential = "Abcdefgh1234567890-_";
        audit.Events.Clear();
        using var response = await Reset(admin, credential);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(audit.Events, e => e.Action == "account.password-reset");
        Assert.IsType<AuditTarget.Unknown>(entry.Target);
        Assert.DoesNotContain(credential, entry.ToString());
    }

    [Fact]
    public async Task FailureAuditSubmissionFailureIsLoggedWithoutCredentialsAndNeverAllowsAction()
    {
        var store = new StartupAdministratorStore();
        var logger = new AccountAuditLogger();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAuditSubmission, FailingAccountAudit>();
                services.AddSingleton<ILogger<AccountAudit>>(logger);
            }));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "ordinary", "Ordinary", AccountType.Ordinary);
        var temporary = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var user = Client(factory);
        await Login(user, "ordinary", temporary);
        await Replace(user, temporary, Replacement);
        using (var duplicate = await Create(admin, "ordinary", "Duplicate", AccountType.Ordinary))
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using (var denied = await Create(user, "denied", "Denied", AccountType.Ordinary))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message =>
        {
            Assert.Contains("audit submission failed", message);
            Assert.DoesNotContain(Initial, message);
            Assert.DoesNotContain(Replacement, message);
            Assert.DoesNotContain(temporary, message);
        });
        Assert.DoesNotContain((await store.List()).Value, account => account.Username == "denied");
    }

    private sealed class FailingAccountAudit : IAuditSubmission
    {
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default) =>
            Task.FromResult(information.Action.StartsWith("account.") ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
    }

    private sealed class AccountAuditLogger : ILogger<AccountAudit>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class RecordingAccountAudit : IAuditSubmission
    {
        public List<AuditInformation> Events { get; } = [];
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default)
        {
            Events.Add(information);
            return Task.FromResult(AuditSubmissionResult.Success);
        }
    }

    private sealed class UncertainTransaction(IAdministratorStore store) : IAccountAuditTransaction
    {
        public bool Uncertain { get; set; }
        public async Task<AccountTransactionResult<T>> Execute<T>(Func<IAdministratorStore, Task<Result<T>>> change,
            AuditInformation success, CancellationToken cancellationToken)
        {
            var result = await change(store);
            return Uncertain && result.IsSuccess
                ? new(Result.Failure<T>("The account change outcome could not be confirmed."), true)
                : new(result);
        }
    }
}
