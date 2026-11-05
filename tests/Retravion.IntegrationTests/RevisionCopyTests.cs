using System.Text.Json;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Revision;
using Retravion.Domain.Product;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Revision;
using Retravion.Infrastructure.TestCase;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static DraftRevision CopyDestination(RevisionSnapshot source, string? identifier = null) =>
        DraftRevision.Create(Guid.NewGuid(), source.Value.ProductInternalId, identifier ?? Guid.NewGuid().ToString(), "Copied description").Value;

    [PostgresFact]
    public async Task RevisionCopyRetainsExactRootsAcrossAllTypesWithoutUpgradesReleaseEvidenceOrSourceChanges()
    {
        await using var db = await Database.Create();
        var graph = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var row = graph.Row;
        foreach (var endpoint in new[] {
            new RevisionSelection(RevisionEntityKind.UserNeed, graph.Need1.Need.InternalId, graph.Need1.Need.VersionId, 1),
            new RevisionSelection(RevisionEntityKind.UserNeed, graph.Need2.Need.InternalId, graph.Need2.Need.VersionId, 2) })
            row = (await store.Select(Guid.NewGuid(), row, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
        var released = (await ReleaseNow(store, await ReleasePreview(store, row))).Value;
        var cases = new PostgresTestCaseStore(db.ConnectionString);
        var successor = (await cases.CreateSuccessor(graph.Case1, "Newer version", RevActor, RevTime)).Value;
        await CaseApprove(cases, successor);
        var sourceJson = JsonSerializer.Serialize((await store.Retrieve(graph.Product.InternalId, released.Value.InternalId)).Value.Record);
        var historyJson = JsonSerializer.Serialize((await store.History(graph.Product.InternalId, released.Value.InternalId)).Value);
        var baselineJson = JsonSerializer.Serialize((await store.Baseline(graph.Product.InternalId, released.Value.InternalId)).Value.Members);
        var beforeEvents = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        var destination = CopyDestination(released, "  Next baseline  "); var operation = Guid.NewGuid();
        var result = await store.Copy(operation, destination, released.Value.InternalId, RevActor, RevTime, RevAllowed);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        var draft = Assert.IsType<DraftRevision>(result.Value.Value);
        Assert.Equal(destination.InternalId, draft.InternalId); Assert.NotEqual(released.Value.InternalId, draft.InternalId);
        Assert.Equal(graph.Product.InternalId, draft.ProductInternalId); Assert.Equal("Next baseline", draft.Identifier);
        Assert.Equal("Copied description", draft.Description); Assert.Equal(1, result.Value.Revision);
        Assert.Equal(released.Value.Selections.ToArray(), draft.Selections.ToArray());
        Assert.DoesNotContain(draft.Selections, s => s.VersionId == successor.TestCase.VersionId);
        var retrieved = (await new PostgresRevisionStore(db.ConnectionString).Retrieve(graph.Product.InternalId, draft.InternalId, operation)).Value;
        Assert.Equal(draft.Selections.ToArray(), retrieved.Record!.Value.Selections.ToArray());
        var history = Assert.Single((await store.History(graph.Product.InternalId, draft.InternalId)).Value);
        Assert.Equal("revision.copy", history.Action); Assert.Equal(RevActor, history.Actor); Assert.Equal(RevTime, history.OccurredAt);
        Assert.Equal(operation, history.Change.OperationId); Assert.Equal(draft.InternalId, history.Change.InternalId);
        Assert.Equal(graph.Product.InternalId, history.Change.ProductInternalId);
        Assert.Equal(released.Value.InternalId, history.Change.SourceRevisionInternalId); Assert.Null(history.Change.Before);
        Assert.Equal(draft.Identifier, history.Change.After!.Identifier); Assert.Equal(draft.Description, history.Change.After.Description);
        Assert.Equal("Draft", history.Change.After.State); Assert.Null(history.Change.After.Release);
        Assert.Equal(draft.Selections.ToArray(), history.Change.After.Selections.ToArray());
        Assert.Equal(JsonSerializer.Serialize(history), JsonSerializer.Serialize(retrieved.Operation));
        var events = await new PostgresAuditQuery(db.ConnectionString).FindAsync(new());
        Assert.Equal(beforeEvents + 1, events.Count);
        var audit = Assert.Single(events, e => e.RevisionChange?.InternalId == draft.InternalId);
        Assert.Equal("revision.copy", audit.Action); Assert.Equal(RevActor, audit.Actor); Assert.Equal(RevTime, audit.OccurredAt);
        Assert.Equal(AuditActionOutcome.Success, audit.Outcome); Assert.Equal(new AuditTarget.Identified(draft.InternalId.ToString()), audit.Target);
        Assert.Equal(JsonSerializer.Serialize(history.Change), JsonSerializer.Serialize(audit.RevisionChange));
        Assert.Equal(baselineJson, JsonSerializer.Serialize((await store.Baseline(graph.Product.InternalId, draft.InternalId)).Value.Members));
        Assert.IsType<DraftRevision>((await store.Baseline(graph.Product.InternalId, draft.InternalId)).Value.Revision.Value);
        // The destination has current Draft closure, not independent frozen release evidence.
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        await using (var evidence = new NpgsqlCommand("SELECT release_evidence IS NULL FROM retravion_revision.revision WHERE internal_id=$1", c))
        { evidence.Parameters.AddWithValue(draft.InternalId); Assert.Equal(true, await evidence.ExecuteScalarAsync()); }
        var removed = (await store.Select(Guid.NewGuid(), result.Value, "remove", draft.Selections[0], null, RevActor, RevTime, RevAllowed)).Value;
        Assert.Equal(draft.Selections.Length - 1, removed.Value.Selections.Length);
        Assert.True((await store.Delete(Guid.NewGuid(), removed, RevActor, RevTime, RevAllowed)).IsSuccess);
        Assert.Equal(sourceJson, JsonSerializer.Serialize((await store.Retrieve(graph.Product.InternalId, released.Value.InternalId)).Value.Record));
        Assert.Equal(historyJson, JsonSerializer.Serialize((await store.History(graph.Product.InternalId, released.Value.InternalId)).Value));
        Assert.Equal(baselineJson, JsonSerializer.Serialize((await store.Baseline(graph.Product.InternalId, released.Value.InternalId)).Value.Members));
        Assert.True((await new PostgresRequirementStore(db.ConnectionString).Update(graph.Child, graph.Child.Requirement.Reopen("Still protected").Value, RevActor, RevTime, "Reopened", "Still protected")).IsFailure);
        Assert.True((await cases.Reopen(graph.Case2, "Still protected", RevActor, RevTime)).IsFailure);
    }

    [PostgresFact]
    public async Task RevisionCopyRejectsInvalidSourcesMetadataConflictsAndDeniedOrRetiredWritesWithoutChangeEvidence()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var released = (await ReleaseNow(store, await ReleasePreview(store, g.Row))).Value;
        var draft = await RevCreate(store, g.Product.InternalId, "Unreleased");
        var deleted = await RevCreate(store, g.Product.InternalId, "Deleted");
        await store.Delete(Guid.NewGuid(), deleted, RevActor, RevTime, RevAllowed);
        var other = (await new CreateProduct(new PostgresProductStore(db.ConnectionString)).Execute("Other", "Other", ProductKind.System, "")).Value;
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        var state = await ReleaseDatabaseState(c);
        foreach (var sourceId in new[] { Guid.Empty, Guid.NewGuid(), draft.Value.InternalId, deleted.Value.InternalId })
            Assert.True((await store.Copy(Guid.NewGuid(), CopyDestination(released), sourceId, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Copy(Guid.NewGuid(), DraftRevision.Create(Guid.NewGuid(), other.InternalId, "Cross Product", "").Value, released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Copy(Guid.Empty, CopyDestination(released), released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Copy(Guid.NewGuid(), DraftRevision.Create(released.Value.InternalId, g.Product.InternalId, "New", "").Value, released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Copy(Guid.NewGuid(), CopyDestination(released, released.Value.Identifier.ToUpperInvariant()), released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Copy(Guid.NewGuid(), (DraftRevision)g.Row.Value, released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Equal("Access denied.", (await store.Copy(Guid.NewGuid(), CopyDestination(released), released.Value.InternalId, RevActor, RevTime, () => Task.FromResult(false))).Error);
        var checks = 0;
        Assert.Equal("Access denied.", (await store.Copy(Guid.NewGuid(), CopyDestination(released), released.Value.InternalId, RevActor, RevTime, () => Task.FromResult(++checks < 2))).Error);
        Assert.Equal(2, checks); Assert.Equal(state, await ReleaseDatabaseState(c));
        var destination = CopyDestination(released, "Concurrent");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.Copy(Guid.NewGuid(), CopyDestination(released, destination.Identifier), released.Value.InternalId, RevActor, RevTime, RevAllowed)));
        Assert.Single(attempts, r => r.IsSuccess); Assert.Single(attempts, r => r.IsFailure);
        var products = new PostgresProductStore(db.ConnectionString);
        Assert.True((await products.Update(g.Product, g.Product.Retire().Value)).IsSuccess);
        state = await ReleaseDatabaseState(c);
        Assert.True((await store.Copy(Guid.NewGuid(), CopyDestination(released), released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Equal(state, await ReleaseDatabaseState(c));
        Assert.IsType<ReleasedRevision>((await store.Retrieve(g.Product.InternalId, released.Value.InternalId)).Value.Record!.Value);
    }

    [PostgresFact]
    public async Task RevisionCopyRollsBackDestinationSelectionsHistoryAndAuditOnEveryPrecommitFailure()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var released = (await ReleaseNow(store, await ReleasePreview(store, g.Row))).Value;
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        var state = await ReleaseDatabaseState(c);
        // Selections share the destination row. Check that nonempty selections are present at its write.
        foreach (var (table, condition) in new[] {
            ("retravion_revision.revision", "true"),
            ("retravion_revision.revision", "jsonb_array_length(NEW.selections)>0"),
            ("retravion_revision.history", "true"), ("retravion_audit.event", "true") })
        {
            await using (var inject = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION public.reject_copy() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
                IF {condition} THEN RAISE EXCEPTION 'private submitted values' USING ERRCODE='42501'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER reject_copy BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_copy();
                """, c)) await inject.ExecuteNonQueryAsync();
            var destination = CopyDestination(released); var operation = Guid.NewGuid();
            var result = await store.Copy(operation, destination, released.Value.InternalId, RevActor, RevTime, RevAllowed);
            Assert.True(result.IsFailure); Assert.DoesNotContain("private", result.Error);
            Assert.Equal(state, await ReleaseDatabaseState(c));
            var found = (await store.Retrieve(g.Product.InternalId, destination.InternalId, operation)).Value;
            Assert.Null(found.Record); Assert.Null(found.Operation);
            await using var remove = new NpgsqlCommand($"DROP TRIGGER reject_copy ON {table}", c); await remove.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task RevisionCopyUncertaintyRetrievesBothActualOutcomesWithoutAutomaticRetryIncludingCancellation()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var released = (await ReleaseNow(store, await ReleasePreview(store, g.Row))).Value;
        foreach (var committed in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        {
            var destination = CopyDestination(released); var operation = Guid.NewGuid(); var attempts = 0;
            var uncertain = new PostgresRevisionStore(db.ConnectionString, async (t, token) =>
            {
                attempts++; if (committed) await t.CommitAsync(token);
                if (cancelled) throw new OperationCanceledException("private acknowledgement");
                throw new IOException("private acknowledgement");
            });
            var count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            var result = await uncertain.Copy(operation, destination, released.Value.InternalId, RevActor, RevTime, RevAllowed);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("do not retry automatically", result.Error);
            Assert.DoesNotContain("private", result.Error); Assert.Equal(1, attempts);
            var found = (await store.Retrieve(g.Product.InternalId, destination.InternalId, operation)).Value;
            Assert.Equal(committed, found.Record is not null); Assert.Equal(committed, found.Operation is not null);
            Assert.Equal(count + (committed ? 1 : 0), (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            if (committed)
            {
                Assert.IsType<DraftRevision>(found.Record!.Value);
                Assert.Equal(released.Value.Selections.ToArray(), found.Record.Value.Selections.ToArray());
                Assert.Equal(released.Value.InternalId, found.Operation!.Change.SourceRevisionInternalId);
                Assert.True((await store.Copy(operation, destination, released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsFailure);
            }
            else Assert.True((await store.Copy(Guid.NewGuid(), destination, released.Value.InternalId, RevActor, RevTime, RevAllowed)).IsSuccess);
        }
    }

    [PostgresFact]
    public async Task RevisionCopyRechecksRetirementAndAuthorizationAfterDatabaseWaits()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var released = (await ReleaseNow(store, await ReleasePreview(store, g.Row))).Value;
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        foreach (var retire in new[] { false, true })
        {
            var destination = CopyDestination(released); var operation = Guid.NewGuid(); var allowed = true;
            await using var t = await c.BeginTransactionAsync();
            await using (var block = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111)", c, t)) await block.ExecuteNonQueryAsync();
            var pending = store.Copy(operation, destination, released.Value.InternalId, RevActor, RevTime, () => Task.FromResult(allowed));
            await ObserveParentLockWait(db.ConnectionString, pending);
            if (retire)
            {
                await using var update = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", c, t);
                update.Parameters.AddWithValue(g.Product.InternalId); await update.ExecuteNonQueryAsync();
            }
            else allowed = false;
            await t.CommitAsync();
            Assert.True((await pending).IsFailure);
            var found = (await store.Retrieve(g.Product.InternalId, destination.InternalId, operation)).Value;
            Assert.Null(found.Record); Assert.Null(found.Operation);
        }
    }
}
