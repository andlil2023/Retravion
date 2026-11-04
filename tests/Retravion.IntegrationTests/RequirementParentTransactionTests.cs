using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static Task<Result<RequirementSnapshot>> ParentOperation(PostgresRequirementStore store, RequirementSnapshot child,
        RequirementParentLink target, string action, CancellationToken token = default) => action switch
    {
        "add" => store.AddParent(child, target, RequirementActor, RequirementTime, token),
        "remove" => store.RemoveParent(child, RequirementActor, RequirementTime, token),
        _ => store.ReplaceParent(child, target, RequirementActor, RequirementTime, token)
    };

    [PostgresFact]
    public async Task ParentAddRemoveAndReplacementRollbackOnEveryBusinessHistoryAndAuditWriteFailure()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var original = await ParentDraft(store, product.InternalId, "Original");
        var replacement = await ParentDraft(store, product.InternalId, "Replacement");
        var audit = new PostgresAuditQuery(database.ConnectionString);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE FUNCTION public.reject_parent_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await setup.ExecuteNonQueryAsync();
        var index = 0;
        foreach (var table in new[] { "retravion_requirement.parent_link", "retravion_requirement.requirement", "retravion_requirement.requirement_version", "retravion_requirement.review", "retravion_audit.event" })
        foreach (var action in new[] { "add", "remove", "replace" })
        {
            var child = await ParentDraft(store, product.InternalId, "Child" + index++);
            if (action != "add") child = (await store.AddParent(child, ParentEndpoint(original), RequirementActor, RequirementTime)).Value;
            var beforeParent = (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value;
            var history = (await store.History(child.Requirement.InternalId)).Value;
            var eventCount = (await audit.FindAsync(new())).Count;
            var versionTable = table == "retravion_requirement.requirement_version";
            var sql = versionTable ? """
                CREATE TRIGGER reject_parent_write BEFORE UPDATE ON retravion_requirement.requirement_version
                    FOR EACH ROW EXECUTE FUNCTION public.reject_parent_write();
                CREATE SEQUENCE public.parent_projection_attempt;
                CREATE FUNCTION public.mark_parent_projection() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN PERFORM nextval('public.parent_projection_attempt'); RETURN NEW; END $$;
                CREATE TRIGGER mark_parent_projection AFTER UPDATE ON retravion_requirement.requirement
                    FOR EACH ROW EXECUTE FUNCTION public.mark_parent_projection();
                """ : $"CREATE TRIGGER reject_parent_write BEFORE INSERT OR UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_parent_write()";
            await using (var trigger = new NpgsqlCommand(sql, connection)) await trigger.ExecuteNonQueryAsync();
            var result = await ParentOperation(store, child, ParentEndpoint(replacement), action);
            Assert.True(result.IsFailure); Assert.DoesNotContain("private", result.Error);
            if (versionTable)
            {
                await using var reached = new NpgsqlCommand("SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.parent_projection_attempt", connection);
                Assert.Equal(1L, await reached.ExecuteScalarAsync());
                await using var cleanup = new NpgsqlCommand("DROP TRIGGER mark_parent_projection ON retravion_requirement.requirement; DROP FUNCTION public.mark_parent_projection(); DROP SEQUENCE public.parent_projection_attempt", connection);
                await cleanup.ExecuteNonQueryAsync();
            }
            await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_parent_write ON {table}", connection)) await drop.ExecuteNonQueryAsync();
            Assert.Equal(child, (await store.Find(child.Requirement.InternalId)).Value.Value);
            Assert.Equal(child, Assert.Single((await store.Versions(child.Requirement.InternalId)).Value));
            Assert.Equal(beforeParent, (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value);
            Assert.Equal(history, (await store.History(child.Requirement.InternalId)).Value);
            Assert.Equal(eventCount, (await audit.FindAsync(new())).Count);
        }
    }

    [PostgresFact]
    public async Task ParentMutationsReportUncertainCommitAndCancellationWithoutRetries()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var original = await ParentDraft(store, product.InternalId, "Original");
        var replacement = await ParentDraft(store, product.InternalId, "Replacement");
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var index = 0;
        foreach (var action in new[] { "add", "remove", "replace" })
        foreach (var committed in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            var child = await ParentDraft(store, product.InternalId, "Child" + index++);
            if (action != "add") child = (await store.AddParent(child, ParentEndpoint(original), RequirementActor, RequirementTime)).Value;
            var beforeParent = (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value;
            var eventsBefore = (await audit.FindAsync(new())).Count;
            var historyBefore = (await store.History(child.Requirement.InternalId)).Value.Count;
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            {
                calls++;
                if (committed) await transaction.CommitAsync(token);
                if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new IOException("private");
            });
            var result = await ParentOperation(uncertain, child, ParentEndpoint(replacement), action, cancellation.Token);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("Retrieve", result.Error);
            Assert.DoesNotContain("private", result.Error); Assert.Equal(1, calls);
            var retrieved = (await store.Find(child.Requirement.InternalId)).Value.Value;
            Assert.Equal(child.Revision + (committed ? 1 : 0), retrieved.Revision);
            var parent = (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value;
            if (!committed) Assert.Equal(beforeParent, parent);
            else if (action == "remove") Assert.True(parent.HasNoValue);
            else Assert.Equal(ParentEndpoint(replacement), parent.Value);
            Assert.Equal(eventsBefore + (committed ? 1 : 0), (await audit.FindAsync(new())).Count);
            Assert.Equal(historyBefore + (committed ? 1 : 0), (await store.History(child.Requirement.InternalId)).Value.Count);
        }
    }

    [PostgresFact]
    public async Task ParentAwareApprovalRetainsAtomicEvidenceOnFailureAndUncertainCommit()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var parent = await ParentApprove(store, await ParentDraft(store, product.InternalId, "Parent"));
        var child = await ParentDraft(store, product.InternalId, "Child");
        child = (await store.AddParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime)).Value;
        child = (await store.Update(child, child.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var eventsBefore = (await audit.FindAsync(new())).Count;
        var history = (await store.History(child.Requirement.InternalId)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE FUNCTION public.reject_parent_approval() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await setup.ExecuteNonQueryAsync();
        foreach (var table in new[] { "retravion_requirement.requirement", "retravion_requirement.requirement_version", "retravion_requirement.review", "retravion_audit.event" })
        {
            var operations = table == "retravion_requirement.requirement_version" ? "UPDATE" : "INSERT OR UPDATE";
            await using (var trigger = new NpgsqlCommand($"CREATE TRIGGER reject_parent_approval BEFORE {operations} ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_parent_approval()", connection)) await trigger.ExecuteNonQueryAsync();
            Assert.True((await store.Update(child, child.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).IsFailure);
            await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_parent_approval ON {table}", connection)) await drop.ExecuteNonQueryAsync();
            Assert.Equal(child, (await store.Find(child.Requirement.InternalId)).Value.Value);
            Assert.Equal(ParentEndpoint(parent), (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value.Value);
            Assert.Equal(history, (await store.History(child.Requirement.InternalId)).Value);
            Assert.Equal(eventsBefore, (await audit.FindAsync(new())).Count);
        }
        var index = 0;
        foreach (var committed in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            var owner = await ParentDraft(store, product.InternalId, "Uncertain" + index++);
            owner = (await store.AddParent(owner, ParentEndpoint(parent), RequirementActor, RequirementTime)).Value;
            owner = (await store.Update(owner, owner.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
            eventsBefore = (await audit.FindAsync(new())).Count;
            var historyBefore = (await store.History(owner.Requirement.InternalId)).Value.Count;
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            {
                calls++; if (committed) await transaction.CommitAsync(token);
                if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new IOException("private");
            });
            var result = await uncertain.Update(owner, owner.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved", cancellationToken: cancellation.Token);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("Retrieve", result.Error); Assert.Equal(1, calls);
            Assert.Equal(committed ? RequirementStatus.Approved : RequirementStatus.InReview, (await store.Find(owner.Requirement.InternalId)).Value.Value.Requirement.Status);
            Assert.Equal(eventsBefore + (committed ? 1 : 0), (await audit.FindAsync(new())).Count);
            Assert.Equal(historyBefore + (committed ? 1 : 0), (await store.History(owner.Requirement.InternalId)).Value.Count);
            Assert.Equal(ParentEndpoint(parent), (await store.Parent(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Value);
            if (committed)
            {
                var approved = Assert.Single((await audit.FindAsync(new())), e => e.Action == "requirement.review.Approved" && e.RequirementChange!.InternalId == owner.Requirement.InternalId);
                Assert.Equal(ParentEndpoint(parent), approved.RequirementChange!.BeforeParent); Assert.Equal(ParentEndpoint(parent), approved.RequirementChange.AfterParent);
            }
        }
    }

    private static async Task ObserveParentLockWait(string connectionString, Task pending)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        var waiting = false;
        for (var i = 0; i < 100 && !waiting; i++)
        {
            await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock')", connection);
            waiting = await query.ExecuteScalarAsync() is true;
            if (!waiting) await Task.Delay(20);
        }
        Assert.True(waiting); Assert.False(pending.IsCompleted);
    }

    [PostgresFact]
    public async Task ParentApprovalCoordinatesWithTargetReopeningInBothCommitOrders()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        foreach (var parentFirst in new[] { true, false })
        {
            var parent = await ParentApprove(store, await ParentDraft(store, product.InternalId, "Parent" + parentFirst));
            var child = await ParentDraft(store, product.InternalId, "Child" + parentFirst);
            child = (await store.AddParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime)).Value;
            child = (await store.Update(child, child.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            { ready.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token); await transaction.CommitAsync(token); });
            var first = parentFirst
                ? held.Update(parent, parent.Requirement.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction")
                : held.Update(child, child.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = parentFirst
                ? store.Update(child, child.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")
                : store.Update(parent, parent.Requirement.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction");
            try { await ObserveParentLockWait(database.ConnectionString, second); }
            finally { release.TrySetResult(); }
            Assert.True((await first).IsSuccess);
            Assert.Equal(!parentFirst, (await second).IsSuccess);
            var current = (await store.Find(child.Requirement.InternalId)).Value.Value;
            Assert.Equal(parentFirst ? Retravion.Domain.Requirement.RequirementStatus.InReview : Retravion.Domain.Requirement.RequirementStatus.Approved, current.Requirement.Status);
            var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.RequirementChange?.InternalId == child.Requirement.InternalId).ToArray();
            Assert.Equal(parentFirst ? 0 : 1, events.Count(e => e.Action == "requirement.review.Approved"));
            Assert.Equal(parentFirst ? 0 : 1, (await store.History(child.Requirement.InternalId)).Value.Count(h => h.Decision == "Approved"));
            if (!parentFirst)
            {
                var approved = Assert.Single(events, e => e.Action == "requirement.review.Approved");
                Assert.Equal(ParentEndpoint(parent), approved.RequirementChange!.AfterParent);
                Assert.Equal(ParentEndpoint(parent), Assert.Single((await store.History(child.Requirement.InternalId)).Value, h => h.Decision == "Approved").AfterParent);
            }
        }
    }

    [PostgresFact]
    public async Task ParentMutationAndProductRetirementRespectBothCommitOrders()
    {
        await using var database = await Database.Create();
        var store = new PostgresRequirementStore(database.ConnectionString);
        var products = new PostgresProductStore(database.ConnectionString);
        foreach (var retireFirst in new[] { true, false })
        {
            var product = (await new CreateProduct(products).Execute("P" + retireFirst, "Product", ProductKind.System, "")).Value;
            var child = await ParentDraft(store, product.InternalId, "Child");
            var parent = await ParentDraft(store, product.InternalId, "Parent");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Hold(NpgsqlTransaction transaction, CancellationToken token)
            { ready.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token); await transaction.CommitAsync(token); }
            if (retireFirst)
            {
                var retiring = new PostgresProductStore(database.ConnectionString, Hold).Update(product, product.Retire().Value);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var adding = store.AddParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime);
                try { await ObserveParentLockWait(database.ConnectionString, adding); }
                finally { release.TrySetResult(); }
                Assert.True((await retiring).IsSuccess); Assert.True((await adding).IsFailure);
            }
            else
            {
                var adding = new PostgresRequirementStore(database.ConnectionString, Hold).AddParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var retiring = products.Update(product, product.Retire().Value);
                try { await ObserveParentLockWait(database.ConnectionString, retiring); }
                finally { release.TrySetResult(); }
                Assert.True((await adding).IsSuccess); Assert.True((await retiring).IsSuccess);
            }
            Assert.Equal(!retireFirst, (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value.HasValue);
            var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
            Assert.Equal(retireFirst ? 0 : 1, events.Count(e => e.Action == "requirement.parent.add" && e.RequirementChange?.InternalId == child.Requirement.InternalId));
        }
    }

    [PostgresFact]
    public async Task ParentInheritanceAndOtherOwnedLinksCommitWithSuccessorHistoryAndAudit()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var parent = await ParentApprove(store, await ParentDraft(store, product.InternalId, "Parent"));
        var child = await ParentDraft(store, product.InternalId, "Child");
        child = (await store.AddParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime)).Value;
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var need = (await needs.Create(Need.Create(product.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        need = (await needs.Update(need, need.Need.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        need = (await needs.Update(need, need.Need.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        child = (await store.ChangeLink(child, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
        child = await ParentApprove(store, child);
        var history = (await store.History(child.Requirement.InternalId)).Value;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var eventsBefore = (await audit.FindAsync(new())).Count;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE FUNCTION public.reject_parent_inheritance() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await setup.ExecuteNonQueryAsync();
        foreach (var table in new[] { "retravion_requirement.parent_link", "retravion_requirement.review", "retravion_audit.event" })
        {
            await using (var trigger = new NpgsqlCommand($"CREATE TRIGGER reject_parent_inheritance BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_parent_inheritance()", connection)) await trigger.ExecuteNonQueryAsync();
            Assert.True((await store.CreateSuccessor(child, "Next", RequirementActor, RequirementTime)).IsFailure);
            await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_parent_inheritance ON {table}", connection)) await drop.ExecuteNonQueryAsync();
            Assert.Equal(child, (await store.Find(child.Requirement.InternalId)).Value.Value);
            Assert.Single((await store.Versions(child.Requirement.InternalId)).Value);
            Assert.Equal(ParentEndpoint(parent), (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value.Value);
            Assert.Equal(Endpoint(need), Assert.Single((await store.Links(child.Requirement.InternalId, child.Requirement.VersionId)).Value));
            // Compare immutable fields and collection contents, not collection reference equality.
            var afterHistory = (await store.History(child.Requirement.InternalId)).Value;
            Assert.Equal(history.Count, afterHistory.Count);
            for (var i = 0; i < history.Count; i++)
            {
                Assert.Equal(history[i] with { BeforeLinks = null, AfterLinks = null }, afterHistory[i] with { BeforeLinks = null, AfterLinks = null });
                Assert.Equal(history[i].BeforeLinks, afterHistory[i].BeforeLinks); Assert.Equal(history[i].AfterLinks, afterHistory[i].AfterLinks);
            }
            Assert.Equal(eventsBefore, (await audit.FindAsync(new())).Count);
        }
        foreach (var committed in new[] { false, true })
        {
            var calls = 0;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            { calls++; if (committed) await transaction.CommitAsync(token); throw new IOException("private"); });
            var result = await uncertain.CreateSuccessor(child, "Next", RequirementActor, RequirementTime);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Equal(1, calls);
            Assert.Equal(committed ? 2 : 1, (await store.Versions(child.Requirement.InternalId)).Value.Count);
            var current = (await store.Find(child.Requirement.InternalId)).Value.Value;
            Assert.Equal(ParentEndpoint(parent), (await store.Parent(current.Requirement.InternalId, current.Requirement.VersionId)).Value.Value);
            Assert.Equal(Endpoint(need), Assert.Single((await store.Links(current.Requirement.InternalId, current.Requirement.VersionId)).Value));
            Assert.Equal(eventsBefore + (committed ? 1 : 0), (await audit.FindAsync(new())).Count);
            if (committed)
            {
                var inherited = Assert.Single((await audit.FindAsync(new())), e => e.Action == "requirement.version.create").RequirementChange!;
                Assert.Equal(ParentEndpoint(parent), inherited.AfterParent); Assert.Null(inherited.BeforeParent);
                Assert.Equal(Endpoint(need), Assert.Single(inherited.AfterLinks!)); Assert.Null(inherited.BeforeLinks);
                Assert.Equal(current.Requirement.VersionId, inherited.VersionId);
            }
        }
    }
}
