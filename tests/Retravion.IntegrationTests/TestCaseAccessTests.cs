using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Application.TestCase;
using Retravion.Domain.TestCase;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.TestCase;
using Xunit;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task TestCaseDeniedMutationsAreSanitizedBeforeValidationAndReadsNeverReachStorage()
    {
        var audit = new CaseAuditRecorder(); var logger = new CaseAuditLogger(); var store = new CaseGateStore();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<ITestCaseStore>(store); services.AddSingleton<ITestCaseChangeTransaction>(store);
            services.AddSingleton<IAuditSubmission>(audit); services.AddSingleton<ILogger<TestCaseOperations>>(logger);
        }));
        using var client = Client(factory);
        var paths = new[] { "/test-cases?product=secret", "/test-cases/create?product=secret", $"/test-cases/details?id={Guid.NewGuid()}", $"/test-cases/edit?id={Guid.NewGuid()}" };
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/", response.Headers.Location!.OriginalString);
        }
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        foreach (var path in paths)
        {
            using var response = await client.GetAsync(path);
            Assert.Equal("/password-replacement", response.Headers.Location!.OriginalString);
        }
        await Replace(client, Initial, Replacement);
        var revoked = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<TestCaseOperations>();
        var expected = new TestCaseSnapshot(Case.Create(Guid.NewGuid(), 1, TestCaseContent.Create("secret", "", "", []).Value).Value, 1);
        foreach (var principal in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted, revoked })
        {
            audit.Events.Clear();
            foreach (var steps in new TestCaseStep[][] { [new("secret-action", "secret-result")], [null!] })
            {
                Assert.True((await operations.Create(principal, expected.TestCase.ProductInternalId, "secret-title", "secret-description", "secret-preconditions", steps)).IsFailure);
                Assert.True((await operations.Edit(principal, expected, "secret-title", "secret-description", "secret-preconditions", steps)).IsFailure);
            }
            Assert.True((await operations.List(principal, expected.TestCase.ProductInternalId)).IsFailure);
            Assert.True((await operations.Find(principal, expected.TestCase.InternalId)).IsFailure);
            Assert.True((await operations.Find(principal, expected.TestCase.InternalId, expected.TestCase.VersionId)).IsFailure);
            Assert.Equal(4, audit.Events.Count);
            Assert.All(audit.Events, e =>
            {
                Assert.Contains(e.Action, new[] { "test-case.create", "test-case.edit" });
                Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.IsType<AuditTarget.Unknown>(e.Target);
                Assert.Null(e.TestCaseChange);
                if (principal.Identity?.IsAuthenticated == true) Assert.Equal(new AuditActor.Identified("admin"), e.Actor);
                else Assert.IsType<AuditActor.Unknown>(e.Actor);
            });
        }
        foreach (var throws in new[] { false, true })
        {
            audit.Fail = true; audit.Throw = throws;
            Assert.True((await operations.Create(revoked, Guid.Empty, "secret", "secret", "secret", [null!])).IsFailure);
            Assert.True((await operations.Edit(revoked, expected, "secret", "secret", "secret", [null!])).IsFailure);
        }
        Assert.Equal(4, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal("Unauthorized Test Case change audit submission failed.", message));
        Assert.Equal(0, store.Calls);
    }

    private sealed class CaseAuditRecorder : IAuditSubmission
    {
        internal List<AuditInformation> Events { get; } = [];
        internal bool Fail, Throw;
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default)
        {
            if (Throw) throw new IOException("secret provider details");
            if (Fail) return Task.FromResult(AuditSubmissionResult.Failure);
            Events.Add(information); return Task.FromResult(AuditSubmissionResult.Success);
        }
    }
    private sealed class CaseAuditLogger : ILogger<TestCaseOperations>
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
    private sealed class CaseGateStore : ITestCaseStore, ITestCaseChangeTransaction
    {
        internal int Calls;
        public Task<Result<IReadOnlyList<TestCaseSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
        public Task<Result<Maybe<TestCaseSnapshot>>> Find(Guid internalId, Guid? versionId = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
        public Task<Result<TestCaseSnapshot>> Create(Guid productInternalId, TestCaseContent content, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
        public Task<Result<TestCaseSnapshot>> Update(TestCaseSnapshot expected, TestCaseContent content, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException(); }
    }
}
