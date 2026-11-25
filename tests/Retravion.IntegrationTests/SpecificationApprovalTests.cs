using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task SpecificationApprovalAndReopeningPreserveLinksAuditStateAndRequireHumanAttestation()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await Product(products, "main")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var versions = await owners.Approve(await owners.Create(product));
        _ = await owners.Successors(versions); // Draft successors do not change approval of the linked exact versions.
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await ops.Create(principal, product, "DEV", "Title", "# Description")).Value;
        Assert.Contains("Only In Review", (await ops.Approve(principal, product, draft.InternalId)).Error);
        draft = (await ops.EditLinks(principal, product, draft.InternalId, [versions.Links[1]])).Value;
        Assert.True((await ops.SubmitForReview(principal, product, draft.InternalId)).IsSuccess);
        Assert.Contains("Only Approved", (await ops.Reopen(principal, product, draft.InternalId, "Reason", true)).Error);
        clock.Now = CreateTime.AddMinutes(1);
        var approval = (await ops.Approve(principal, product, draft.InternalId)).Value;
        Assert.Equal(new[] { SpecificationLinkKind.UserNeed, SpecificationLinkKind.TestCase }, approval.MissingLinkTypes);
        Assert.Equal(draft.Links, approval.Specification.Links);
        Assert.Equal(approval.Specification, (await ops.Find(principal, product, draft.InternalId)).Value.Value);
        Assert.Equal(approval.Specification, Assert.Single((await ops.List(principal, product)).Value));
        Assert.Contains("Only In Review", (await ops.Approve(principal, product, draft.InternalId)).Error);
        Assert.Contains("Only In Review", (await ops.ReturnToDraft(principal, product, draft.InternalId, "Reason")).Error);
        Assert.Contains("reason is required", (await ops.Reopen(principal, product, draft.InternalId, " \t", true)).Error);
        Assert.Contains("valid UTF-8", (await ops.Reopen(principal, product, draft.InternalId, "\0", true)).Error);
        Assert.Contains("attest", (await ops.Reopen(principal, product, draft.InternalId, "Reason", false)).Error);
        Assert.Empty(await SpecificationEvents(db, "specification.reopen"));
        Assert.Equal(approval.Specification, (await ops.Find(principal, product, draft.InternalId)).Value.Value);

        var review = new SpecificationAuditValues(product, draft.Id, draft.Title, draft.Description, "In Review");
        var approved = review with { Status = "Approved" };
        Assert.Equal(TransitionEvent(draft.InternalId, clock.Now, review, approved),
            Assert.Single(await SpecificationEvents(db, "specification.approve")));
        clock.Now = CreateTime.AddMinutes(2);
        const string reason = "  Update implementation details.  ";
        Assert.Equal(draft, (await ops.Reopen(principal, product, draft.InternalId, reason, true)).Value);
        Assert.Equal(draft, (await ops.Find(principal, product, draft.InternalId)).Value.Value);
        Assert.Equal(new CreateEvent("User", clock.Now.UtcTicks, "identified", draft.InternalId.ToString(), "success",
                new(draft.InternalId, approved, review with { Status = "Draft" }, reason, true)),
            Assert.Single(await SpecificationEvents(db, "specification.reopen")));
        Assert.Contains("Only In Review", (await ops.Approve(principal, product, draft.InternalId)).Error);
        Assert.True((await ops.SubmitForReview(principal, product, draft.InternalId)).IsSuccess);
        Assert.Equal(approval.Specification, (await ops.Approve(principal, product, draft.InternalId)).Value.Specification);
        Assert.Equal(2, (await SpecificationEvents(db, "specification.approve")).Count);
    }

    [PostgresFact]
    public async Task SpecificationApprovalRejectsUnapprovedMissingAndForeignExactTargets()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await Product(products, "main")).InternalId;
        var foreign = (await Product(products, "foreign")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var local = await owners.Approve(await owners.Create(product));
        var other = await owners.Approve(await owners.Create(foreign));
        var successors = await owners.Successors(local);
        var store = new PostgresSpecificationWriteStore(db.ConnectionString);
        var read = new PostgresSpecificationReadStore(db.ConnectionString);
        await using var seed = new NpgsqlConnection(db.ConnectionString);
        await seed.OpenAsync();
        Assert.Empty((await read.List(product)).Value);
        foreach (var target in successors.Links.Concat(other.Links)
            .Concat(Enum.GetValues<SpecificationLinkKind>().Select(kind => new SpecificationLink(kind, Guid.NewGuid()))))
        {
            var id = Guid.NewGuid();
            await Seed(seed, product, id, id.ToString(), "Description");
            // Also cover invalid persisted references that the Draft link command would refuse.
            await using var insert = new NpgsqlCommand("INSERT INTO retravion_specification.link VALUES ($1, $2, $3)", seed);
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue((short)target.Kind);
            insert.Parameters.AddWithValue(target.VersionId);
            await insert.ExecuteNonQueryAsync();
            await SetReopenSeedStatus(seed, id, "In Review");
            Assert.Contains($"linked {target.Kind} version must be Approved in this Product",
                (await store.Approve(product, id, Assert.IsType<InReviewSpecification>((await read.Find(product, id)).Value.Value), CreateActor, CreateTime, ApprovalAuthorized)).Error);
            Assert.IsType<InReviewSpecification>((await read.Find(product, id)).Value.Value);
        }
        Assert.Empty(await SpecificationEvents(db, "specification.approve"));
    }

    [PostgresFact]
    public async Task SpecificationApprovalAndReopeningRollBackOnAuditFailureAndRespectAccessAndRetirement()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "main");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var principal = await Principal(sessions, "User", now: CreateTime);
        var revoked = await Principal(sessions, "User", now: CreateTime);
        var review = (await ops.Create(principal, product.InternalId, "REVIEW", "Review", "Description")).Value.InternalId;
        var approved = (await ops.Create(principal, product.InternalId, "APPROVED", "Approved", "Description")).Value.InternalId;
        Assert.True((await ops.SubmitForReview(principal, product.InternalId, review)).IsSuccess);
        Assert.True((await ops.SubmitForReview(principal, product.InternalId, approved)).IsSuccess);
        Assert.Equal(Enum.GetValues<SpecificationLinkKind>(), (await ops.Approve(principal, product.InternalId, approved)).Value.MissingLinkTypes);
        await sessions.RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        Assert.Equal("Access denied.", (await ops.Approve(revoked, product.InternalId, review)).Error);
        Assert.Equal("Access denied.", (await ops.Reopen(revoked, product.InternalId, approved, "Secret", true)).Error);
        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        Assert.Equal("The Specification operation could not be completed.", (await ops.Approve(principal, product.InternalId, review)).Error);
        Assert.Equal("The Specification operation could not be completed.", (await ops.Reopen(principal, product.InternalId, approved, "Reason", true)).Error);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await ops.Approve(principal, product.InternalId, review)).Error);
        Assert.Contains("retired", (await ops.Reopen(principal, product.InternalId, approved, "Reason", true)).Error);
        Assert.IsType<InReviewSpecification>((await ops.Find(principal, product.InternalId, review)).Value.Value);
        Assert.IsType<ApprovedSpecification>((await ops.Find(principal, product.InternalId, approved)).Value.Value);
        var denial = new CreateEvent("User", CreateTime.UtcTicks, "unknown", null, "failure", null);
        var approvals = await SpecificationEvents(db, "specification.approve");
        Assert.Equal(2, approvals.Count);
        Assert.Equal(denial, approvals[1]);
        Assert.Equal(denial, Assert.Single(await SpecificationEvents(db, "specification.reopen")));
    }

    [PostgresFact]
    public async Task SpecificationApprovalCompetesWithEveryOwnerReopeningInBothCommitOrders()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var regularOwners = new ReopenOwners(db.ConnectionString);
        var read = new PostgresSpecificationReadStore(db.ConnectionString);
        var regularWrites = new PostgresSpecificationWriteStore(db.ConnectionString);
        var count = 0;
        foreach (var kind in Enum.GetValues<SpecificationLinkKind>())
        foreach (var approvalFirst in new[] { true, false })
        {
            var product = (await Product(products, $"race-{++count}")).InternalId;
            var versions = await regularOwners.Approve(await regularOwners.Create(product));
            var draft = DraftSpecification.Create(product, "DEV", "Title", "Description").Value;
            Assert.True((await regularWrites.Create(draft, CreateActor, CreateTime, ApprovalAuthorized)).IsSuccess);
            Assert.True((await regularWrites.EditLinks(product, draft.InternalId, draft.Links, versions.Links, CreateActor, CreateTime, ApprovalAuthorized)).IsSuccess);
            var submitted = (await regularWrites.SubmitForReview(product, draft.InternalId, CreateActor, CreateTime, ApprovalAuthorized)).Value;

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task PauseCommit(NpgsqlTransaction transaction, CancellationToken token)
            {
                ready.SetResult(transaction.Connection!.ProcessID);
                await release.Task.WaitAsync(token);
                await transaction.CommitAsync(token);
            }
            var writes = approvalFirst ? new PostgresSpecificationWriteStore(db.ConnectionString, PauseCommit) : regularWrites;
            var owners = approvalFirst ? regularOwners : new ReopenOwners(db.ConnectionString, PauseCommit);
            var approval = approvalFirst ? StartApproval() : null;
            var reopen = approvalFirst ? null : StartReopen();
            try
            {
                var blocker = await ready.Task.WaitAsync(deadline.Token);
                approval ??= StartApproval();
                reopen ??= StartReopen();
                await using var observer = new NpgsqlConnection(db.ConnectionString);
                await observer.OpenAsync(deadline.Token);
                await using var blocked = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND $1 = ANY(pg_blocking_pids(pid)))
                    """, observer);
                blocked.Parameters.AddWithValue(blocker);
                while (!approval.IsCompleted && !reopen.IsCompleted && await blocked.ExecuteScalarAsync(deadline.Token) is not true)
                    await Task.Delay(20, deadline.Token);
                Assert.False(approval.IsCompleted);
                Assert.False(reopen.IsCompleted);
            }
            finally { release.TrySetResult(); }

            var approved = await approval;
            var reopenError = await reopen;
            var current = (await read.Find(product, draft.InternalId)).Value.Value;
            if (approvalFirst)
            {
                Assert.Empty(approved.Value.MissingLinkTypes);
                Assert.Equal(ReopenBlocked, reopenError);
                Assert.Equal(approved.Value.Specification, current);
                AssertUnchanged(versions, await regularOwners.Find(versions, exact: true));
                Assert.True((await regularWrites.Reopen(product, draft.InternalId, "Release links", true,
                    CreateActor, CreateTime, ApprovalAuthorized)).IsSuccess);
                Assert.Null(await regularOwners.ReopenTarget(kind, versions, CancellationToken.None));
            }
            else
            {
                Assert.Null(reopenError);
                Assert.Contains($"linked {kind} version must be Approved", approved.Error);
                Assert.IsType<InReviewSpecification>(current);
                var target = versions.Links.Single(link => link.Kind == kind);
                Assert.False((await read.ResolveVersion(product, target)).Value.Value.IsApproved);
            }

            Task<Result<SpecificationApproval>> StartApproval() =>
                writes.Approve(product, draft.InternalId, submitted, CreateActor, CreateTime, ApprovalAuthorized, deadline.Token);
            Task<string?> StartReopen() => owners.ReopenTarget(kind, versions, deadline.Token);
        }
        Assert.Equal(3, (await SpecificationEvents(db, "specification.approve")).Count);
    }

    [PostgresFact]
    public async Task StaleLinkEditorAndReviewerCannotChangeAnotherSessionsWork()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "main")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var links = (await owners.Approve(await owners.Create(pid))).Links;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var author = await Principal(sessions, "User", now: CreateTime);
        var reviewer = await Principal(sessions, "Administrator", now: CreateTime);
        var draft = (await ops.Create(author, pid, "SPEC", "Title", "First description")).Value;
        var id = draft.InternalId;
        var initial = (await ops.EditLinks(author, pid, id, draft.Links, links.Take(1))).Value;
        var opened = (await ops.Find(reviewer, pid, id)).Value.Value.Links;
        var updated = (await ops.EditLinks(author, pid, id, initial.Links, links.Take(2))).Value;
        var events = await SpecificationEvents(db, "specification.edit-links");
        var staleSave = await ops.EditLinks(reviewer, pid, id, opened, [links[0], links[2]]);
        Assert.Contains("Links changed", staleSave.Error);
        Assert.Equal(updated.Links, (await ops.Find(author, pid, id)).Value.Value.Links);
        Assert.Equal(events.Count, (await SpecificationEvents(db, "specification.edit-links")).Count);

        Assert.True((await ops.SubmitForReview(author, pid, id)).IsSuccess);
        foreach (var changeLinks in new[] { false, true })
        {
            var seen = Assert.IsType<InReviewSpecification>((await ops.Find(reviewer, pid, id)).Value.Value);
            var reopened = (await ops.ReturnToDraft(author, pid, id, null)).Value;
            if (changeLinks) Assert.True((await ops.EditLinks(author, pid, id, reopened.Links, links)).IsSuccess);
            else Assert.True((await ops.EditContent(author, pid, reopened, reopened.Id, reopened.Title, "Revised description")).IsSuccess);
            var resubmitted = (await ops.SubmitForReview(author, pid, id)).Value;
            Assert.Contains("Review the refreshed", (await ops.Approve(reviewer, pid, id, seen)).Error);
            Assert.Equal(resubmitted, (await ops.Find(reviewer, pid, id)).Value.Value);
            Assert.Empty(await SpecificationEvents(db, "specification.approve"));
        }
        var refreshed = Assert.IsType<InReviewSpecification>((await ops.Find(reviewer, pid, id)).Value.Value);
        Assert.True((await ops.Approve(reviewer, pid, id, refreshed)).IsSuccess);
        Assert.Single(await SpecificationEvents(db, "specification.approve"));
    }

    private static Task<bool> ApprovalAuthorized() => Task.FromResult(true);
}
