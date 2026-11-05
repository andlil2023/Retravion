using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Application.UserNeed;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Relationships;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static ServiceProvider ExplorationServices(RelationshipOperations operations, ClaimsPrincipal actor) =>
        new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(new RequirementNavigation())
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(actor))
            .AddSingleton(operations).BuildServiceProvider();

    // The same exact node may appear at multiple edge endpoints. Either copy controls the shared state.
    private static Task ExplorationClick(RequirementRenderer renderer, string label) => renderer.Components<MudButton>()
        .First(b => b.UserAttributes?.TryGetValue("aria-label", out var value) == true && Equals(value, label))
        .OnClick.InvokeAsync(new MouseEventArgs());

    [PostgresFact]
    public async Task ExpansionTraversesMultiHopCyclesOnceAndCollapsePrunesOnlyUnreachableBranches()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Cycle Product", ProductKind.System, "")).Value;
        var n1 = (await needs.Create(Need.Create(product.InternalId, "N1", "First need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var n2 = (await needs.Create(Need.Create(product.InternalId, "N2", "Indirect need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var r1 = await ParentDraft(store, product.InternalId, "R1");
        var r2 = await ParentDraft(store, product.InternalId, "R2");
        foreach (var need in new[] { n1, n2 })
        {
            r1 = (await store.ChangeLink(r1, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
            r2 = (await store.ChangeLink(r2, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
        }
        await using var host = new McpTestHost(services =>
        {
            RegisterRelationshipCases(services, database.ConnectionString);
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRequirementStore>(store); services.AddSingleton<IRequirementLinkStore>(store);
            services.AddSingleton<IRequirementChangeTransaction>(store);
            services.AddSingleton<IUserNeedStore>(needs); services.AddSingleton<IUserNeedChangeTransaction>(needs);
        });
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var baseline = (await audit.FindAsync(new())).Count;
        var history = (await store.History(r1.Requirement.InternalId)).Value.Count;
        var root = (await operations.Load(actor, "user-need", n1.Need.InternalId, n1.Need.VersionId)).Value.Center;
        var expanded = new[] { r1.Requirement.VersionId, n2.Need.VersionId, r2.Requirement.VersionId };
        var cycle = (await operations.Explore(actor, root.Kind, root.InternalId, root.VersionId,
            expanded.Concat(expanded).Append(root.VersionId).ToArray())).Value;
        Assert.Equal(root.VersionId, cycle.Root.Center.VersionId);
        Assert.Equal(4, cycle.Neighborhoods.Count);
        Assert.Equal(4, cycle.Edges.Count);
        Assert.Equal(3, cycle.Expanded.Count);
        var shared = (await operations.Explore(actor, root.Kind, root.InternalId, root.VersionId,
            new[] { n2.Need.VersionId, r2.Requirement.VersionId })).Value;
        Assert.Equal(4, shared.Edges.Count); // N2 is still reachable through R2.
        var collapsed = (await operations.Explore(actor, root.Kind, root.InternalId, root.VersionId,
            new[] { n2.Need.VersionId })).Value;
        Assert.Empty(collapsed.Expanded); // N2 is not directly reachable from the root.
        Assert.Equal(2, collapsed.Edges.Count);

        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
            Assert.DoesNotContain("Indirect need", renderer.Text());
            await renderer.Click("Expand Requirement R1 · v1");
            Assert.Contains("Indirect need", renderer.Text());
            Assert.Contains("Center: User Need N1 · v1", renderer.Text());
            await ExplorationClick(renderer, "Expand User Need N2 · v1");
            await ExplorationClick(renderer, "Expand Requirement R2 · v1");
            Assert.Contains("4 relationship(s) in the explored view", renderer.Text());
            var graphLinks = renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray();
            await renderer.Click("Table view");
            Assert.Equal(4, Assert.Single(renderer.Components<MudTable<RelationshipEdge>>()).Items!.Count());
            Assert.Equal(graphLinks, renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray());
            await ExplorationClick(renderer, "Collapse Requirement R1 · v1");
            Assert.Contains("Indirect need", renderer.Text());
            await ExplorationClick(renderer, "Collapse Requirement R2 · v1");
            Assert.DoesNotContain("Indirect need", renderer.Text());
            await ExplorationClick(renderer, "Expand Requirement R1 · v1");
            await renderer.Click("Graph view");
            Assert.Contains("Indirect need", renderer.Text()); // switching views preserves exploration
            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            await ExplorationClick(renderer, "Expand User Need N2 · v1");
            Assert.Contains("Read-only: Product retired", renderer.Text());
            await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(actor.FindFirstValue(SessionTicketStore.SessionClaim)!);
            await ExplorationClick(renderer, "Collapse User Need N2 · v1");
            Assert.Contains("Access denied", renderer.Text());
            Assert.DoesNotContain("Indirect need", renderer.Text());
            Assert.Empty(renderer.Components<MudLink>());
        });
        Assert.Empty(renderer.Errors);
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
        Assert.Equal(history, (await store.History(r1.Requirement.InternalId)).Value.Count);
        Assert.Equal(2, (await store.Links(r1.Requirement.InternalId, r1.Requirement.VersionId)).Value.Count);
    }

    [PostgresFact]
    public async Task ExpansionKeepsHistoricalExactVersionsAndNeverSubstitutesSuccessors()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        await using var host = new McpTestHost(services =>
        {
            RegisterRelationshipCases(services, database.ConnectionString);
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRequirementStore>(store); services.AddSingleton<IRequirementLinkStore>(store);
            services.AddSingleton<IRequirementChangeTransaction>(store);
            services.AddSingleton<IUserNeedStore>(needs); services.AddSingleton<IUserNeedChangeTransaction>(needs);
        });
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var view = (await operations.Explore(actor, "user-need", d.Need.Need.InternalId, d.Need.Need.VersionId,
            new[] { d.Owner.Requirement.VersionId, d.Parent.Requirement.VersionId })).Value;
        Assert.Equal(d.Need.Need.VersionId, view.Root.Center.VersionId);
        Assert.Equal(4, view.Edges.Count);
        Assert.Contains(view.Edges, e => e.Owner.VersionId == d.Successor.Requirement.VersionId);
        Assert.Contains(view.Edges, e => e.Owner.VersionId == d.Child.Requirement.VersionId);
        Assert.DoesNotContain(view.Edges, e => e.Target.VersionId == d.NewNeed.Need.VersionId || e.Target.VersionId == d.NewParent.Requirement.VersionId);
        Assert.True(view.Neighborhoods.Single(n => n.Center.VersionId == d.Owner.Requirement.VersionId).Center.HasNewerVersion);
    }

    [PostgresFact]
    public async Task RelationshipPagesBoundBothViewsAndDiscloseOverflowWithoutLosingRelationships()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Paged Product", ProductKind.System, "")).Value;
        var parent = await ParentDraft(store, product.InternalId, "Root");
        for (var i = 0; i < 21; i++)
        {
            var child = await ParentDraft(store, product.InternalId, $"Child-{i:D2}");
            Assert.True((await store.AddParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime)).IsSuccess);
        }
        await using var host = new McpTestHost(services =>
        {
            RegisterRelationshipCases(services, database.ConnectionString);
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRequirementStore>(store); services.AddSingleton<IRequirementLinkStore>(store);
            services.AddSingleton<IRequirementChangeTransaction>(store);
            services.AddSingleton<IUserNeedStore>(needs); services.AddSingleton<IUserNeedChangeTransaction>(needs);
        });
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var root = (await operations.Load(actor, "requirement", parent.Requirement.InternalId, parent.Requirement.VersionId)).Value.Center;
        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
            Assert.Contains("Showing 20 of 21", renderer.Text());
            Assert.Contains("More relationships are on other pages", renderer.Text());
            Assert.Contains("Neighborhood not expanded", renderer.Text());
            var first = renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray();
            await renderer.Click("Table view");
            Assert.Equal(20, Assert.Single(renderer.Components<MudTable<RelationshipEdge>>()).Items!.Count());
            Assert.Equal(first, renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray());
            await renderer.Click("Next relationships");
            Assert.Contains("Page 2 of 2", renderer.Text());
            Assert.Contains("Showing 1 of 21", renderer.Text());
            var last = renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray();
            Assert.Equal(21, first.Concat(last).Where(h => h!.StartsWith("/products/links") && h != root.LinksPath).Distinct().Count());
            await renderer.Click("Graph view");
            Assert.Equal(last, renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray());
            await renderer.Click("Previous relationships");
            Assert.Equal(first, renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray());
        });
        Assert.Empty(renderer.Errors);
    }
}
