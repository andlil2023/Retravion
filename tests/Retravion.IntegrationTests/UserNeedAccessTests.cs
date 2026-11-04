using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Application.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task DeniedNeedCallbacksAreAuditedWithoutSubmittedValuesAndFailureLogsAreSafe()
    {
        var recorder = new NeedAuditRecorder();
        var logger = new NeedAuditLogger();
        var store = new NeedStoreDouble();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IUserNeedStore>(store);
            services.AddSingleton<IUserNeedChangeTransaction>(store);
            services.AddSingleton<IAuditSubmission>(recorder);
            services.AddSingleton<ILogger<UserNeedOperations>>(logger);
        }));
        using var client = Client(factory);
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        await Replace(client, Initial, Replacement);
        var revoked = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<UserNeedOperations>();
        var need = Need.Create(Guid.NewGuid(), "secret-id", "secret-title", "secret-statement").Value;
        var expected = new UserNeedSnapshot(need, 1);
        foreach (var principal in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted, revoked })
        {
            recorder.Events.Clear();
            Assert.True((await operations.Create(principal, need.ProductInternalId, "secret-id", "secret-title", "secret-statement")).IsFailure);
            Assert.True((await operations.Edit(principal, expected, "secret-new-id", "secret-title", "secret-statement")).IsFailure);
            foreach (var decision in new[] { "InReview", "Approved", "Draft", "secret-decision" })
                Assert.True((await operations.Review(principal, expected, decision, "secret-comment")).IsFailure);
            Assert.True((await operations.List(principal, need.ProductInternalId)).IsFailure);
            Assert.True((await operations.Find(principal, need.InternalId)).IsFailure);
            Assert.True((await operations.History(principal, need.InternalId)).IsFailure);
            Assert.True((await operations.Versions(principal, need.InternalId)).IsFailure);
            Assert.True((await operations.Find(principal, need.InternalId, versionId: need.VersionId)).IsFailure);
            Assert.True((await operations.CreateSuccessor(principal, expected, "secret-reason")).IsFailure);
            Assert.True((await operations.Reopen(principal, expected, "secret-reason")).IsFailure);
            Assert.Equal(8, recorder.Events.Count);
            Assert.All(recorder.Events, e =>
            {
                Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
                Assert.IsType<AuditTarget.Unknown>(e.Target);
                Assert.Null(e.UserNeedChange);
                Assert.DoesNotContain("secret", e.Action);
                if (principal.Identity?.IsAuthenticated == true) Assert.Equal(new AuditActor.Identified("admin"), e.Actor);
                else Assert.IsType<AuditActor.Unknown>(e.Actor);
            });
        }
        foreach (var throwing in new[] { false, true })
        {
            recorder.Fail = true; recorder.Throw = throwing;
            Assert.True((await operations.Create(revoked, need.ProductInternalId, "secret-id", "secret-title", "secret-statement")).IsFailure);
            Assert.True((await operations.CreateSuccessor(revoked, expected, "secret-reason")).IsFailure);
            Assert.True((await operations.Reopen(revoked, expected, "secret-reason")).IsFailure);
        }
        Assert.Equal(6, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal("Unauthorized User Need change audit submission failed.", message));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task UserNeedNavigationListDetailsAndEditorRespectProductScopeAndReadRestrictions()
    {
        var products = new ProductStoreDouble();
        var product = Retravion.Domain.Product.Product.Create("Owner", "Product", Retravion.Domain.Product.ProductKind.System, "").Value;
        Assert.True((await products.Create(product)).IsSuccess);
        var need = Need.Create(product.InternalId, "NEED-1", "Need title", "# Statement\n\n**Content**").Value;
        var store = new NeedReadDouble(new UserNeedSnapshot(need, 1));
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IUserNeedStore>(store);
            services.AddSingleton<IUserNeedChangeTransaction>(new NeedStoreDouble());
        }));
        using var client = Client(factory);
        foreach (var path in new[] { "/user-needs?product=Owner", $"/user-needs/details?id={need.InternalId}", "/user-needs/create?product=Owner" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, denied.StatusCode);
        }
        await Login(client, "admin", Initial);
        await Replace(client, Initial, Replacement);
        var list = await client.GetStringAsync("/user-needs?product=Owner");
        Assert.Contains("User Needs", list);
        Assert.Contains("NEED-1", list);
        Assert.Contains("Need title", list);
        Assert.Contains("/user-needs/create?product=Owner", list);
        Assert.Contains("/products/details?id=Owner", list);
        Assert.DoesNotContain(product.InternalId.ToString(), list);
        var form = await client.GetStringAsync("/user-needs/create?product=Owner");
        Assert.Contains("User Need ID", form);
        Assert.Contains("Statement (Markdown)", form);
        Assert.Contains("Statement preview", form);
        var details = await client.GetStringAsync($"/user-needs/details?id={need.InternalId}");
        Assert.Contains("<h1>Statement</h1>", details);
        Assert.Contains("<strong>Content</strong>", details);
        Assert.Contains("Review history", details);
        Assert.Contains("Submit for review", details);
        Assert.Contains("Historical title", details);
        Assert.Contains("Historical statement", details);
        Assert.Contains("User Needs for Owner", list);
        Assert.Contains("navigation-toggle", list);
        var other = Retravion.Domain.Product.Product.Create("Other", "Other Product", Retravion.Domain.Product.ProductKind.System, "").Value;
        Assert.True((await products.Create(other)).IsSuccess);
        var empty = await client.GetStringAsync("/user-needs?product=Other");
        Assert.Contains("No User Needs yet.", empty);
        Assert.DoesNotContain("NEED-1</a>", empty);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        var retired = await client.GetStringAsync("/user-needs?product=Owner");
        Assert.Contains("NEED-1", retired);
        Assert.Contains("read-only", retired);
        Assert.DoesNotContain("/user-needs/create?product=Owner", retired);
        details = await client.GetStringAsync($"/user-needs/details?id={need.InternalId}");
        Assert.DoesNotContain("Submit for review", details);
        Assert.DoesNotContain("/user-needs/edit?", details);
        Assert.Contains("retired", await client.GetStringAsync("/user-needs/create?product=Owner"));
    }

    private sealed class NeedReadDouble(UserNeedSnapshot need) : IUserNeedStore
    {
        public Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(Guid product, CancellationToken token = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<UserNeedSnapshot>>(product == need.Need.ProductInternalId ? [need] : []));
        public Task<Result<Maybe<UserNeedSnapshot>>> Find(Guid id, CancellationToken token = default, Guid? versionId = null) =>
            Task.FromResult(Result.Success(id == need.Need.InternalId && (versionId is null || versionId == need.Need.VersionId) ? Maybe<UserNeedSnapshot>.From(need) : Maybe<UserNeedSnapshot>.None));
        public Task<Result<IReadOnlyList<UserNeedSnapshot>>> Versions(Guid id, CancellationToken token = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<UserNeedSnapshot>>(id == need.Need.InternalId ? [need] : []));
        public Task<Result<IReadOnlyList<UserNeedReview>>> History(Guid id, CancellationToken token = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<UserNeedReview>>([new(id, new AuditActor.Identified("author"), DateTimeOffset.UnixEpoch, "Draft", "comment", "Historical title", "Historical statement")]));
    }

    private sealed class NeedAuditRecorder : IAuditSubmission
    {
        public List<AuditInformation> Events { get; } = [];
        public bool Fail, Throw;
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken token = default)
        {
            if (!information.Action.StartsWith("need.", StringComparison.Ordinal)) return Task.FromResult(AuditSubmissionResult.Success);
            if (Throw) throw new IOException("private-provider-details");
            Events.Add(information);
            return Task.FromResult(Fail ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
        }
    }

    private sealed class NeedAuditLogger : ILogger<UserNeedOperations>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class NeedStoreDouble : IUserNeedStore, IUserNeedChangeTransaction
    {
        public int Calls;
        public Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(Guid productInternalId, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<Maybe<UserNeedSnapshot>>> Find(Guid id, CancellationToken token = default, Guid? versionId = null) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<UserNeedSnapshot>>> Versions(Guid id, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<UserNeedSnapshot>> CreateSuccessor(UserNeedSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<IReadOnlyList<UserNeedReview>>> History(Guid id, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<UserNeedSnapshot>> Create(Need need, AuditActor actor, DateTimeOffset time, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
        public Task<Result<UserNeedSnapshot>> Update(UserNeedSnapshot expected, Need replacement, AuditActor actor, DateTimeOffset time, string? decision = null, string? comment = null, CancellationToken token = default) { Calls++; throw new InvalidOperationException(); }
    }
}
