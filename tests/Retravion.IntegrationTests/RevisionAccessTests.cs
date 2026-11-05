using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Revision;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task RevisionDeniedCallbacksAndRoutesNeverReachStorageAndAuditBeforeValidation()
    {
        var audit = new CaseAuditRecorder(); var logger = new RevisionLogger(); var store = new RevisionGateStore();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IRevisionStore>(store); services.AddSingleton<IRevisionChangeTransaction>(store);
            services.AddSingleton<IAuditSubmission>(audit); services.AddSingleton<ILogger<RevisionOperations>>(logger);
        }));
        using var client = Client(factory);
        var paths = new[] { "/revisions?product=secret", "/revisions/create?product=secret", "/revisions/edit?product=secret", $"/revisions/details?product=secret&id={Guid.NewGuid()}&operation={Guid.NewGuid()}" };
        foreach (var path in paths) { using var response = await client.GetAsync(path); Assert.Equal("/", response.Headers.Location!.OriginalString); }
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        foreach (var path in paths) { using var response = await client.GetAsync(path); Assert.Equal("/password-replacement", response.Headers.Location!.OriginalString); }
        await Replace(client, Initial, Replacement);
        var revoked = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        var sessions = factory.Services.GetRequiredService<SessionTicketStore>();
        await sessions.RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        using var scope = factory.Services.CreateScope(); var operations = scope.ServiceProvider.GetRequiredService<RevisionOperations>();
        var product = Guid.NewGuid(); var id = Guid.NewGuid();
        async Task Mutations(ClaimsPrincipal principal)
        {
            Assert.True((await operations.Create(principal, Guid.Empty, Guid.Empty, product, null, "secret description")).IsFailure);
            Assert.True((await operations.Edit(principal, Guid.Empty, null!, "secret identifier", "secret description")).IsFailure);
            foreach (var action in new[] { "add", "remove", "replace", "secret invalid action" })
                Assert.True((await operations.Select(principal, Guid.Empty, null!, action, null!, null, true)).IsFailure);
            Assert.True((await operations.Delete(principal, Guid.Empty, null!, true)).IsFailure);
        }
        foreach (var principal in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted, revoked })
        {
            audit.Events.Clear();
            await Mutations(principal);
            Assert.True((await operations.List(principal, product)).IsFailure);
            Assert.True((await operations.Retrieve(principal, product, id, Guid.NewGuid())).IsFailure);
            Assert.True((await operations.History(principal, product, id)).IsFailure);
            Assert.True((await operations.Candidates(principal, product)).IsFailure);
            Assert.True((await operations.Baseline(principal, product, id)).IsFailure);
            Assert.Equal(7, audit.Events.Count);
            Assert.All(audit.Events, e =>
            {
                Assert.StartsWith("revision.", e.Action); Assert.DoesNotContain("secret", e.Action);
                Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.IsType<AuditTarget.Unknown>(e.Target); Assert.Null(e.RevisionChange);
                if (principal.Identity?.IsAuthenticated == true) Assert.Equal(new AuditActor.Identified("admin"), e.Actor); else Assert.IsType<AuditActor.Unknown>(e.Actor);
            });
        }
        foreach (var throws in new[] { false, true }) { audit.Fail = true; audit.Throw = throws; await Mutations(revoked); }
        Assert.Equal(14, logger.Messages.Count); Assert.All(logger.Messages, m => Assert.Equal("Unauthorized revision change audit submission failed.", m));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task RevisionSessionWritesAreOrderedWithLogoutAndResetRevocationAndDoNotExtendExpiry()
    {
        using var sessions = new SessionTicketStore();
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[]
        { new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Administrator") }, "Cookies")),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, "Cookies");
        foreach (var reset in new[] { false, true })
        {
            var key = await sessions.StoreAsync(ticket);
            var expiry = (await sessions.RetrieveAsync(key))!.Properties.ExpiresUtc;
            await sessions.MutationGate.WaitAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var revoked = Task.Run(async () =>
            {
                entered.SetResult();
                if (reset) sessions.RevokeAccount("ADMIN"); else await sessions.RemoveAsync(key);
            });
            await entered.Task;
            Assert.False(revoked.IsCompleted); Assert.True(sessions.IsActive(key));
            Assert.Equal(expiry, (await sessions.RetrieveAsync(key))!.Properties.ExpiresUtc);
            sessions.MutationGate.Release();
            await revoked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sessions.IsActive(key));
            await sessions.RenewAsync(key, ticket); Assert.Null(await sessions.RetrieveAsync(key));
        }
    }

    private sealed class RevisionLogger : ILogger<RevisionOperations>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
    private sealed class RevisionGateStore : IRevisionStore, IRevisionChangeTransaction
    {
        internal int Calls;
        private Task<Result<T>> Unexpected<T>() { Calls++; throw new InvalidOperationException("Unauthorized storage access"); }
        public Task<Result<IReadOnlyList<RevisionSnapshot>>> List(Guid product, CancellationToken token = default) => Unexpected<IReadOnlyList<RevisionSnapshot>>();
        public Task<Result<RevisionRetrieval>> Retrieve(Guid product, Guid id, Guid? operation = null, CancellationToken token = default) => Unexpected<RevisionRetrieval>();
        public Task<Result<IReadOnlyList<RevisionHistory>>> History(Guid product, Guid id, CancellationToken token = default) => Unexpected<IReadOnlyList<RevisionHistory>>();
        public Task<Result<IReadOnlyList<RevisionBaselineMember>>> Candidates(Guid product, CancellationToken token = default) => Unexpected<IReadOnlyList<RevisionBaselineMember>>();
        public Task<Result<RevisionBaseline>> Baseline(Guid product, Guid id, CancellationToken token = default) => Unexpected<RevisionBaseline>();
        public Task<Result<RevisionSnapshot>> Create(Guid operation, DraftRevision draft, AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) => Unexpected<RevisionSnapshot>();
        public Task<Result<RevisionSnapshot>> Edit(Guid operation, RevisionSnapshot expected, string? identifier, string? description, AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) => Unexpected<RevisionSnapshot>();
        public Task<Result<RevisionSnapshot>> Select(Guid operation, RevisionSnapshot expected, string action, RevisionSelection target, RevisionSelection? replacement, AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) => Unexpected<RevisionSnapshot>();
        public Task<Result<RevisionSnapshot>> Delete(Guid operation, RevisionSnapshot expected, AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) => Unexpected<RevisionSnapshot>();
    }
}
