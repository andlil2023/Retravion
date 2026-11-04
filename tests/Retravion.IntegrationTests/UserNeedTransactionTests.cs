using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task UserNeedChangesAreAtomicAuditedAndRejectStaleAndRetiredWrites()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var actor = new AuditActor.Identified("author");
        var time = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var need = Need.Create(product.InternalId, " Need ", "Before", "# Statement").Value;
        var first = (await store.Create(need, actor, time)).Value;
        Assert.True((await store.Create(Need.Create(product.InternalId, "need", "Duplicate", "Statement").Value, actor, time)).IsFailure);
        var edited = (await store.Update(first, need.Edit("Renamed", "After", "Updated").Value, actor, time)).Value;
        Assert.True((await store.Update(first, need.Edit("stale", "stale", "stale").Value, actor, time)).IsFailure);
        Assert.True((await store.Update(edited, edited.Need, actor, time)).IsSuccess);
        var reviewed = (await store.Update(edited, edited.Need.SubmitForReview().Value, actor, time, "InReview", "Ready")).Value;
        var approved = (await store.Update(reviewed, reviewed.Need.Approve().Value, actor, time, "Approved")).Value;
        var returned = (await store.Update(approved, approved.Need.Reopen("Correction").Value, actor, time, "Reopened", "Correction")).Value;
        var history = (await store.History(need.InternalId)).Value;
        Assert.Equal(3, history.Count);
        Assert.All(history, row => { Assert.Equal("After", row.Title); Assert.Equal("Updated", row.Statement); });
        Assert.Equal("Ready", history[0].Comment);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.UserNeedChange is not null).ToArray();
        Assert.Equal(5, events.Length);
        Assert.All(events, e => { Assert.Equal(actor, e.Actor); Assert.Equal(new AuditTarget.Identified(need.InternalId.ToString()), e.Target); });
        static UserNeedAuditValues Values(Need value) => new(value.ProductInternalId, value.Id, value.Title, value.Statement, value.Status.ToString());
        Assert.Equal(new UserNeedAuditDetails(need.InternalId, null, Values(need), null, null, need.VersionId, 1),
            events.Single(e => e.Action == "need.create").UserNeedChange);
        Assert.Equal(new UserNeedAuditDetails(need.InternalId, Values(need), Values(edited.Need), null, null, need.VersionId, 1),
            events.Single(e => e.Action == "need.edit").UserNeedChange);
        Assert.Equal(new UserNeedAuditDetails(need.InternalId, Values(edited.Need), Values(reviewed.Need), "InReview", "Ready", need.VersionId, 1),
            events.Single(e => e.Action == "need.review.InReview").UserNeedChange);
        Assert.Equal(new UserNeedAuditDetails(need.InternalId, Values(reviewed.Need), Values(approved.Need), "Approved", null, need.VersionId, 1),
            events.Single(e => e.Action == "need.review.Approved").UserNeedChange);
        Assert.Equal(new UserNeedAuditDetails(need.InternalId, Values(approved.Need), Values(returned.Need), "Reopened", "Correction", need.VersionId, 1, null, "Correction"),
            events.Single(e => e.Action == "need.reopen").UserNeedChange);
        Assert.All(events, e => { Assert.Equal(time.UtcTicks, e.OccurredAt.UtcTicks); Assert.Equal(AuditActionOutcome.Success, e.Outcome); });
        var later = (await store.Update(returned, returned.Need.Edit("Renamed", "Later title", "Later statement").Value, actor, time)).Value;
        Assert.Equal(history, (await store.History(need.InternalId)).Value);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.True((await store.Update(later, later.Need.Edit("Renamed", "x", "x").Value, actor, time)).IsFailure);
        Assert.True((await store.Create(Need.Create(product.InternalId, "new", "Title", "Statement").Value, actor, time)).IsFailure);
        Assert.True((await store.Find(need.InternalId)).Value.HasValue);
    }

    [PostgresFact]
    public async Task ConcurrentNeedEditsHaveOneWinnerAndReleasedProductLockBlocksRetirement()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var actor = new AuditActor.Identified("author");
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var need = Need.Create(product.InternalId, "ID", "Title", "Statement").Value;
        var original = (await store.Create(need, actor, DateTimeOffset.UtcNow)).Value;
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            store.Update(original, need.Edit("ID", $"Edit {i}", "Statement").Value, actor, DateTimeOffset.UtcNow)));
        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(3, results.Count(r => r.IsFailure));
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        Assert.Single(events, e => e.Action == "need.edit");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var lockedRetirement = new NpgsqlCommand("UPDATE retravion_product.product SET is_active = false WHERE internal_id=$1", connection, transaction))
        {
            lockedRetirement.Parameters.AddWithValue(product.InternalId);
            await lockedRetirement.ExecuteNonQueryAsync();
        }
        var current = results.Single(r => r.IsSuccess).Value;
        var pending = store.Update(current, current.Need.SubmitForReview().Value, actor, DateTimeOffset.UtcNow, "InReview");
        await transaction.CommitAsync();
        Assert.True((await pending).IsFailure);
        Assert.Empty((await store.History(need.InternalId)).Value);
    }

    [PostgresFact]
    public async Task UserNeedAuditFailureRollsBackAndUncertainCommitIsReported()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var actor = new AuditActor.Identified("author");
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var need = Need.Create(product.InternalId, "ID", "Title", "Statement").Value;
        var created = (await store.Create(need, actor, DateTimeOffset.UtcNow)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("CREATE FUNCTION retravion_audit.reject_need() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE = '42501'; END $$; CREATE TRIGGER reject_need BEFORE INSERT ON retravion_audit.event FOR EACH ROW EXECUTE FUNCTION retravion_audit.reject_need();", connection)) await inject.ExecuteNonQueryAsync();
        Assert.True((await store.Create(Need.Create(product.InternalId, "blocked", "Title", "Statement").Value, actor, DateTimeOffset.UtcNow)).IsFailure);
        Assert.True((await store.Update(created, need.Edit("ID", "Changed", "Statement").Value, actor, DateTimeOffset.UtcNow)).IsFailure);
        Assert.True((await store.Update(created, need.SubmitForReview().Value, actor, DateTimeOffset.UtcNow, "InReview")).IsFailure);
        Assert.Empty((await store.History(need.InternalId)).Value);
        Assert.Equal(created, (await store.Find(need.InternalId)).Value.Value);
        await using (var remove = new NpgsqlCommand("DROP TRIGGER reject_need ON retravion_audit.event", connection)) await remove.ExecuteNonQueryAsync();
        var uncertain = new PostgresUserNeedStore(database.ConnectionString, async (transaction, token) => { await transaction.CommitAsync(token); throw new IOException(); });
        var result = await uncertain.Update(created, need.Edit("ID", "Changed", "Statement").Value, actor, DateTimeOffset.UtcNow);
        Assert.True(result.IsFailure);
        Assert.Contains("could not be confirmed", result.Error);
        var committed = (await store.Find(need.InternalId)).Value.Value;
        Assert.Equal("Changed", committed.Need.Title);
        var uncommitted = new PostgresUserNeedStore(database.ConnectionString, (transaction, token) => throw new IOException("commit not sent"));
        var unknown = await uncommitted.Update(committed, committed.Need.Edit("ID", "Not committed", "Statement").Value, actor, DateTimeOffset.UtcNow);
        Assert.True(unknown.IsFailure);
        Assert.Contains("could not be confirmed", unknown.Error);
        Assert.Equal(committed, (await store.Find(need.InternalId)).Value.Value);
    }
}
