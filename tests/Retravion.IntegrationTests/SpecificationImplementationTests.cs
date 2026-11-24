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
    private const string ImplementAction = "specification.implement";

    [PostgresFact]
    public async Task SpecificationImplementationRequiresApprovedCurrentValidEvidenceAndAuthorizedAffirmativeAttestation()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var pid = product.InternalId;
        var other = (await Product(products, "other")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var user = await Principal(sessions, "User", now: CreateTime);
        var restricted = await Principal(sessions, "Administrator", restricted: true, now: CreateTime);
        var id = (await ops.Create(user, pid, "DEV-1", "Title", "Description")).Value.InternalId;
        Assert.Contains("Only Approved", (await ops.Implement(user, pid, id, true)).Error);
        Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
        Assert.Contains("Only Approved", (await ops.Implement(user, pid, id, true)).Error);
        Assert.True((await ops.Approve(user, pid, id)).IsSuccess);
        Assert.Contains("Current evidence", (await ops.Implement(user, pid, id, true)).Error);
        Assert.True((await ops.SaveEvidence(user, pid, id, "# Report", null)).IsSuccess);
        Assert.Contains("HTTPS PR URL", (await ops.Implement(user, pid, id, true)).Error);
        Assert.True((await ops.SaveEvidence(user, pid, id, "# Report", EvidenceUrl)).IsSuccess);
        Assert.True((await ops.Reopen(user, pid, id, "Revise", true)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
        Assert.True((await ops.Approve(user, pid, id)).IsSuccess);
        Assert.Contains("Current evidence", (await ops.Implement(user, pid, id, true)).Error);
        Assert.True((await ops.ConfirmEvidenceCurrent(user, pid, id, true)).IsSuccess);

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var corrupt = new NpgsqlCommand("""
            UPDATE retravion_specification.evidence SET pull_request_url = 'http://forge.example/pr/42';
            """, connection))
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        Assert.Equal("The persisted Specification is invalid.", (await ops.Implement(user, pid, id, true)).Error);
        await using (var blank = new NpgsqlCommand("UPDATE retravion_specification.evidence SET markdown = E' \\t'", connection))
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => blank.ExecuteNonQueryAsync())).SqlState);
        Assert.True((await ops.SaveEvidence(user, pid, id, "# Report", EvidenceUrl)).IsFailure); // Invalid persisted evidence cannot be hydrated.
        await using (var repair = new NpgsqlCommand("UPDATE retravion_specification.evidence SET pull_request_url = $1", connection))
        {
            repair.Parameters.AddWithValue(EvidenceUrl);
            Assert.Equal(1, await repair.ExecuteNonQueryAsync());
        }
        Assert.Contains("Explicitly attest", (await ops.Implement(user, pid, id, false)).Error);
        Assert.Contains("not found", (await ops.Implement(user, other, id, true)).Error);
        Assert.Equal("Access denied.", (await ops.Implement(restricted, pid, id, true)).Error);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await ops.Implement(user, pid, id, true)).Error);
        Assert.IsType<ApprovedSpecification>((await ops.Find(user, pid, id)).Value.Value);
        Assert.Equal(new CreateEvent("Administrator", CreateTime.UtcTicks, "unknown", null, "failure", null),
            Assert.Single(await SpecificationEvents(db, ImplementAction)));
    }

    [PostgresFact]
    public async Task SpecificationImplementationPersistsAttestationAndClearsAssignmentWhileMakingEveryExistingMutationIneligible()
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
        var attester = await Principal(sessions, "Administrator", now: CreateTime);
        var accounts = scope.ServiceProvider.GetRequiredService<IAdministratorStore>();
        var first = await CreateAssignee(accounts, "First.User");
        var second = await CreateAssignee(accounts, "Second.User");
        var draft = (await ops.Create(user, pid, "DEV-1", "Title", "Description")).Value;
        var id = draft.InternalId;
        Assert.True((await ops.EditLinks(user, pid, id, linked.Links)).IsSuccess);
        var review = (await ops.SubmitAiReview(user, pid, id, new SpecificationAiReviewInput.Entered("# Review"), true)).Value;
        Assert.True((await ops.ChangeAssignment(user, pid, id, first.Username)).IsSuccess);
        clock.Now = CreateTime.AddTicks(11);
        Assert.True((await ops.ChangeAssignment(user, pid, id, second.Username)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
        var approved = (await ops.Approve(user, pid, id)).Value.Specification;
        var evidence = (await ops.SaveEvidence(user, pid, id, "# Final evidence", EvidenceUrl)).Value;
        var before = (await ops.FindDetail(user, pid, id)).Value.Value;
        Assert.Equal(second.Username, before.Assignee);

        clock.Now = CreateTime.AddTicks(23);
        var completed = await ops.Implement(attester, pid, id, true);
        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error : null);
        var implemented = completed.Value;
        Assert.Equal(("Administrator", clock.Now), (implemented.MergeAttestedBy, implemented.MergeAttestedAt));
        Assert.Equal((approved.InternalId, approved.ProductInternalId, approved.Id, approved.Title, approved.Description, approved.Links),
            (implemented.InternalId, implemented.ProductInternalId, implemented.Id, implemented.Title, implemented.Description, implemented.Links));
        Assert.Equal(evidence, implemented.Evidence);
        Assert.Equal(implemented, (await ops.Find(user, pid, id)).Value.Value);
        Assert.Equal(implemented, Assert.Single((await ops.List(user, pid)).Value));
        var detail = (await ops.FindDetail(user, pid, id)).Value.Value;
        Assert.Equal(implemented, detail.Specification);
        Assert.Null(detail.Assignee);
        Assert.Equal(before.AssignmentHistory.Append(new("Administrator", clock.Now, second.Username, null)), detail.AssignmentHistory);
        Assert.Equal(review, detail.CurrentAiReview);
        Assert.Equal(evidence, detail.Evidence);
        var snapshot = new SpecificationAuditValues(pid, draft.Id, draft.Title, draft.Description, "Approved");
        Assert.Equal(new CreateEvent("Administrator", clock.Now.UtcTicks, "identified", id.ToString(), "success",
            new(id, snapshot, snapshot with { Status = "Implemented" }, Assignment: new(second.Username, null), MergeAttested: true)),
            Assert.Single(await SpecificationEvents(db, ImplementAction)));

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var eventCount = new NpgsqlCommand("SELECT count(*) FROM retravion_audit.event WHERE action LIKE 'specification.%'", connection);
        var beforeAttempts = await eventCount.ExecuteScalarAsync();
        Assert.True((await ops.Create(user, pid, draft.Id, "Duplicate", null)).IsFailure); // New identity only; cannot recreate this ID.
        Assert.Contains("Only Draft", (await ops.EditContent(user, pid, id, "NEW-ID", "Changed", "New description")).Error);
        Assert.Contains("Only Draft", (await ops.EditLinks(user, pid, id, [])).Error);
        Assert.Contains("Assignment cannot", (await ops.ChangeAssignment(user, pid, id, first.Username)).Error);
        Assert.Contains("Assignment cannot", (await ops.ChangeAssignment(user, pid, id, null)).Error);
        Assert.Contains("only be submitted", (await ops.SubmitAiReview(user, pid, id, new SpecificationAiReviewInput.Entered("# Changed"), true)).Error);
        Assert.Contains("only be submitted", (await ops.SubmitAiReview(user, pid, id, new SpecificationAiReviewInput.Uploaded("# Changed"u8.ToArray()), true)).Error);
        Assert.Contains("Approved", (await ops.SaveEvidence(user, pid, id, "# Changed", null)).Error);
        Assert.Contains("Approved", (await ops.ConfirmEvidenceCurrent(user, pid, id, true)).Error);
        Assert.Contains("Only Draft", (await ops.SubmitForReview(user, pid, id)).Error);
        Assert.Contains("Only In Review", (await ops.ReturnToDraft(user, pid, id, "Changed")).Error);
        Assert.Contains("Only In Review", (await ops.Approve(user, pid, id)).Error);
        Assert.Contains("Only Approved", (await ops.Reopen(user, pid, id, "Changed", true)).Error);
        Assert.Contains("Only Approved", (await ops.Implement(user, pid, id, true)).Error);
        var afterAttempts = (await ops.FindDetail(user, pid, id)).Value.Value;
        Assert.Equal(detail.Specification, afterAttempts.Specification);
        Assert.Equal(detail.AssignmentHistory, afterAttempts.AssignmentHistory);
        Assert.Equal(detail.CurrentAiReview, afterAttempts.CurrentAiReview);
        Assert.Equal(detail.Evidence, afterAttempts.Evidence);
        Assert.Equal(beforeAttempts, await eventCount.ExecuteScalarAsync());

        await AssertReopenBlocked(owners, linked);
        var successors = await owners.Approve(await owners.Successors(linked));
        AssertReopened(successors, await owners.Reopen(successors));
        AssertUnchanged(linked, await owners.Find(linked, exact: true));
    }

    [PostgresFact]
    public async Task SpecificationImplementationRollsBackAttestationStateAndAssignmentWhenAuditFails()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var account = await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "First.User");
        var id = (await ops.Create(user, pid, "DEV-1", "Title", "Description")).Value.InternalId;
        Assert.True((await ops.ChangeAssignment(user, pid, id, account.Username)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
        var approved = (await ops.Approve(user, pid, id)).Value.Specification;
        var evidence = (await ops.SaveEvidence(user, pid, id, "# Report", EvidenceUrl)).Value;
        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        Assert.Equal("The Specification operation could not be completed.", (await ops.Implement(user, pid, id, true)).Error);
        var detail = (await ops.FindDetail(user, pid, id)).Value.Value;
        Assert.Equal(approved, detail.Specification); // Mapping also rejects stray attestation fields in Approved.
        Assert.Equal(evidence, detail.Evidence);
        Assert.Equal(account.Username, detail.Assignee);
        Assert.Equal(new SpecificationAssignmentEntry("User", CreateTime, null, account.Username), Assert.Single(detail.AssignmentHistory));
        Assert.Empty(await SpecificationEvents(db, ImplementAction));
    }

    [PostgresFact]
    public async Task SpecificationImplementationUpgradesApprovedStorageAndRejectsCorruptFinalizedRecords()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var writes = new PostgresSpecificationWriteStore(db.ConnectionString);
        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        var draft = DraftSpecification.Create(pid, "DEV-1", "Title", "Description").Value;
        var id = draft.InternalId;
        static Task<bool> Authorized() => Task.FromResult(true);
        Assert.True((await writes.Create(draft, CreateActor, CreateTime, Authorized)).IsSuccess);
        Assert.True((await writes.SubmitForReview(pid, id, CreateActor, CreateTime, Authorized)).IsSuccess);
        var approved = (await writes.Approve(pid, id, CreateActor, CreateTime, Authorized)).Value.Specification;
        var evidence = (await writes.SaveEvidence(pid, id, "# Report", EvidenceUrl, CreateActor, CreateTime, Authorized)).Value;

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        async Task Execute(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        await Execute("""
            ALTER TABLE retravion_specification.specification DROP CONSTRAINT specification_implementation_check,
                DROP COLUMN merge_attested_by, DROP COLUMN merge_attested_at_utc_ticks,
                DROP CONSTRAINT specification_status_check,
                ADD CONSTRAINT specification_status_check CHECK (status IN ('Draft', 'In Review', 'Approved'));
            """);
        var upgraded = (await reads.FindDetail(pid, id)).Value.Value;
        Assert.Equal(approved, upgraded.Specification);
        Assert.Equal(evidence, upgraded.Evidence);
        Assert.True((await writes.Implement(pid, id, true, CreateActor, CreateTime, Authorized)).IsSuccess);

        await Execute("UPDATE retravion_specification.evidence SET is_outdated = true");
        const string invalid = "The persisted Specification is invalid.";
        Assert.Equal(invalid, (await reads.Find(pid, id)).Error);
        Assert.Equal(invalid, (await reads.FindDetail(pid, id)).Error);
        Assert.Equal(invalid, (await reads.List(pid)).Error);
        await Execute("DELETE FROM retravion_specification.evidence");
        Assert.Equal(invalid, (await reads.Find(pid, id)).Error);
        Assert.Single(await SpecificationEvents(db, ImplementAction));
    }

    [PostgresFact]
    public async Task SpecificationImplementationUncertainCommitIsNotRetriedAndDoesNotAppendHistoryForUnassignedRecords()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var setup = new PostgresSpecificationWriteStore(db.ConnectionString);
        var reads = new PostgresSpecificationReadStore(db.ConnectionString);
        static Task<bool> Authorized() => Task.FromResult(true);
        foreach (var committed in new[] { false, true })
        {
            var draft = DraftSpecification.Create(pid, $"DEV-{committed}", "Title", "Description").Value;
            var id = draft.InternalId;
            Assert.True((await setup.Create(draft, CreateActor, CreateTime, Authorized)).IsSuccess);
            Assert.True((await setup.SubmitForReview(pid, id, CreateActor, CreateTime, Authorized)).IsSuccess);
            Assert.True((await setup.Approve(pid, id, CreateActor, CreateTime, Authorized)).IsSuccess);
            Assert.True((await setup.SaveEvidence(pid, id, "# Report", EvidenceUrl, CreateActor, CreateTime, Authorized)).IsSuccess);
            var attempts = 0;
            var writes = new PostgresSpecificationWriteStore(db.ConnectionString, async (transaction, token) =>
            {
                attempts++;
                if (committed) await transaction.CommitAsync(token);
                throw new IOException("private provider details");
            });
            var result = await writes.Implement(pid, id, true, CreateActor, CreateTime, Authorized);
            Assert.Contains("outcome could not be confirmed", result.Error);
            Assert.DoesNotContain("private", result.Error);
            Assert.Equal(1, attempts);
            var detail = (await reads.FindDetail(pid, id)).Value.Value;
            Assert.Empty(detail.AssignmentHistory);
            Assert.IsType(committed ? typeof(ImplementedSpecification) : typeof(ApprovedSpecification), detail.Specification);
            var events = await SpecificationEvents(db, ImplementAction);
            if (committed)
            {
                var audit = Assert.Single(events);
                Assert.Equal(id.ToString(), audit.Target);
                Assert.Null(audit.Change!.Assignment);
            }
            else Assert.Empty(events);
        }
    }
}
