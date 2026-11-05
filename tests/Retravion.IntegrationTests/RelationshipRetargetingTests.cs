using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Npgsql;
using Retravion.Application.Requirement;
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
    public async Task RetargetingPreservesExactEndpointsAndRecordsOneAtomicOldNewChange()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        var owner = (await store.ChangeLink(d.Successor, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var baseline = (await audit.FindAsync(new())).Count;
        var history = (await store.History(owner.Requirement.InternalId)).Value.Count;
        Assert.Equal(Endpoint(d.Need), Assert.Single((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value));
        Assert.True((await store.RetargetLink(d.Owner, Endpoint(d.Need), Endpoint(d.NewNeed), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.RetargetLink(owner, Endpoint(d.NewNeed), Endpoint(d.Need), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.RetargetLink(owner, Endpoint(d.Need), new(Guid.NewGuid(), d.NewNeed.Need.VersionId), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.RetargetLink(owner, Endpoint(d.Need), new(d.Need.Need.InternalId, Guid.NewGuid()), RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(owner, (await store.RetargetLink(owner, Endpoint(d.Need), Endpoint(d.Need), RequirementActor, RequirementTime)).Value);
        var inReview = (await store.Update(owner, owner.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        Assert.True((await store.RetargetLink(inReview, Endpoint(d.Need), Endpoint(d.NewNeed), RequirementActor, RequirementTime)).IsFailure);
        owner = (await store.Update(inReview, inReview.Requirement.ReturnToDraft().Value, RequirementActor, RequirementTime, "Draft")).Value;
        baseline += 2; history += 2;
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
        var stale = owner;
        owner = (await store.RetargetLink(owner, Endpoint(d.Need), Endpoint(d.NewNeed), RequirementActor, RequirementTime)).Value;
        Assert.True((await store.RetargetLink(stale, Endpoint(d.Need), Endpoint(d.NewNeed), RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(Endpoint(d.NewNeed), Assert.Single((await new PostgresRequirementStore(database.ConnectionString).Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value));
        Assert.Equal(Endpoint(d.Need), Assert.Single((await store.Links(d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId)).Value));
        var change = Assert.Single((await audit.FindAsync(new())), e => e.Action == "requirement.link.retarget");
        Assert.Equal(RequirementActor, change.Actor); Assert.Equal(RequirementTime, change.OccurredAt);
        Assert.Equal(Retravion.Application.AuditTrail.AuditActionOutcome.Success, change.Outcome);
        Assert.Equal(new Retravion.Application.AuditTrail.AuditTarget.Identified(owner.Requirement.InternalId.ToString()), change.Target);
        Assert.Equal(owner.Requirement.VersionId, change.RequirementChange!.VersionId);
        Assert.Equal("Requirement–User Need", change.RequirementChange.RelationshipType);
        Assert.Equal(Endpoint(d.Need), Assert.Single(change.RequirementChange.BeforeLinks!));
        Assert.Equal(Endpoint(d.NewNeed), Assert.Single(change.RequirementChange.AfterLinks!));
        var entry = Assert.Single((await store.History(owner.Requirement.InternalId)).Value, h => h.Decision == "LinkRetargeted");
        Assert.Equal(RequirementActor, entry.Actor); Assert.Equal(RequirementTime, entry.OccurredAt);
        Assert.Equal(owner.Requirement.VersionId, entry.VersionId);
        Assert.Equal(Endpoint(d.Need), Assert.Single(entry.BeforeLinks!));
        Assert.Equal(Endpoint(d.NewNeed), Assert.Single(entry.AfterLinks!));
        Assert.Equal(history + 1, (await store.History(owner.Requirement.InternalId)).Value.Count);
        Assert.Equal(baseline + 1, (await audit.FindAsync(new())).Count);
        // A duplicate destination rejects the whole change, retaining both original endpoints.
        owner = (await store.ChangeLink(owner, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
        baseline = (await audit.FindAsync(new())).Count;
        Assert.True((await store.RetargetLink(owner, Endpoint(d.Need), Endpoint(d.NewNeed), RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(2, (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Count);
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
        Assert.True((await products.Update(d.Product, d.Product.Retire().Value)).IsSuccess);
        Assert.True((await store.RetargetLink(owner, Endpoint(d.Need), Endpoint(d.Need), RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task RetargetingRollsBackBusinessHistoryAndAuditAndReportsBothUncertainOutcomes()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        var owner = (await store.ChangeLink(d.Successor, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE FUNCTION public.reject_retarget() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await setup.ExecuteNonQueryAsync();
        foreach (var parent in new[] { false, true })
        {
            var history = (await store.History(owner.Requirement.InternalId)).Value.Count;
            var baseline = (await audit.FindAsync(new())).Count;
            foreach (var table in new[] { parent ? "retravion_requirement.parent_link" : "retravion_requirement.user_need_link", "retravion_requirement.requirement", "retravion_requirement.requirement_version", "retravion_requirement.review", "retravion_audit.event" })
            {
                var versionTable = table == "retravion_requirement.requirement_version";
                var triggerSql = versionTable ? """
                    CREATE TRIGGER reject_retarget BEFORE UPDATE ON retravion_requirement.requirement_version
                        FOR EACH ROW EXECUTE FUNCTION public.reject_retarget();
                    CREATE SEQUENCE public.retarget_projection_attempt;
                    CREATE FUNCTION public.mark_retarget_projection_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN PERFORM nextval('public.retarget_projection_attempt'); RETURN NEW; END $$;
                    CREATE TRIGGER mark_retarget_projection_attempt AFTER UPDATE ON retravion_requirement.requirement
                        FOR EACH ROW EXECUTE FUNCTION public.mark_retarget_projection_attempt();
                    """ : $"CREATE TRIGGER reject_retarget BEFORE INSERT OR UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_retarget()";
                await using (var trigger = new NpgsqlCommand(triggerSql, connection)) await trigger.ExecuteNonQueryAsync();
                var failed = parent
                    ? await store.ReplaceParent(owner, ParentEndpoint(d.NewParent), RequirementActor, RequirementTime)
                    : await store.RetargetLink(owner, Endpoint(d.Need), Endpoint(d.NewNeed), RequirementActor, RequirementTime);
                Assert.True(failed.IsFailure); Assert.DoesNotContain("private", failed.Error);
                if (versionTable)
                {
                    // A sequence survives rollback, proving the failure followed the staged link/projection writes.
                    await using var reached = new NpgsqlCommand("SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.retarget_projection_attempt", connection);
                    Assert.Equal(1L, await reached.ExecuteScalarAsync());
                    await using var cleanup = new NpgsqlCommand("DROP TRIGGER mark_retarget_projection_attempt ON retravion_requirement.requirement; DROP FUNCTION public.mark_retarget_projection_attempt(); DROP SEQUENCE public.retarget_projection_attempt", connection);
                    await cleanup.ExecuteNonQueryAsync();
                }
                await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_retarget ON {table}", connection)) await drop.ExecuteNonQueryAsync();
                Assert.Equal(owner, (await store.Find(owner.Requirement.InternalId)).Value.Value);
                Assert.Equal(Endpoint(d.Need), Assert.Single((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value));
                Assert.Equal(ParentEndpoint(d.Parent), (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Value);
                Assert.Equal(history, (await store.History(owner.Requirement.InternalId)).Value.Count);
                Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
            }
        }
        foreach (var parent in new[] { false, true })
        foreach (var committed in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        {
            var calls = 0;
            var history = (await store.History(owner.Requirement.InternalId)).Value.Count;
            var baseline = (await audit.FindAsync(new())).Count;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            {
                calls++; if (committed) await transaction.CommitAsync(token);
                if (cancelled) throw new OperationCanceledException("private");
                throw new IOException("private");
            });
            var result = parent
                ? await uncertain.ReplaceParent(owner, ParentEndpoint(d.NewParent), RequirementActor, RequirementTime)
                : await uncertain.RetargetLink(owner, Endpoint(d.Need), Endpoint(d.NewNeed), RequirementActor, RequirementTime);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("Retrieve", result.Error); Assert.Equal(1, calls);
            var retrieved = (await store.Find(owner.Requirement.InternalId)).Value.Value;
            Assert.Equal(owner.Revision + (committed ? 1 : 0), retrieved.Revision);
            Assert.Equal(history + (committed ? 1 : 0), (await store.History(owner.Requirement.InternalId)).Value.Count);
            Assert.Equal(baseline + (committed ? 1 : 0), (await audit.FindAsync(new())).Count);
            if (parent)
                Assert.Equal(ParentEndpoint(committed ? d.NewParent : d.Parent), (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Value);
            else
                Assert.Equal(Endpoint(committed ? d.NewNeed : d.Need), Assert.Single((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value));
            if (committed) owner = parent
                ? (await store.ReplaceParent(retrieved, ParentEndpoint(d.Parent), RequirementActor, RequirementTime)).Value
                : (await store.RetargetLink(retrieved, Endpoint(d.NewNeed), Endpoint(d.Need), RequirementActor, RequirementTime)).Value;
        }
    }

    [PostgresFact]
    public async Task RetargetControlsConfirmExactVersionsAndCancelInGraphAndTableForIncomingAndExpandedOwners()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        await using var host = ManagementHost(products, store, needs, database.ConnectionString);
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var audit = new PostgresAuditQuery(database.ConnectionString);
        foreach (var table in new[] { false, true })
        foreach (var parent in new[] { false, true })
        {
            var owner = await ParentDraft(store, d.Product.InternalId, $"Retarget-{table}-{parent}");
            owner = parent ? (await store.AddParent(owner, ParentEndpoint(d.Parent), RequirementActor, RequirementTime)).Value
                : (await store.ChangeLink(owner, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
            var root = (await operations.Load(actor, parent ? "requirement" : "user-need",
                parent ? d.Parent.Requirement.InternalId : d.Need.Need.InternalId,
                parent ? d.Parent.Requirement.VersionId : d.Need.Need.VersionId)).Value.Center;
            var relationship = parent ? RelationshipOperations.ParentRelationship : RelationshipOperations.NeedRelationship;
            var ownerLabel = $"Requirement {owner.Requirement.Id} · v1";
            var label = $"Retarget {relationship}: {ownerLabel} → {root.Label}";
            var newLabel = parent ? "Requirement Parent · v2" : "User Need N · v2";
            var newVersion = parent ? d.NewParent.Requirement.VersionId : d.NewNeed.Need.VersionId;
            var baseline = (await audit.FindAsync(new())).Count;
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (table) await renderer.Click("Table view");
                var lockedLabel = $"Retarget {relationship}: Requirement Owner · v1 → {root.Label}";
                Assert.True(ManagementButton(renderer, lockedLabel).Disabled);
                await ManagementClick(renderer, lockedLabel); // Direct invocation must also refuse a locked owner.
                Assert.DoesNotContain("Replacement exact version", renderer.Text());
                await ExplorationClick(renderer, $"Expand {ownerLabel}");
                Assert.Contains("Newer version available; showing this exact version.", renderer.Text());
                await ManagementClick(renderer, label);
                Assert.True(ManagementButton(renderer, "Confirm selected retarget").Disabled);
                Assert.Single(renderer.Components<MudSelectItem<Guid?>>()); // Only another version of the same target.
                await renderer.Components<MudSelect<Guid?>>().Single(s => s.Label == "Replacement exact version").ValueChanged.InvokeAsync(newVersion);
                Assert.Contains($"Confirm old target {root.Label} → new target {newLabel}", renderer.Text());
                Assert.Contains(renderer.Components<MudLink>(), l => l.Target == "_blank" && l.Href!.Contains(newVersion.ToString()));
                await renderer.Click("Cancel retarget");
                Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
                await ManagementClick(renderer, label);
                await renderer.Components<MudSelect<Guid?>>().Single(s => s.Label == "Replacement exact version").ValueChanged.InvokeAsync(newVersion);
                await ManagementClick(renderer, "Confirm selected retarget");
                Assert.Contains("Link retargeted.", renderer.Text());
            });
            Assert.Empty(renderer.Errors);
            if (parent) Assert.Equal(newVersion, (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Value.VersionId);
            else Assert.Equal(newVersion, Assert.Single((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value).UserNeedVersionId);
            Assert.Equal(baseline + 1, (await audit.FindAsync(new())).Count);
            if (parent)
            {
                var change = Assert.Single(await audit.FindAsync(new()), e => e.Action == "requirement.parent.replace" && e.RequirementChange!.InternalId == owner.Requirement.InternalId);
                Assert.Equal(ParentEndpoint(d.Parent), change.RequirementChange!.BeforeParent);
                Assert.Equal(ParentEndpoint(d.NewParent), change.RequirementChange.AfterParent);
                Assert.Equal(owner.Requirement.VersionId, change.RequirementChange.VersionId);
                var entry = Assert.Single((await store.History(owner.Requirement.InternalId)).Value, h => h.Decision == "ParentReplaced");
                Assert.Equal(ParentEndpoint(d.Parent), entry.BeforeParent); Assert.Equal(ParentEndpoint(d.NewParent), entry.AfterParent);
            }
        }
    }

    [PostgresFact]
    public async Task StaleRetargetConfirmationPreservesBothOriginalLinksAndRequiresRetrieval()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var d = await RelationshipSetup(products, store, needs);
        var owner = (await store.ChangeLink(d.Successor, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
        await using var host = ManagementHost(products, store, needs, database.ConnectionString);
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var audit = new PostgresAuditQuery(database.ConnectionString);
        foreach (var parent in new[] { false, true })
        {
            var view = (await operations.Load(actor, "requirement", owner.Requirement.InternalId, owner.Requirement.VersionId)).Value;
            var edge = view.Edges.Single(e => e.Owner.VersionId == owner.Requirement.VersionId && e.Relationship == (parent ? RelationshipOperations.ParentRelationship : RelationshipOperations.NeedRelationship));
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(view.Center));
                if (parent) await renderer.Click("Table view");
                await ManagementClick(renderer, $"Retarget {edge.Relationship}: {edge.Owner.Label} → {edge.Target.Label}");
                await renderer.Components<MudSelect<Guid?>>().Single(s => s.Label == "Replacement exact version").ValueChanged.InvokeAsync(parent ? d.NewParent.Requirement.VersionId : d.NewNeed.Need.VersionId);
                owner = (await store.Update(owner, owner.Requirement.Edit(owner.Requirement.Id, $"Changed {parent}", "Statement", owner.Requirement.Type, null).Value, RequirementActor, RequirementTime)).Value;
                var baseline = (await audit.FindAsync(new())).Count;
                var history = (await store.History(owner.Requirement.InternalId)).Value.Count;
                await ManagementClick(renderer, "Confirm selected retarget");
                Assert.Contains("Retrieve it again", renderer.Text());
                Assert.DoesNotContain("Link retargeted.", renderer.Text());
                Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
                Assert.Equal(history, (await store.History(owner.Requirement.InternalId)).Value.Count);
                Assert.Equal(Endpoint(d.Need), Assert.Single((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value));
                Assert.Equal(ParentEndpoint(d.Parent), (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Value);
                Assert.True(ManagementButton(renderer, $"Retarget {edge.Relationship}: {edge.Owner.Label} → {edge.Target.Label}").Disabled);
                await renderer.Click("Retrieve relationships before another action");
                Assert.False(ManagementButton(renderer, $"Retarget {edge.Relationship}: {edge.Owner.Label} → {edge.Target.Label}").Disabled);
            });
        }
        Assert.Empty(renderer.Errors);
    }
}
