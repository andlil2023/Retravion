using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Identity.Application;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    private const string DeleteAction = "specification.delete";

    [PostgresFact]
    public async Task SpecificationDeletionRequiresMatchingConfirmationAndRunAttestationInEveryEligibleState()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var pid = (await Product(products, "first")).InternalId;
        var other = (await Product(products, "other")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var user = await Principal(sessions, "User", now: CreateTime);
        var otherId = (await ops.Create(user, other, "DEV-1", "Title", "Description")).Value.InternalId;

        foreach (var status in new[] { "Draft", "In Review", "Approved" })
        {
            var id = (await ops.Create(user, pid, "DEV-1", "Title", "Description")).Value.InternalId;
            if (status != "Draft") Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
            if (status == "Approved") Assert.True((await ops.Approve(user, pid, id)).IsSuccess);
            var before = (await ops.Find(user, pid, id)).Value.Value;

            Assert.Contains("Confirm deletion", (await ops.Delete(user, pid, id, null, true)).Error);
            Assert.Contains("Confirm deletion", (await ops.Delete(user, pid, id, otherId, true)).Error);
            Assert.Contains("Explicitly attest", (await ops.Delete(user, pid, id, id, false)).Error);
            Assert.Contains("not found", (await ops.Delete(user, other, id, id, true)).Error);
            Assert.Equal(before, (await ops.Find(user, pid, id)).Value.Value);
            Assert.DoesNotContain(await SpecificationEvents(db, DeleteAction), entry => entry.Target == id.ToString());

            Assert.True((await ops.Delete(user, pid, id, id, true)).IsSuccess);
            Assert.True((await ops.Find(user, pid, id)).Value.HasNoValue);
            Assert.Contains("not found", (await ops.Delete(user, pid, id, id, true)).Error);
            var entry = Assert.Single(await SpecificationEvents(db, DeleteAction), entry => entry.Target == id.ToString());
            Assert.Equal(status, entry.Change!.Before!.Status);
            Assert.Null(entry.Change.After);
        }

        Assert.True((await ops.Find(user, other, otherId)).Value.HasValue);
        Assert.Equal(3, (await SpecificationEvents(db, DeleteAction)).Count);
    }

    [PostgresFact]
    public async Task SpecificationDeletionRejectsImplementedUnauthorizedAndRetiredProductRecords()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var pid = product.InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var user = await Principal(sessions, "User", now: CreateTime);
        var restricted = await Principal(sessions, "Administrator", restricted: true, now: CreateTime);
        var draft = (await ops.Create(user, pid, "DRAFT", "Title", "Description")).Value;
        var id = (await ops.Create(user, pid, "FINAL", "Title", "Description")).Value.InternalId;
        Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
        Assert.True((await ops.Approve(user, pid, id)).IsSuccess);
        Assert.True((await ops.SaveEvidence(user, pid, id, "# Report", EvidenceUrl)).IsSuccess);
        var implemented = (await ops.Implement(user, pid, id, true)).Value;

        Assert.Contains("Implemented", (await ops.Delete(user, pid, id, id, true)).Error);
        Assert.Equal("Access denied.", (await ops.Delete(restricted, pid, draft.InternalId, draft.InternalId, true)).Error);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await ops.Delete(user, pid, draft.InternalId, draft.InternalId, true)).Error);
        Assert.Equal(draft, (await ops.Find(user, pid, draft.InternalId)).Value.Value);
        Assert.Equal(implemented, (await ops.Find(user, pid, id)).Value.Value);
        Assert.Equal(new CreateEvent("Administrator", CreateTime.UtcTicks, "unknown", null, "failure", null),
            Assert.Single(await SpecificationEvents(db, DeleteAction)));
    }

    [PostgresFact]
    public async Task SpecificationDeletionRemovesOwnedRowsWhileKeepingExactVersionsAndDurableAudit()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var linked = await owners.Approve(await owners.Create(pid));
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var user = await Principal(sessions, "User", now: CreateTime);
        var administrator = await Principal(sessions, "Administrator", now: CreateTime);
        var assignee = (await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "First.User")).Username;
        var draft = await PopulateSpecificationForDeletion(ops, user, pid, linked, assignee, clock);
        var sibling = (await ops.Create(user, pid, "OTHER", "Other", "Description")).Value;
        Assert.True((await ops.EditLinks(user, pid, sibling.InternalId, linked.Links)).IsSuccess);
        var id = draft.InternalId;
        Assert.Equal(new long[] { 1, 3, 2, 1, 1 }, await DeletionRowCounts(db, id));
        await AssertReopenBlocked(owners, linked);

        clock.Now = CreateTime.AddTicks(24);
        Assert.True((await ops.Delete(administrator, pid, id, id, true)).IsSuccess);

        Assert.True((await ops.FindDetail(user, pid, id)).Value.HasNoValue);
        Assert.Equal(new long[] { 0, 0, 0, 0, 0 }, await DeletionRowCounts(db, id));
        Assert.Equal(new long[] { 1, 3, 0, 0, 0 }, await DeletionRowCounts(db, sibling.InternalId));
        AssertUnchanged(linked, await owners.Find(linked, exact: true));
        AssertReopened(linked, await owners.Reopen(linked)); // Removed the only Approved reference, not its target versions.

        Assert.Contains(await CreateEvents(db), entry => entry.Target == id.ToString());
        var audit = Assert.Single(await SpecificationEvents(db, DeleteAction));
        Assert.Equal(("Administrator", clock.Now.UtcTicks, "identified", id.ToString(), "success"),
            (audit.Actor, audit.UtcTicks, audit.TargetKind, audit.Target, audit.Outcome));
        var change = Assert.IsType<SpecificationAuditDetails>(audit.Change);
        Assert.Equal(id, change.InternalId);
        Assert.True(change.NoActiveOrUncertainRunAttested);
        Assert.Null(change.After);
        var before = Assert.IsType<SpecificationAuditValues>(change.Before);
        Assert.Equal(new SpecificationAuditValues(pid, draft.Id, draft.Title, draft.Description, "Approved"), before with { Links = null });
        Assert.Equal(linked.Links.Select(link => new SpecificationAuditLink(link.Kind.ToString(), link.VersionId)), before.Links!);
        var deletion = Assert.IsType<SpecificationAuditDeletion>(change.Deletion);
        Assert.Equal(new SpecificationAuditAiReviewValue("# Review", true), deletion.AiReview);
        Assert.Equal(new SpecificationAuditEvidenceValue("# Report", EvidenceUrl, true), deletion.Evidence);
        Assert.Equal(new SpecificationAuditAssignmentEntry[]
        {
            new(1, "User", CreateTime, null, assignee),
            new(2, "User", CreateTime.AddTicks(13), assignee, null)
        }, deletion.AssignmentHistory);
    }

    [PostgresFact]
    public async Task SpecificationDeletionRollsBackAllOwnedRowsWhenCleanupOrAuditFails()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var linked = await owners.Approve(await owners.Create(pid));
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var assignee = (await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "First.User")).Username;
        var id = (await PopulateSpecificationForDeletion(ops, user, pid, linked, assignee, clock)).InternalId;
        var before = (await ops.FindDetail(user, pid, id)).Value.Value;

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var fault = new NpgsqlCommand("""
            CREATE FUNCTION retravion_specification.fail_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE insufficient_privilege USING MESSAGE = 'private provider details'; END;
            $$;
            CREATE TRIGGER fail_cleanup BEFORE DELETE ON retravion_specification.evidence
            FOR EACH ROW EXECUTE FUNCTION retravion_specification.fail_cleanup();
            """, connection))
            await fault.ExecuteNonQueryAsync();
        await AssertRolledBack();
        await using (var clear = new NpgsqlCommand("DROP TRIGGER fail_cleanup ON retravion_specification.evidence", connection))
            await clear.ExecuteNonQueryAsync();

        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        await AssertRolledBack();

        async Task AssertRolledBack()
        {
            Assert.Equal("The Specification operation could not be completed.", (await ops.Delete(user, pid, id, id, true)).Error);
            Assert.Equal(new long[] { 1, 3, 2, 1, 1 }, await DeletionRowCounts(db, id));
            var after = (await ops.FindDetail(user, pid, id)).Value.Value;
            Assert.Equal(before.Specification, after.Specification);
            Assert.Equal(before.AssignmentHistory, after.AssignmentHistory);
            Assert.Equal(before.CurrentAiReview, after.CurrentAiReview);
            Assert.Equal(before.Evidence, after.Evidence);
            Assert.Empty(await SpecificationEvents(db, DeleteAction));
            await AssertReopenBlocked(owners, linked);
        }
    }

    [PostgresFact]
    public async Task SpecificationDeletionRechecksAccessBeforeCommitAndNeverRetriesAnUncertainCommit()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var setup = new PostgresSpecificationWriteStore(db.ConnectionString);
        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        static Task<bool> Authorized() => Task.FromResult(true);
        var draft = DraftSpecification.Create(pid, "DEV-1", "Title", "Description").Value;
        Assert.True((await setup.Create(draft, CreateActor, CreateTime, Authorized)).IsSuccess);
        var id = draft.InternalId;
        var checks = 0;
        Assert.Equal("Access denied.", (await setup.Delete(pid, id, id, true, CreateActor, CreateTime,
            () => Task.FromResult(++checks == 1))).Error);
        Assert.Equal(2, checks);
        Assert.Equal(draft, (await reads.Find(pid, id)).Value.Value);
        Assert.Empty(await SpecificationEvents(db, DeleteAction));

        foreach (var committed in new[] { false, true })
        {
            var attempts = 0;
            var writes = new PostgresSpecificationWriteStore(db.ConnectionString, async (transaction, token) =>
            {
                attempts++;
                if (committed) await transaction.CommitAsync(token);
                throw new IOException("private provider details");
            });
            var deleted = await writes.Delete(pid, id, id, true, CreateActor, CreateTime, Authorized);
            Assert.Contains("outcome could not be confirmed", deleted.Error);
            Assert.DoesNotContain("private", deleted.Error);
            Assert.Equal(1, attempts);
            Assert.Equal(!committed, (await reads.Find(pid, id)).Value.HasValue);
            Assert.Equal(committed ? 1 : 0, (await SpecificationEvents(db, DeleteAction)).Count);
        }
    }

    private static async Task<DraftSpecification> PopulateSpecificationForDeletion(SpecificationOperations ops,
        ClaimsPrincipal user, Guid productId, OwnerVersions linked, string assignee, CreateClock clock)
    {
        var draft = (await ops.Create(user, productId, "DEV-1", "Title", "Description")).Value;
        var id = draft.InternalId;
        Assert.True((await ops.EditLinks(user, productId, id, linked.Links)).IsSuccess);
        Assert.True((await ops.SubmitAiReview(user, productId, id, new SpecificationAiReviewInput.Entered("# Review"), true)).IsSuccess);
        Assert.True((await ops.ChangeAssignment(user, productId, id, assignee)).IsSuccess);
        clock.Now = CreateTime.AddTicks(13);
        Assert.True((await ops.ChangeAssignment(user, productId, id, null)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, productId, id)).IsSuccess);
        Assert.True((await ops.Approve(user, productId, id)).IsSuccess);
        Assert.True((await ops.SaveEvidence(user, productId, id, "# Report", EvidenceUrl)).IsSuccess);
        Assert.True((await ops.Reopen(user, productId, id, "Revise", true)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, productId, id)).IsSuccess);
        Assert.True((await ops.Approve(user, productId, id)).IsSuccess);
        return draft;
    }

    private static async Task<long[]> DeletionRowCounts(Database db, Guid id)
    {
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT ARRAY[
                (SELECT count(*) FROM retravion_specification.specification WHERE internal_id = $1),
                (SELECT count(*) FROM retravion_specification.link WHERE specification_internal_id = $1),
                (SELECT count(*) FROM retravion_specification.assignment_history WHERE specification_internal_id = $1),
                (SELECT count(*) FROM retravion_specification.ai_review WHERE specification_internal_id = $1),
                (SELECT count(*) FROM retravion_specification.evidence WHERE specification_internal_id = $1)]
            """, connection);
        query.Parameters.AddWithValue(id);
        return (long[])(await query.ExecuteScalarAsync())!;
    }
}
