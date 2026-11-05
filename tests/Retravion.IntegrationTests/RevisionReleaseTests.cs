using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Application.Revision;
using Retravion.Application.TestCase;
using Retravion.Application.UserNeed;
using Retravion.Domain.Product;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Revision;
using Retravion.Infrastructure.TestCase;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.TestCase;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private sealed record ReleaseGraph(Product Product, RevisionSnapshot Row, UserNeedSnapshot Need1, UserNeedSnapshot Need2,
        RequirementSnapshot Parent1, RequirementSnapshot Parent2, RequirementSnapshot Child, TestCaseSnapshot Case1, TestCaseSnapshot Case2);

    private static async Task<ReleaseGraph> PrepareRelease(string connection)
    {
        var product = (await new CreateProduct(new PostgresProductStore(connection)).Execute("P-" + Guid.NewGuid(), "Release Product", ProductKind.System, "")).Value;
        var needs = new PostgresUserNeedStore(connection);
        var n1 = await ApprovedNeed(needs, product.InternalId, "N");
        var n2 = (await needs.CreateSuccessor(n1, "Second exact version", RevActor, RevTime)).Value;
        n2 = (await needs.Update(n2, n2.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
        n2 = (await needs.Update(n2, n2.Need.Approve().Value, RevActor, RevTime, "Approved")).Value;
        var requirements = new PostgresRequirementStore(connection);
        var parent = await ParentDraft(requirements, product.InternalId, "Parent");
        parent = (await requirements.ChangeLink(parent, new(n1.Need.InternalId, n1.Need.VersionId), true, RevActor, RevTime)).Value;
        parent = await ParentApprove(requirements, parent);
        var parent2 = await ParentApprove(requirements, (await requirements.CreateSuccessor(parent, "Second parent", RevActor, RevTime)).Value);
        var child = await ParentDraft(requirements, product.InternalId, "Child");
        child = (await requirements.AddParent(child, ParentEndpoint(parent), RevActor, RevTime)).Value;
        child = (await requirements.ChangeLink(child, new(n2.Need.InternalId, n2.Need.VersionId), true, RevActor, RevTime)).Value;
        child = await ParentApprove(requirements, child);
        var cases = new PostgresTestCaseStore(connection);
        async Task<TestCaseSnapshot> Root(string title)
        {
            var value = (await cases.Create(product.InternalId, CaseContent(title), RevActor, RevTime)).Value;
            value = (await cases.ChangeLink(value, "add", CaseEndpoint(child), null, RevActor, RevTime)).Value;
            return await CaseApprove(cases, value);
        }
        var c1 = await Root("First root"); var c2 = await Root("Shared root");
        var revisions = new PostgresRevisionStore(connection);
        var row = await RevCreate(revisions, product.InternalId);
        foreach (var endpoint in new[] {
            new RevisionSelection(RevisionEntityKind.TestCase, c1.TestCase.InternalId, c1.TestCase.VersionId, 1),
            new RevisionSelection(RevisionEntityKind.TestCase, c2.TestCase.InternalId, c2.TestCase.VersionId, 1),
            new RevisionSelection(RevisionEntityKind.Requirement, parent2.Requirement.InternalId, parent2.Requirement.VersionId, 2) })
            row = (await revisions.Select(Guid.NewGuid(), row, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
        return new(product, row, n1, n2, parent, parent2, child, c1, c2);
    }

    private static async Task<RevisionBaseline> ReleasePreview(PostgresRevisionStore store, RevisionSnapshot row) =>
        (await store.Baseline(row.Value.ProductInternalId, row.Value.InternalId)).Value;
    private static Task<Result<RevisionSnapshot>> ReleaseNow(PostgresRevisionStore store, RevisionBaseline preview, Guid? operation = null) =>
        store.Release(operation ?? Guid.NewGuid(), preview.Revision, preview.Token, RevActor, RevTime, RevAllowed);

    [PostgresFact]
    public async Task ReleaseFreezesCompleteSharedMultiVersionClosureProtectsEveryTypeAndLeavesIncomingOwnersIndependent()
    {
        await using var db = await Database.Create();
        var graph = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var needs = new PostgresUserNeedStore(db.ConnectionString); var requirements = new PostgresRequirementStore(db.ConnectionString);
        var cases = new PostgresTestCaseStore(db.ConnectionString);
        var incoming = await ParentDraft(requirements, graph.Product.InternalId, "Incoming");
        var needEndpoint = new Retravion.Domain.Requirement.RequirementUserNeedLink(graph.Need2.Need.InternalId, graph.Need2.Need.VersionId);
        incoming = (await requirements.ChangeLink(incoming, needEndpoint, true, RevActor, RevTime)).Value;
        incoming = (await requirements.AddParent(incoming, ParentEndpoint(graph.Child), RevActor, RevTime)).Value;
        var incomingCase = (await cases.Create(graph.Product.InternalId, CaseContent("Incoming only"), RevActor, RevTime)).Value;
        incomingCase = (await cases.ChangeLink(incomingCase, "add", CaseEndpoint(graph.Child), null, RevActor, RevTime)).Value;
        var preview = await ReleasePreview(store, graph.Row);
        Assert.Equal(7, preview.Members.Count); Assert.Equal(3, preview.Members.Count(m => m.Explicit));
        Assert.All(preview.Members, m => Assert.Equal("Approved", m.Status));
        Assert.Equal(new[] { 1, 2 }, preview.Members.Where(m => m.Endpoint.InternalId == graph.Need1.Need.InternalId).Select(m => m.Endpoint.VersionNumber).Order());
        Assert.Equal(new[] { 1, 2 }, preview.Members.Where(m => m.Endpoint.InternalId == graph.Parent1.Requirement.InternalId).Select(m => m.Endpoint.VersionNumber).Order());
        Assert.DoesNotContain(preview.Members, m => m.Endpoint.InternalId == incoming.Requirement.InternalId || m.Endpoint.InternalId == incomingCase.TestCase.InternalId);
        var eventCount = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        var operation = Guid.NewGuid();
        var result = await ReleaseNow(store, preview, operation); Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        var released = Assert.IsType<ReleasedRevision>(result.Value.Value);
        Assert.Equal(graph.Row.Revision + 1, result.Value.Revision);
        Assert.Equal(graph.Row.Value.Selections.ToArray(), released.Selections.ToArray());
        Assert.Equal(RevActor is AuditActor.Identified a ? a.Identifier : "", released.ReleasedBy); Assert.Equal(RevTime.UtcTicks, released.ReleasedAt.UtcTicks);
        var events = await new PostgresAuditQuery(db.ConnectionString).FindAsync(new());
        Assert.Equal(eventCount + 1, events.Count); // Protection alone is not a per-member edit.
        var audit = Assert.Single(events, e => e.Action == "revision.release");
        Assert.Equal(RevActor, audit.Actor); Assert.Equal(RevTime.UtcTicks, audit.OccurredAt.UtcTicks); Assert.Equal(AuditActionOutcome.Success, audit.Outcome);
        Assert.Equal(new AuditTarget.Identified(released.InternalId.ToString()), audit.Target);
        var change = audit.RevisionChange!;
        Assert.Equal(operation, change.OperationId); Assert.Equal(graph.Product.InternalId, change.ProductInternalId);
        Assert.Equal("Draft", change.Before!.State); Assert.Equal("Released", change.After!.State);
        Assert.Equal(released.Identifier, change.After.Identifier); Assert.Equal(released.Description, change.After.Description);
        Assert.Equal(released.Members.ToArray(), change.After.Release!.Members.ToArray());
        var history = (await store.History(graph.Product.InternalId, released.InternalId)).Value;
        Assert.Equal(JsonSerializer.Serialize(change), JsonSerializer.Serialize(history[^1].Change));
        Assert.Equal(operation, (await store.Retrieve(graph.Product.InternalId, released.InternalId, operation)).Value.Operation!.Change.OperationId);
        Assert.True((await needs.Update(graph.Need2, graph.Need2.Need.Reopen("Protected").Value, RevActor, RevTime, "Reopened", "Protected")).IsFailure);
        Assert.True((await requirements.Update(graph.Child, graph.Child.Requirement.Reopen("Protected").Value, RevActor, RevTime, "Reopened", "Protected")).IsFailure);
        Assert.True((await cases.Reopen(graph.Case1, "Protected", RevActor, RevTime)).IsFailure);
        Assert.True((await cases.Update(graph.Case1, CaseContent("Forbidden"), RevActor, RevTime)).IsFailure);
        Assert.True((await cases.ChangeLink(graph.Case1, "remove", CaseEndpoint(graph.Child), null, RevActor, RevTime)).IsFailure);
        Assert.True((await cases.Review(graph.Case1, "Draft", null, RevActor, RevTime)).IsFailure);
        Assert.True((await requirements.ChangeLink(graph.Child, needEndpoint, false, RevActor, RevTime)).IsFailure);
        Assert.True((await requirements.RemoveParent(graph.Child, RevActor, RevTime)).IsFailure);
        Assert.True((await store.Edit(Guid.NewGuid(), result.Value, "Changed", "", RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Delete(Guid.NewGuid(), result.Value, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Select(Guid.NewGuid(), result.Value, "remove", released.Selections[0], null, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await ReleaseNow(store, preview)).IsFailure);
        var forged = result.Value with { Value = DraftRevision.Restore(released.InternalId, released.ProductInternalId, released.Identifier, released.Description, released.Selections).Value };
        Assert.True((await store.Delete(Guid.NewGuid(), forged, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Equal(eventCount + 1, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
        // Sharing protection with another release cannot remove or duplicate entity-edit evidence.
        var shared = await RevCreate(store, graph.Product.InternalId, "Shared revision");
        shared = (await store.Select(Guid.NewGuid(), shared, "add", released.Selections[0], null, RevActor, RevTime, RevAllowed)).Value;
        Assert.True((await ReleaseNow(store, await ReleasePreview(store, shared))).IsSuccess);
        incoming = (await requirements.ChangeLink(incoming, needEndpoint, false, RevActor, RevTime)).Value;
        incoming = (await requirements.RemoveParent(incoming, RevActor, RevTime)).Value;
        Assert.Empty((await requirements.Links(incoming.Requirement.InternalId, incoming.Requirement.VersionId)).Value);
        Assert.True((await requirements.Parent(incoming.Requirement.InternalId, incoming.Requirement.VersionId)).Value.HasNoValue);
        incomingCase = (await cases.ChangeLink(incomingCase, "remove", CaseEndpoint(graph.Child), null, RevActor, RevTime)).Value;
        Assert.Empty(incomingCase.TestCase.Links);
        var n3 = (await needs.CreateSuccessor(graph.Need2, "Allowed successor", RevActor, RevTime)).Value;
        n3 = (await needs.Update(n3, n3.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
        Assert.True((await needs.Update(n3, n3.Need.Approve().Value, RevActor, RevTime, "Approved")).IsSuccess);
        await ParentApprove(requirements, (await requirements.CreateSuccessor(graph.Child, "Allowed successor", RevActor, RevTime)).Value);
        await CaseApprove(cases, (await cases.CreateSuccessor(graph.Case1, "Allowed successor", RevActor, RevTime)).Value);
        var frozen = (await new PostgresRevisionStore(db.ConnectionString).Baseline(graph.Product.InternalId, released.InternalId)).Value;
        Assert.Equal(JsonSerializer.Serialize(preview.Members), JsonSerializer.Serialize(frozen.Members));
        Assert.IsType<ReleasedRevision>(frozen.Revision.Value);
        // Frozen reads must not need outgoing storage at all; rollback the test-only rename afterwards.
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        await using var t = await c.BeginTransactionAsync();
        await using (var rename = new NpgsqlCommand("ALTER TABLE retravion_requirement.parent_link RENAME TO hidden_parent_link", c, t)) await rename.ExecuteNonQueryAsync();
        Assert.Equal(7, (await store.Baseline(graph.Product.InternalId, released.InternalId)).Value.Members.Count);
        await t.RollbackAsync();
    }

    [PostgresFact]
    public async Task EligibleReopeningAcrossAllTypesInvalidatesCurrentApprovalAndStaleConfirmationButNotOwnersOrHistory()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString); var needs = new PostgresUserNeedStore(db.ConnectionString);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var empty = await RevCreate(store, g.Product.InternalId, "Empty");
        Assert.True((await ReleaseNow(store, await ReleasePreview(store, empty))).IsFailure);
        var preview = await ReleasePreview(store, g.Row);
        var n = (await needs.Update(g.Need2, g.Need2.Need.Reopen("Need correction").Value, RevActor, RevTime, "Reopened", "Need correction")).Value;
        Assert.Equal(1, n.LatestApprovedVersionNumber);
        Assert.Equal("Approved", (await requirements.Find(g.Child.Requirement.InternalId)).Value.Value.Requirement.Status.ToString());
        Assert.True((await ReleaseNow(store, preview)).IsFailure);
        var blocked = await ReleaseNow(store, await ReleasePreview(store, g.Row)); Assert.True(blocked.IsFailure); Assert.Contains("entirely Approved", blocked.Error);
        n = (await needs.Update(n, n.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
        n = (await needs.Update(n, n.Need.Approve().Value, RevActor, RevTime, "Approved")).Value;
        Assert.True((await ReleaseNow(store, preview)).IsFailure); // Same closure, changed version revisions.
        var r = (await requirements.Update(g.Child, g.Child.Requirement.Reopen("Requirement correction").Value, RevActor, RevTime, "Reopened", "Requirement correction")).Value;
        Assert.Equal("Approved", (await cases.Find(g.Case1.TestCase.InternalId)).Value.Value.TestCase.Status);
        Assert.True((await ReleaseNow(store, await ReleasePreview(store, g.Row))).IsFailure);
        r = await ParentApprove(requirements, r);
        var approvedHistory = JsonSerializer.Serialize((await cases.History(g.Case1.TestCase.InternalId)).Value);
        var tc = (await cases.Reopen(g.Case1, "Case correction", RevActor, RevTime)).Value;
        Assert.Equal(g.Case1.TestCase.VersionId, tc.TestCase.VersionId); Assert.Equal(1, tc.TestCase.VersionNumber);
        Assert.Null(tc.LatestApprovedVersionNumber); Assert.Equal("Draft", tc.TestCase.Status);
        Assert.True(tc.TestCase.Content.SameContent(g.Case1.TestCase.Content)); Assert.Equal(g.Case1.TestCase.Links.ToArray(), tc.TestCase.Links.ToArray());
        var evidence = (await cases.History(tc.TestCase.InternalId)).Value;
        Assert.Equal(approvedHistory, JsonSerializer.Serialize(evidence.Take(evidence.Count - 1)));
        var reopened = evidence[^1]; Assert.Equal("test-case.reopen", reopened.Action); Assert.Equal("Reopened", reopened.Change.Decision);
        Assert.Equal("Case correction", reopened.Change.Reason); Assert.Equal("Approved", reopened.Change.Before!.Status); Assert.Equal("Draft", reopened.Change.After.Status);
        Assert.Equal(RevActor, reopened.Actor); Assert.Equal(RevTime.UtcTicks, reopened.OccurredAt.UtcTicks);
        Assert.True((await ReleaseNow(store, await ReleasePreview(store, g.Row))).IsFailure);
        tc = await CaseApprove(cases, tc);
        var released = (await ReleaseNow(store, await ReleasePreview(store, g.Row))).Value;
        var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        Assert.True((await needs.Update(n, n.Need.Reopen("Blocked").Value, RevActor, RevTime, "Reopened", "Blocked")).IsFailure);
        Assert.True((await requirements.Update(r, r.Requirement.Reopen("Blocked").Value, RevActor, RevTime, "Reopened", "Blocked")).IsFailure);
        Assert.True((await cases.Reopen(tc, "Blocked", RevActor, RevTime)).IsFailure);
        Assert.True((await cases.Reopen(tc, " ", RevActor, RevTime)).IsFailure);
        Assert.Equal(events, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
        Assert.IsType<ReleasedRevision>(released.Value);
    }

    private static async Task<Result<bool>> ReleaseEntityWrite(string connection, ReleaseGraph g, RevisionEntityKind kind,
        bool successor, Func<NpgsqlTransaction, CancellationToken, Task>? commit = null)
    {
        if (kind == RevisionEntityKind.UserNeed)
        {
            var store = new PostgresUserNeedStore(connection, commit);
            return (successor ? await store.CreateSuccessor(g.Need2, "Next", RevActor, RevTime) :
                await store.Update(g.Need2, g.Need2.Need.Reopen("Correction").Value, RevActor, RevTime, "Reopened", "Correction")).Map(_ => true);
        }
        if (kind == RevisionEntityKind.Requirement)
        {
            var store = new PostgresRequirementStore(connection, commit);
            return (successor ? await store.CreateSuccessor(g.Child, "Next", RevActor, RevTime) :
                await store.Update(g.Child, g.Child.Requirement.Reopen("Correction").Value, RevActor, RevTime, "Reopened", "Correction")).Map(_ => true);
        }
        var cases = new PostgresTestCaseStore(connection, commit);
        return (successor ? await cases.CreateSuccessor(g.Case1, "Next", RevActor, RevTime) :
            await cases.Reopen(g.Case1, "Correction", RevActor, RevTime)).Map(_ => true);
    }

    [PostgresFact]
    public async Task ReleaseSerializesWithReopeningAndSuccessorsAcrossAllTypesInBothCommitOrders()
    {
        await using var db = await Database.Create();
        foreach (var kind in Enum.GetValues<RevisionEntityKind>())
        foreach (var successor in new[] { false, true })
        foreach (var releaseFirst in new[] { false, true })
        {
            var g = await PrepareRelease(db.ConnectionString); var store = new PostgresRevisionStore(db.ConnectionString);
            var preview = await ReleasePreview(store, g.Row);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Held(NpgsqlTransaction t, CancellationToken token)
            { ready.SetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(20), token); await t.CommitAsync(token); }
            Task<Result<RevisionSnapshot>> releasing;
            Task<Result<bool>> changing;
            if (releaseFirst)
            {
                releasing = ReleaseNow(new PostgresRevisionStore(db.ConnectionString, Held), preview);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                changing = ReleaseEntityWrite(db.ConnectionString, g, kind, successor);
                try { await ObserveParentLockWait(db.ConnectionString, changing); }
                finally { finish.TrySetResult(); }
            }
            else
            {
                changing = ReleaseEntityWrite(db.ConnectionString, g, kind, successor, Held);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                releasing = ReleaseNow(store, preview);
                try { await ObserveParentLockWait(db.ConnectionString, releasing); }
                finally { finish.TrySetResult(); }
            }
            Assert.Equal(releaseFirst || successor, (await releasing).IsSuccess);
            Assert.Equal(!releaseFirst || successor, (await changing).IsSuccess);
            var actual = await ReleasePreview(store, g.Row);
            Assert.Equal(releaseFirst || successor ? "Released" : "Draft", actual.Revision.Value.State);
            if (actual.Revision.Value is ReleasedRevision) Assert.All(actual.Members, m => Assert.Equal("Approved", m.Status));
            else Assert.Contains(actual.Members, m => m.Status == "Draft");
        }
    }

    private static async Task<Result<bool>> ReleaseRevisionWrite(string connection, ReleaseGraph g, string action,
        Func<NpgsqlTransaction, CancellationToken, Task>? commit = null)
    {
        if (action == "retire")
            return (await new PostgresProductStore(connection, commit).Update(g.Product, g.Product.Retire().Value)).Map(() => true);
        var store = new PostgresRevisionStore(connection, commit);
        return (action switch
        {
            "edit" => await store.Edit(Guid.NewGuid(), g.Row, "Changed", "Changed metadata", RevActor, RevTime, RevAllowed),
            "selection" => await store.Select(Guid.NewGuid(), g.Row, "remove", g.Row.Value.Selections[0], null, RevActor, RevTime, RevAllowed),
            _ => await store.Delete(Guid.NewGuid(), g.Row, RevActor, RevTime, RevAllowed)
        }).Map(_ => true);
    }

    [PostgresFact]
    public async Task ReleaseSerializesWithRevisionMetadataSelectionDeletionRetirementAndRevocation()
    {
        await using var db = await Database.Create();
        foreach (var action in new[] { "edit", "selection", "delete", "retire" })
        foreach (var releaseFirst in new[] { false, true })
        {
            var g = await PrepareRelease(db.ConnectionString); var store = new PostgresRevisionStore(db.ConnectionString);
            var preview = await ReleasePreview(store, g.Row);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Held(NpgsqlTransaction t, CancellationToken token)
            { ready.SetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(20), token); await t.CommitAsync(token); }
            Task<Result<RevisionSnapshot>> releasing;
            Task<Result<bool>> changing;
            if (releaseFirst)
            {
                releasing = ReleaseNow(new PostgresRevisionStore(db.ConnectionString, Held), preview);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)); changing = ReleaseRevisionWrite(db.ConnectionString, g, action);
                try { await ObserveParentLockWait(db.ConnectionString, changing); }
                finally { finish.TrySetResult(); }
            }
            else
            {
                changing = ReleaseRevisionWrite(db.ConnectionString, g, action, Held);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)); releasing = ReleaseNow(store, preview);
                try { await ObserveParentLockWait(db.ConnectionString, releasing); }
                finally { finish.TrySetResult(); }
            }
            Assert.Equal(releaseFirst, (await releasing).IsSuccess);
            Assert.Equal(!releaseFirst || action == "retire", (await changing).IsSuccess);
            var row = (await store.Retrieve(g.Product.InternalId, g.Row.Value.InternalId)).Value.Record!;
            Assert.Equal(releaseFirst ? "Released" : "Draft", row.Value.State); Assert.Equal(!releaseFirst && action == "delete", row.Deleted);
        }
        var graph = await PrepareRelease(db.ConnectionString); var normal = new PostgresRevisionStore(db.ConnectionString);
        var baseline = await ReleasePreview(normal, graph.Row);
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        var state = await ReleaseDatabaseState(c);
        await using (var t = await c.BeginTransactionAsync())
        {
            await using var block = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111)", c, t); await block.ExecuteNonQueryAsync();
            var allowed = true;
            var pending = normal.Release(Guid.NewGuid(), graph.Row, baseline.Token, RevActor, RevTime, () => Task.FromResult(allowed));
            await ObserveParentLockWait(db.ConnectionString, pending); allowed = false; await t.CommitAsync();
            Assert.Equal("Access denied.", (await pending).Error);
        }
        Assert.Equal(state, await ReleaseDatabaseState(c));
        var checks = 0;
        var denied = await normal.Release(Guid.NewGuid(), graph.Row, baseline.Token, RevActor, RevTime, () => Task.FromResult(++checks < 2));
        Assert.Equal("Access denied.", denied.Error); Assert.Equal(2, checks);
        Assert.Equal(state, await ReleaseDatabaseState(c));
    }

    [PostgresFact]
    public async Task TestCaseReopeningOrdersDatabaseWaitWithLogoutResetAndRechecksAuthorizationInsideSessionGate()
    {
        await using var db = await Database.Create();
        var cases = new PostgresTestCaseStore(db.ConnectionString); var writes = new CaseWriteHook(cases);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<ITestCaseStore>(cases); s.AddSingleton<ITestCaseChangeTransaction>(writes);
            s.AddSingleton<IAuditSubmission>(new PostgresAuditSubmission(db.ConnectionString));
        });
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<TestCaseOperations>();
        var sessions = host.Factory.Services.GetRequiredService<SessionTicketStore>();
        async Task<(string Key, ClaimsPrincipal Principal)> Session()
        {
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "reopening-author"), new Claim(ClaimTypes.Role, "User")
            }, "Cookies")), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, "Cookies");
            var key = await sessions.StoreAsync(ticket);
            return (key, (await sessions.RetrieveAsync(key))!.Principal);
        }
        foreach (var reset in new[] { false, true })
        {
            var graph = await PrepareRelease(db.ConnectionString); var session = await Session();
            var expiry = (await sessions.RetrieveAsync(session.Key))!.Properties.ExpiresUtc;
            var before = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
            await using var t = await c.BeginTransactionAsync();
            await using (var block = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111)", c, t)) await block.ExecuteNonQueryAsync();
            var reopening = operations.Reopen(session.Principal, graph.Case1, "Correction while authorized");
            Task revoked = Task.CompletedTask;
            try
            {
                await ObserveParentLockWait(db.ConnectionString, reopening);
                Assert.Equal(0, sessions.MutationGate.CurrentCount);
                if (reset)
                {
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    revoked = Task.Run(() => { entered.SetResult(); sessions.RevokeAccount("REOPENING-AUTHOR"); });
                    await entered.Task;
                }
                else revoked = sessions.RemoveAsync(session.Key);
                Assert.False(revoked.IsCompleted); Assert.True(sessions.IsActive(session.Key));
                Assert.Equal(expiry, (await sessions.RetrieveAsync(session.Key))!.Properties.ExpiresUtc);
            }
            finally { await t.CommitAsync(); }
            Assert.True((await reopening).IsSuccess);
            await revoked.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(sessions.IsActive(session.Key));
            var persisted = (await cases.Find(graph.Case1.TestCase.InternalId)).Value.Value;
            Assert.Equal("Draft", persisted.TestCase.Status); Assert.Equal(graph.Case1.TestCase.VersionId, persisted.TestCase.VersionId);
            var events = await new PostgresAuditQuery(db.ConnectionString).FindAsync(new());
            Assert.Equal(before + 1, events.Count);
            var success = Assert.Single(events, e => e.TestCaseChange?.InternalId == graph.Case1.TestCase.InternalId && e.Action == "test-case.reopen");
            Assert.Equal(AuditActionOutcome.Success, success.Outcome); Assert.Equal(new AuditActor.Identified("reopening-author"), success.Actor);
        }
        // A request queued behind the session gate must not retain authority from before revocation.
        var current = await PrepareRelease(db.ConnectionString); var queuedSession = await Session();
        var calls = writes.Calls;
        var count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        await sessions.MutationGate.WaitAsync();
        Task<Result<TestCaseSnapshot>> pending;
        try
        {
            pending = operations.Reopen(queuedSession.Principal, current.Case1, "Private submitted reason");
            Assert.False(pending.IsCompleted); Assert.Equal(calls, writes.Calls);
            sessions.RevokeAccountWithinMutation("REOPENING-AUTHOR");
            Assert.False(sessions.IsActive(queuedSession.Key));
        }
        finally { sessions.MutationGate.Release(); }
        Assert.Equal("Access denied.", (await pending).Error); Assert.Equal(calls, writes.Calls);
        Assert.Equal("Approved", (await cases.Find(current.Case1.TestCase.InternalId)).Value.Value.TestCase.Status);
        var audit = await new PostgresAuditQuery(db.ConnectionString).FindAsync(new());
        Assert.Equal(count + 1, audit.Count);
        var denied = Assert.Single(audit, e => e.Action == "test-case.reopen" && e.Outcome == AuditActionOutcome.Failure);
        Assert.Equal(new AuditActor.Identified("reopening-author"), denied.Actor);
        Assert.IsType<AuditTarget.Unknown>(denied.Target); Assert.Null(denied.TestCaseChange);
        Assert.Equal(1, sessions.MutationGate.CurrentCount);
    }

    private static async Task<string> ReleaseDatabaseState(NpgsqlConnection c)
    {
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object(
              'revisions',(SELECT jsonb_agg(to_jsonb(t) ORDER BY internal_id) FROM retravion_revision.revision t),
              'history',(SELECT jsonb_agg(to_jsonb(t) ORDER BY sequence) FROM retravion_revision.history t),
              'needs',(SELECT jsonb_agg(to_jsonb(t) ORDER BY version_id) FROM retravion_need.need_version t),
              'requirements',(SELECT jsonb_agg(to_jsonb(t) ORDER BY version_id) FROM retravion_requirement.requirement_version t),
              'cases',(SELECT jsonb_agg(to_jsonb(t) ORDER BY version_id) FROM retravion_test_case.test_case_version t),
              'audit',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM retravion_audit.event t))::text
            """, c);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task ReleaseRollsBackEveryProtectionDepthFrozenEvidenceHistoryAndAuditFailureAndReportsBothUncertainOutcomes()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString); var preview = await ReleasePreview(store, g.Row);
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        var state = await ReleaseDatabaseState(c);
        foreach (var (table, id) in new[] {
            ("retravion_need.need_version", g.Need1.Need.VersionId), ("retravion_need.need_version", g.Need2.Need.VersionId),
            ("retravion_requirement.requirement_version", g.Child.Requirement.VersionId), ("retravion_requirement.requirement_version", g.Parent1.Requirement.VersionId),
            ("retravion_test_case.test_case_version", g.Case1.TestCase.VersionId), ("retravion_test_case.test_case_version", g.Case2.TestCase.VersionId),
            ("retravion_revision.revision", Guid.Empty), ("retravion_revision.history", Guid.Empty), ("retravion_audit.event", Guid.Empty) })
        {
            var filter = id == Guid.Empty ? "" : $" WHEN (NEW.version_id='{id}')";
            await using (var inject = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION public.fail_release() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private provider/submitted values' USING ERRCODE='42501'; END $$;
                CREATE TRIGGER fail_release BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW{filter} EXECUTE FUNCTION public.fail_release();
                """, c)) await inject.ExecuteNonQueryAsync();
            var failed = await ReleaseNow(store, preview);
            Assert.True(failed.IsFailure, table); Assert.DoesNotContain("private", failed.Error);
            Assert.Equal(state, await ReleaseDatabaseState(c));
            await using var remove = new NpgsqlCommand($"DROP TRIGGER fail_release ON {table}", c); await remove.ExecuteNonQueryAsync();
        }
        foreach (var committed in new[] { false, true })
        {
            var calls = 0; var operation = Guid.NewGuid();
            var uncertain = new PostgresRevisionStore(db.ConnectionString, async (t, token) =>
            { calls++; if (committed) await t.CommitAsync(token); throw new OperationCanceledException("private lost acknowledgement"); });
            var outcome = await ReleaseNow(uncertain, preview, operation);
            Assert.True(outcome.IsFailure); Assert.Contains("could not be confirmed", outcome.Error); Assert.Contains("do not retry automatically", outcome.Error);
            Assert.Equal(1, calls);
            var retrieved = (await store.Retrieve(g.Product.InternalId, g.Row.Value.InternalId, operation)).Value;
            Assert.Equal(committed, retrieved.Operation is not null); Assert.Equal(committed ? "Released" : "Draft", retrieved.Record!.Value.State);
            if (!committed) Assert.Equal(state, await ReleaseDatabaseState(c));
            else
            {
                Assert.Equal(7, ((ReleasedRevision)retrieved.Record.Value).Members.Length);
                Assert.Equal(7, (await ReleasePreview(store, retrieved.Record)).Members.Count);
                Assert.True((await ReleaseNow(store, preview, operation)).IsFailure);
            }
        }
    }
}
