using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Revision;
using Retravion.Domain.Product;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Revision;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Revision;
using Retravion.Web.Features.Relationships;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise the real Blazor callbacks with the existing renderer.
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task RevisionListCreationIsHeaderNavigationAndHiddenWhenRetiredOrLoadingFails()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P & Q", "Product", ProductKind.System, "")).Value;
        var active = (await new CreateProduct(products).Execute("Active", "Active", ProductKind.System, "")).Value;
        var store = new PostgresRevisionStore(db.ConnectionString);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            s.AddSingleton<IRevisionStore>(store); s.AddSingleton<IRevisionChangeTransaction>(store);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, CaseJS>().AddSingleton<NavigationManager>(new CaseNavigation())
            .AddSingleton<AuthenticationStateProvider>(new CaseAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RevisionOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task Mount(string id) => await renderer.Mount(typeof(RevisionListWithHeader), new()
                { [nameof(RevisionListWithHeader.ProductId)] = id });
            void AssertNoCreation()
            {
                Assert.Single(renderer.Components<Retravion.Web.Components.PageHeader>());
                Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.Href?.Contains("/revisions/create") == true);
                Assert.DoesNotContain("Create empty Draft", renderer.Text());
            }
            await Mount(product.Id);
            Assert.Empty(renderer.Errors);
            Assert.Contains("Create Revision", renderer.Text());
            var button = Assert.Single(renderer.Components<MudButton>(), b => b.Href?.Contains("/revisions/create") == true);
            Assert.Equal("/revisions/create?product=P%20%26%20Q", button.Href);
            Assert.Equal(Variant.Text, button.Variant); Assert.Equal(Color.Default, button.Color);
            Assert.Equal(Icons.Material.Outlined.Add, button.StartIcon);
            Assert.Single(renderer.Components<MudButton>(), b => b.Href?.Contains("/revisions/create") == true);
            Assert.DoesNotContain("Create empty Draft", renderer.Text());
            var header = Assert.Single(renderer.Components<Retravion.Web.Components.PageHeader>());
            using var actions = new Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder();
            header.Actions!(actions);
            Assert.Contains(actions.GetFrames().Array.Take(actions.GetFrames().Count), frame =>
                frame.FrameType == Microsoft.AspNetCore.Components.RenderTree.RenderTreeFrameType.Component && frame.ComponentType == typeof(MudButton));
            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            await Mount(product.Id); Assert.Contains("read-only", renderer.Text()); AssertNoCreation();
            await Mount("missing");
            Assert.Single(renderer.Elements("div"), attrs => attrs.Any(a => a.AttributeName == "role" && Equals(a.AttributeValue, "alert")));
            AssertNoCreation();
            var row = await RevCreate(store, active.InternalId, "Invalid persisted revision");
            await using var connection = new Npgsql.NpgsqlConnection(db.ConnectionString); await connection.OpenAsync();
            await using var corrupt = new Npgsql.NpgsqlCommand("UPDATE retravion_revision.revision SET selections='[null]'::jsonb WHERE internal_id=$1", connection);
            corrupt.Parameters.AddWithValue(row.Value.InternalId); await corrupt.ExecuteNonQueryAsync();
            Assert.True((await store.List(active.InternalId)).IsFailure);
            await Mount(active.Id);
            Assert.Single(renderer.Elements("div"), attrs => attrs.Any(a => a.AttributeName == "role" && Equals(a.AttributeValue, "alert")));
            AssertNoCreation();
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class RevisionListWithHeader : RevisionList
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenRegion(0); base.BuildRenderTree(builder); builder.CloseRegion();
            builder.OpenComponent<Microsoft.AspNetCore.Components.Sections.SectionOutlet>(1);
            builder.AddAttribute(2, "SectionName", "product-page-header"); builder.CloseComponent();
        }
    }

    [PostgresFact]
    public async Task RevisionPagesCreateEditSelectReplaceRemoveInspectHistoryAndConfirmDeletion()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var r1 = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "R"));
        var r2 = (await requirements.CreateSuccessor(r1, "next", RevActor, RevTime)).Value;
        var e1 = new RevisionSelection(RevisionEntityKind.Requirement, r1.Requirement.InternalId, r1.Requirement.VersionId, 1);
        var e2 = e1 with { VersionId = r2.Requirement.VersionId, VersionNumber = 2 };
        var store = new PostgresRevisionStore(db.ConnectionString); var writes = new RevisionWriteHook(store);
        await RevCreate(store, other.InternalId, "Other Product secret");
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRevisionStore>(store); services.AddSingleton<IRevisionChangeTransaction>(writes);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope(); var navigation = new CaseNavigation(); navigation.Go("revisions/create?product=P");
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, CaseJS>().AddSingleton<NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new CaseAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RevisionOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RevisionEditor), new() { [nameof(RevisionEditor.ProductId)] = "P" });
            Assert.Single(renderer.Elements("form"));
            var save = renderer.Button("Save Draft"); Assert.Equal(Variant.Filled, save.Variant); Assert.Equal(Color.Primary, save.Color);
            await renderer.Event("onsubmit", EventArgs.Empty);
            Assert.Contains("identifier is required", renderer.Text()); Assert.Empty((await store.List(product.InternalId)).Value);
            await renderer.Field("Revision identifier").ValueChanged.InvokeAsync("  UI revision  ");
            await renderer.Field("Description (optional)").ValueChanged.InvokeAsync("<script>private()</script>");
            await renderer.Event("onsubmit", EventArgs.Empty);
            var row = Assert.Single((await store.List(product.InternalId)).Value);
            Assert.Equal("UI revision", row.Value.Identifier); Assert.Empty(row.Value.Selections);
            Assert.Contains($"id={row.Value.InternalId}", navigation.Uri);
            navigation.Go($"revisions/edit?product=P&id={row.Value.InternalId}");
            await renderer.Mount(typeof(RevisionEditor), new() { [nameof(RevisionEditor.ProductId)] = "P", [nameof(RevisionEditor.Id)] = row.Value.InternalId });
            await renderer.Field("Revision identifier").ValueChanged.InvokeAsync("Renamed UI revision");
            await renderer.Event("onsubmit", EventArgs.Empty);
            Assert.Equal("Renamed UI revision", (await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Value.Identifier);
            await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = "P", [nameof(RevisionDetails.Id)] = row.Value.InternalId });
            Assert.Contains("Release blocked", renderer.Text());
            Assert.Contains("kind=revision", renderer.Button("Manage links").Href);
            Assert.Empty(renderer.Components<MudSelect<string>>());
            await RevisionTab(renderer, 1); Assert.Contains("Empty baseline", renderer.Text());
            await MountMembership(renderer, "P", row.Value.InternalId);
            await AddRevisionMember(renderer, e1);
            Assert.Contains("Requirement R · v1", renderer.Text()); Assert.Contains("Selected", renderer.Text());
            await InspectMembership(renderer, "Requirement R · v1");
            var before = (await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!;
            await renderer.Click("Replace selection"); await ChooseRevisionMember(renderer, e2);
            Assert.Contains("New selection: Requirement R · v2", renderer.Text());
            Assert.Equal(before.Revision, (await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Revision);
            await renderer.Click("Cancel selection");
            await renderer.Click("Replace selection"); await ChooseRevisionMember(renderer, e2); await renderer.Click("Confirm selection");
            Assert.Equal(e2, Assert.Single((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Value.Selections));
            await InspectMembership(renderer, "Requirement R · v2");
            await renderer.Click("Remove selection"); await renderer.Click("Cancel selection");
            await renderer.Click("Remove selection"); await renderer.Click("Confirm selection");
            Assert.Empty((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Value.Selections);
            await AddRevisionMember(renderer, e1);
            await AddRevisionMember(renderer, e2);
            Assert.Contains("Requirement R · v1", renderer.Text()); Assert.Contains("Requirement R · v2", renderer.Text());
            Assert.Equal(2, (await store.Baseline(product.InternalId, row.Value.InternalId)).Value.Members.Count);
            await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = "P", [nameof(RevisionDetails.Id)] = row.Value.InternalId });
            await renderer.Click("Delete Draft"); Assert.Contains("Confirm deletion", renderer.Text());
            await renderer.Click("Cancel change"); Assert.False((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Deleted);
            await renderer.Click("Delete Draft"); await renderer.Click("Confirm deletion");
            Assert.True((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Deleted);
            Assert.Contains("Deleted Draft — retained evidence", renderer.Text());
            await RevisionTab(renderer, 2); Assert.Contains("revision.delete", renderer.Text());
            Assert.Equal(2, (await requirements.Versions(r1.Requirement.InternalId)).Value.Count);
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.OnClick.HasDelegate && b.Variant == Variant.Filled);
            await renderer.Mount(typeof(RevisionList), new() { [nameof(RevisionList.ProductId)] = "P" });
            Assert.Contains("No revisions", renderer.Text()); Assert.DoesNotContain("Other Product secret", renderer.Text());
            await RevCreate(store, product.InternalId, "Visible revision");
            var html = await host.Browser.GetStringAsync("/revisions?product=P");
            Assert.Contains("Revisions for P", html); Assert.Contains("/revisions/create?product=P", html); Assert.DoesNotContain("Other Product secret", html);
            var details = await host.Browser.GetStringAsync($"/revisions/details?product=P&id={row.Value.InternalId}");
            Assert.Contains("&lt;script&gt;private()&lt;/script&gt;", details); Assert.DoesNotContain("<script>private()", details);
            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            await renderer.Mount(typeof(RevisionList), new() { [nameof(RevisionList.ProductId)] = "P" });
            Assert.Contains("read-only", renderer.Text()); Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.Href?.Contains("/create") == true);
            await renderer.Mount(typeof(RevisionEditor), new() { [nameof(RevisionEditor.ProductId)] = "P" });
            Assert.Contains("retired", renderer.Text()); Assert.Empty(renderer.Elements("form"));
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    public async Task RevisionEveryUiWriteBlocksRetriesUntilAuthorizedOutcomeRetrievalInBothActualOutcomes()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var r1 = await ParentDraft(requirements, product.InternalId, "R1"); var r2 = await ParentDraft(requirements, product.InternalId, "R2");
        var e1 = new RevisionSelection(RevisionEntityKind.Requirement, r1.Requirement.InternalId, r1.Requirement.VersionId, 1);
        var e2 = new RevisionSelection(RevisionEntityKind.Requirement, r2.Requirement.InternalId, r2.Requirement.VersionId, 1);
        var store = new PostgresRevisionStore(db.ConnectionString); var writes = new RevisionWriteHook(store);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            s.AddSingleton<IRevisionStore>(store); s.AddSingleton<IRevisionChangeTransaction>(writes);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope(); var navigation = new CaseNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, CaseJS>()
            .AddSingleton<NavigationManager>(navigation).AddSingleton<AuthenticationStateProvider>(new CaseAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RevisionOperations>()).AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            foreach (var committed in new[] { false, true })
            foreach (var action in new[] { "create", "edit", "add", "remove", "replace", "delete" })
            {
                var row = await RevCreate(store, product.InternalId, Guid.NewGuid().ToString());
                if (action is "remove" or "replace" or "delete")
                    row = (await store.Select(Guid.NewGuid(), row, "add", e1, null, RevActor, RevTime, RevAllowed)).Value;
                writes.Store = new PostgresRevisionStore(db.ConnectionString, async (t, cancellation) =>
                { if (committed) await t.CommitAsync(cancellation); throw new IOException("private provider details"); });
                var calls = writes.Calls;
                Func<Task> submit;
                if (action is "create" or "edit")
                {
                    navigation.Go($"revisions/{action}?product=P&id={row.Value.InternalId}");
                    await renderer.Mount(typeof(RevisionEditor), new() { [nameof(RevisionEditor.ProductId)] = "P", [nameof(RevisionEditor.Id)] = row.Value.InternalId });
                    await renderer.Field("Revision identifier").ValueChanged.InvokeAsync("Uncertain " + Guid.NewGuid());
                    await renderer.Field("Description (optional)").ValueChanged.InvokeAsync("Input retained");
                    submit = () => renderer.Event("onsubmit", EventArgs.Empty);
                }
                else
                {
                    if (action == "delete")
                    {
                        await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = "P", [nameof(RevisionDetails.Id)] = row.Value.InternalId });
                        await renderer.Click("Delete Draft"); submit = () => renderer.Click("Confirm deletion");
                    }
                    else
                    {
                        await MountMembership(renderer, "P", row.Value.InternalId);
                        if (action != "add") await InspectMembership(renderer, "Requirement R1 · v1");
                        await renderer.Click(action == "add" ? "Add link" : action == "remove" ? "Remove selection" : "Replace selection");
                        if (action is "add" or "replace") await ChooseRevisionMember(renderer, e2);
                        submit = () => renderer.Click("Confirm selection");
                    }
                }
                await submit(); Assert.Equal(calls + 1, writes.Calls); Assert.Contains("could not be confirmed", renderer.Text());
                Assert.Contains("No automatic retry", renderer.Text());
                if (action is "create" or "edit")
                {
                    Assert.Equal("Input retained", renderer.Field("Description (optional)").GetState(f => f.Value));
                    await submit(); Assert.Equal(calls + 1, writes.Calls);
                }
                else
                {
                    if (action == "delete")
                    { Assert.True(renderer.Button("Review release").Disabled); await renderer.Click("Review release"); }
                    else
                    {
                        if (!renderer.HasButton("Add link")) await renderer.Click("Inspect revision");
                        Assert.True(renderer.Button("Add link").Disabled); await renderer.Click("Add link");
                    }
                    Assert.Equal(calls + 1, writes.Calls);
                }
                await renderer.Click("Retrieve outcome");
                Assert.Contains(committed ? "operation committed" : "operation did not commit", renderer.Text());
                Assert.Equal(calls + 1, writes.Calls);
            }
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    public async Task RevisionExistingUserAndAdministratorCapabilitiesPermitReadsAndDraftManagementButRetirementBlocksAllWrites()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString); var store = new PostgresRevisionStore(db.ConnectionString);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            s.AddSingleton<IRevisionStore>(store); s.AddSingleton<IRevisionChangeTransaction>(store);
            s.AddSingleton<IAuditSubmission>(new Retravion.Infrastructure.AuditTrail.PostgresAuditSubmission(db.ConnectionString));
        });
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RevisionOperations>();
        var sessions = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Identity.Logout.SessionTicketStore>();
        foreach (var role in new[] { "User", "Administrator" })
        {
            var product = (await new CreateProduct(products).Execute(role, role, ProductKind.System, "")).Value;
            var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, role),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role)
            }, "Cookies")), new Microsoft.AspNetCore.Authentication.AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, "Cookies");
            var key = await sessions.StoreAsync(ticket); var principal = (await sessions.RetrieveAsync(key))!.Principal;
            var id = Guid.NewGuid();
            var row = (await operations.Create(principal, Guid.NewGuid(), id, product.InternalId, "Revision", "")).Value;
            Assert.Single((await operations.List(principal, product.InternalId)).Value);
            Assert.NotNull((await operations.Retrieve(principal, product.InternalId, id)).Value.Record);
            Assert.Empty((await operations.Baseline(principal, product.InternalId, id)).Value.Members);
            var count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            Assert.True((await operations.Edit(principal, Guid.NewGuid(), row, "Revision", "")).IsSuccess);
            Assert.True((await operations.Edit(principal, Guid.NewGuid(), row, " ", "")).IsFailure);
            Assert.True((await operations.Delete(principal, Guid.NewGuid(), row, false)).IsFailure);
            Assert.Equal(count, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            var requirements = new PostgresRequirementStore(db.ConnectionString);
            var approved = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "Release target"));
            var releaseRow = (await operations.Create(principal, Guid.NewGuid(), Guid.NewGuid(), product.InternalId, "Release revision", "")).Value;
            releaseRow = (await operations.Select(principal, Guid.NewGuid(), releaseRow, "add",
                new(RevisionEntityKind.Requirement, approved.Requirement.InternalId, approved.Requirement.VersionId, 1))).Value;
            var preview = (await operations.Baseline(principal, product.InternalId, releaseRow.Value.InternalId)).Value;
            count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            Assert.True((await operations.Release(principal, Guid.NewGuid(), releaseRow, preview.Token, false)).IsFailure);
            Assert.Equal(count, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            Assert.IsType<ReleasedRevision>((await operations.Release(principal, Guid.NewGuid(), releaseRow, preview.Token, true)).Value.Value);
            count++;
            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            var endpoint = new RevisionSelection(RevisionEntityKind.UserNeed, Guid.NewGuid(), Guid.NewGuid(), 1);
            Assert.True((await operations.Create(principal, Guid.NewGuid(), Guid.NewGuid(), product.InternalId, "New", "")).IsFailure);
            Assert.True((await operations.Edit(principal, Guid.NewGuid(), row, "Changed", "")).IsFailure);
            foreach (var action in new[] { "add", "remove", "replace" })
                Assert.True((await operations.Select(principal, Guid.NewGuid(), row, action, endpoint, action == "replace" ? endpoint : null, true)).IsFailure);
            Assert.True((await operations.Delete(principal, Guid.NewGuid(), row, true)).IsFailure);
            Assert.True((await operations.Release(principal, Guid.NewGuid(), row, "invalid", true)).IsFailure);
            Assert.Equal(count, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            Assert.NotNull((await operations.Retrieve(principal, product.InternalId, id)).Value.Record);
            await sessions.RemoveAsync(key);
            Assert.True((await operations.Create(principal, Guid.Empty, Guid.Empty, product.InternalId, "secret", "secret")).IsFailure);
            var denied = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).First(e => e.Action == "revision.create" && e.Outcome == AuditActionOutcome.Failure);
            Assert.IsType<AuditTarget.Unknown>(denied.Target); Assert.Null(denied.RevisionChange);
        }
    }

    [PostgresFact]
    public async Task ReleaseUiShowsExactBlockersRequiresFreshConfirmationAndRetrievesBothUncertainOutcomesWithoutRetry()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString); var store = new PostgresRevisionStore(db.ConnectionString);
        var writes = new RevisionWriteHook(store);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            s.AddSingleton<IRevisionStore>(store); s.AddSingleton<IRevisionChangeTransaction>(writes);
        });
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope(); var navigation = new CaseNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, CaseJS>()
            .AddSingleton<NavigationManager>(navigation).AddSingleton<AuthenticationStateProvider>(new CaseAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RevisionOperations>()).AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            foreach (var committed in new[] { false, true })
            {
                var graph = await PrepareRelease(db.ConnectionString);
                async Task Mount() => await renderer.Mount(typeof(RevisionDetails), new()
                    { [nameof(RevisionDetails.ProductId)] = graph.Product.Id, [nameof(RevisionDetails.Id)] = graph.Row.Value.InternalId });
                var empty = await RevCreate(store, graph.Product.InternalId, "Empty");
                await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = graph.Product.Id, [nameof(RevisionDetails.Id)] = empty.Value.InternalId });
                Assert.True(renderer.Button("Review release").Disabled); Assert.Contains("Release blocked", renderer.Text());
                var needs = new Retravion.Infrastructure.UserNeed.PostgresUserNeedStore(db.ConnectionString);
                var reopened = (await needs.Update(graph.Need2, graph.Need2.Need.Reopen("Correction").Value, RevActor, RevTime, "Reopened", "Correction")).Value;
                await Mount(); Assert.True(renderer.Button("Review release").Disabled);
                Assert.Contains("Release blocked", renderer.Text()); Assert.Contains("User Need N · v2", renderer.Text());
                reopened = (await needs.Update(reopened, reopened.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
                await renderer.Click("Refresh baseline"); Assert.True(renderer.Button("Review release").Disabled);
                reopened = (await needs.Update(reopened, reopened.Need.Approve().Value, RevActor, RevTime, "Approved")).Value;
                await renderer.Click("Refresh baseline"); Assert.False(renderer.Button("Review release").Disabled);
                var before = writes.Calls;
                await renderer.Click("Review release"); Assert.Contains("Confirm release", renderer.Text());
                Assert.Contains("Requirement Parent · v1", renderer.Text()); Assert.Contains("Requirement Parent · v2", renderer.Text());
                await renderer.Click("Cancel change"); Assert.Equal(before, writes.Calls);
                Assert.IsType<DraftRevision>((await store.Retrieve(graph.Product.InternalId, graph.Row.Value.InternalId)).Value.Record!.Value);
                await renderer.Click("Review release");
                reopened = (await needs.Update(reopened, reopened.Need.Reopen("Later correction").Value, RevActor, RevTime, "Reopened", "Later correction")).Value;
                reopened = (await needs.Update(reopened, reopened.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
                await needs.Update(reopened, reopened.Need.Approve().Value, RevActor, RevTime, "Approved");
                await renderer.Click("Confirm release"); Assert.Contains("baseline changed", renderer.Text());
                Assert.IsType<DraftRevision>((await store.Retrieve(graph.Product.InternalId, graph.Row.Value.InternalId)).Value.Record!.Value);
                await renderer.Click("Refresh baseline"); await renderer.Click("Review release");
                writes.Store = new PostgresRevisionStore(db.ConnectionString, async (t, token) =>
                { if (committed) await t.CommitAsync(token); throw new IOException("private provider details"); });
                before = writes.Calls;
                await renderer.Click("Confirm release"); Assert.Equal(before + 1, writes.Calls);
                Assert.Contains("could not be confirmed", renderer.Text()); Assert.Contains("No automatic retry", renderer.Text());
                Assert.True(renderer.Button("Review release").Disabled); Assert.True(renderer.Button("Manage links").Disabled);
                await renderer.Click("Review release"); Assert.Equal(before + 1, writes.Calls);
                await renderer.Click("Retrieve outcome"); Assert.Contains(committed ? "operation committed" : "operation did not commit", renderer.Text());
                Assert.Equal(before + 1, writes.Calls); writes.Store = store;
                if (!committed) { await renderer.Click("Review release"); await renderer.Click("Confirm release"); }
                Assert.Contains("Frozen release evidence", renderer.Text()); Assert.Contains("permanently protected", renderer.Text());
                Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.OnClick.HasDelegate && b.Variant == Variant.Filled);
                Assert.IsType<ReleasedRevision>((await store.Retrieve(graph.Product.InternalId, graph.Row.Value.InternalId)).Value.Record!.Value);
                navigation.Go($"revisions/edit?product={graph.Product.Id}&id={graph.Row.Value.InternalId}");
                await renderer.Mount(typeof(RevisionEditor), new() { [nameof(RevisionEditor.ProductId)] = graph.Product.Id, [nameof(RevisionEditor.Id)] = graph.Row.Value.InternalId });
                Assert.Empty(renderer.Elements("form")); Assert.Contains("Editable Draft revision not found", renderer.Text());
            }
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    public async Task RevisionCopyUiCreatesIndependentDraftAndRequiresAuthorizedRetrievalBeforeRetryInBothOutcomes()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString); var store = new PostgresRevisionStore(db.ConnectionString);
        var writes = new RevisionWriteHook(store);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            s.AddSingleton<IRevisionStore>(store); s.AddSingleton<IRevisionChangeTransaction>(writes);
        });
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope(); var navigation = new CaseNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, CaseJS>()
            .AddSingleton<NavigationManager>(navigation).AddSingleton<AuthenticationStateProvider>(new CaseAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RevisionOperations>()).AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var g = await PrepareRelease(db.ConnectionString);
            var source = (await ReleaseNow(store, await ReleasePreview(store, g.Row))).Value;
            var sourceId = source.Value.InternalId;
            async Task MountCopy(Guid id)
            {
                navigation.Go($"revisions/create?product={g.Product.Id}&source={id}");
                await renderer.Mount(typeof(RevisionEditor), new() { [nameof(RevisionEditor.ProductId)] = g.Product.Id, [nameof(RevisionEditor.SourceId)] = id });
            }
            await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = g.Product.Id, [nameof(RevisionDetails.Id)] = sourceId });
            Assert.Contains($"source={sourceId}", renderer.Button("Copy to new Draft").Href);
            var empty = await RevCreate(store, g.Product.InternalId, "Not Released");
            await MountCopy(empty.Value.InternalId); Assert.Empty(renderer.Elements("form")); Assert.Contains("Released source revision not found", renderer.Text());
            await MountCopy(Guid.NewGuid()); Assert.Empty(renderer.Elements("form"));
            await MountCopy(sourceId);
            Assert.Contains("Exact explicit selections", renderer.Text()); Assert.Contains("release evidence is not copied", renderer.Text());
            var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            await renderer.Event("onsubmit", EventArgs.Empty); Assert.Contains("identifier is required", renderer.Text());
            await renderer.Field("Revision identifier").ValueChanged.InvokeAsync(source.Value.Identifier);
            await renderer.Event("onsubmit", EventArgs.Empty); Assert.Contains("already in use", renderer.Text());
            Assert.Equal(events, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            await renderer.Field("Revision identifier").ValueChanged.InvokeAsync("First copied Draft");
            await renderer.Field("Description (optional)").ValueChanged.InvokeAsync("UI copy description");
            await renderer.Event("onsubmit", EventArgs.Empty);
            var first = Assert.Single((await store.List(g.Product.InternalId)).Value, r => r.Value.Identifier == "First copied Draft");
            Assert.IsType<DraftRevision>(first.Value); Assert.Equal(source.Value.Selections.ToArray(), first.Value.Selections.ToArray());
            Assert.Equal("UI copy description", first.Value.Description);
            await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = g.Product.Id, [nameof(RevisionDetails.Id)] = first.Value.InternalId });
            await RevisionTab(renderer, 2);
            Assert.Contains("Copied from Released revision", renderer.Text()); Assert.Contains(sourceId.ToString(), renderer.Text());
            await RevisionTab(renderer, 1);
            Assert.Contains("Complete outgoing baseline", renderer.Text()); Assert.DoesNotContain("Frozen release evidence", renderer.Text());
            foreach (var committed in new[] { false, true })
            {
                await MountCopy(sourceId);
                var identifier = "Uncertain copy " + Guid.NewGuid();
                await renderer.Field("Revision identifier").ValueChanged.InvokeAsync(identifier);
                writes.Store = new PostgresRevisionStore(db.ConnectionString, async (t, token) =>
                { if (committed) await t.CommitAsync(token); throw new OperationCanceledException("private details"); });
                var before = writes.Calls;
                await renderer.Event("onsubmit", EventArgs.Empty);
                Assert.Equal(before + 1, writes.Calls); Assert.Contains("could not be confirmed", renderer.Text());
                Assert.Contains("No automatic retry", renderer.Text()); Assert.True(renderer.Button("Save Draft").Disabled);
                await renderer.Event("onsubmit", EventArgs.Empty); Assert.Equal(before + 1, writes.Calls);
                await renderer.Click("Retrieve outcome"); Assert.Equal(before + 1, writes.Calls);
                writes.Store = store;
                if (committed) Assert.Contains("/revisions/details", navigation.Uri);
                else
                {
                    Assert.Contains("operation did not commit", renderer.Text()); Assert.False(renderer.Button("Save Draft").Disabled);
                    await renderer.Event("onsubmit", EventArgs.Empty); Assert.Equal(before + 2, writes.Calls);
                }
                var copy = Assert.Single((await store.List(g.Product.InternalId)).Value, r => r.Value.Identifier == identifier);
                Assert.IsType<DraftRevision>(copy.Value); Assert.Equal(source.Value.Selections.ToArray(), copy.Value.Selections.ToArray());
                Assert.Equal("revision.copy", Assert.Single((await store.History(g.Product.InternalId, copy.Value.InternalId)).Value).Action);
            }
            // Lost access cannot unlock an uncertain creation for retry or disclose its outcome.
            await MountCopy(sourceId); await renderer.Field("Revision identifier").ValueChanged.InvokeAsync("Denied retrieval");
            writes.Store = new PostgresRevisionStore(db.ConnectionString, (_, _) => throw new IOException("private details"));
            await renderer.Event("onsubmit", EventArgs.Empty); var calls = writes.Calls;
            var sessions = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Identity.Logout.SessionTicketStore>();
            await sessions.RemoveAsync(principal.FindFirst(Retravion.Web.Features.Identity.Logout.SessionTicketStore.SessionClaim)!.Value);
            await renderer.Click("Retrieve outcome"); Assert.Contains("Access denied", renderer.Text());
            Assert.True(renderer.Button("Save Draft").Disabled); Assert.Equal(calls, writes.Calls);
            var operations = scope.ServiceProvider.GetRequiredService<RevisionOperations>();
            Assert.Equal("Access denied.", (await operations.Copy(principal, Guid.NewGuid(), Guid.NewGuid(), g.Product.InternalId, sourceId, "secret", "secret")).Error);
            Assert.Equal(calls, writes.Calls);
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class RevisionWriteHook(PostgresRevisionStore store) : IRevisionChangeTransaction
    {
        internal PostgresRevisionStore Store = store;
        internal int Calls;
        public Task<Result<RevisionSnapshot>> Create(Guid op, DraftRevision d, AuditActor a, DateTimeOffset at, Func<Task<bool>> allowed, CancellationToken token = default)
        { Calls++; return Store.Create(op, d, a, at, allowed, token); }
        public Task<Result<RevisionSnapshot>> Copy(Guid op, DraftRevision d, Guid sourceId, AuditActor a, DateTimeOffset at, Func<Task<bool>> allowed, CancellationToken token = default)
        { Calls++; return Store.Copy(op, d, sourceId, a, at, allowed, token); }
        public Task<Result<RevisionSnapshot>> Edit(Guid op, RevisionSnapshot e, string? id, string? description, AuditActor a, DateTimeOffset at, Func<Task<bool>> allowed, CancellationToken token = default)
        { Calls++; return Store.Edit(op, e, id, description, a, at, allowed, token); }
        public Task<Result<RevisionSnapshot>> Select(Guid op, RevisionSnapshot e, string action, RevisionSelection target, RevisionSelection? replacement, AuditActor a, DateTimeOffset at, Func<Task<bool>> allowed, CancellationToken token = default)
        { Calls++; return Store.Select(op, e, action, target, replacement, a, at, allowed, token); }
        public Task<Result<RevisionSnapshot>> Release(Guid op, RevisionSnapshot e, string baselineToken, AuditActor a, DateTimeOffset at, Func<Task<bool>> allowed, CancellationToken token = default)
        { Calls++; return Store.Release(op, e, baselineToken, a, at, allowed, token); }
        public Task<Result<RevisionSnapshot>> Delete(Guid op, RevisionSnapshot e, AuditActor a, DateTimeOffset at, Func<Task<bool>> allowed, CancellationToken token = default)
        { Calls++; return Store.Delete(op, e, a, at, allowed, token); }
    }
}
