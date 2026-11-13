using System.Security.Claims;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Requirement;
using Xunit;
using RequirementListPage = Retravion.Web.Features.Requirement.RequirementList;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise production-rendered controls and real callbacks.
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    [Trait("Req", "REQ-REQ-002")]
    [Trait("Req", "REQ-REQ-006")]
    [Trait("Req", "REQ-AREA-003")]
    [Trait("Req", "REQ-LINK-003")]
    [Trait("Req", "REQ-REQ-005")]
    [Trait("Req", "REQ-LINK-006")]
    public async Task RequirementHierarchyComponentsExpandFilterAndNavigateExactVersionsWithoutWrites()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var empty = (await new CreateProduct(products).Execute("EMPTY", "Empty", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(db.ConnectionString);
        var areas = new PostgresProductAreaStore(db.ConnectionString);
        var area = (await areas.Save(Guid.NewGuid(), null, ProductArea.Create(product.InternalId, "SW", "Software").Value,
            RequirementActor, RequirementTime, () => Task.FromResult(true))).Value;
        var a1 = await ParentApprove(store, await ParentDraft(store, product.InternalId, "A"));
        var c = await ParentDraft(store, product.InternalId, "C");
        c = (await store.AddParent(c, ParentEndpoint(a1), RequirementActor, RequirementTime)).Value;
        c = await ParentApprove(store, c);
        var d = await ParentDraft(store, product.InternalId, "D");
        d = (await store.AddParent(d, ParentEndpoint(c), RequirementActor, RequirementTime)).Value;
        var g = (await store.Create(RequirementState.Create(product.InternalId, "G", "Deep target", "Statement",
            RequirementType.Software, areaId: area.Area.InternalId).Value, RequirementActor, RequirementTime)).Value;
        g = (await store.AddParent(g, ParentEndpoint(d), RequirementActor, RequirementTime)).Value;
        g = (await store.Update(g, g.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var sibling = await ParentDraft(store, product.InternalId, "S");
        sibling = (await store.AddParent(sibling, ParentEndpoint(a1), RequirementActor, RequirementTime)).Value;
        var e = await ParentDraft(store, product.InternalId, "E");
        // B's historical parent is A, but its selected successor now belongs under E.
        var b1 = await ParentDraft(store, product.InternalId, "B");
        b1 = (await store.AddParent(b1, ParentEndpoint(a1), RequirementActor, RequirementTime)).Value;
        b1 = await ParentApprove(store, b1);
        var b2 = (await store.CreateSuccessor(b1, "Change parent", RequirementActor, RequirementTime)).Value;
        b2 = (await store.ReplaceParent(b2, ParentEndpoint(e), RequirementActor, RequirementTime)).Value;
        var a2 = (await store.CreateSuccessor(a1, "New wording", RequirementActor, RequirementTime)).Value;
        await ParentDraft(store, other.InternalId, "A"); await ParentDraft(store, other.InternalId, "Q-ONLY");
        area = (await areas.Save(Guid.NewGuid(), area, area.Area.Retire(), RequirementActor, RequirementTime,
            () => Task.FromResult(true))).Value;

        await using var host = RequirementMcpHost(products, store, store, db.ConnectionString);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        await using var services = HierarchyServices(scope.ServiceProvider, principal);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var before = await HierarchyEvidence(store, db.ConnectionString, product.InternalId);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RequirementListPage), new() { [nameof(RequirementListPage.ProductId)] = "P" });
            Assert.Equal(["A", "E"], HierarchyIds(renderer));
            Assert.DoesNotContain("Q-ONLY", renderer.Text());
            Assert.Contains("v2", renderer.Text()); Assert.Contains("v1", renderer.Text());
            var button = renderer.Elements("button").Single(a => Equals(a.GetValueOrDefault("aria-label"), "Expand A branch"));
            Assert.Equal("false", button["aria-expanded"]);
            Assert.Equal("button", button["type"]); // Native button: keyboard focus and Enter/Space activation.
            Assert.False(button.ContainsKey("disabled"));
            Assert.False(button.TryGetValue("tabindex", out var tab) && Equals(tab, "-1"));
            await renderer.ActivateBranch("Expand A branch");
            Assert.Equal(["A", "C", "S", "E"], HierarchyIds(renderer));
            Assert.Contains("Child of", renderer.Text());
            var exact = renderer.Components<MudLink>().Where(link => link.Href == $"/requirements/details?id={a1.Requirement.InternalId}&version={a1.Requirement.VersionId}").ToArray();
            Assert.Equal(2, exact.Length); // Both C and S retain the same exact A v1 parent endpoint.
            Assert.Contains("A · v1", renderer.Text());
            Assert.Contains(renderer.Components<MudLink>(), link => link.Href == $"/requirements/details?id={a2.Requirement.InternalId}");
            Assert.DoesNotContain(a1.Requirement.InternalId.ToString(), renderer.Text());
            Assert.DoesNotContain(a1.Requirement.VersionId.ToString(), renderer.Text());
            await renderer.ActivateBranch("Expand C branch"); await renderer.ActivateBranch("Expand D branch");
            Assert.Equal(["A", "C", "D", "G", "S", "E"], HierarchyIds(renderer));
            Assert.DoesNotContain(renderer.Elements("button"), attrs => Equals(attrs.GetValueOrDefault("aria-label"), "Expand G branch"));
            await renderer.ActivateBranch("Collapse A branch");
            await renderer.ActivateBranch("Expand E branch");
            Assert.Equal(["A", "E", "B"], HierarchyIds(renderer));
            await renderer.ActivateBranch("Expand A branch");
            Assert.Equal(["A", "C", "D", "G", "S", "E", "B"], HierarchyIds(renderer));
            await renderer.ActivateBranch("Collapse A branch");

            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("dEeP tArGeT");
            Assert.Equal(["A", "C", "D", "G"], HierarchyIds(renderer));
            var filtered = HierarchyRows(renderer);
            Assert.All(filtered.Take(3), row => Assert.True(row.IsContext)); Assert.False(filtered[^1].IsContext);
            Assert.Contains("Ancestor context — not a match", renderer.Text());
            Assert.All(renderer.Components<MudIconButton>().Where(b => b.UserAttributes?.ContainsKey("aria-expanded") == true), b => Assert.True(b.Disabled));
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Software);
            await renderer.Components<MudSelect<RequirementStatus?>>().Single().ValueChanged.InvokeAsync(RequirementStatus.InReview);
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(area.Area.InternalId);
            Assert.Equal(["A", "C", "D", "G"], HierarchyIds(renderer));
            await renderer.Components<MudSelect<RequirementStatus?>>().Single().ValueChanged.InvokeAsync(RequirementStatus.Approved);
            Assert.Empty(HierarchyIds(renderer)); Assert.Contains("No Requirements match", renderer.Text());
            await renderer.Components<MudSelect<RequirementStatus?>>().Single().ValueChanged.InvokeAsync(RequirementStatus.InReview);
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(Guid.Empty);
            Assert.Empty(HierarchyIds(renderer));
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(null);
            await renderer.Components<MudSelect<RequirementStatus?>>().Single().ValueChanged.InvokeAsync(null);
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(null);
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("");
            Assert.Equal(["A", "E", "B"], HierarchyIds(renderer)); // Forced filter expansion did not overwrite choices.
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("Title A");
            Assert.Equal(["A"], HierarchyIds(renderer)); // A parent match does not expose unrelated children.
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("missing");
            Assert.Contains("No Requirements match", renderer.Text());
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync(null!);
            await renderer.Mount(typeof(RequirementDetails), new()
            {
                [nameof(RequirementDetails.Id)] = a1.Requirement.InternalId,
                [nameof(RequirementDetails.VersionId)] = a1.Requirement.VersionId
            });
            Assert.Contains("v1", renderer.Text()); Assert.DoesNotContain("Successor creation reason: New wording", renderer.Text());
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = a1.Requirement.InternalId });
            Assert.Contains("Successor creation reason: New wording", renderer.Text());
            await renderer.Mount(typeof(RequirementListPage), new() { [nameof(RequirementListPage.ProductId)] = "EMPTY" });
            Assert.Empty(HierarchyIds(renderer)); Assert.Contains("No Requirements yet", renderer.Text());
            await renderer.Navigate(new() { [nameof(RequirementListPage.ProductId)] = "Q" });
            Assert.Equal(["A", "Q-ONLY"], HierarchyIds(renderer)); Assert.DoesNotContain("Deep target", renderer.Text());
        });
        Assert.Equal(before, await HierarchyEvidence(store, db.ConnectionString, product.InternalId));
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        before = await HierarchyEvidence(store, db.ConnectionString, product.InternalId);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RequirementListPage), new() { [nameof(RequirementListPage.ProductId)] = "P" });
            Assert.Contains("read-only", renderer.Text());
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.Href?.StartsWith("/requirements/create", StringComparison.Ordinal) == true);
            await renderer.ActivateBranch("Expand A branch"); await renderer.ActivateBranch("Expand C branch");
            Assert.Equal(["A", "C", "D", "S", "E"], HierarchyIds(renderer));
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("G");
            Assert.Equal(["A", "C", "D", "G"], HierarchyIds(renderer));
        });
        Assert.Equal(before, await HierarchyEvidence(store, db.ConnectionString, product.InternalId));
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    [Trait("Req", "REQ-REQ-002")]
    [Trait("Req", "REQ-SYS-AUTH-002")]
    public async Task RequirementHierarchyRevocationClearsCachedRowsOnExpansionAndFiltering()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var p = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(db.ConnectionString);
        var a = await ParentDraft(store, p.InternalId, "PRIVATE-A");
        var b = await ParentDraft(store, p.InternalId, "PRIVATE-B");
        await store.AddParent(b, ParentEndpoint(a), RequirementActor, RequirementTime);
        await using var host = RequirementMcpHost(products, store, store, db.ConnectionString);
        await host.Login(); await host.Replace();
        foreach (var action in new[] { "expand", "filter" })
        {
            var token = await host.Authenticate();
            var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
            using var scope = host.Factory.Services.CreateScope();
            await using var services = HierarchyServices(scope.ServiceProvider, principal);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RequirementListPage), new() { [nameof(RequirementListPage.ProductId)] = "P" });
                Assert.Equal(["PRIVATE-A"], HierarchyIds(renderer));
                await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(
                    principal.FindFirstValue(SessionTicketStore.SessionClaim)!);
                if (action == "expand") await renderer.ActivateBranch("Expand PRIVATE-A branch");
                else await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("PRIVATE-B");
                Assert.Contains("Access denied", renderer.Text()); Assert.DoesNotContain("PRIVATE-", renderer.Text());
                Assert.Empty(renderer.Components<MudTable<RequirementHierarchyRow>>());
            });
            Assert.Empty(renderer.Errors);
            // Re-login provides a new live session for the second scenario.
            if (action == "expand") await host.Login("Replacement123!", "/home");
        }
    }

    [PostgresFact]
    [Trait("Req", "REQ-REQ-002")]
    public async Task RequirementHierarchyReadFailuresNeverPublishPartialOrMisleadingTrees()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var p = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(db.ConnectionString);
        var a = await ParentDraft(store, p.InternalId, "A"); var b = await ParentDraft(store, p.InternalId, "B");
        var reads = new HierarchyParentReads(store);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<IProductStore>(products); s.AddSingleton<IProductAreaStore>(new PostgresProductAreaStore(db.ConnectionString));
            s.AddSingleton<IRequirementStore>(store); s.AddSingleton<IRequirementChangeTransaction>(store); s.AddSingleton<IRequirementLinkStore>(reads);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        await using var services = HierarchyServices(scope.ServiceProvider, principal);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var before = await HierarchyEvidence(store, db.ConnectionString, p.InternalId);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            foreach (var mode in new[] { "failure", "missing-parent", "missing-version", "cycle" })
            {
                reads.Read = (id, _) => mode switch
                {
                    "failure" when id == b.Requirement.InternalId => Result.Failure<Maybe<RequirementParentLink>>("Hierarchy read unavailable."),
                    "missing-parent" when id == b.Requirement.InternalId => Result.Success(Maybe<RequirementParentLink>.From(new RequirementParentLink(Guid.NewGuid(), Guid.NewGuid()))),
                    "missing-version" when id == b.Requirement.InternalId => Result.Success(Maybe<RequirementParentLink>.From(new RequirementParentLink(a.Requirement.InternalId, Guid.NewGuid()))),
                    "cycle" => Result.Success(Maybe<RequirementParentLink>.From(ParentEndpoint(id == a.Requirement.InternalId ? b : a))),
                    _ => Result.Success(Maybe<RequirementParentLink>.None)
                };
                await renderer.Mount(typeof(RequirementListPage), new() { [nameof(RequirementListPage.ProductId)] = "P" });
                Assert.Empty(renderer.Components<MudTable<RequirementHierarchyRow>>());
                Assert.DoesNotContain("No Requirements yet", renderer.Text()); Assert.DoesNotContain("Root Requirement", renderer.Text());
                Assert.Contains(mode switch { "failure" => "Hierarchy read unavailable", "missing-parent" => "incomplete", "missing-version" => "unavailable", _ => "completely" }, renderer.Text());
            }
        });
        Assert.Equal(before, await HierarchyEvidence(store, db.ConnectionString, p.InternalId));
        Assert.Empty(renderer.Errors);
    }

    private static ServiceProvider HierarchyServices(IServiceProvider scope, ClaimsPrincipal principal) =>
        new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(new RequirementNavigation())
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.GetRequiredService<ProductOperations>()).AddSingleton(scope.GetRequiredService<ProductAreaOperations>())
            .AddSingleton(scope.GetRequiredService<RequirementOperations>()).BuildServiceProvider();

    private static RequirementHierarchyRow[] HierarchyRows(RequirementRenderer renderer) =>
        renderer.Components<MudTable<RequirementHierarchyRow>>().Single().Items!.ToArray();
    private static IEnumerable<string> HierarchyIds(RequirementRenderer renderer) =>
        HierarchyRows(renderer).Select(row => row.Item.Snapshot.Requirement.Id);

    private static async Task<string> HierarchyEvidence(PostgresRequirementStore store, string connection, Guid product)
    {
        var evidence = new List<object>();
        foreach (var selected in (await store.List(product)).Value)
        foreach (var row in (await store.Versions(selected.Requirement.InternalId)).Value)
        {
            var parent = (await store.Parent(row.Requirement.InternalId, row.Requirement.VersionId)).Value;
            evidence.Add(new { Row = row, Parent = parent.HasValue ? parent.Value : null,
                History = (await store.History(row.Requirement.InternalId)).Value });
        }
        return JsonSerializer.Serialize(new { Selected = (await store.List(product)).Value, Versions = evidence,
            Audit = await new PostgresAuditQuery(connection).FindAsync(new()) });
    }

    private sealed class HierarchyParentReads(PostgresRequirementStore store) : IRequirementLinkStore
    {
        internal Func<Guid, Guid, Result<Maybe<RequirementParentLink>>>? Read;
        public Task<Result<Maybe<RequirementParentLink>>> Parent(Guid id, Guid version, CancellationToken token = default) =>
            Read is null ? store.Parent(id, version, token) : Task.FromResult(Read(id, version));
        public Task<Result<IReadOnlyList<RequirementVersionReference>>> IncomingUserNeed(Guid id, Guid version, CancellationToken token = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<RequirementVersionReference>>> Children(Guid id, Guid version, CancellationToken token = default) => throw new NotSupportedException();
        public Task<Result<IReadOnlyList<RequirementUserNeedLink>>> Links(Guid id, Guid version, CancellationToken token = default) => throw new NotSupportedException();
        public Task<Result<RequirementSnapshot>> AddParent(RequirementSnapshot expected, RequirementParentLink target, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
        public Task<Result<RequirementSnapshot>> RemoveParent(RequirementSnapshot expected, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
        public Task<Result<RequirementSnapshot>> ReplaceParent(RequirementSnapshot expected, RequirementParentLink target, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
        public Task<Result<RequirementSnapshot>> RetargetLink(RequirementSnapshot expected, RequirementUserNeedLink target, RequirementUserNeedLink replacement, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
        public Task<Result<RequirementSnapshot>> ChangeLink(RequirementSnapshot expected, RequirementUserNeedLink target, bool add, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
    }
}
#pragma warning restore BL0006
