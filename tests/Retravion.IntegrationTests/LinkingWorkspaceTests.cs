using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using MudBlazor.Extensions;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Relationships;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    [Trait("Req", "REQ-LINK-004")]
    [Trait("Req", "REQ-LINK-005")]
    [Trait("Req", "REQ-LINK-006")]
    public async Task LinkingWorkspaceSeparatesSelectionFocusAndReviewedSearchInBothViews()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        var draft = await ParentDraft(store, d.Product.InternalId, "Workspace");
        draft = (await store.ChangeLink(draft, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
        await using var host = ManagementHost(products, store, needs, database.ConnectionString);
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var root = (await operations.Load(actor, "requirement", draft.Requirement.InternalId, draft.Requirement.VersionId)).Value.Center;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var baseline = (await audit.FindAsync(new())).Count;
        var viewportModule = await host.Browser.GetStringAsync("/Features/Relationships/RelationshipLinks.razor.js");
        Assert.Contains("export function centerFocus", viewportModule);
        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            foreach (var table in new[] { false, true })
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (table) await renderer.Click("Table view");
                else
                {
                    await renderer.Components<MudIconButton>().Single(b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString() == "Zoom out").OnClick.InvokeAsync(new MouseEventArgs());
                    await renderer.Components<MudIconButton>().Single(b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString() == "Expand diagram height").OnClick.InvokeAsync(new MouseEventArgs());
                    void AssertViewState()
                    {
                        Assert.Contains("90%", renderer.Text());
                        Assert.Contains(renderer.Components<MudIconButton>(), b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString() == "Restore diagram height");
                    }
                    AssertViewState();
                    await renderer.Click("Table view"); await renderer.Click("Graph view"); AssertViewState();
                    await ExplorationClick(renderer, "Expand User Need N · v1"); AssertViewState();
                    await ExplorationClick(renderer, "Collapse User Need N · v1"); AssertViewState();
                }
                Assert.All(renderer.Components<MudButton>().Where(b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString()?.StartsWith("Inspect ") == true), b =>
                {
                    Assert.Equal(Variant.Text, b.Variant);
                    if (table) Assert.Equal(Size.Small, b.Size);
                });
                Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString()?.StartsWith("Add outgoing") == true);
                await ExplorationClick(renderer, "Inspect User Need N · v1");
                Assert.Contains("Center: Requirement Workspace · v1", renderer.Text());
                Assert.Contains(renderer.Components<MudLink>(), l => l.Href == $"/products/links?kind=user-need&id={d.Need.Need.InternalId}&version={d.Need.Need.VersionId}");
                Assert.Contains(renderer.Components<MudLink>(), l => l.Href == $"/user-needs/details?id={d.Need.Need.InternalId}&version={d.Need.Need.VersionId}");
                Assert.Contains("Newer version available", renderer.Text());
                await ExplorationClick(renderer, "Inspect " + root.Label);
                await ManagementButton(renderer, "Add link for " + root.Label).OnClick.InvokeAsync(new MouseEventArgs());
                Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.UserAttributes?.GetValueOrDefault("aria-label")?.ToString() == "Add selected link");
                Assert.Equal(new[] { "need", "parent", "child", "test-case" }, renderer.Components<MudSelectItem<string>>().Select(i => i.Value));
                Assert.Equal("need", renderer.Components<MudSelect<string>>().Single().GetState(x => x.Value));
                var parentChoice = renderer.Components<MudSelectItem<string>>().Single(i => i.Value == "parent");
                await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync(parentChoice.Value);
                Assert.Equal("parent", renderer.Components<MudSelect<string>>().Single().GetState(x => x.Value));
                Assert.Contains(d.Parent.Requirement.VersionId, CandidateVersions(renderer));
                var needChoice = renderer.Components<MudSelectItem<string>>().Single(i => i.Value == "need");
                await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync(needChoice.Value);
                Assert.True(renderer.Components<MudButton>().Single(b => b.ChildContent is not null && b.Variant == Variant.Filled).Disabled);
                await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("nEeD TiTlE");
                Assert.Equal("nEeD TiTlE", renderer.Field("Search ID or title").GetState(x => x.Value));
                Assert.Equal(new[] { d.Need.Need.VersionId, d.NewNeed.Need.VersionId }.Order(), CandidateVersions(renderer).Order());
                var selected = renderer.Components<MudButton>().Single(b => b.UserAttributes?.GetValueOrDefault("data-version")?.ToString() == d.NewNeed.Need.VersionId.ToString());
                await selected.OnClick.InvokeAsync(new MouseEventArgs());
                await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("no matching entity");
                Assert.Empty(CandidateVersions(renderer));
                Assert.Contains("Selected: User Need N · v2", renderer.Text());
                Assert.Contains("No matching records", renderer.Text());
                Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
                await renderer.Click("Review link");
                Assert.Contains("Requirement Workspace · v1 → Addresses User Need → User Need N · v2", renderer.Text());
                Assert.Contains("Need title", renderer.Text());
                var plainText = System.Text.RegularExpressions.Regex.Replace(renderer.Text(), "<[^>]+>", "");
                foreach (var identity in new[] { root.InternalId, root.VersionId, root.ProductInternalId, d.NewNeed.Need.VersionId })
                    Assert.DoesNotContain(identity.ToString(), plainText);
                Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
                if (!table)
                {
                    await renderer.Click("Back");
                    Assert.Contains("Selected: User Need N · v2", renderer.Text());
                    await renderer.Click("Cancel add");
                    Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
                }
                else
                {
                    await renderer.Click("Add link");
                    Assert.Contains("Link added.", renderer.Text());
                    Assert.Equal(baseline + 1, (await audit.FindAsync(new())).Count);
                    Assert.Equal(2, (await store.Links(root.InternalId, root.VersionId)).Value.Count);
                    Assert.Empty(renderer.Components<MudDialog>());
                }
            }
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    [Trait("Req", "REQ-LINK-004")]
    public async Task LinkingGraphRoutesSameColumnArrowsToTheVisibleTargetBorder()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        Assert.True((await store.ChangeLink(d.Child, Endpoint(d.Need), true, RequirementActor, RequirementTime)).IsSuccess);
        await using var host = ManagementHost(products, store, needs, database.ConnectionString);
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var root = (await operations.Load(actor, "user-need", d.Need.Need.InternalId, d.Need.Need.VersionId)).Value.Center;
        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
            await ExplorationClick(renderer, "Expand Requirement Child · v1");
            var nodes = renderer.Elements("article").ToArray();
            var owner = nodes.Single(n => n.GetValueOrDefault("data-version")?.ToString() == d.Child.Requirement.VersionId.ToString());
            var target = nodes.Single(n => n.GetValueOrDefault("data-version")?.ToString() == d.Owner.Requirement.VersionId.ToString());
            static int Left(Dictionary<string, object?> node) => int.Parse(System.Text.RegularExpressions.Regex.Match(node["style"]!.ToString()!, @"left:(\d+)px").Groups[1].Value);
            Assert.Equal(Left(owner), Left(target));
            Assert.True(Left(owner) >= 300); // Leftmost routing lane leaves room for readable labels.
            var path = renderer.Elements("path").Single(p => p.GetValueOrDefault("data-owner")?.ToString() == d.Child.Requirement.VersionId.ToString()
                && p.GetValueOrDefault("data-target")?.ToString() == d.Owner.Requirement.VersionId.ToString());
            var route = path["d"]!.ToString()!;
            Assert.EndsWith($"H{Left(target)}", route);
            Assert.Equal("url(#link-arrow)", path["marker-end"]);
            Assert.Contains($"H{Left(owner) - 30} V", route);
        });
        Assert.Empty(renderer.Errors);
    }
}
