using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Npgsql;
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
    private static McpTestHost ManagementHost(PostgresProductStore products, PostgresRequirementStore store,
        PostgresUserNeedStore needs, Retravion.Application.AuditTrail.IAuditSubmission? audit = null) => new(services =>
        {
            if (audit is not null) services.AddSingleton(audit);
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRequirementStore>(store); services.AddSingleton<IRequirementLinkStore>(store);
            services.AddSingleton<IRequirementChangeTransaction>(store);
            services.AddSingleton<IUserNeedStore>(needs); services.AddSingleton<IUserNeedChangeTransaction>(needs);
        });

    private static Task SelectExact(RequirementRenderer renderer, Guid version) => renderer.Components<MudSelect<Guid?>>()
        .Single(s => s.Label == "Existing exact version").ValueChanged.InvokeAsync(version);

    private static MudButton ManagementButton(RequirementRenderer renderer, string label) => renderer.Components<MudButton>()
        .First(b => b.UserAttributes?.TryGetValue("aria-label", out var value) == true && Equals(value, label));

    private static Task ManagementClick(RequirementRenderer renderer, string label) =>
        ManagementButton(renderer, label).OnClick.InvokeAsync(new MouseEventArgs());

    [PostgresFact]
    public async Task LinkManagementAddsAndRemovesIncomingReferencesToExactApprovedTargetsInBothViews()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        var audit = new PostgresAuditQuery(database.ConnectionString);
        await using var host = ManagementHost(products, store, needs);
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        foreach (var table in new[] { false, true })
        foreach (var parent in new[] { false, true })
        {
            var owner = await ParentDraft(store, d.Product.InternalId, $"Incoming-{table}-{parent}");
            var root = (await operations.Load(actor, parent ? "requirement" : "user-need",
                parent ? d.Parent.Requirement.InternalId : d.Need.Need.InternalId,
                parent ? d.Parent.Requirement.VersionId : d.Need.Need.VersionId)).Value.Center;
            var relationship = parent ? RelationshipOperations.ParentRelationship : RelationshipOperations.NeedRelationship;
            var before = (await audit.FindAsync(new())).Count;
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (table) await renderer.Click("Table view");
                if (parent)
                {
                    Assert.True(ManagementButton(renderer, $"Add outgoing link from {root.Label}").Disabled);
                    Assert.Contains("Outgoing links locked", renderer.Text());
                }
                await ManagementClick(renderer, $"Add incoming link to {root.Label}");
                var candidates = renderer.Components<MudSelectItem<Guid?>>().Select(i => i.Value).ToArray();
                Assert.Contains(owner.Requirement.VersionId, candidates);
                Assert.DoesNotContain(d.Owner.Requirement.VersionId, candidates); // Historical Approved owner is not editable.
                await SelectExact(renderer, owner.Requirement.VersionId);
                Assert.Contains($"Requirement {owner.Requirement.Id} · v1 → {relationship} → {root.Label}", renderer.Text());
                await renderer.Click("Cancel add");
                Assert.Equal(before, (await audit.FindAsync(new())).Count);
                await ManagementClick(renderer, $"Add incoming link to {root.Label}");
                await SelectExact(renderer, owner.Requirement.VersionId);
                await renderer.Click("Add link");
                Assert.Contains("Link added.", renderer.Text());
                var fresh = (await store.Find(owner.Requirement.InternalId)).Value.Value;
                if (parent) Assert.Equal(root.VersionId, (await store.Parent(fresh.Requirement.InternalId, fresh.Requirement.VersionId)).Value.Value.VersionId);
                else Assert.Equal(root.VersionId, Assert.Single((await store.Links(fresh.Requirement.InternalId, fresh.Requirement.VersionId)).Value).UserNeedVersionId);
                var removeLabel = $"Remove {relationship}: Requirement {owner.Requirement.Id} · v1 → {root.Label}";
                await ManagementClick(renderer, removeLabel);
                Assert.Contains($"Remove {relationship}: Requirement {owner.Requirement.Id} · v1 → {root.Label}?", renderer.Text());
                await renderer.Click("Cancel removal");
                Assert.Equal(before + 1, (await audit.FindAsync(new())).Count);
                await ManagementClick(renderer, removeLabel);
                await renderer.Click("Confirm remove link");
                Assert.Contains("Link removed.", renderer.Text());
                Assert.Empty(renderer.Components<MudDialog>()); // No entity creation dialog.
            });
            Assert.Empty(renderer.Errors);
            Assert.Equal(before + 2, (await audit.FindAsync(new())).Count);
            Assert.Equal(2, (await store.History(owner.Requirement.InternalId)).Value.Count);
            Assert.Empty((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value);
            Assert.True((await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.HasNoValue);
        }
    }

    [PostgresFact]
    public async Task ExpandedDraftOwnerCanAddBothOutgoingTypesAndStaleOrDuplicateCallbacksDoNotWrite()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        var owner = await ParentDraft(store, d.Product.InternalId, "Expanded");
        owner = (await store.ChangeLink(owner, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        await using var host = ManagementHost(products, store, needs);
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
            await ExplorationClick(renderer, "Expand Requirement Expanded · v1");
            await ManagementClick(renderer, "Add outgoing link from Requirement Expanded · v1");
            await renderer.Components<MudSelect<string>>().Single(s => s.Label == "Relationship").ValueChanged.InvokeAsync(RelationshipOperations.ParentRelationship);
            await SelectExact(renderer, d.Parent.Requirement.VersionId);
            await renderer.Click("Add link");
            Assert.Contains("Link added.", renderer.Text());
            Assert.Equal(d.Parent.Requirement.VersionId, (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Value.VersionId);
            Assert.Contains("Neighborhood expanded", renderer.Text());
            await renderer.Click("Table view");
            await ManagementClick(renderer, "Add outgoing link from Requirement Expanded · v1");
            await SelectExact(renderer, d.NewNeed.Need.VersionId);
            // Capture the displayed revision, then edit independently. The UI must not silently refresh it at save.
            var latest = (await store.Find(owner.Requirement.InternalId)).Value.Value;
            Assert.True((await store.Update(latest, latest.Requirement.Edit("Expanded", "Changed elsewhere", "Statement", latest.Requirement.Type, null).Value,
                RequirementActor, RequirementTime)).IsSuccess);
            var before = (await audit.FindAsync(new())).Count;
            await renderer.Click("Add link");
            Assert.Contains("Retrieve it again", renderer.Text());
            Assert.DoesNotContain("Link added.", renderer.Text());
            Assert.Equal(before, (await audit.FindAsync(new())).Count);
            await renderer.Click("Retrieve relationships before another action");
            await ManagementClick(renderer, "Add outgoing link from Requirement Expanded · v1");
            await SelectExact(renderer, d.NewNeed.Need.VersionId);
            await renderer.Click("Add link");
            Assert.Contains("Link added.", renderer.Text());
            Assert.Equal(2, (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Count);
            await ManagementClick(renderer, "Add outgoing link from Requirement Expanded · v1");
            await SelectExact(renderer, d.NewNeed.Need.VersionId);
            before = (await audit.FindAsync(new())).Count;
            await renderer.Click("Add link");
            Assert.DoesNotContain("Link added.", renderer.Text());
            Assert.Equal(before, (await audit.FindAsync(new())).Count);
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    public async Task LinkManagementDeniesRevokedCallbacksAndRetiredProductsWithoutSuccessEvents()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        await using var host = ManagementHost(products, store, needs, new PostgresAuditSubmission(database.ConnectionString));
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var root = (await operations.Load(actor, "requirement", d.Child.Requirement.InternalId, d.Child.Requirement.VersionId)).Value.Center;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
            await ManagementClick(renderer, $"Remove {RelationshipOperations.ParentRelationship}: {root.Label} → Requirement Owner · v1");
            Assert.True((await products.Update(d.Product, d.Product.Retire().Value)).IsSuccess);
            var before = (await audit.FindAsync(new())).Count;
            await renderer.Click("Confirm remove link");
            Assert.DoesNotContain("Link removed.", renderer.Text());
            Assert.Equal(before, (await audit.FindAsync(new())).Count);
            await renderer.Click("Retrieve relationships before another action");
            Assert.Contains("Product retired", renderer.Text());
            Assert.True(ManagementButton(renderer, $"Add incoming link to {root.Label}").Disabled);
            // Reactivate separately, prepare a real callback, then revoke the session before submitting.
            var retired = (await products.List()).Value.Single(p => p.InternalId == d.Product.InternalId);
            Assert.True((await products.Update(retired, retired.Reactivate().Value)).IsSuccess);
            await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
            await ManagementClick(renderer, $"Add outgoing link from {root.Label}");
            await SelectExact(renderer, d.Need.Need.VersionId);
            await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(actor.FindFirst(SessionTicketStore.SessionClaim)!.Value);
            before = (await audit.FindAsync(new())).Count;
            await renderer.Click("Add link");
            Assert.Contains("Access denied", renderer.Text());
            Assert.DoesNotContain("Link added.", renderer.Text());
            var events = await audit.FindAsync(new());
            Assert.Equal(before + 1, events.Count);
            var denied = Assert.Single(events, e => e.Action == "requirement.link.add" && e.Outcome == Retravion.Application.AuditTrail.AuditActionOutcome.Failure);
            Assert.Equal("requirement.link.add", denied.Action);
            Assert.Equal(Retravion.Application.AuditTrail.AuditActionOutcome.Failure, denied.Outcome);
            Assert.IsType<Retravion.Application.AuditTrail.AuditTarget.Unknown>(denied.Target);
        });
        Assert.Empty(renderer.Errors);
        Assert.Empty((await store.Links(d.Child.Requirement.InternalId, d.Child.Requirement.VersionId)).Value);
        Assert.True((await store.Parent(d.Child.Requirement.InternalId, d.Child.Requirement.VersionId)).Value.HasValue);
    }

    [PostgresFact]
    public async Task LinkManagementReportsUncertaintyForEveryMutationWithoutRetryUntilRetrieval()
    {
        foreach (var parent in new[] { false, true })
        foreach (var remove in new[] { false, true })
        foreach (var committed in new[] { false, true })
        {
            await using var database = await Database.Create();
            var products = new PostgresProductStore(database.ConnectionString);
            var store = new PostgresRequirementStore(database.ConnectionString);
            var needs = new PostgresUserNeedStore(database.ConnectionString);
            var d = await RelationshipSetup(products, store, needs);
            var owner = await ParentDraft(store, d.Product.InternalId, "Uncertain");
            if (remove) owner = parent
                ? (await store.AddParent(owner, ParentEndpoint(d.Parent), RequirementActor, RequirementTime)).Value
                : (await store.ChangeLink(owner, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
            var calls = 0;
            var inject = false;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            {
                if (!inject) { await transaction.CommitAsync(token); return; }
                calls++;
                if (committed) await transaction.CommitAsync(token);
                throw new IOException("private-provider-details");
            });
            await using var host = ManagementHost(products, uncertain, needs);
            await host.Login(); await host.Replace(); var credential = await host.Authenticate();
            var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
            using var scope = host.Factory.Services.CreateScope();
            var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
            var root = (await operations.Load(actor, "requirement", owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Center;
            var target = (await operations.Load(actor, parent ? "requirement" : "user-need",
                parent ? d.Parent.Requirement.InternalId : d.Need.Need.InternalId,
                parent ? d.Parent.Requirement.VersionId : d.Need.Need.VersionId)).Value.Center;
            var relationship = parent ? RelationshipOperations.ParentRelationship : RelationshipOperations.NeedRelationship;
            var audit = new PostgresAuditQuery(database.ConnectionString);
            var before = (await audit.FindAsync(new())).Count;
            var history = (await store.History(owner.Requirement.InternalId)).Value.Count;
            await using var services = ExplorationServices(operations, actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (remove)
                    await ManagementClick(renderer, $"Remove {relationship}: {root.Label} → {target.Label}");
                else
                {
                    await ManagementClick(renderer, $"Add outgoing link from {root.Label}");
                    if (parent) await renderer.Components<MudSelect<string>>().Single(s => s.Label == "Relationship").ValueChanged.InvokeAsync(relationship);
                    await SelectExact(renderer, target.VersionId);
                }
                var callback = ManagementButton(renderer, remove ? "Confirm selected removal" : "Add selected link").OnClick;
                inject = true;
                await callback.InvokeAsync(new MouseEventArgs());
                Assert.Contains("outcome could not be confirmed", renderer.Text());
                Assert.Contains("Retrieve the Requirement before retrying", renderer.Text());
                Assert.DoesNotContain("Link added.", renderer.Text());
                Assert.DoesNotContain("Link removed.", renderer.Text());
                Assert.DoesNotContain("private-provider-details", renderer.Text());
                Assert.True(ManagementButton(renderer, $"Add outgoing link from {root.Label}").Disabled);
                await callback.InvokeAsync(new MouseEventArgs()); // A retained/direct callback cannot retry.
                Assert.Equal(1, calls);
                inject = false;
                await renderer.Click("Retrieve relationships before another action");
                Assert.DoesNotContain("outcome could not be confirmed", renderer.Text());
                Assert.Equal(1, calls);
            });
            Assert.Empty(renderer.Errors);
            Assert.Equal(before + (committed ? 1 : 0), (await audit.FindAsync(new())).Count);
            Assert.Equal(history + (committed ? 1 : 0), (await store.History(owner.Requirement.InternalId)).Value.Count);
            var hasLink = parent
                ? (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.HasValue
                : (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Count != 0;
            Assert.Equal(remove ? !committed : committed, hasLink);
        }
    }

    [PostgresFact]
    public async Task LinkManagementNavigationDuringRefreshDoesNotPublishSuccessOnAnotherCenter()
    {
        foreach (var remove in new[] { false, true })
        {
            await using var database = await Database.Create();
            var products = new PostgresProductStore(database.ConnectionString);
            var store = new PostgresRequirementStore(database.ConnectionString);
            var needs = new PostgresUserNeedStore(database.ConnectionString);
            var d = await RelationshipSetup(products, store, needs);
            var owner = await ParentDraft(store, d.Product.InternalId, "Navigation");
            if (remove) owner = (await store.ChangeLink(owner, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
            var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var nextIsWrite = false;
            var holdRefresh = false;
            var held = false;
            var refreshReturnedNormally = false;
            var delayed = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            {
                if (nextIsWrite)
                {
                    await transaction.CommitAsync(token);
                    nextIsWrite = false; holdRefresh = true;
                    return;
                }
                if (holdRefresh && !held)
                {
                    // Delay the read callback, not its advisory lock: navigation needs the same lock.
                    await transaction.CommitAsync(token);
                    held = true; refreshStarted.TrySetResult();
                    await releaseRefresh.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
                    refreshReturnedNormally = true;
                    return;
                }
                await transaction.CommitAsync(token);
            });
            await using var host = ManagementHost(products, delayed, needs);
            await host.Login(); await host.Replace(); var credential = await host.Authenticate();
            var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
            using var scope = host.Factory.Services.CreateScope();
            var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
            var root = (await operations.Load(actor, "requirement", owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Center;
            var other = (await operations.Load(actor, "requirement", d.Parent.Requirement.InternalId, d.Parent.Requirement.VersionId)).Value.Center;
            await using var services = ExplorationServices(operations, actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (remove)
                    await ManagementClick(renderer, $"Remove {RelationshipOperations.NeedRelationship}: {root.Label} → User Need N · v1");
                else
                {
                    await ManagementClick(renderer, $"Add outgoing link from {root.Label}");
                    await SelectExact(renderer, d.Need.Need.VersionId);
                }
                nextIsWrite = true;
                var save = ManagementButton(renderer, remove ? "Confirm selected removal" : "Add selected link").OnClick.InvokeAsync(new MouseEventArgs());
                try
                {
                    await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await renderer.Navigate(Parameters(other));
                    Assert.Contains("Center: Requirement Parent · v1", renderer.Text());
                    Assert.DoesNotContain("Link added.", renderer.Text());
                    Assert.DoesNotContain("Link removed.", renderer.Text());
                }
                finally { releaseRefresh.TrySetResult(); }
                await save;
                Assert.True(refreshReturnedNormally); // A timeout must not be the mechanism that unblocks navigation.
                Assert.Contains("Center: Requirement Parent · v1", renderer.Text());
                Assert.DoesNotContain("Link added.", renderer.Text());
                Assert.DoesNotContain("Link removed.", renderer.Text());
            });
            Assert.Empty(renderer.Errors);
            Assert.Equal(remove ? 0 : 1, (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Count);
        }
    }

    [PostgresFact]
    public async Task LinkManagementAuditFailuresRollBackEveryMutationAndNeverReportSuccess()
    {
        foreach (var parent in new[] { false, true })
        foreach (var remove in new[] { false, true })
        {
            await using var database = await Database.Create();
            var products = new PostgresProductStore(database.ConnectionString);
            var store = new PostgresRequirementStore(database.ConnectionString);
            var needs = new PostgresUserNeedStore(database.ConnectionString);
            var d = await RelationshipSetup(products, store, needs);
            var owner = await ParentDraft(store, d.Product.InternalId, "AuditFailure");
            if (remove) owner = parent
                ? (await store.AddParent(owner, ParentEndpoint(d.Parent), RequirementActor, RequirementTime)).Value
                : (await store.ChangeLink(owner, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
            await using var host = ManagementHost(products, store, needs);
            await host.Login(); await host.Replace(); var credential = await host.Authenticate();
            var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
            using var scope = host.Factory.Services.CreateScope();
            var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
            var root = (await operations.Load(actor, "requirement", owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Center;
            var target = (await operations.Load(actor, parent ? "requirement" : "user-need",
                parent ? d.Parent.Requirement.InternalId : d.Need.Need.InternalId,
                parent ? d.Parent.Requirement.VersionId : d.Need.Need.VersionId)).Value.Center;
            var relationship = parent ? RelationshipOperations.ParentRelationship : RelationshipOperations.NeedRelationship;
            var audit = new PostgresAuditQuery(database.ConnectionString);
            var before = (await audit.FindAsync(new())).Count;
            var history = (await store.History(owner.Requirement.InternalId)).Value.Count;
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync();
            await using (var trigger = new NpgsqlCommand("""
                CREATE FUNCTION public.reject_management_audit() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN RAISE EXCEPTION 'private-provider-details' USING ERRCODE='42501'; END $$;
                CREATE TRIGGER reject_management_audit BEFORE INSERT ON retravion_audit.event
                FOR EACH ROW EXECUTE FUNCTION public.reject_management_audit();
                """, connection)) await trigger.ExecuteNonQueryAsync();
            await using var services = ExplorationServices(operations, actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                await renderer.Click("Table view");
                if (remove)
                {
                    await ManagementClick(renderer, $"Remove {relationship}: {root.Label} → {target.Label}");
                    await renderer.Click("Confirm remove link");
                }
                else
                {
                    await ManagementClick(renderer, $"Add outgoing link from {root.Label}");
                    if (parent) await renderer.Components<MudSelect<string>>().Single(s => s.Label == "Relationship").ValueChanged.InvokeAsync(relationship);
                    await SelectExact(renderer, target.VersionId);
                    await renderer.Click("Add link");
                }
                Assert.Contains("Retrieve relationships before another action", renderer.Text());
                Assert.DoesNotContain("Link added.", renderer.Text());
                Assert.DoesNotContain("Link removed.", renderer.Text());
                Assert.DoesNotContain("private-provider-details", renderer.Text());
            });
            Assert.Empty(renderer.Errors);
            Assert.Equal(before, (await audit.FindAsync(new())).Count);
            Assert.Equal(history, (await store.History(owner.Requirement.InternalId)).Value.Count);
            Assert.Equal(owner, (await store.Find(owner.Requirement.InternalId)).Value.Value);
            Assert.Equal(remove, parent
                ? (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.HasValue
                : (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Count != 0);
        }
    }
}
