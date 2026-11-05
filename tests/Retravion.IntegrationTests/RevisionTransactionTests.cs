using CSharpFunctionalExtensions;
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
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static readonly AuditActor RevActor = new AuditActor.Identified("revision-author");
    private static readonly DateTimeOffset RevTime = DateTimeOffset.Parse("2026-01-02T03:04:05.1234567+02:00");
    private static Task<bool> RevAllowed() => Task.FromResult(true);
    private static async Task<RevisionSnapshot> RevCreate(PostgresRevisionStore store, Guid product, string identifier = "Baseline") =>
        (await store.Create(Guid.NewGuid(), DraftRevision.Create(Guid.NewGuid(), product, identifier, "Description").Value, RevActor, RevTime, RevAllowed)).Value;

    [PostgresFact]
    public async Task RevisionMetadataUniquenessStaleWritesDeletionAndImmutableEvidence()
    {
        await using var db = await Database.Create(useCLocale: true);
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var store = new PostgresRevisionStore(db.ConnectionString);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => store.Create(Guid.NewGuid(),
            DraftRevision.Create(Guid.NewGuid(), product.InternalId, i % 2 == 0 ? " ÄLPHA " : "älpha", "").Value, RevActor, RevTime, RevAllowed)));
        var original = Assert.Single(outcomes, r => r.IsSuccess).Value;
        Assert.All(outcomes.Where(r => r.IsFailure), r => Assert.Contains("already in use", r.Error));
        await RevCreate(store, other.InternalId, "älpha");
        var accent = await RevCreate(store, product.InternalId, "alpha");
        var longId = string.Concat(Enumerable.Repeat("😀", 100));
        await RevCreate(store, product.InternalId, longId);
        var renameOperation = Guid.NewGuid();
        var edited = (await store.Edit(renameOperation, original, " Renamed ", "<b>event value</b>", RevActor, RevTime, RevAllowed)).Value;
        Assert.Equal(original.Draft.InternalId, edited.Draft.InternalId); Assert.Equal(product.InternalId, edited.Draft.ProductInternalId);
        Assert.Equal(original.Revision + 1, edited.Revision);
        Assert.True((await store.Edit(Guid.NewGuid(), edited, "ALPHA", "", RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Edit(Guid.NewGuid(), original, "stale", "", RevActor, RevTime, RevAllowed)).IsFailure);
        var forged = edited with { Draft = DraftRevision.Create(edited.Draft.InternalId, other.InternalId, edited.Draft.Identifier, edited.Draft.Description).Value };
        Assert.True((await store.Delete(Guid.NewGuid(), forged, RevActor, RevTime, RevAllowed)).IsFailure);
        var unchanged = (await store.Edit(Guid.NewGuid(), edited, " Renamed ", "<b>event value</b>", RevActor, RevTime, RevAllowed)).Value;
        Assert.Equal(edited.Revision, unchanged.Revision);
        Assert.True((await store.Edit(renameOperation, edited, "new", "", RevActor, RevTime, RevAllowed)).IsFailure);
        var deleteOperation = Guid.NewGuid();
        var deleted = (await store.Delete(deleteOperation, edited, RevActor, RevTime, RevAllowed)).Value;
        Assert.True(deleted.Deleted); Assert.Equal(edited.Revision + 1, deleted.Revision);
        Assert.DoesNotContain((await store.List(product.InternalId)).Value, r => r.Draft.InternalId == edited.Draft.InternalId);
        var retained = (await new PostgresRevisionStore(db.ConnectionString).Retrieve(product.InternalId, edited.Draft.InternalId, deleteOperation)).Value;
        Assert.True(retained.Record!.Deleted); Assert.Equal("revision.delete", retained.Operation!.Action);
        Assert.Equal("Renamed", retained.Operation.Change.Before!.Identifier); Assert.Null(retained.Operation.Change.After);
        Assert.True((await store.Retrieve(other.InternalId, edited.Draft.InternalId, deleteOperation)).Value.Record is null);
        Assert.Empty((await store.History(other.InternalId, edited.Draft.InternalId)).Value);
        Assert.True((await store.Delete(Guid.NewGuid(), edited, RevActor, RevTime, RevAllowed)).IsFailure);
        await RevCreate(store, product.InternalId, "renamed"); // Deleted identifiers may be reused, never identities.
        var history = (await store.History(product.InternalId, original.Draft.InternalId)).Value;
        Assert.Equal(new[] { "revision.create", "revision.edit", "revision.delete" }, history.Select(h => h.Action));
        Assert.All(history, h => { Assert.Equal(RevActor, h.Actor); Assert.Equal(RevTime.UtcTicks, h.OccurredAt.UtcTicks); Assert.Equal(product.InternalId, h.Change.ProductInternalId); });
        Assert.Equal(original.Draft.Identifier, history[0].Change.After!.Identifier);
        Assert.Equal(original.Draft.Description, history[0].Change.After!.Description);
        Assert.Empty(history[0].Change.After!.Selections);
        var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Where(e => e.RevisionChange?.InternalId == original.Draft.InternalId).ToArray();
        Assert.Equal(3, events.Length); Assert.All(events, e => { Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.Equal(new AuditTarget.Identified(original.Draft.InternalId.ToString()), e.Target); });
        Assert.Equal(renameOperation, events.Single(e => e.Action == "revision.edit").RevisionChange!.OperationId);
        Assert.Equal(accent.Draft.InternalId, (await store.Retrieve(product.InternalId, accent.Draft.InternalId)).Value.Record!.Draft.InternalId);
    }

    [PostgresFact]
    public async Task RevisionSelectionsAreExactAtomicAndNeverChangeReferencedRecords()
    {
        await using var db = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(db.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var first = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "R"));
        var second = (await requirements.CreateSuccessor(first, "next", RevActor, RevTime)).Value;
        var e1 = new RevisionSelection(RevisionEntityKind.Requirement, first.Requirement.InternalId, first.Requirement.VersionId, 1);
        var e2 = e1 with { VersionId = second.Requirement.VersionId, VersionNumber = 2 };
        var store = new PostgresRevisionStore(db.ConnectionString);
        var empty = await RevCreate(store, product.InternalId);
        var one = (await store.Select(Guid.NewGuid(), empty, "add", e1, null, RevActor, RevTime, RevAllowed)).Value;
        var both = (await store.Select(Guid.NewGuid(), one, "add", e2, null, RevActor, RevTime, RevAllowed)).Value;
        Assert.Equal(2, both.Draft.Selections.Length);
        Assert.True((await store.Select(Guid.NewGuid(), one, "replace", e1, e2, RevActor, RevTime, RevAllowed)).IsFailure);
        var removed = (await store.Select(Guid.NewGuid(), both, "remove", e1, null, RevActor, RevTime, RevAllowed)).Value;
        var replaced = (await store.Select(Guid.NewGuid(), removed, "replace", e2, e1, RevActor, RevTime, RevAllowed)).Value;
        Assert.Equal(e1, Assert.Single(replaced.Draft.Selections));
        foreach (var endpoint in new[] { e1 with { VersionNumber = 8 }, e1 with { InternalId = Guid.NewGuid() }, e1 with { VersionId = Guid.NewGuid() } })
            Assert.True((await store.Select(Guid.NewGuid(), replaced, "add", endpoint, null, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Equal(second.Revision, (await requirements.Find(first.Requirement.InternalId)).Value.Value.Revision);
        var history = (await store.History(product.InternalId, empty.Draft.InternalId)).Value;
        Assert.Equal(5, history.Count);
        Assert.Equal(new[] { e2 }, history[^1].Change.Before!.Selections.ToArray()); Assert.Equal(new[] { e1 }, history[^1].Change.After!.Selections.ToArray());
        await store.Delete(Guid.NewGuid(), replaced, RevActor, RevTime, RevAllowed);
        Assert.Equal(e1, Assert.Single((await store.History(product.InternalId, empty.Draft.InternalId)).Value[^1].Change.Before!.Selections));
        Assert.Equal(2, (await requirements.Versions(first.Requirement.InternalId)).Value.Count);
    }

    [PostgresFact]
    public async Task RevisionRacesRejectStaleRenamesRetirementAndAccessRevokedDuringDatabaseWait()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRevisionStore(db.ConnectionString);
        var first = await RevCreate(store, product.InternalId, "A"); var second = await RevCreate(store, product.InternalId, "B");
        var races = await Task.WhenAll(store.Edit(Guid.NewGuid(), first, "Same", "", RevActor, RevTime, RevAllowed), store.Edit(Guid.NewGuid(), second, "same", "", RevActor, RevTime, RevAllowed));
        Assert.Single(races, r => r.IsSuccess);
        var winner = races.Single(r => r.IsSuccess).Value;
        var stale = await Task.WhenAll(store.Delete(Guid.NewGuid(), winner, RevActor, RevTime, RevAllowed), store.Edit(Guid.NewGuid(), winner, "Changed", "", RevActor, RevTime, RevAllowed));
        Assert.Single(stale, r => r.IsSuccess);
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        await using (var t = await c.BeginTransactionAsync())
        {
            await using var retirement = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", c, t);
            retirement.Parameters.AddWithValue(product.InternalId); await retirement.ExecuteNonQueryAsync();
            var pending = store.Create(Guid.NewGuid(), DraftRevision.Create(Guid.NewGuid(), product.InternalId, "Retirement", "").Value, RevActor, RevTime, RevAllowed);
            await t.CommitAsync(); Assert.True((await pending).IsFailure);
        }
        Assert.True((await store.Edit(Guid.NewGuid(), first, "A", "", RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Delete(Guid.NewGuid(), second, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.NotEmpty((await store.List(product.InternalId)).Value);
        var allowed = true;
        await using (var t = await c.BeginTransactionAsync())
        {
            await using var block = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620111)", c, t); await block.ExecuteNonQueryAsync();
            var pending = store.Create(Guid.NewGuid(), DraftRevision.Create(Guid.NewGuid(), product.InternalId, "Denied", "").Value, RevActor, RevTime, () => Task.FromResult(allowed));
            allowed = false; await t.CommitAsync();
            Assert.Equal("Access denied.", (await pending).Error);
        }
    }

    [PostgresFact]
    public async Task RevisionFinalAuthorizationRecheckRollsBackAndInvalidPersistedSelectionsFailExplicitly()
    {
        await using var db = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(db.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRevisionStore(db.ConnectionString);
        var operation = Guid.NewGuid(); var draft = DraftRevision.Create(Guid.NewGuid(), product.InternalId, "Denied before commit", "").Value;
        var checks = 0;
        var result = await store.Create(operation, draft, RevActor, RevTime, () => Task.FromResult(++checks < 2));
        Assert.Equal("Access denied.", result.Error); Assert.Equal(2, checks);
        var outcome = (await store.Retrieve(product.InternalId, draft.InternalId, operation)).Value;
        Assert.Null(outcome.Record); Assert.Null(outcome.Operation);
        Assert.DoesNotContain(await new PostgresAuditQuery(db.ConnectionString).FindAsync(new()), e => e.RevisionChange is not null);
        var row = await RevCreate(store, product.InternalId);
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        foreach (var json in new[] { "[null]", "[{}]", "[1]" })
        {
            await using var corrupt = new NpgsqlCommand("UPDATE retravion_revision.revision SET selections=$1::jsonb WHERE internal_id=$2", c);
            corrupt.Parameters.AddWithValue(json); corrupt.Parameters.AddWithValue(row.Draft.InternalId); await corrupt.ExecuteNonQueryAsync();
            Assert.True((await store.Retrieve(product.InternalId, row.Draft.InternalId)).IsFailure);
            Assert.True((await store.List(product.InternalId)).IsFailure);
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Retrieve(product.InternalId, row.Draft.InternalId, token: canceled.Token));
    }

    [PostgresFact]
    public async Task RevisionEveryWriteRollsBackBusinessHistoryAuditAndRetainedEvidenceFailures()
    {
        await using var db = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(db.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var r1 = await ParentDraft(requirements, product.InternalId, "R1"); var r2 = await ParentDraft(requirements, product.InternalId, "R2");
        var e1 = new RevisionSelection(RevisionEntityKind.Requirement, r1.Requirement.InternalId, r1.Requirement.VersionId, 1);
        var e2 = new RevisionSelection(RevisionEntityKind.Requirement, r2.Requirement.InternalId, r2.Requirement.VersionId, 1);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var empty = await RevCreate(store, product.InternalId);
        var row = (await store.Select(Guid.NewGuid(), empty, "add", e1, null, RevActor, RevTime, RevAllowed)).Value;
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        foreach (var table in new[] { "retravion_revision.revision", "retravion_revision.history", "retravion_audit.event" })
        {
            await using (var inject = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION public.reject_revision() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private submitted values' USING ERRCODE='42501'; END $$;
                CREATE TRIGGER reject_revision BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_revision();
                """, c)) await inject.ExecuteNonQueryAsync();
            var attempts = new Func<Task<Result<RevisionSnapshot>>>[]
            {
                () => store.Create(Guid.NewGuid(), DraftRevision.Create(Guid.NewGuid(), product.InternalId, "Fail", "").Value, RevActor, RevTime, RevAllowed),
                () => store.Edit(Guid.NewGuid(), row, "Fail", "new description", RevActor, RevTime, RevAllowed),
                () => store.Select(Guid.NewGuid(), row, "add", e2, null, RevActor, RevTime, RevAllowed),
                () => store.Select(Guid.NewGuid(), row, "remove", e1, null, RevActor, RevTime, RevAllowed),
                () => store.Select(Guid.NewGuid(), row, "replace", e1, e2, RevActor, RevTime, RevAllowed),
                () => store.Delete(Guid.NewGuid(), row, RevActor, RevTime, RevAllowed)
            };
            foreach (var attempt in attempts)
            {
                var result = await attempt(); Assert.True(result.IsFailure); Assert.DoesNotContain("private", result.Error);
                var saved = (await store.Retrieve(product.InternalId, row.Draft.InternalId)).Value.Record!;
                Assert.False(saved.Deleted); Assert.Equal(row.Revision, saved.Revision); Assert.True(row.Draft.SameValues(saved.Draft));
                Assert.Single((await store.List(product.InternalId)).Value);
                Assert.Equal(2, (await store.History(product.InternalId, row.Draft.InternalId)).Value.Count);
                Assert.Equal(2, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count(e => e.RevisionChange is not null));
            }
            await using var remove = new NpgsqlCommand($"DROP TRIGGER reject_revision ON {table}", c); await remove.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task RevisionEveryWriteUncertaintyRetrievesBothActualOutcomesByImmutableOperationIdentity()
    {
        await using var db = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(db.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var r1 = await ParentDraft(requirements, product.InternalId, "R1"); var r2 = await ParentDraft(requirements, product.InternalId, "R2");
        var e1 = new RevisionSelection(RevisionEntityKind.Requirement, r1.Requirement.InternalId, r1.Requirement.VersionId, 1);
        var e2 = new RevisionSelection(RevisionEntityKind.Requirement, r2.Requirement.InternalId, r2.Requirement.VersionId, 1);
        var store = new PostgresRevisionStore(db.ConnectionString);
        foreach (var committed in new[] { false, true })
        foreach (var action in new[] { "create", "edit", "add", "remove", "replace", "delete" })
        {
            var before = await RevCreate(store, product.InternalId, Guid.NewGuid().ToString());
            before = (await store.Select(Guid.NewGuid(), before, "add", e1, null, RevActor, RevTime, RevAllowed)).Value;
            var operation = Guid.NewGuid(); var draft = DraftRevision.Create(Guid.NewGuid(), product.InternalId, Guid.NewGuid().ToString(), "uncertain").Value;
            var attempts = 0;
            var uncertain = new PostgresRevisionStore(db.ConnectionString, async (t, token) =>
            { attempts++; if (committed) await t.CommitAsync(token); throw new OperationCanceledException("private lost acknowledgement"); });
            var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            var result = action switch
            {
                "create" => await uncertain.Create(operation, draft, RevActor, RevTime, RevAllowed),
                "edit" => await uncertain.Edit(operation, before, "Changed " + Guid.NewGuid(), "uncertain", RevActor, RevTime, RevAllowed),
                "delete" => await uncertain.Delete(operation, before, RevActor, RevTime, RevAllowed),
                _ => await uncertain.Select(operation, before, action, action == "add" ? e2 : e1, action == "replace" ? e2 : null, RevActor, RevTime, RevAllowed)
            };
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("do not retry automatically", result.Error); Assert.Equal(1, attempts);
            var id = action == "create" ? draft.InternalId : before.Draft.InternalId;
            var retrieved = (await store.Retrieve(product.InternalId, id, operation)).Value;
            Assert.Equal(committed, retrieved.Operation is not null);
            Assert.Equal(events + (committed ? 1 : 0), (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            if (action == "create") Assert.Equal(committed, retrieved.Record is not null);
            else
            {
                Assert.Equal(committed && action == "delete", retrieved.Record!.Deleted);
                Assert.Equal(before.Revision + (committed ? 1 : 0), retrieved.Record.Revision);
                if (!committed) Assert.True(before.Draft.SameValues(retrieved.Record.Draft));
            }
        }
    }
}
