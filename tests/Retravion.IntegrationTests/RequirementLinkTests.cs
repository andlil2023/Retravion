using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Application.UserNeed;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static RequirementUserNeedLink Endpoint(UserNeedSnapshot n) => new(n.Need.InternalId, n.Need.VersionId);

    [PostgresFact]
    public async Task RequirementLinksEnforceOwnershipApprovalInheritanceAndExactEndpoints()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Other", "Other", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var owner = (await store.Create(RequirementState.Create(product.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var draft = (await needs.Create(Need.Create(product.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var wrongProduct = (await needs.Create(Need.Create(other.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var baseline = (await audit.FindAsync(new())).Count;
        Assert.True((await store.ChangeLink(owner, Endpoint(wrongProduct), true, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ChangeLink(owner, new(Guid.NewGuid(), Guid.NewGuid()), true, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ChangeLink(owner, new(Guid.NewGuid(), draft.Need.VersionId), true, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ChangeLink(owner, Endpoint(draft), true, new AuditActor.Unknown(), RequirementTime)).IsFailure);
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
        var linked = (await store.ChangeLink(owner, Endpoint(draft), true, RequirementActor, RequirementTime)).Value;
        Assert.Equal(owner.Revision + 1, linked.Revision);
        Assert.Equal(Endpoint(draft), Assert.Single((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value));
        Assert.True((await store.Links(Guid.NewGuid(), owner.Requirement.VersionId)).IsFailure);
        Assert.True((await store.ChangeLink(owner, Endpoint(draft), false, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Update(owner, owner.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).IsFailure);
        baseline = (await audit.FindAsync(new())).Count;
        Assert.True((await store.ChangeLink(linked, Endpoint(draft), true, RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(linked, (await store.ChangeLink(linked, new(Guid.NewGuid(), Guid.NewGuid()), false, RequirementActor, RequirementTime)).Value);
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
        var inReview = (await store.Update(linked, linked.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        Assert.True((await store.ChangeLink(inReview, Endpoint(draft), false, RequirementActor, RequirementTime)).IsFailure);
        baseline = (await audit.FindAsync(new())).Count;
        Assert.True((await store.Update(inReview, inReview.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).IsFailure);
        var needReview = (await needs.Update(draft, draft.Need.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        Assert.True((await store.Update(inReview, inReview.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).IsFailure);
        Assert.Equal(baseline + 1, (await audit.FindAsync(new())).Count);
        var needApproved = (await needs.Update(needReview, needReview.Need.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var approved = (await store.Update(inReview, inReview.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        Assert.True((await store.ChangeLink(approved, Endpoint(draft), false, RequirementActor, RequirementTime)).IsFailure);
        var newerNeed = (await needs.CreateSuccessor(needApproved, "New need", RequirementActor, RequirementTime)).Value;
        var successor = (await store.CreateSuccessor(approved, "New requirement", RequirementActor, RequirementTime)).Value;
        Assert.Equal(Endpoint(draft), Assert.Single((await store.Links(successor.Requirement.InternalId, successor.Requirement.VersionId)).Value));
        Assert.NotEqual(newerNeed.Need.VersionId, draft.Need.VersionId);
        // One User Need can be addressed by several Requirement owners, and one owner by several exact targets.
        var secondOwner = (await store.Create(RequirementState.Create(product.InternalId, "R2", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        Assert.True((await store.ChangeLink(secondOwner, Endpoint(draft), true, RequirementActor, RequirementTime)).IsSuccess);
        successor = (await store.ChangeLink(successor, Endpoint(newerNeed), true, RequirementActor, RequirementTime)).Value;
        Assert.Equal(2, (await store.Links(successor.Requirement.InternalId, successor.Requirement.VersionId)).Value.Count);
        successor = (await store.ChangeLink(successor, Endpoint(draft), false, RequirementActor, RequirementTime)).Value;
        Assert.Equal(Endpoint(newerNeed), Assert.Single((await store.Links(successor.Requirement.InternalId, successor.Requirement.VersionId)).Value));
        Assert.Equal(Endpoint(draft), Assert.Single((await store.Links(approved.Requirement.InternalId, approved.Requirement.VersionId)).Value));
        Assert.Equal(approved.Requirement, (await store.Find(approved.Requirement.InternalId, versionId: approved.Requirement.VersionId)).Value.Value.Requirement);
        var history = (await store.History(approved.Requirement.InternalId)).Value;
        var inherited = Assert.Single(history, h => h.Decision == "Successor");
        Assert.Null(inherited.BeforeLinks); Assert.Equal(Endpoint(draft), Assert.Single(inherited.AfterLinks!));
        var removal = Assert.Single(history, h => h.Decision == "LinkRemoved");
        Assert.Equal(2, removal.BeforeLinks!.Count); Assert.Equal(Endpoint(newerNeed), Assert.Single(removal.AfterLinks!));
        var events = await audit.FindAsync(new());
        var initial = Assert.Single(events, e => e.Action == "requirement.link.add" && e.RequirementChange!.VersionId == owner.Requirement.VersionId && e.RequirementChange.InternalId == owner.Requirement.InternalId);
        Assert.Equal(RequirementActor, initial.Actor); Assert.Equal(RequirementTime, initial.OccurredAt);
        Assert.Equal(new AuditTarget.Identified(owner.Requirement.InternalId.ToString()), initial.Target);
        Assert.Equal(AuditActionOutcome.Success, initial.Outcome);
        Assert.Equal("Requirement–User Need", initial.RequirementChange!.RelationshipType);
        Assert.Empty(initial.RequirementChange.BeforeLinks!); Assert.Equal(Endpoint(draft), Assert.Single(initial.RequirementChange.AfterLinks!));
        var creation = Assert.Single(events, e => e.Action == "requirement.version.create");
        Assert.Null(creation.RequirementChange!.BeforeLinks); Assert.Equal(Endpoint(draft), Assert.Single(creation.RequirementChange.AfterLinks!));
        Assert.Single(events, e => e.Action == "requirement.link.add" && e.RequirementChange!.VersionId == successor.Requirement.VersionId);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        baseline = (await audit.FindAsync(new())).Count;
        Assert.True((await store.ChangeLink(successor, Endpoint(newerNeed), false, RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(Endpoint(newerNeed), Assert.Single((await new PostgresRequirementStore(database.ConnectionString).Links(successor.Requirement.InternalId, successor.Requirement.VersionId)).Value));
        Assert.Equal(baseline, (await audit.FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task RequirementLinkWritesRollbackTogetherAndReportUncertainCommitWithoutRetry()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var owner = (await store.Create(RequirementState.Create(product.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var target = Endpoint((await needs.Create(Need.Create(product.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value);
        var audit = new PostgresAuditQuery(database.ConnectionString);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE FUNCTION public.reject_link() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await setup.ExecuteNonQueryAsync();
        foreach (var table in new[] { "retravion_requirement.user_need_link", "retravion_requirement.requirement", "retravion_requirement.requirement_version", "retravion_requirement.review", "retravion_audit.event" })
        {
            foreach (var add in new[] { true, false })
            {
                var before = (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value;
                var historyCount = (await store.History(owner.Requirement.InternalId)).Value.Count;
                var eventCount = (await audit.FindAsync(new())).Count;
                var versionTable = table == "retravion_requirement.requirement_version";
                var triggerSql = versionTable ? """
                    CREATE TRIGGER reject_link BEFORE UPDATE ON retravion_requirement.requirement_version
                        FOR EACH ROW EXECUTE FUNCTION public.reject_link();
                    CREATE SEQUENCE public.link_projection_attempt;
                    CREATE FUNCTION public.mark_link_projection_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
                        BEGIN PERFORM nextval('public.link_projection_attempt'); RETURN NEW; END $$;
                    CREATE TRIGGER mark_link_projection_attempt AFTER UPDATE ON retravion_requirement.requirement
                        FOR EACH ROW EXECUTE FUNCTION public.mark_link_projection_attempt();
                    """ : $"CREATE TRIGGER reject_link BEFORE INSERT OR UPDATE OR DELETE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_link()";
                await using (var trigger = new NpgsqlCommand(triggerSql, connection)) await trigger.ExecuteNonQueryAsync();
                var failed = await store.ChangeLink(owner, target, add, RequirementActor, RequirementTime);
                Assert.True(failed.IsFailure); Assert.DoesNotContain("private", failed.Error);
                if (versionTable)
                {
                    // Sequence increments survive rollback, proving the failure followed the projection/link writes.
                    await using var reached = new NpgsqlCommand("SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.link_projection_attempt", connection);
                    Assert.Equal(1L, await reached.ExecuteScalarAsync());
                    await using var cleanup = new NpgsqlCommand("DROP TRIGGER mark_link_projection_attempt ON retravion_requirement.requirement; DROP FUNCTION public.mark_link_projection_attempt(); DROP SEQUENCE public.link_projection_attempt", connection);
                    await cleanup.ExecuteNonQueryAsync();
                }
                await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_link ON {table}", connection)) await drop.ExecuteNonQueryAsync();
                Assert.Equal(owner, (await store.Find(owner.Requirement.InternalId)).Value.Value);
                Assert.Equal(before, (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value);
                Assert.Equal(historyCount, (await store.History(owner.Requirement.InternalId)).Value.Count);
                Assert.Equal(eventCount, (await audit.FindAsync(new())).Count);
                // Prepare the opposite operation for the next failure injection.
                owner = (await store.ChangeLink(owner, target, add, RequirementActor, RequirementTime)).Value;
            }
        }
        foreach (var committed in new[] { false, true })
        {
            var calls = 0;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            { calls++; if (committed) await transaction.CommitAsync(token); throw new IOException("private"); });
            var result = await uncertain.ChangeLink(owner, target, true, RequirementActor, RequirementTime);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("Retrieve", result.Error); Assert.Equal(1, calls);
            var retrieved = (await store.Find(owner.Requirement.InternalId)).Value.Value;
            Assert.Equal(owner.Revision + (committed ? 1 : 0), retrieved.Revision);
            Assert.Equal(committed ? 1 : 0, (await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value.Count);
            if (committed) owner = (await store.ChangeLink(retrieved, target, false, RequirementActor, RequirementTime)).Value;
        }
    }

    [PostgresFact]
    public async Task RequirementInheritedLinksRollbackWithSuccessorHistoryAndAudit()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var target = (await needs.Create(Need.Create(product.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        target = (await needs.Update(target, target.Need.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var owner = (await store.Create(RequirementState.Create(product.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        // Adding an In Review target is allowed, as is removing/re-adding an Approved target.
        owner = (await store.ChangeLink(owner, Endpoint(target), true, RequirementActor, RequirementTime)).Value;
        target = (await needs.Update(target, target.Need.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        owner = (await store.ChangeLink(owner, Endpoint(target), false, RequirementActor, RequirementTime)).Value;
        owner = (await store.ChangeLink(owner, Endpoint(target), true, RequirementActor, RequirementTime)).Value;
        owner = (await store.Update(owner, owner.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        owner = (await store.Update(owner, owner.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var eventCount = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        var historyCount = (await store.History(owner.Requirement.InternalId)).Value.Count;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("CREATE FUNCTION public.reject_inheritance() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await setup.ExecuteNonQueryAsync();
        foreach (var table in new[] { "retravion_requirement.user_need_link", "retravion_requirement.review", "retravion_audit.event" })
        {
            await using (var trigger = new NpgsqlCommand($"CREATE TRIGGER reject_inheritance BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_inheritance()", connection)) await trigger.ExecuteNonQueryAsync();
            Assert.True((await store.CreateSuccessor(owner, "Next", RequirementActor, RequirementTime)).IsFailure);
            await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_inheritance ON {table}", connection)) await drop.ExecuteNonQueryAsync();
            Assert.Equal(owner, (await store.Find(owner.Requirement.InternalId)).Value.Value);
            Assert.Single((await store.Versions(owner.Requirement.InternalId)).Value);
            Assert.Equal(Endpoint(target), Assert.Single((await store.Links(owner.Requirement.InternalId, owner.Requirement.VersionId)).Value));
            Assert.Equal(eventCount, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            Assert.Equal(historyCount, (await store.History(owner.Requirement.InternalId)).Value.Count);
        }
        // Successful authorized callbacks use the same mutation and read boundaries; no UI/MCP tools are introduced.
        await using var host = RequirementMcpHost(new PostgresProductStore(database.ConnectionString), store, store);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<Retravion.Web.Features.Requirement.RequirementOperations>();
        var next = (await operations.CreateSuccessor(principal, owner, "Next")).Value;
        next = (await operations.RemoveLink(principal, next, Endpoint(target))).Value;
        Assert.Empty((await operations.Links(principal, next.Requirement.InternalId, next.Requirement.VersionId)).Value);
        Assert.True((await operations.AddLink(principal, next, Endpoint(target))).IsSuccess);
    }

    [PostgresFact]
    public async Task RequirementApprovalCoordinatesWithConcurrentTargetReopening()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var target = (await needs.Create(Need.Create(product.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        target = (await needs.Update(target, target.Need.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        target = (await needs.Update(target, target.Need.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var owner = (await store.Create(RequirementState.Create(product.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        owner = (await store.ChangeLink(owner, Endpoint(target), true, RequirementActor, RequirementTime)).Value;
        owner = (await store.Update(owner, owner.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
        { ready.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token); await transaction.CommitAsync(token); });
        var approval = held.Update(owner, owner.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved");
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var reopening = needs.Update(target, target.Need.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction");
        try
        {
            // Observe an actual server-side advisory-lock wait, not just task scheduling latency.
            await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
            var waiting = false;
            for (var i = 0; i < 100 && !waiting; i++)
            {
                await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event='advisory')", connection);
                waiting = await query.ExecuteScalarAsync() is true;
                if (!waiting) await Task.Delay(20);
            }
            Assert.True(waiting); Assert.False(reopening.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.True((await approval).IsSuccess); Assert.True((await reopening).IsSuccess);
        Assert.Equal(RequirementStatus.Approved, (await store.Find(owner.Requirement.InternalId)).Value.Value.Requirement.Status);
    }

    [PostgresFact]
    public async Task RequirementLinkRacesRejectStaleOwnersAndApprovalUsesCurrentTargetState()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var need = (await needs.Create(Need.Create(product.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var owner = (await store.Create(RequirementState.Create(product.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ChangeLink(owner, Endpoint(need), true, RequirementActor, RequirementTime)));
        owner = Assert.Single(attempts, a => a.IsSuccess).Value; Assert.Equal(3, attempts.Count(a => a.IsFailure));
        var review = (await needs.Update(need, need.Need.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var approved = (await needs.Update(review, review.Need.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var requirementReview = (await store.Update(owner, owner.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        // Reopening does not revoke an existing owner's approval; if it commits first, it must block new approval.
        var reopen = await needs.Update(approved, approved.Need.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction");
        Assert.True(reopen.IsSuccess);
        Assert.True((await store.Update(requirementReview, requirementReview.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).IsFailure);
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        Assert.Single(events, e => e.Action == "requirement.link.add");
        Assert.DoesNotContain(events, e => e.Action == "requirement.review.Approved");
    }
}
