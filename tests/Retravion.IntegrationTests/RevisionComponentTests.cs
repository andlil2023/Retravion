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
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise the real Blazor callbacks with the existing renderer.
public sealed partial class PostgresProductTests
{
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
            Assert.Contains("Empty baseline", renderer.Text());
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.OnClick.HasDelegate && b.Href?.Contains("release", StringComparison.OrdinalIgnoreCase) == true);
            await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync($"{e1.Kind}:{e1.VersionId}");
            await renderer.Click("Add exact selection");
            Assert.Contains("Requirement R · v1", renderer.Text()); Assert.Contains("Explicit", renderer.Text());
            await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync($"{e2.Kind}:{e2.VersionId}");
            var before = (await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!;
            await renderer.Click("Replace selection"); Assert.Contains("Confirm replacement", renderer.Text());
            Assert.Contains("New exact selection: Requirement R · v2", renderer.Text());
            Assert.Equal(before.Revision, (await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Revision);
            await renderer.Click("Cancel change");
            await renderer.Click("Replace selection"); await renderer.Click("Confirm change");
            Assert.Equal(e2, Assert.Single((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Value.Selections));
            await renderer.Click("Remove selection"); await renderer.Click("Cancel change");
            await renderer.Click("Remove selection"); await renderer.Click("Confirm change");
            Assert.Empty((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Value.Selections);
            await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync($"{e1.Kind}:{e1.VersionId}");
            await renderer.Click("Add exact selection");
            await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync($"{e2.Kind}:{e2.VersionId}");
            await renderer.Click("Add exact selection");
            Assert.Contains("Requirement R · v1", renderer.Text()); Assert.Contains("Requirement R · v2", renderer.Text());
            Assert.Equal(2, (await store.Baseline(product.InternalId, row.Value.InternalId)).Value.Members.Count);
            await renderer.Click("Delete Draft"); Assert.Contains("Confirm deletion", renderer.Text());
            await renderer.Click("Cancel change"); Assert.False((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Deleted);
            await renderer.Click("Delete Draft"); await renderer.Click("Confirm change");
            Assert.True((await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!.Deleted);
            Assert.Contains("Deleted Draft — retained evidence", renderer.Text()); Assert.Contains("revision.delete", renderer.Text());
            Assert.Equal(2, (await requirements.Versions(r1.Requirement.InternalId)).Value.Count);
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.OnClick.HasDelegate);
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
                    await renderer.Mount(typeof(RevisionDetails), new() { [nameof(RevisionDetails.ProductId)] = "P", [nameof(RevisionDetails.Id)] = row.Value.InternalId });
                    if (action is "add" or "replace") await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync($"{e2.Kind}:{e2.VersionId}");
                    if (action is "remove" or "replace" or "delete") await renderer.Click(action == "remove" ? "Remove selection" : action == "replace" ? "Replace selection" : "Delete Draft");
                    submit = () => renderer.Click(action == "add" ? "Add exact selection" : "Confirm change");
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
                    Assert.True(renderer.Button("Add exact selection").Disabled);
                    await renderer.Click("Add exact selection"); Assert.Equal(calls + 1, writes.Calls);
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
                Assert.True(renderer.Button("Release revision").Disabled); Assert.Contains("Release blocked", renderer.Text());
                var needs = new Retravion.Infrastructure.UserNeed.PostgresUserNeedStore(db.ConnectionString);
                var reopened = (await needs.Update(graph.Need2, graph.Need2.Need.Reopen("Correction").Value, RevActor, RevTime, "Reopened", "Correction")).Value;
                await Mount(); Assert.True(renderer.Button("Release revision").Disabled);
                Assert.Contains("Release blocked", renderer.Text()); Assert.Contains("User Need N · v2", renderer.Text());
                reopened = (await needs.Update(reopened, reopened.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
                await renderer.Click("Refresh baseline"); Assert.True(renderer.Button("Release revision").Disabled);
                reopened = (await needs.Update(reopened, reopened.Need.Approve().Value, RevActor, RevTime, "Approved")).Value;
                await renderer.Click("Refresh baseline"); Assert.False(renderer.Button("Release revision").Disabled);
                Assert.Contains("Requirement Parent · v1", renderer.Text()); Assert.Contains("Requirement Parent · v2", renderer.Text());
                var before = writes.Calls;
                await renderer.Click("Release revision"); Assert.Contains("Confirm release", renderer.Text());
                await renderer.Click("Cancel change"); Assert.Equal(before, writes.Calls);
                Assert.IsType<DraftRevision>((await store.Retrieve(graph.Product.InternalId, graph.Row.Value.InternalId)).Value.Record!.Value);
                await renderer.Click("Release revision");
                reopened = (await needs.Update(reopened, reopened.Need.Reopen("Later correction").Value, RevActor, RevTime, "Reopened", "Later correction")).Value;
                reopened = (await needs.Update(reopened, reopened.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
                await needs.Update(reopened, reopened.Need.Approve().Value, RevActor, RevTime, "Approved");
                await renderer.Click("Confirm change"); Assert.Contains("baseline changed", renderer.Text());
                Assert.IsType<DraftRevision>((await store.Retrieve(graph.Product.InternalId, graph.Row.Value.InternalId)).Value.Record!.Value);
                await renderer.Click("Refresh baseline"); await renderer.Click("Release revision");
                writes.Store = new PostgresRevisionStore(db.ConnectionString, async (t, token) =>
                { if (committed) await t.CommitAsync(token); throw new IOException("private provider details"); });
                before = writes.Calls;
                await renderer.Click("Confirm change"); Assert.Equal(before + 1, writes.Calls);
                Assert.Contains("could not be confirmed", renderer.Text()); Assert.Contains("No automatic retry", renderer.Text());
                Assert.True(renderer.Button("Release revision").Disabled); Assert.True(renderer.Button("Add exact selection").Disabled);
                await renderer.Click("Release revision"); await renderer.Click("Add exact selection"); Assert.Equal(before + 1, writes.Calls);
                await renderer.Click("Retrieve outcome"); Assert.Contains(committed ? "operation committed" : "operation did not commit", renderer.Text());
                Assert.Equal(before + 1, writes.Calls); writes.Store = store;
                if (!committed) { await renderer.Click("Release revision"); await renderer.Click("Confirm change"); }
                Assert.Contains("Frozen release evidence", renderer.Text()); Assert.Contains("permanently protected", renderer.Text());
                Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.OnClick.HasDelegate);
                Assert.IsType<ReleasedRevision>((await store.Retrieve(graph.Product.InternalId, graph.Row.Value.InternalId)).Value.Record!.Value);
                navigation.Go($"revisions/edit?product={graph.Product.Id}&id={graph.Row.Value.InternalId}");
                await renderer.Mount(typeof(RevisionEditor), new() { [nameof(RevisionEditor.ProductId)] = graph.Product.Id, [nameof(RevisionEditor.Id)] = graph.Row.Value.InternalId });
                Assert.Empty(renderer.Elements("form")); Assert.Contains("Editable Draft revision not found", renderer.Text());
            }
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class RevisionWriteHook(PostgresRevisionStore store) : IRevisionChangeTransaction
    {
        internal PostgresRevisionStore Store = store;
        internal int Calls;
        public Task<Result<RevisionSnapshot>> Create(Guid op, DraftRevision d, AuditActor a, DateTimeOffset at, Func<Task<bool>> allowed, CancellationToken token = default)
        { Calls++; return Store.Create(op, d, a, at, allowed, token); }
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
