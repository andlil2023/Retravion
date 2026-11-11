using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Web.Features.Feature;
using Retravion.Web.Features.Identity.Logout;
using Xunit;
using FeatureRecord = Retravion.Domain.Feature.Feature;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task FeatureDeniedMutationsAreSanitizedAndReadsDoNotReachStorage()
    {
        var audit = new CaseAuditRecorder(); var logger = new FeatureAuditLogger(); var store = new FeatureGateStore();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IFeatureStore>(store); services.AddSingleton<IFeatureChangeTransaction>(store);
            services.AddSingleton<IAuditSubmission>(audit); services.AddSingleton<ILogger<FeatureOperations>>(logger);
        }));
        using var client = Client(factory);
        foreach (var path in new[] { "/features?product=secret", "/features/create?product=secret", $"/features/details?id={Guid.NewGuid()}", $"/features/edit?id={Guid.NewGuid()}" })
        {
            using var response = await client.GetAsync(path); Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode); Assert.Equal("/", response.Headers.Location!.OriginalString);
        }
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        await Replace(client, Initial, Replacement);
        var revoked = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        using var scope = factory.Services.CreateScope(); var operations = scope.ServiceProvider.GetRequiredService<FeatureOperations>();
        var expected = new FeatureSnapshot(FeatureRecord.Create(Guid.NewGuid(), 1, FeatureContent.Create("secret", "private")).Value, 1);
        async Task Mutations(ClaimsPrincipal principal)
        {
            Assert.True((await operations.Create(principal, expected.Feature.ProductInternalId, "secret-title", "secret-description")).IsFailure);
            Assert.True((await operations.Edit(principal, expected, "secret-title", "secret-description")).IsFailure);
            foreach (var decision in new[] { "InReview", "Approved", "Draft", "secret-decision" }) Assert.True((await operations.Review(principal, expected, decision, "secret-comment")).IsFailure);
            Assert.True((await operations.Reopen(principal, expected, "secret-reason")).IsFailure);
            Assert.True((await operations.CreateSuccessor(principal, expected, "secret-reason")).IsFailure);
        }
        foreach (var principal in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted, revoked })
        {
            audit.Events.Clear(); await Mutations(principal);
            Assert.True((await operations.List(principal, expected.Feature.ProductInternalId)).IsFailure);
            Assert.True((await operations.Find(principal, expected.Feature.InternalId)).IsFailure);
            Assert.True((await operations.Versions(principal, expected.Feature.InternalId)).IsFailure);
            Assert.True((await operations.History(principal, expected.Feature.InternalId)).IsFailure);
            Assert.Equal(8, audit.Events.Count);
            Assert.All(audit.Events, e => { Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.IsType<AuditTarget.Unknown>(e.Target); Assert.Null(e.FeatureChange); Assert.DoesNotContain("secret", e.Action); });
        }
        foreach (var throws in new[] { false, true }) { audit.Fail = true; audit.Throw = throws; await Mutations(revoked); }
        Assert.Equal(16, logger.Messages.Count); Assert.All(logger.Messages, message => Assert.Equal("Unauthorized Feature change audit submission failed.", message));
        Assert.Equal(0, store.Calls);
    }
    private sealed class FeatureAuditLogger : ILogger<FeatureOperations>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
    private sealed class FeatureGateStore : IFeatureStore, IFeatureChangeTransaction
    {
        internal int Calls;
        private T Called<T>() { Calls++; throw new InvalidOperationException(); }
        public Task<Result<Maybe<FeatureAuditDetails>>> Outcome(Guid product, Guid operation, CancellationToken token = default) => Called<Task<Result<Maybe<FeatureAuditDetails>>>>();
        public Task<Result<IReadOnlyList<FeatureSnapshot>>> List(Guid product, CancellationToken token = default) => Called<Task<Result<IReadOnlyList<FeatureSnapshot>>>>();
        public Task<Result<Maybe<FeatureSnapshot>>> Find(Guid id, Guid? version = null, CancellationToken token = default) => Called<Task<Result<Maybe<FeatureSnapshot>>>>();
        public Task<Result<IReadOnlyList<FeatureSnapshot>>> Versions(Guid id, CancellationToken token = default) => Called<Task<Result<IReadOnlyList<FeatureSnapshot>>>>();
        public Task<Result<IReadOnlyList<FeatureHistory>>> History(Guid id, CancellationToken token = default) => Called<Task<Result<IReadOnlyList<FeatureHistory>>>>();
        public Task<Result<FeatureSnapshot>> Create(Guid product, FeatureContent content, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => Called<Task<Result<FeatureSnapshot>>>();
        public Task<Result<FeatureSnapshot>> Update(FeatureSnapshot expected, FeatureContent content, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => Called<Task<Result<FeatureSnapshot>>>();
        public Task<Result<FeatureSnapshot>> Review(FeatureSnapshot expected, string decision, string? comment, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => Called<Task<Result<FeatureSnapshot>>>();
        public Task<Result<FeatureSnapshot>> Reopen(FeatureSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => Called<Task<Result<FeatureSnapshot>>>();
        public Task<Result<FeatureSnapshot>> CreateSuccessor(FeatureSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => Called<Task<Result<FeatureSnapshot>>>();
    }
}
