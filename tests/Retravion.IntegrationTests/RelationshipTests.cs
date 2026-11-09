using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
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
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Relationships;
using Retravion.Web.Features.Requirement;
using Retravion.Web.Features.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private sealed record RelationshipData(Retravion.Domain.Product.Product Product,
        RequirementSnapshot Owner, RequirementSnapshot Successor, RequirementSnapshot Child,
        RequirementSnapshot Parent, RequirementSnapshot NewParent, UserNeedSnapshot Need, UserNeedSnapshot NewNeed);

    private static async Task<RelationshipData> RelationshipSetup(PostgresProductStore products,
        PostgresRequirementStore store, PostgresUserNeedStore needs)
    {
        var product = (await new CreateProduct(products).Execute("P", "Relationship Product", ProductKind.System, "")).Value;
        var need = (await needs.Create(Need.Create(product.InternalId, "N", "Need title", "Statement").Value, RequirementActor, RequirementTime)).Value;
        need = (await needs.Update(need, need.Need.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        need = (await needs.Update(need, need.Need.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var parent = await ParentApprove(store, await ParentDraft(store, product.InternalId, "Parent"));
        var owner = await ParentDraft(store, product.InternalId, "Owner");
        owner = (await store.ChangeLink(owner, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
        owner = (await store.AddParent(owner, ParentEndpoint(parent), RequirementActor, RequirementTime)).Value;
        owner = await ParentApprove(store, owner);
        var successor = (await store.CreateSuccessor(owner, "Successor owner", RequirementActor, RequirementTime)).Value;
        // Only the historical owner retains the need link. Both exact owner versions still reference parent v1.
        successor = (await store.ChangeLink(successor, Endpoint(need), false, RequirementActor, RequirementTime)).Value;
        var child = await ParentDraft(store, product.InternalId, "Child");
        child = (await store.AddParent(child, ParentEndpoint(owner), RequirementActor, RequirementTime)).Value;
        var newNeed = (await needs.CreateSuccessor(need, "Successor need", RequirementActor, RequirementTime)).Value;
        var newParent = (await store.CreateSuccessor(parent, "Successor parent", RequirementActor, RequirementTime)).Value;
        return new(product, owner, successor, child, parent, newParent, need, newNeed);
    }

    [Theory]
    [InlineData("Draft", false, true, "Draft owner eligible")]
    [InlineData("In Review", false, true, "Read-only: owner is In Review")]
    [InlineData("Approved", false, true, "Read-only: owner is Approved")]
    [InlineData("Draft", true, true, "Read-only: a newer owner version exists")]
    [InlineData("Draft", false, false, "Read-only: Product retired")]
    public void RelationshipOwnershipTextDescribesTheOwnerNotTheApprovedTarget(string status, bool newer, bool active, string expected)
    {
        var owner = new RelationshipNode("requirement", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "R", "Owner", 1, status, newer);
        var target = new RelationshipNode("user-need", Guid.NewGuid(), Guid.NewGuid(), owner.ProductInternalId, "N", "Target", 1, "Approved", false);
        var explanation = new RelationshipEdge(owner, target, "Addresses User Need").Ownership(active);
        Assert.Contains("Owner: Requirement R · v1", explanation);
        Assert.Contains(expected, explanation);
    }

    [PostgresFact]
    public async Task IncomingRelationshipQueriesRetainHistoricalOwnersAndExactTargetsWithoutWrites()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var baseline = (await audit.FindAsync(new())).Count;
        var history = (await store.History(d.Owner.Requirement.InternalId)).Value.Count;
        var fresh = new PostgresRequirementStore(database.ConnectionString);
        Assert.Equal(new RequirementVersionReference(d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId),
            Assert.Single((await fresh.IncomingUserNeed(d.Need.Need.InternalId, d.Need.Need.VersionId)).Value));
        Assert.Empty((await fresh.IncomingUserNeed(d.NewNeed.Need.InternalId, d.NewNeed.Need.VersionId)).Value);
        Assert.Empty((await fresh.IncomingUserNeed(Guid.NewGuid(), d.Need.Need.VersionId)).Value);
        Assert.Equal(new[] { d.Owner.Requirement.VersionId, d.Successor.Requirement.VersionId },
            (await fresh.Children(d.Parent.Requirement.InternalId, d.Parent.Requirement.VersionId)).Value.Select(x => x.VersionId));
        Assert.Empty((await fresh.Children(d.NewParent.Requirement.InternalId, d.NewParent.Requirement.VersionId)).Value);
        Assert.Empty((await fresh.Children(Guid.NewGuid(), d.Parent.Requirement.VersionId)).Value);
        Assert.Equal(d.Child.Requirement.VersionId,
            Assert.Single((await fresh.Children(d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId)).Value).VersionId);
        Assert.True((await products.Update(d.Product, d.Product.Retire().Value)).IsSuccess);
        Assert.Single((await fresh.IncomingUserNeed(d.Need.Need.InternalId, d.Need.Need.VersionId)).Value);
        Assert.Equal(2, (await fresh.Children(d.Parent.Requirement.InternalId, d.Parent.Requirement.VersionId)).Value.Count);
        Assert.Equal(history, (await store.History(d.Owner.Requirement.InternalId)).Value.Count);
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task RelationshipComponentsKeepExactFocusEquivalentViewsOwnershipAndAccess()
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
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var view = (await operations.Load(principal, "requirement", d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId)).Value;
        Assert.Equal(d.Owner.Requirement.VersionId, view.Center.VersionId);
        Assert.Equal(3, view.Edges.Count);
        Assert.True(view.Center.HasNewerVersion);
        Assert.Contains(view.Edges, e => e.Target.VersionId == d.Need.Need.VersionId && e.Target.HasNewerVersion);
        Assert.Contains(view.Edges, e => e.Target.VersionId == d.Parent.Requirement.VersionId && e.Target.HasNewerVersion);
        Assert.Contains(view.Edges, e => e.Owner.VersionId == d.Child.Requirement.VersionId && e.Ownership(true).Contains("Draft owner eligible"));
        Assert.Contains(view.Edges, e => e.Owner.VersionId == d.Owner.Requirement.VersionId && e.Ownership(true).Contains("newer owner version"));
        Assert.True((await operations.Load(principal, "requirement", d.Owner.Requirement.InternalId, null)).IsFailure);
        Assert.True((await operations.Load(principal, "test-case", d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId)).IsFailure);
        Assert.True((await operations.Load(principal, "requirement", d.Owner.Requirement.InternalId, d.Successor.Requirement.VersionId)).IsSuccess);
        Assert.True((await operations.Load(principal, "requirement", d.Owner.Requirement.InternalId, d.Parent.Requirement.VersionId)).IsFailure);
        var baseline = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        // Follow a real graph destination through HTTP routing as well as exercising circuit callbacks below.
        var graphPage = await host.Browser.GetStringAsync(view.Center.LinksPath);
        Assert.Contains("Center: Requirement Owner", graphPage);
        Assert.Contains("Links</h1>", graphPage);
        var connectedNode = view.Edges.Single(e => e.Target.Kind == "user-need").Target;
        Assert.Contains(connectedNode.VersionId.ToString(), graphPage);
        var focusedPage = await host.Browser.GetStringAsync(connectedNode.LinksPath);
        Assert.Contains("Center: User Need N", focusedPage);
        Assert.DoesNotContain("Requirement Child", focusedPage);
        var missingPage = await host.Browser.GetStringAsync($"/products/links?kind=user-need&id={d.Need.Need.InternalId}&version={Guid.NewGuid()}");
        Assert.Contains("User Need version not found.", missingPage);
        Assert.DoesNotContain("Need title", missingPage);
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(new RequirementNavigation())
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(operations)
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RequirementOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<UserNeedOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = d.Owner.Requirement.InternalId, [nameof(RequirementDetails.VersionId)] = d.Owner.Requirement.VersionId });
            Assert.Contains(renderer.Components<MudButton>(), b => b.Href == view.Center.LinksPath);
            await renderer.Mount(typeof(UserNeedDetails), new() { [nameof(UserNeedDetails.Id)] = d.Need.Need.InternalId, [nameof(UserNeedDetails.VersionId)] = d.Need.Need.VersionId });
            var needView = (await operations.Load(principal, "user-need", d.Need.Need.InternalId, d.Need.Need.VersionId)).Value;
            Assert.Contains(renderer.Components<MudButton>(), b => b.Href == needView.Center.LinksPath);
            Assert.Single(needView.Edges);
            await renderer.Mount(typeof(RelationshipLinks), Parameters(view.Center));
            Assert.Empty(renderer.Errors);
            Assert.Contains("Center: Requirement Owner · v1", renderer.Text());
            foreach (var text in new[] { "Child", "Parent", "Need title", "Addresses User Need", "Child → parent Requirement", "Relationship Product", "Newer version available", "Draft owner eligible", "newer owner version", "Incoming", "Outgoing" })
                Assert.Contains(text, renderer.Text());
            Assert.DoesNotContain(d.NewNeed.Need.VersionId.ToString(), string.Join(" ", renderer.Components<MudLink>().Select(l => l.Href)));
            var graphLinks = renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray();
            await renderer.Click("Table view");
            Assert.Empty(renderer.Errors);
            Assert.Single(renderer.Components<MudTable<RelationshipEdge>>());
            Assert.Equal(graphLinks, renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray());
            foreach (var edge in view.Edges) Assert.Contains(edge.Ownership(true), renderer.Text());
            // Selection inspects without refocusing; the explicit Focus here href navigates exactly.
            var connected = view.Edges.Single(e => e.Target.Kind == "user-need").Target;
            await ExplorationClick(renderer, "Inspect " + connected.Label);
            Assert.Contains("Center: Requirement Owner · v1", renderer.Text());
            Assert.Contains(renderer.Components<MudLink>(), l => l.Href == connected.LinksPath);
            await renderer.Mount(typeof(RelationshipLinks), Parameters(connected));
            Assert.Contains("Center: User Need N · v1", renderer.Text());
            Assert.DoesNotContain("Requirement Child", renderer.Text()); // direct-only, no accidental indirect loading
            await ExplorationClick(renderer, "Inspect " + view.Center.Label);
            Assert.Contains(renderer.Components<MudLink>(), l => l.Href == view.Center.LinksPath);
            await renderer.Mount(typeof(RelationshipLinks), Parameters(view.Center));
            await renderer.Click("Table view");
            Assert.True((await products.Update(d.Product, d.Product.Retire().Value)).IsSuccess);
            await renderer.Click("Graph view");
            Assert.Contains("Read-only: Product retired", renderer.Text());
            Assert.Contains("Need title", renderer.Text());
            await renderer.Click("Table view");
            Assert.Contains("Read-only: Product retired", renderer.Text());
            await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(principal.FindFirstValue(SessionTicketStore.SessionClaim)!);
            await renderer.Click("Graph view");
            Assert.Contains("Access denied", renderer.Text()); Assert.DoesNotContain("Need title", renderer.Text());
            Assert.Empty(renderer.Components<MudLink>());
            await renderer.Mount(typeof(RelationshipLinks), Parameters(connected));
            Assert.Contains("Access denied", renderer.Text()); Assert.DoesNotContain("Need title", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
        Assert.Equal(baseline, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
    }

    private static Dictionary<string, object?> Parameters(RelationshipNode node) => new()
    {
        [nameof(RelationshipLinks.Kind)] = node.Kind, [nameof(RelationshipLinks.Id)] = node.InternalId,
        [nameof(RelationshipLinks.VersionId)] = node.VersionId
    };
}
