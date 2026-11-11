using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Domain.Feature;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.Product;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static readonly AuditActor FeatureActor = new AuditActor.Identified("feature-author");
    private static readonly DateTimeOffset FeatureTime = DateTimeOffset.Parse("2026-01-02T03:04:05.1234567+02:00");

    [PostgresFact]
    public async Task FeatureTransactionsPreserveEvidenceAndRejectInvalidStaleAndRetiredWrites()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        var markdown = "Paragraph\n\n| A | B |\n|---|---|\n| x | y |\n\n```mermaid\ngraph TD\n A-->B\n```";
        var created = (await store.Create(product.InternalId, FeatureContent.Create("", markdown), FeatureActor, FeatureTime)).Value;
        Assert.Equal("F-001", created.Feature.Id);
        Assert.Equal("F-001", (await store.Create(other.InternalId, FeatureContent.Create("Other", ""), FeatureActor, FeatureTime)).Value.Feature.Id);
        Assert.True((await store.Review(created, "InReview", null, FeatureActor, FeatureTime)).IsFailure);
        var unchanged = (await store.Update(created, created.Feature.Content, FeatureActor, FeatureTime)).Value;
        Assert.Equal(created.Revision, unchanged.Revision);
        var edited = (await store.Update(created, FeatureContent.Create("Title", markdown), FeatureActor, FeatureTime)).Value;
        Assert.True((await store.Update(created, FeatureContent.Create("Stale", ""), FeatureActor, FeatureTime)).IsFailure);
        var found = (await store.Find(edited.Feature.InternalId, edited.Feature.VersionId)).Value.Value;
        Assert.Equal(markdown, found.Feature.Content.Description);
        var review = (await store.Review(edited, "InReview", "submit", FeatureActor, FeatureTime)).Value;
        var returned = (await store.Review(review, "Draft", "revise", FeatureActor, FeatureTime)).Value;
        review = (await store.Review(returned, "InReview", "resubmit", FeatureActor, FeatureTime)).Value;
        var approved = (await store.Review(review, "Approved", "yes", FeatureActor, FeatureTime)).Value;
        Assert.True((await store.Reopen(approved, "", FeatureActor, FeatureTime)).IsFailure);
        var successor = (await store.CreateSuccessor(approved, "improve", FeatureActor, FeatureTime)).Value;
        Assert.Equal(approved.Feature.VersionId, successor.Feature.SourceVersionId);
        Assert.True((await store.Reopen(approved, "old", FeatureActor, FeatureTime)).IsFailure);
        Assert.Equal(2, (await store.Versions(created.Feature.InternalId)).Value.Count);
        Assert.Equal("Approved", (await store.Find(created.Feature.InternalId, approved.Feature.VersionId)).Value.Value.Feature.Status);
        var history = (await store.History(created.Feature.InternalId)).Value;
        Assert.Equal(7, history.Count);
        Assert.Equal("submit", history.First(h => h.Action == "feature.review.InReview").Change.Comment);
        Assert.Equal("revise", history.Single(h => h.Action == "feature.review.Draft").Change.Comment);
        Assert.Equal("InReview", history.Single(h => h.Action == "feature.review.Draft").Change.Before!.Status);
        Assert.Equal("Draft", history.Single(h => h.Action == "feature.review.Draft").Change.After.Status);
        Assert.Equal("improve", history.Last().Change.Reason);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.FeatureChange?.InternalId == created.Feature.InternalId).ToArray();
        Assert.Equal(7, events.Length);
        Assert.All(events, e => { Assert.Equal(FeatureActor, e.Actor); Assert.Equal(FeatureTime.UtcTicks, e.OccurredAt.UtcTicks); Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.Equal(new AuditTarget.Identified(created.Feature.InternalId.ToString()), e.Target); });
        Assert.Equal(markdown, events.Single(e => e.Action == "feature.create").FeatureChange!.After.Description);
        Assert.Equal("", events.Single(e => e.Action == "feature.edit").FeatureChange!.Before!.Title);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var retire = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", connection)) { retire.Parameters.AddWithValue(product.InternalId); await retire.ExecuteNonQueryAsync(); }
        Assert.True((await store.Update(successor, FeatureContent.Create("retired", ""), FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.Create(product.InternalId, FeatureContent.Create("retired", ""), FeatureActor, FeatureTime)).IsFailure);
    }

    [PostgresFact]
    public async Task FeatureConcurrentWritesAndProtectedReopeningAreEnforced()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => store.Create(product.InternalId, FeatureContent.Create($"Title {i}", ""), FeatureActor, FeatureTime)));
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), results.Select(r => r.Value.Feature.Number).Order());
        var initial = results[0].Value;
        var edits = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => store.Update(initial, FeatureContent.Create($"Edit {i}", ""), FeatureActor, FeatureTime)));
        var edited = Assert.Single(edits, r => r.IsSuccess).Value;
        var review = (await store.Review(edited, "InReview", null, FeatureActor, FeatureTime)).Value;
        var approved = (await store.Review(review, "Approved", "approved", FeatureActor, FeatureTime)).Value;
        var reopened = (await store.Reopen(approved, "correction", FeatureActor, FeatureTime)).Value;
        Assert.Equal(approved.Feature.VersionId, reopened.Feature.VersionId);
        Assert.Equal("correction", (await store.History(initial.Feature.InternalId)).Value.Last().Change.Reason);
        review = (await store.Review(reopened, "InReview", null, FeatureActor, FeatureTime)).Value;
        approved = (await store.Review(review, "Approved", null, FeatureActor, FeatureTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var cmd = new NpgsqlCommand("UPDATE retravion_feature.feature_version SET release_protected=true WHERE version_id=$1", connection)) { cmd.Parameters.AddWithValue(approved.Feature.VersionId); await cmd.ExecuteNonQueryAsync(); }
        Assert.True((await store.Reopen(approved, "protected", FeatureActor, FeatureTime)).IsFailure);
        var successor = await store.CreateSuccessor(approved, "change", FeatureActor, FeatureTime);
        Assert.True(successor.IsSuccess, successor.IsFailure ? successor.Error : "");
    }

    [PostgresFact]
    public async Task FeatureAuditFailureRollsBackEveryLifecyclePath()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        var draft = (await store.Create(product.InternalId, FeatureContent.Create("Title", "original"), FeatureActor, FeatureTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        async Task Reject() { await using var cmd = new NpgsqlCommand("CREATE TRIGGER reject_feature BEFORE INSERT ON retravion_audit.event FOR EACH ROW EXECUTE FUNCTION public.reject_feature_event()", connection); await cmd.ExecuteNonQueryAsync(); }
        async Task Allow() { await using var cmd = new NpgsqlCommand("DROP TRIGGER reject_feature ON retravion_audit.event", connection); await cmd.ExecuteNonQueryAsync(); }
        await using (var cmd = new NpgsqlCommand("CREATE FUNCTION public.reject_feature_event() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await cmd.ExecuteNonQueryAsync();
        await Reject();
        Assert.True((await store.Create(product.InternalId, FeatureContent.Create("new", ""), FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.Update(draft, FeatureContent.Create("edit", ""), FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.Review(draft, "InReview", "submit", FeatureActor, FeatureTime)).IsFailure);
        Assert.Single((await store.List(product.InternalId)).Value); Assert.Single((await store.History(draft.Feature.InternalId)).Value);
        await Allow();
        var review = (await store.Review(draft, "InReview", "submit", FeatureActor, FeatureTime)).Value;
        await Reject();
        Assert.True((await store.Review(review, "Draft", "return", FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.Review(review, "Approved", "approve", FeatureActor, FeatureTime)).IsFailure);
        Assert.Equal("InReview", (await store.Find(draft.Feature.InternalId)).Value.Value.Feature.Status);
        await Allow();
        var approved = (await store.Review(review, "Approved", "approve", FeatureActor, FeatureTime)).Value;
        await Reject();
        Assert.True((await store.Reopen(approved, "reopen", FeatureActor, FeatureTime)).IsFailure);
        Assert.True((await store.CreateSuccessor(approved, "successor", FeatureActor, FeatureTime)).IsFailure);
        Assert.Equal("Approved", (await store.Find(draft.Feature.InternalId)).Value.Value.Feature.Status);
        Assert.Equal(3, (await store.History(draft.Feature.InternalId)).Value.Count);
        Assert.Single((await store.Versions(draft.Feature.InternalId)).Value);
        await Allow();
    }

    [PostgresFact]
    public async Task FeatureReviewAndReopenRecheckAuthorityBeforeCommit()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        var draft = (await store.Create(product.InternalId, FeatureContent.Create("Title", ""), FeatureActor, FeatureTime)).Value;
        var calls = 0;
        var denied = await store.Review(draft, "InReview", null, FeatureActor, FeatureTime, authorized: () => Task.FromResult(++calls == 1));
        Assert.True(denied.IsFailure); Assert.Equal("Access denied.", denied.Error); Assert.Equal(2, calls);
        Assert.Equal("Draft", (await store.Find(draft.Feature.InternalId)).Value.Value.Feature.Status);
        Assert.Single((await store.History(draft.Feature.InternalId)).Value);
        var review = (await store.Review(draft, "InReview", null, FeatureActor, FeatureTime)).Value;
        var approved = (await store.Review(review, "Approved", null, FeatureActor, FeatureTime)).Value;
        calls = 0;
        denied = await store.Reopen(approved, "reason", FeatureActor, FeatureTime, authorized: () => Task.FromResult(++calls == 1));
        Assert.True(denied.IsFailure); Assert.Equal("Access denied.", denied.Error); Assert.Equal(2, calls);
        Assert.Equal("Approved", (await store.Find(draft.Feature.InternalId)).Value.Value.Feature.Status);
        Assert.Equal(3, (await store.History(draft.Feature.InternalId)).Value.Count);
    }

    [PostgresFact]
    public async Task FeatureAuditFailureRollsBackAndUncertainOutcomesAreRetrievable()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        var first = (await store.Create(product.InternalId, FeatureContent.Create("Title", "old"), FeatureActor, FeatureTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var cmd = new NpgsqlCommand("CREATE FUNCTION public.reject_feature_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$; CREATE TRIGGER reject_feature_audit BEFORE INSERT ON retravion_audit.event FOR EACH ROW EXECUTE FUNCTION public.reject_feature_audit();", connection)) await cmd.ExecuteNonQueryAsync();
        Assert.True((await store.Update(first, FeatureContent.Create("Changed", "new"), FeatureActor, FeatureTime)).IsFailure);
        Assert.Equal("Title", (await store.Find(first.Feature.InternalId)).Value.Value.Feature.Content.Title);
        Assert.Single((await store.History(first.Feature.InternalId)).Value);
        await using (var cmd = new NpgsqlCommand("DROP TRIGGER reject_feature_audit ON retravion_audit.event", connection)) await cmd.ExecuteNonQueryAsync();
        var committedId = Guid.NewGuid();
        var committed = new PostgresFeatureStore(database.ConnectionString, async (tx, token) => { await tx.CommitAsync(token); throw new Exception("lost acknowledgement"); });
        var result = await committed.Update(first, FeatureContent.Create("Committed", "new"), FeatureActor, FeatureTime, operationId: committedId);
        Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error);
        Assert.True((await store.Outcome(product.InternalId, committedId)).Value.HasValue);
        var current = (await store.Find(first.Feature.InternalId)).Value.Value;
        Assert.Equal("Committed", current.Feature.Content.Title);
        var rolledBackId = Guid.NewGuid();
        var rolledBack = new PostgresFeatureStore(database.ConnectionString, (_, _) => throw new Exception("commit not performed"));
        result = await rolledBack.Update(current, FeatureContent.Create("Uncommitted", ""), FeatureActor, FeatureTime, operationId: rolledBackId);
        Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error);
        Assert.True((await store.Outcome(product.InternalId, rolledBackId)).Value.HasNoValue);
        Assert.Equal("Committed", (await store.Find(first.Feature.InternalId)).Value.Value.Feature.Content.Title);
    }
}
