using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Services;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Revision;
using Retravion.Web.Features.Relationships;
using Retravion.Web.Features.Revision;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Native details attributes through the existing component renderer.
public sealed partial class PostgresProductTests
{
    private static async Task MountMembership(CaseRenderer renderer, string product, Guid id, bool table = true)
    {
        await renderer.Mount(typeof(RevisionLinks), new() { [nameof(RevisionLinks.ProductId)] = product, [nameof(RevisionLinks.Id)] = id });
        if (table) await renderer.Click("Table view");
    }
    private static Task InspectMembership(CaseRenderer renderer, string label) => renderer.Components<MudButton>()
        .First(b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString() == "Inspect " + label).OnClick.InvokeAsync(new MouseEventArgs());
    private static async Task ChooseRevisionMember(CaseRenderer renderer, RevisionSelection endpoint)
    {
        await renderer.Components<MudButton>().Single(b => b.UserAttributes?.GetValueOrDefault("data-version")?.ToString() == endpoint.VersionId.ToString()).OnClick.InvokeAsync(new MouseEventArgs());
        await renderer.Click("Review selection");
    }
    private static async Task AddRevisionMember(CaseRenderer renderer, RevisionSelection endpoint)
    {
        await renderer.Click("Add link"); await ChooseRevisionMember(renderer, endpoint); await renderer.Click("Confirm selection");
    }
    private static Task RevisionTab(CaseRenderer renderer, int index) => renderer.Components<MudTabs>().Single().ActivePanelIndexChanged.InvokeAsync(index);

    [PostgresFact]
    [Trait("Req", "REQ-REV-002")]
    [Trait("Req", "REQ-REV-004")]
    [Trait("Req", "REQ-REV-006")]
    public async Task RevisionWorkspaceDistinguishesSelectedAndIndirectFrozenMembershipAndKeepsHistoryCollapsed()
    {
        await using var db = await Database.Create(); var graph = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<Retravion.Application.Product.IProductStore>(new Retravion.Infrastructure.Product.PostgresProductStore(db.ConnectionString));
            s.AddSingleton<IRevisionStore>(store); s.AddSingleton<IRevisionChangeTransaction>(store);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        await using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime, CaseJS>().AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(new CaseNavigation())
            .AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(new CaseAuthentication(actor))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RevisionOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<Retravion.Web.Features.Product.ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var audit = new PostgresAuditQuery(db.ConnectionString); var before = (await audit.FindAsync(new())).Count;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = graph.Product.Id, [nameof(RevisionDetails.Id)] = graph.Row.Value.InternalId });
            Assert.Empty(renderer.Components<MudSelect<string>>());
            Assert.Contains("kind=revision", renderer.Button("Manage links").Href);
            Assert.DoesNotContain("Revision history", renderer.Text());
            await RevisionTab(renderer, 1);
            var baseline = Assert.Single(renderer.Components<MudTable<RevisionBaselineMember>>()).Items!.ToArray();
            Assert.Equal(8, baseline.Length); Assert.Contains(baseline, m => !m.Explicit);
            var feature = Assert.Single(baseline, m => m.Endpoint.Kind == RevisionEntityKind.Feature);
            Assert.Equal("Feature F-001 · v1", RevisionPresentation.Label(feature));
            Assert.Contains(renderer.Components<MudLink>(), l => l.Href == RevisionPresentation.EndpointPath(feature.Endpoint) && l.Href.StartsWith("/features/details"));
            Assert.Contains("Selected", renderer.Text()); Assert.Contains("Included through links", renderer.Text());
            await renderer.Field("Search contents by ID or title").ValueChanged.InvokeAsync("not a record");
            Assert.Empty(Assert.Single(renderer.Components<MudTable<RevisionBaselineMember>>()).Items!);
            await RevisionTab(renderer, 2);
            Assert.Contains("Revision history", renderer.Text());
            Assert.All(renderer.Elements("details"), attrs => Assert.DoesNotContain(attrs, a => a.AttributeName == "open"));
            await MountMembership(renderer, graph.Product.Id, graph.Row.Value.InternalId, false);
            Assert.Contains("not inter-record link topology", renderer.Text());
            var indirect = (await store.Baseline(graph.Product.InternalId, graph.Row.Value.InternalId)).Value.Members.First(m => !m.Explicit);
            await InspectMembership(renderer, RevisionPresentation.Label(indirect));
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString()?.StartsWith("Remove") == true);
            Assert.DoesNotContain("Remove selection", renderer.Text());
            Assert.DoesNotContain("Replace selection", renderer.Text());
            Assert.Contains("cannot be removed", renderer.Text());
            Assert.Equal(before, (await audit.FindAsync(new())).Count);
        });
        Assert.Empty(renderer.Errors);
        var released = (await ReleaseNow(store, await ReleasePreview(store, graph.Row))).Value;
        var frozen = (await store.Baseline(graph.Product.InternalId, released.Value.InternalId)).Value;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = graph.Product.Id, [nameof(RevisionDetails.Id)] = released.Value.InternalId });
            Assert.Contains("kind=revision", renderer.Button("View baseline").Href);
            Assert.DoesNotContain("Manage links", renderer.Text()); Assert.DoesNotContain("Review release", renderer.Text());
            await MountMembership(renderer, graph.Product.Id, released.Value.InternalId);
            Assert.Contains("Frozen release evidence", renderer.Text());
            Assert.Equal(frozen.Members, Assert.Single(renderer.Components<MudTable<RevisionBaselineMember>>()).Items!);
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.OnClick.HasDelegate && b.Variant == Variant.Filled);
            foreach (var member in frozen.Members)
            {
                await InspectMembership(renderer, RevisionPresentation.Label(member));
                Assert.Contains(member.Title, renderer.Text());
                Assert.DoesNotContain(renderer.Components<MudLink>(), l => l.Href?.StartsWith("/products/links?kind=") == true);
            }
        });
        Assert.Empty(renderer.Errors);
        var response = await host.Browser.GetStringAsync(RevisionPresentation.WorkspacePath(graph.Product.Id, released.Value.InternalId));
        Assert.Contains("Frozen release evidence", response); // Actual shared route, not a parallel standalone page.
        var retiredDraft = await RevCreate(store, graph.Product.InternalId, "Retired readiness");
        foreach (var endpoint in graph.Row.Value.Selections)
            retiredDraft = (await store.Select(Guid.NewGuid(), retiredDraft, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
        var productStore = new Retravion.Infrastructure.Product.PostgresProductStore(db.ConnectionString);
        Assert.True((await productStore.Update(graph.Product, graph.Product.Retire().Value)).IsSuccess);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = graph.Product.Id, [nameof(RevisionDetails.Id)] = retiredDraft.Value.InternalId });
            Assert.Contains("Release blocked", renderer.Text());
            Assert.Contains("All 8 baseline records are Approved", renderer.Text());
            Assert.DoesNotContain("Ready for release review", renderer.Text());
            Assert.DoesNotContain("0 records need approval", renderer.Text());
            Assert.False(renderer.HasButton("Review release"));
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    [Trait("Req", "REQ-REV-002")]
    [Trait("Req", "REQ-REV-003")]
    [Trait("Req", "REQ-REV-006")]
    public async Task RevisionMembershipUiSearchesAllTypesRejectsStaleAndRevokedConfirmationsWithoutLosingExactEndpoints()
    {
        await using var db = await Database.Create(); var graph = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var products = new Retravion.Infrastructure.Product.PostgresProductStore(db.ConnectionString);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            s.AddSingleton<IRevisionStore>(store); s.AddSingleton<IRevisionChangeTransaction>(store);
            s.AddSingleton<Retravion.Application.AuditTrail.IAuditSubmission>(new PostgresAuditSubmission(db.ConnectionString));
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<Microsoft.JSInterop.IJSRuntime, CaseJS>().AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(new CaseNavigation())
            .AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(new CaseAuthentication(actor))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RevisionOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<Retravion.Web.Features.Product.ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var audit = new PostgresAuditQuery(db.ConnectionString);
        var endpoints = new[] {
            new RevisionSelection(RevisionEntityKind.UserNeed, graph.Need1.Need.InternalId, graph.Need1.Need.VersionId, 1),
            new RevisionSelection(RevisionEntityKind.Requirement, graph.Parent1.Requirement.InternalId, graph.Parent1.Requirement.VersionId, 1),
            new RevisionSelection(RevisionEntityKind.TestCase, graph.Case1.TestCase.InternalId, graph.Case1.TestCase.VersionId, 1),
            new RevisionSelection(RevisionEntityKind.Feature, graph.Feature.Feature.InternalId, graph.Feature.Feature.VersionId, 1) };
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            foreach (var endpoint in endpoints)
            {
                var row = await RevCreate(store, graph.Product.InternalId, endpoint.Kind.ToString());
                await MountMembership(renderer, graph.Product.Id, row.Value.InternalId, false);
                await renderer.Click("Add link");
                var choice = renderer.Components<MudSelectItem<RevisionEntityKind?>>().Single(i => i.Value == endpoint.Kind);
                await renderer.Components<MudSelect<RevisionEntityKind?>>().Single().ValueChanged.InvokeAsync(choice.Value);
                var available = renderer.Components<MudButton>().Where(b => b.UserAttributes?.ContainsKey("data-version") == true).ToArray();
                Assert.NotEmpty(available);
                var baseline = (await audit.FindAsync(new())).Count;
                await ChooseRevisionMember(renderer, endpoint);
                Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
                Assert.True((await store.Edit(Guid.NewGuid(), row, row.Value.Identifier, "changed elsewhere", RevActor, RevTime, RevAllowed)).IsSuccess);
                baseline = (await audit.FindAsync(new())).Count;
                await renderer.Click("Confirm selection");
                Assert.Contains("Retrieve revision", renderer.Text());
                Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
                Assert.Empty((await store.Retrieve(graph.Product.InternalId, row.Value.InternalId)).Value.Record!.Value.Selections);
                await renderer.Click("Retrieve revision");
                await AddRevisionMember(renderer, endpoint);
                Assert.Equal(endpoint, Assert.Single((await store.Retrieve(graph.Product.InternalId, row.Value.InternalId)).Value.Record!.Value.Selections));
                Assert.Equal(baseline + 1, (await audit.FindAsync(new())).Count);
                if (endpoint.Kind == RevisionEntityKind.Feature)
                {
                    await InspectMembership(renderer, "Feature F-001 · v1");
                    Assert.Contains(renderer.Components<MudLink>(), l => l.Href == $"/products/links?kind=feature&id={endpoint.InternalId}&version={endpoint.VersionId}");
                }
            }
            var deniedRow = await RevCreate(store, graph.Product.InternalId, "Denied callback");
            await MountMembership(renderer, graph.Product.Id, deniedRow.Value.InternalId);
            await renderer.Click("Add link"); await ChooseRevisionMember(renderer, endpoints[2]);
            var callback = renderer.Button("Confirm selection").OnClick;
            var ticket = actor.FindFirst(Retravion.Web.Features.Identity.Logout.SessionTicketStore.SessionClaim)!.Value;
            await host.Factory.Services.GetRequiredService<Retravion.Web.Features.Identity.Logout.SessionTicketStore>().RemoveAsync(ticket);
            var before = (await audit.FindAsync(new())).Count;
            await callback.InvokeAsync(new MouseEventArgs());
            Assert.Contains("Access denied", renderer.Text()); Assert.DoesNotContain("Selection added.", renderer.Text());
            var events = await audit.FindAsync(new());
            Assert.Equal(before + 1, events.Count);
            var denied = Assert.Single(events, e => e.Action == "revision.selection.add" && e.Outcome == Retravion.Application.AuditTrail.AuditActionOutcome.Failure);
            Assert.Equal("revision.selection.add", denied.Action);
            Assert.Equal(Retravion.Application.AuditTrail.AuditActionOutcome.Failure, denied.Outcome);
            Assert.IsType<Retravion.Application.AuditTrail.AuditTarget.Unknown>(denied.Target);
            await callback.InvokeAsync(new MouseEventArgs());
            Assert.Equal(before + 1, (await audit.FindAsync(new())).Count);
            Assert.Empty((await store.Retrieve(graph.Product.InternalId, deniedRow.Value.InternalId)).Value.Record!.Value.Selections);
        });
        Assert.Empty(renderer.Errors);
    }
}
