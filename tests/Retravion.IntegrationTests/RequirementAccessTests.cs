using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task DeniedRequirementCallbacksAreSanitizedAndAllFailureLogsRemainSafe()
    {
        var audit = new RequirementAuditRecorder(); var logger = new RequirementAuditLogger();
        var store = new RequirementStoreDouble();
        var needStore = new NeedStoreDouble();
        var caseStore = new CaseGateStore();
        await using var original = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IRequirementStore>(store); services.AddSingleton<IRequirementChangeTransaction>(store);
            services.AddSingleton<IRequirementLinkStore>(store);
            services.AddSingleton<Retravion.Application.TestCase.ITestCaseStore>(caseStore);
            services.AddSingleton<Retravion.Application.TestCase.ITestCaseChangeTransaction>(caseStore);
            services.AddSingleton<Retravion.Application.UserNeed.IUserNeedStore>(needStore);
            services.AddSingleton<Retravion.Application.UserNeed.IUserNeedChangeTransaction>(needStore);
            services.AddSingleton<IAuditSubmission>(audit); services.AddSingleton<ILogger<RequirementOperations>>(logger);
        }));
        using var client = Client(factory);
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        await Replace(client, Initial, Replacement);
        var revoked = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RequirementOperations>();
        var r = RequirementState.Create(Guid.NewGuid(), "secret-id", "secret-title", "secret-statement", RequirementType.Safety, "secret-rationale").Value;
        var expected = new RequirementSnapshot(r, 1);
        foreach (var principal in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted, revoked })
        foreach (var failureMode in new[] { "success", "failure", "throw" })
        {
            audit.Events.Clear(); audit.Fail = failureMode == "failure"; audit.Throw = failureMode == "throw";
            var logsBefore = logger.Messages.Count;
            Assert.True((await operations.Create(principal, r.ProductInternalId, "", "secret-title", "secret-statement", null, "secret-rationale")).IsFailure);
            Assert.True((await operations.Derive(principal, expected, Guid.NewGuid(), "", "secret-title", "secret-statement", null, "secret-rationale")).IsFailure);
            Assert.True((await operations.Provenance(principal, r.InternalId)).IsFailure);
            Assert.True((await operations.Edit(principal, expected, "secret", "secret", "secret", RequirementType.Hardware, "secret")).IsFailure);
            foreach (var decision in new[] { "InReview", "Approved", "Draft", "secret-decision" })
                Assert.True((await operations.Review(principal, expected, decision, "secret-comment")).IsFailure);
            Assert.True((await operations.List(principal, r.ProductInternalId)).IsFailure);
            Assert.True((await operations.Find(principal, r.InternalId)).IsFailure);
            Assert.True((await operations.History(principal, r.InternalId)).IsFailure);
            Assert.True((await operations.Versions(principal, r.InternalId)).IsFailure);
            Assert.True((await operations.Find(principal, r.InternalId, versionId: r.VersionId)).IsFailure);
            Assert.True((await operations.CreateSuccessor(principal, expected, "secret-reason")).IsFailure);
            Assert.True((await operations.Reopen(principal, expected, "secret-reason")).IsFailure);
            var target = new RequirementUserNeedLink(Guid.NewGuid(), Guid.NewGuid());
            Assert.True((await operations.AddLink(principal, expected, target)).IsFailure);
            Assert.True((await operations.RemoveLink(principal, expected, target)).IsFailure);
            Assert.True((await operations.RetargetLink(principal, expected, target, new(Guid.NewGuid(), Guid.NewGuid()))).IsFailure);
            Assert.True((await operations.Links(principal, r.InternalId, r.VersionId)).IsFailure);
            var parent = new RequirementParentLink(Guid.NewGuid(), Guid.NewGuid());
            Assert.True((await operations.AddParent(principal, expected, parent)).IsFailure);
            Assert.True((await operations.RemoveParent(principal, expected)).IsFailure);
            Assert.True((await operations.ReplaceParent(principal, expected, parent)).IsFailure);
            Assert.True((await operations.RetargetParent(principal, expected, parent, new(Guid.NewGuid(), Guid.NewGuid()))).IsFailure);
            Assert.True((await operations.Parent(principal, r.InternalId, r.VersionId)).IsFailure);
            Assert.True((await operations.Children(principal, r.InternalId, r.VersionId)).IsFailure);
            Assert.True((await operations.IncomingUserNeed(principal, target.UserNeedInternalId, target.UserNeedVersionId)).IsFailure);
            var relationships = scope.ServiceProvider.GetRequiredService<Retravion.Web.Features.Relationships.RelationshipOperations>();
            foreach (var kind in new[] { "requirement", "user-need" })
            {
                Assert.Equal("Access denied.", (await relationships.Load(principal, kind, r.InternalId, r.VersionId)).Error);
                Assert.Equal("Access denied.", (await relationships.Explore(principal, kind, r.InternalId, r.VersionId,
                    new[] { target.UserNeedVersionId, parent.VersionId })).Error);
            }
            Assert.Equal(failureMode == "throw" ? 0 : 16, audit.Events.Count);
            Assert.All(audit.Events, e =>
            {
                Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.IsType<AuditTarget.Unknown>(e.Target);
                Assert.Null(e.RequirementChange); Assert.Null(e.UserNeedChange); Assert.Null(e.ProductChange);
                Assert.DoesNotContain("secret", e.Action);
                Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
                if (principal.Identity?.IsAuthenticated == true) Assert.Equal(new AuditActor.Identified("admin"), e.Actor);
                else Assert.IsType<AuditActor.Unknown>(e.Actor);
            });
            Assert.Equal(logsBefore + (failureMode == "success" ? 0 : 16), logger.Messages.Count);
        }
        Assert.All(logger.Messages, message => Assert.Equal("Unauthorized Requirement change audit submission failed.", message));
        Assert.Equal(0, store.Calls);
        Assert.Equal(0, needStore.Calls);
        Assert.Equal(0, caseStore.Calls);
    }

    [Fact]
    public async Task RequirementPagesAreProductScopedRenderMarkdownAndEncodePlainTextAndRespectAccessAndRetirement()
    {
        var products = new ProductStoreDouble();
        var product = Retravion.Domain.Product.Product.Create("Owner", "Product", Retravion.Domain.Product.ProductKind.System, "").Value;
        Assert.True((await products.Create(product)).IsSuccess);
        var r = RequirementState.Create(product.InternalId, "REQ-1", "Requirement title", "# Statement\n\n**Content**", RequirementType.Hardware, "**Plain** <script>unsafe()</script>").Value;
        var store = new RequirementReadDouble(new(r, 1));
        await using var original = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRequirementStore>(store); services.AddSingleton<IRequirementChangeTransaction>(new RequirementStoreDouble());
            services.AddSingleton<IRequirementLinkStore>(new RequirementStoreDouble());
        }));
        using var client = Client(factory);
        var paths = new[] { "/requirements?product=Owner", $"/requirements/details?id={r.InternalId}", "/requirements/create?product=Owner", $"/requirements/edit?id={r.InternalId}",
            $"/products/links?kind=requirement&id={r.InternalId}&version={r.VersionId}",
            $"/products/links?kind=user-need&id={r.InternalId}&version={r.VersionId}" };
        foreach (var path in paths) { using var denied = await client.GetAsync(path); Assert.Equal(System.Net.HttpStatusCode.Redirect, denied.StatusCode); }
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        foreach (var path in paths) { using var denied = await client.GetAsync(path); Assert.Equal(System.Net.HttpStatusCode.Redirect, denied.StatusCode); }
        await Replace(client, Initial, Replacement);
        var active = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        var list = await client.GetStringAsync(paths[0]);
        Assert.Contains("REQ-1", list); Assert.Contains("Requirement title", list); Assert.Contains("Hardware", list);
        Assert.Contains("Search ID or title", list); Assert.Contains("Requirements for Owner", list);
        Assert.Contains("/requirements/create?product=Owner", list); Assert.Contains("/products/details?id=Owner", list);
        Assert.DoesNotContain(product.InternalId.ToString(), list);
        var form = await client.GetStringAsync(paths[2]);
        Assert.Contains("Requirement ID", form); Assert.Contains("Statement (Markdown)", form); Assert.Contains("Statement preview", form);
        Assert.Contains("Rationale (optional, plain text)", form); Assert.Contains("Type", form);
        var details = await client.GetStringAsync(paths[1]);
        Assert.Contains("<h1>Statement</h1>", details); Assert.Contains("<strong>Content</strong>", details);
        Assert.Contains("**Plain** &lt;script&gt;unsafe()&lt;/script&gt;", details);
        Assert.DoesNotContain("<script>unsafe()", details); Assert.Contains("Submit for review", details);
        Assert.Contains("Historical ID", details); Assert.Contains("Historical title", details); Assert.Contains("Historical statement", details);
        Assert.Contains("Historical rationale", details); Assert.Contains("&lt;b&gt;comment&lt;/b&gt;", details);
        var other = Retravion.Domain.Product.Product.Create("Other", "Other", Retravion.Domain.Product.ProductKind.System, "").Value;
        Assert.True((await products.Create(other)).IsSuccess);
        var empty = await client.GetStringAsync("/requirements?product=Other");
        Assert.Contains("No Requirements yet.", empty); Assert.DoesNotContain("REQ-1</a>", empty);
        foreach (var status in new[] { RequirementStatus.InReview, RequirementStatus.Approved })
        {
            store.Snapshot = new(RequirementState.Restore(r.InternalId, r.ProductInternalId, r.Id, r.Title, r.Statement, r.Type, r.Rationale, status).Value, 2);
            Assert.Contains("Only Draft Requirements can be edited.", await client.GetStringAsync(paths[3]));
            details = await client.GetStringAsync(paths[1]);
            Assert.DoesNotContain("/requirements/edit?", details);
            Assert.Contains(status == RequirementStatus.InReview ? "Return to Draft" : "Reopen to Draft", details);
            if (status == RequirementStatus.InReview) Assert.Matches("<button[^>]*>.*?<span[^>]*>Approve</span>.*?</button>", details);
        }
        store.Snapshot = new(r, 1);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        var retired = await client.GetStringAsync(paths[0]);
        Assert.Contains("REQ-1", retired); Assert.Contains("read-only", retired); Assert.DoesNotContain("/requirements/create?product=Owner", retired);
        details = await client.GetStringAsync(paths[1]); Assert.DoesNotContain("Submit for review", details); Assert.DoesNotContain("/requirements/edit?", details);
        Assert.Contains("retired", await client.GetStringAsync(paths[2])); Assert.Contains("retired", await client.GetStringAsync(paths[3]));
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(active.FindFirstValue(SessionTicketStore.SessionClaim)!);
        foreach (var path in paths) { using var denied = await client.GetAsync(path); Assert.Equal(System.Net.HttpStatusCode.Redirect, denied.StatusCode); }
    }

    private sealed class RequirementReadDouble(RequirementSnapshot snapshot) : IRequirementStore
    {
        public RequirementSnapshot Snapshot = snapshot;
        public Task<Result<Maybe<RequirementProvenance>>> Provenance(Guid id, CancellationToken token = default) => Task.FromResult(Result.Success(Maybe<RequirementProvenance>.None));
        public Task<Result<IReadOnlyList<RequirementSnapshot>>> List(Guid product, CancellationToken token = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<RequirementSnapshot>>(product == Snapshot.Requirement.ProductInternalId ? [Snapshot] : []));
        public Task<Result<Maybe<RequirementSnapshot>>> Find(Guid id, CancellationToken token = default, Guid? versionId = null) =>
            Task.FromResult(Result.Success(id == Snapshot.Requirement.InternalId && (versionId is null || versionId == Snapshot.Requirement.VersionId) ? Maybe<RequirementSnapshot>.From(Snapshot) : Maybe<RequirementSnapshot>.None));
        public Task<Result<IReadOnlyList<RequirementSnapshot>>> Versions(Guid id, CancellationToken token = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<RequirementSnapshot>>(id == Snapshot.Requirement.InternalId ? [Snapshot] : []));
        public Task<Result<IReadOnlyList<RequirementReview>>> History(Guid id, CancellationToken token = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<RequirementReview>>([new(id, new AuditActor.Identified("author"), DateTimeOffset.UnixEpoch, "Draft", "<b>comment</b>", "Historical ID", "Historical title", "Historical statement", RequirementType.Safety, "Historical rationale")]));
    }
    private sealed class RequirementAuditRecorder : IAuditSubmission
    {
        public List<AuditInformation> Events { get; } = [];
        public bool Fail, Throw;
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken token = default)
        {
            if (!information.Action.StartsWith("requirement.", StringComparison.Ordinal)) return Task.FromResult(AuditSubmissionResult.Success);
            if (Throw) throw new IOException("private-provider-details");
            Events.Add(information); return Task.FromResult(Fail ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
        }
    }
    private sealed class RequirementAuditLogger : ILogger<RequirementOperations>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
    private sealed class RequirementStoreDouble : IRequirementStore, IRequirementChangeTransaction, IRequirementLinkStore
    {
        public int Calls;
        public Task<Result<Maybe<RequirementProvenance>>> Provenance(Guid id, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> Derive(RequirementSnapshot source, RequirementState destination, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<RequirementVersionReference>>> IncomingUserNeed(Guid id, Guid version, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<RequirementVersionReference>>> Children(Guid id, Guid version, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<Maybe<RequirementParentLink>>> Parent(Guid id, Guid version, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> AddParent(RequirementSnapshot expected, RequirementParentLink target, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> RemoveParent(RequirementSnapshot expected, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> ReplaceParent(RequirementSnapshot expected, RequirementParentLink target, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<RequirementUserNeedLink>>> Links(Guid id, Guid version, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> RetargetLink(RequirementSnapshot expected, RequirementUserNeedLink target, RequirementUserNeedLink replacement, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> ChangeLink(RequirementSnapshot expected, RequirementUserNeedLink target, bool add, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<RequirementSnapshot>>> List(Guid product, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<Maybe<RequirementSnapshot>>> Find(Guid id, CancellationToken token = default, Guid? versionId = null) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<RequirementSnapshot>>> Versions(Guid id, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> CreateSuccessor(RequirementSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<RequirementReview>>> History(Guid id, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> Create(RequirementState r, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<RequirementSnapshot>> Update(RequirementSnapshot expected, RequirementState replacement, AuditActor actor, DateTimeOffset time, string? decision = null, string? comment = null, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
    }
}
