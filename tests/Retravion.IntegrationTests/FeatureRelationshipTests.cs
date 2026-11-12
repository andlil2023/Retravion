using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Npgsql;
using Retravion.Application.Feature;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Web.Features.Relationships;
using Retravion.Domain.Feature;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task FeatureLinksPreserveExactEndpointsAndRequireApprovedTargets()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var data = await RelationshipSetup(products, requirements, new PostgresUserNeedStore(database.ConnectionString));
        var store = new PostgresFeatureStore(database.ConnectionString);
        var draft = (await store.Create(data.Product.InternalId, FeatureContent.Create("Capability", ""), FeatureActor, FeatureTime)).Value;
        var old = new FeatureRequirementLink(data.Owner.Requirement.InternalId, data.Owner.Requirement.VersionId);
        var newer = new FeatureRequirementLink(data.Successor.Requirement.InternalId, data.Successor.Requirement.VersionId);
        var otherProduct = (await new CreateProduct(products).Execute("OTHER", "Other", ProductKind.System, "")).Value;
        var otherRequirement = await ParentDraft(requirements, otherProduct.InternalId, "Foreign");
        var foreign = new FeatureRequirementLink(otherRequirement.Requirement.InternalId, otherRequirement.Requirement.VersionId);
        Assert.True((await store.ChangeLink(draft, "add", foreign, null, FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.ChangeLink(draft, "add", new(Guid.Empty, Guid.Empty), null, FeatureActor, FeatureTime)).IsFailure);
        var linked = (await store.ChangeLink(draft, "add", old, null, FeatureActor, FeatureTime)).Value;
        Assert.True((await store.ChangeLink(linked, "retarget", old, foreign, FeatureActor, FeatureTime)).IsFailure);
        Assert.Equal(old, Assert.Single((await store.Find(draft.Feature.InternalId)).Value.Value.Feature.Links));
        Assert.True((await store.ChangeLink(draft, "add", old, null, FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.ChangeLink(linked, "add", newer, null, FeatureActor, FeatureTime)).IsFailure);
        var retarget = (await store.ChangeLink(linked, "retarget", old, newer, FeatureActor, FeatureTime)).Value;
        Assert.Equal(newer, Assert.Single(retarget.Feature.Links));
        var review = (await store.Review(retarget, "InReview", null, FeatureActor, FeatureTime)).Value;
        Assert.True((await store.Review(review, "Approved", null, FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.ChangeLink(review, "remove", newer, null, FeatureActor, FeatureTime)).IsFailure);
        var returned = (await store.Review(review, "Draft", null, FeatureActor, FeatureTime)).Value;
        linked = (await store.ChangeLink(returned, "retarget", newer, old, FeatureActor, FeatureTime)).Value;
        review = (await store.Review(linked, "InReview", null, FeatureActor, FeatureTime)).Value;
        var approved = (await store.Review(review, "Approved", null, FeatureActor, FeatureTime)).Value;
        var successor = (await store.CreateSuccessor(approved, "change", FeatureActor, FeatureTime)).Value;
        Assert.Equal(old, Assert.Single(successor.Feature.Links));
        var removed = (await store.ChangeLink(successor, "remove", old, null, FeatureActor, FeatureTime)).Value;
        Assert.Empty(removed.Feature.Links);
        Assert.Equal(old, Assert.Single((await store.Find(approved.Feature.InternalId, approved.Feature.VersionId)).Value.Value.Feature.Links));
        var history = (await store.History(draft.Feature.InternalId)).Value;
        var eventRetarget = history.First(h => h.Action == "feature.link.retarget").Change;
        Assert.Equal(old, Assert.Single(eventRetarget.Before!.Links!));
        Assert.Equal(newer, Assert.Single(eventRetarget.After.Links!));
        Assert.Equal(old, Assert.Single(history.Single(h => h.Action == "feature.version.create").Change.After.Links!));
        Assert.Equal(4, history.Count(h => h.Action.StartsWith("feature.link.")));
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        Assert.Equal(4, events.Count(e => e.Action.StartsWith("feature.link.")));
    }

    [PostgresFact]
    public async Task FeatureApprovalSerializesWithTargetReopeningInBothOrders()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var store = new PostgresFeatureStore(database.ConnectionString);
        var target = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "R"));
        var row = (await store.Create(product.InternalId, FeatureContent.Create("Capability", ""), FeatureActor, FeatureTime)).Value;
        row = (await store.ChangeLink(row, "add", new(target.Requirement.InternalId, target.Requirement.VersionId), null, FeatureActor, FeatureTime)).Value;
        row = (await store.Review(row, "InReview", null, FeatureActor, FeatureTime)).Value;
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetWriter = new PostgresRequirementStore(database.ConnectionString, async (tx, token) =>
        { staged.SetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(20), token); await tx.CommitAsync(token); });
        var reopen = targetWriter.Update(target, target.Requirement.Reopen("Correction").Value, FeatureActor, FeatureTime, "Reopened", "Correction");
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var approval = store.Review(row, "Approved", null, FeatureActor, FeatureTime);
        finish.SetResult();
        target = (await reopen).Value;
        Assert.True((await approval).IsFailure);
        Assert.Equal("InReview", (await store.Find(row.Feature.InternalId)).Value.Value.Feature.Status);
        target = await ParentApprove(requirements, target);
        staged = new(TaskCreationOptions.RunContinuationsAsynchronously); finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var featureWriter = new PostgresFeatureStore(database.ConnectionString, async (tx, token) =>
        { staged.SetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(20), token); await tx.CommitAsync(token); });
        approval = featureWriter.Review(row, "Approved", null, FeatureActor, FeatureTime);
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(20));
        reopen = requirements.Update(target, target.Requirement.Reopen("Later correction").Value, FeatureActor, FeatureTime, "Reopened", "Later correction");
        finish.SetResult();
        Assert.True((await approval).IsSuccess); Assert.True((await reopen).IsSuccess);
        Assert.Equal("Approved", (await store.Find(row.Feature.InternalId)).Value.Value.Feature.Status);
    }

    [PostgresFact]
    public async Task FeatureLinksUseSharedWorkspaceFromBothDirectionsAndViews()
    {
        foreach (var incoming in new[] { false, true })
        {
            await using var f = await CaseRelationshipFixture.Create();
            var store = f.Scope.ServiceProvider.GetRequiredService<IFeatureChangeTransaction>();
            var draft = (await store.Create(f.Data.Product.InternalId, FeatureContent.Create("Capability", ""), FeatureActor, FeatureTime)).Value;
            var feature = (await f.Operations.Load(f.Actor, "feature", draft.Feature.InternalId, draft.Feature.VersionId)).Value.Center;
            var requirement = (await f.Operations.Load(f.Actor, "requirement", f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId)).Value.Center;
            await using var services = ExplorationServices(f.Operations, f.Actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(incoming ? requirement : feature));
                if (incoming) await renderer.Click("Table view");
                if (incoming)
                {
                    await ManagementClick(renderer, $"Add incoming link to {requirement.Label}");
                    await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync("feature");
                    await SelectExact(renderer, feature.VersionId);
                }
                else
                {
                    await ManagementClick(renderer, $"Add outgoing link from {feature.Label}");
                    await SelectExact(renderer, requirement.VersionId);
                }
                await renderer.Click("Add link");
                Assert.Contains("Link added.", renderer.Text());
                Assert.Contains(RelationshipOperations.FeatureRelationship, renderer.Text());
                await ManagementClick(renderer, $"Remove {RelationshipOperations.FeatureRelationship}: {feature.Label} → {requirement.Label}");
                await renderer.Click("Confirm remove link");
                Assert.Contains("Link removed.", renderer.Text());
            });
            Assert.Empty(renderer.Errors);
        }
    }

    [PostgresFact]
    public async Task FeatureLinkAuditFailureAndUncertainCommitsDoNotReportSuccess()
    {
        foreach (var operation in new[] { "add", "remove", "retarget" })
        {
            await using var database = await Database.Create();
            var products = new PostgresProductStore(database.ConnectionString);
            var requirements = new PostgresRequirementStore(database.ConnectionString);
            var data = await RelationshipSetup(products, requirements, new PostgresUserNeedStore(database.ConnectionString));
            var store = new PostgresFeatureStore(database.ConnectionString);
            var draft = (await store.Create(data.Product.InternalId, FeatureContent.Create("Feature", ""), FeatureActor, FeatureTime)).Value;
            var endpoint = new FeatureRequirementLink(data.Owner.Requirement.InternalId, data.Owner.Requirement.VersionId);
            var replacement = new FeatureRequirementLink(data.Successor.Requirement.InternalId, data.Successor.Requirement.VersionId);
            if (operation != "add") draft = (await store.ChangeLink(draft, "add", endpoint, null, FeatureActor, FeatureTime)).Value;
            var expectedBefore = operation == "add" ? Array.Empty<FeatureRequirementLink>() : new[] { endpoint };
            var expectedAfter = operation == "remove" ? Array.Empty<FeatureRequirementLink>() : new[] { operation == "add" ? endpoint : replacement };
            var next = operation == "retarget" ? replacement : null;
            var audit = new PostgresAuditQuery(database.ConnectionString);
            var historyBefore = (await store.History(draft.Feature.InternalId)).Value.Count;
            var eventsBefore = (await audit.FindAsync(new())).Count(e => e.FeatureChange?.InternalId == draft.Feature.InternalId);
            async Task AssertUnchanged()
            {
                var found = (await store.Find(draft.Feature.InternalId)).Value.Value;
                Assert.Equal(draft.Revision, found.Revision);
                Assert.Equal(expectedBefore, found.Feature.Links);
                Assert.Equal(historyBefore, (await store.History(draft.Feature.InternalId)).Value.Count);
                Assert.Equal(eventsBefore, (await audit.FindAsync(new())).Count(e => e.FeatureChange?.InternalId == draft.Feature.InternalId));
            }
            await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
            await using (var cmd = new NpgsqlCommand("CREATE FUNCTION public.reject_feature_link() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$; CREATE TRIGGER reject_feature_link BEFORE INSERT ON retravion_audit.event FOR EACH ROW EXECUTE FUNCTION public.reject_feature_link()", connection)) await cmd.ExecuteNonQueryAsync();
            Assert.True((await store.ChangeLink(draft, operation, endpoint, next, FeatureActor, FeatureTime)).IsFailure);
            await AssertUnchanged();
            await using (var cmd = new NpgsqlCommand("DROP TRIGGER reject_feature_link ON retravion_audit.event", connection)) await cmd.ExecuteNonQueryAsync();
            var uncommitted = new PostgresFeatureStore(database.ConnectionString, (_, _) => throw new Exception());
            Assert.Contains("could not be confirmed", (await uncommitted.ChangeLink(draft, operation, endpoint, next, FeatureActor, FeatureTime)).Error);
            await AssertUnchanged();
            var committed = new PostgresFeatureStore(database.ConnectionString, async (tx, token) => { await tx.CommitAsync(token); throw new Exception(); });
            Assert.Contains("could not be confirmed", (await committed.ChangeLink(draft, operation, endpoint, next, FeatureActor, FeatureTime)).Error);
            var saved = (await store.Find(draft.Feature.InternalId)).Value.Value;
            Assert.Equal(draft.Revision + 1, saved.Revision);
            Assert.Equal(expectedAfter, saved.Feature.Links);
            var history = (await store.History(draft.Feature.InternalId)).Value;
            Assert.Equal(historyBefore + 1, history.Count);
            var entry = history.Last();
            Assert.Equal("feature.link." + operation, entry.Action);
            Assert.Equal(FeatureActor, entry.Actor);
            Assert.Equal(FeatureTime.UtcTicks, entry.OccurredAt.UtcTicks);
            Assert.Equal(expectedBefore, entry.Change.Before!.Links);
            Assert.Equal(expectedAfter, entry.Change.After.Links);
            var events = (await audit.FindAsync(new())).Where(e => e.FeatureChange?.InternalId == draft.Feature.InternalId).ToArray();
            Assert.Equal(eventsBefore + 1, events.Length);
            var recorded = Assert.Single(events, e => e.Action == "feature.link." + operation && e.FeatureChange!.VersionId == draft.Feature.VersionId &&
                e.FeatureChange.Before!.Links!.SequenceEqual(expectedBefore) && e.FeatureChange.After.Links!.SequenceEqual(expectedAfter));
            Assert.Equal(FeatureActor, recorded.Actor);
            Assert.Equal(FeatureTime.UtcTicks, recorded.OccurredAt.UtcTicks);
            Assert.Equal(AuditActionOutcome.Success, recorded.Outcome);
            Assert.Equal(new AuditTarget.Identified(draft.Feature.InternalId.ToString()), recorded.Target);
            Assert.Equal(draft.Feature.InternalId, recorded.FeatureChange!.InternalId);
            Assert.Equal(draft.Feature.VersionNumber, recorded.FeatureChange.VersionNumber);
            Assert.Equal(data.Product.InternalId, recorded.FeatureChange.After.ProductInternalId);
            Assert.Equal(operation switch { "add" => "LinkAdded", "remove" => "LinkRemoved", _ => "LinkRetargeted" }, recorded.FeatureChange.Decision);
        }
    }
}
