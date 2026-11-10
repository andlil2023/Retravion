using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static readonly AuditActor RequirementActor = new AuditActor.Identified("author");
    private static readonly DateTimeOffset RequirementTime = DateTimeOffset.Parse("2026-01-02T03:04:05Z").AddTicks(1234);
    private static RequirementAuditValues RequirementValues(RequirementState r) =>
        new(r.ProductInternalId, r.Id, r.Title, r.Statement, r.Type.ToString(), r.Rationale, r.Status.ToString(), AreaEvidence.Unclassified);

    [PostgresFact]
    public async Task RequirementReadsBeforeProductInitializationReturnAbsenceAndWritesFailSafely()
    {
        await using var database = await Database.Create();
        var store = new PostgresRequirementStore(database.ConnectionString);
        var identity = Guid.NewGuid();
        var found = await store.Find(identity);
        Assert.True(found.IsSuccess); Assert.True(found.Value.HasNoValue);
        var listed = await store.List(Guid.NewGuid());
        Assert.True(listed.IsSuccess); Assert.Empty(listed.Value);
        var history = await store.History(identity);
        Assert.True(history.IsSuccess); Assert.Empty(history.Value);
        Assert.True((await store.Find(identity, versionId: Guid.NewGuid())).Value.HasNoValue);
        Assert.Empty((await store.Versions(identity)).Value);
        var r = RequirementState.Create(Guid.NewGuid(), "ID", "Title", "Statement", RequirementType.System).Value;
        var created = await store.Create(r, RequirementActor, RequirementTime);
        Assert.True(created.IsFailure); Assert.Contains("Product", created.Error);
        var updated = await store.Update(new(r, 1), r.Edit("ID", "Changed", "Statement", r.Type, null).Value, RequirementActor, RequirementTime);
        Assert.True(updated.IsFailure); Assert.Contains("Product", updated.Error);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var check = new NpgsqlCommand("SELECT to_regnamespace('retravion_product') IS NULL AND to_regnamespace('retravion_requirement') IS NULL AND to_regnamespace('retravion_audit') IS NULL", connection);
        Assert.Equal(true, await check.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task RequirementsPersistAllFieldsProductScopedIdsAndExactImmutableReviewAndAuditHistory()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Other", "Other", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var original = RequirementState.Create(product.InternalId, " REQ-1 ", "Before", "# Statement", RequirementType.Software, "Reason").Value;
        var first = (await store.Create(original, RequirementActor, RequirementTime)).Value;
        Assert.True((await store.Create(RequirementState.Create(product.InternalId, "req-1", "Duplicate", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Create(RequirementState.Create(other.InternalId, "req-1", "Other", "Statement", RequirementType.Hardware).Value, RequirementActor, RequirementTime)).IsSuccess);
        var edited = (await store.Update(first, original.Edit("REQ-2", "After", "Updated", RequirementType.Safety, null).Value, RequirementActor, RequirementTime)).Value;
        Assert.Equal(edited, (await store.Update(edited, edited.Requirement, RequirementActor, RequirementTime)).Value);
        Assert.True((await store.Update(first, original, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Update(first, original.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).IsFailure);
        var states = new List<RequirementSnapshot> { edited };
        foreach (var decision in new[] { "InReview", "Draft", "InReview", "Approved", "Reopened" })
        {
            var current = states[^1];
            var next = decision switch { "InReview" => current.Requirement.SubmitForReview().Value, "Approved" => current.Requirement.Approve().Value, "Reopened" => current.Requirement.Reopen("comment").Value, _ => current.Requirement.ReturnToDraft().Value };
            states.Add((await store.Update(current, next, RequirementActor, RequirementTime, decision, decision == "Approved" ? null : "comment")).Value);
        }
        var history = (await store.History(original.InternalId)).Value;
        Assert.Equal(5, history.Count);
        Assert.All(history, h =>
        {
            Assert.Equal("REQ-2", h.Id); Assert.Equal("After", h.Title); Assert.Equal("Updated", h.Statement);
            Assert.Equal(RequirementType.Safety, h.Type); Assert.Null(h.Rationale);
            Assert.Equal(RequirementActor, h.Actor); Assert.Equal(RequirementTime, h.OccurredAt);
        });
        Assert.Null(history[3].Comment); Assert.Equal("comment", history[0].Comment);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.RequirementChange?.InternalId == original.InternalId).ToArray();
        Assert.Equal(7, events.Length);
        Assert.All(events, e => { Assert.Equal(RequirementActor, e.Actor); Assert.Equal(RequirementTime, e.OccurredAt); Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.Equal(new AuditTarget.Identified(original.InternalId.ToString()), e.Target); });
        Assert.Equal(new RequirementAuditDetails(original.InternalId, null, RequirementValues(original), null, null, original.VersionId, 1), events.Single(e => e.Action == "requirement.create").RequirementChange);
        Assert.Equal(new RequirementAuditDetails(original.InternalId, RequirementValues(original), RequirementValues(edited.Requirement), null, null, original.VersionId, 1), events.Single(e => e.Action == "requirement.edit").RequirementChange);
        for (var i = 1; i < states.Count; i++)
        {
            var after = states[i].Requirement;
            Assert.Contains(events, e => e.RequirementChange == new RequirementAuditDetails(original.InternalId, RequirementValues(states[i - 1].Requirement), RequirementValues(after), states[i - 1].Requirement.Status == RequirementStatus.Approved ? "Reopened" : after.Status.ToString(),
                after.Status == RequirementStatus.Approved ? null : "comment", original.VersionId, 1, null,
                states[i - 1].Requirement.Status == RequirementStatus.Approved ? "comment" : null));
        }
        var later = (await store.Update(states[^1], states[^1].Requirement.Edit("REQ-2", "Later title", "Later statement", RequirementType.Regulatory, "Later rationale").Value, RequirementActor, RequirementTime)).Value;
        Assert.Equal(history, (await store.History(original.InternalId)).Value);
        Assert.Equal(later, (await new PostgresRequirementStore(database.ConnectionString).Find(original.InternalId)).Value.Value);
        Assert.Single((await store.List(product.InternalId)).Value);
        Assert.Single((await store.List(other.InternalId)).Value);
        // Neither a forged identity/owner nor changed content on a review transition is accepted.
        var moved = RequirementState.Restore(later.Requirement.InternalId, other.InternalId, "Later", "Later title", "Later statement", RequirementType.Regulatory, "Later rationale", RequirementStatus.Draft).Value;
        Assert.True((await store.Update(later, moved, RequirementActor, RequirementTime)).IsFailure);
        var forgedReview = RequirementState.Restore(later.Requirement.InternalId, product.InternalId, "Later", "Changed during review", "Later statement", RequirementType.Regulatory, "Later rationale", RequirementStatus.InReview).Value;
        Assert.True((await store.Update(later, forgedReview, RequirementActor, RequirementTime, "InReview")).IsFailure);
        foreach (var status in Enum.GetValues<RequirementStatus>())
        {
            var r = RequirementState.Create(product.InternalId, "retired-" + status, "Title", "Statement", RequirementType.System).Value;
            var snapshot = (await store.Create(r, RequirementActor, RequirementTime)).Value;
            if (status != RequirementStatus.Draft) snapshot = (await store.Update(snapshot, r.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
            if (status == RequirementStatus.Approved) snapshot = (await store.Update(snapshot, snapshot.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        }
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        foreach (var snapshot in (await store.List(product.InternalId)).Value)
        {
            var r = snapshot.Requirement;
            if (r.Status == RequirementStatus.Draft) Assert.True((await store.Update(snapshot, r.Edit(r.Id, "Denied", r.Statement, r.Type, r.Rationale).Value, RequirementActor, RequirementTime)).IsFailure);
            var decisions = r.Status switch { RequirementStatus.Draft => new[] { "InReview" }, RequirementStatus.InReview => new[] { "Approved", "Draft" }, _ => new[] { "Reopened" } };
            foreach (var decision in decisions)
            {
                var next = decision switch { "InReview" => r.SubmitForReview().Value, "Approved" => r.Approve().Value, "Reopened" => r.Reopen("Retired").Value, _ => r.ReturnToDraft().Value };
                Assert.True((await store.Update(snapshot, next, RequirementActor, RequirementTime, decision, decision == "Reopened" ? "Retired" : null)).IsFailure);
            }
            Assert.True((await store.Find(r.InternalId)).Value.HasValue);
        }
        Assert.True((await store.Create(RequirementState.Create(product.InternalId, "blocked", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).IsFailure);
    }

    [PostgresFact]
    public async Task ConcurrentRequirementEditsHaveOneWinnerAndRetirementPreventsPendingReview()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var original = (await store.Create(RequirementState.Create(product.InternalId, "ID", "Before", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => store.Update(original,
            original.Requirement.Edit("ID", $"Edit {i}", "Statement", RequirementType.Software, "Reason").Value, RequirementActor, RequirementTime)));
        Assert.Single(results, r => r.IsSuccess); Assert.Equal(3, results.Count(r => r.IsFailure));
        Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()), e => e.Action == "requirement.edit");
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var retire = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", connection, transaction))
        { retire.Parameters.AddWithValue(product.InternalId); await retire.ExecuteNonQueryAsync(); }
        var current = results.Single(r => r.IsSuccess).Value;
        var pending = store.Update(current, current.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview");
        await transaction.CommitAsync();
        Assert.True((await pending).IsFailure);
        Assert.Empty((await store.History(current.Requirement.InternalId)).Value);
    }

    [PostgresFact]
    public async Task RequirementAuditAndHistoryFailuresRollBackEveryMutationAndBothCommitOutcomesAreUncertain()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var snapshots = new List<RequirementSnapshot>();
        foreach (var status in Enum.GetValues<RequirementStatus>())
        {
            var r = RequirementState.Create(product.InternalId, status.ToString(), "Title", "Statement", RequirementType.System, "Reason").Value;
            var s = (await store.Create(r, RequirementActor, RequirementTime)).Value;
            if (status != RequirementStatus.Draft) s = (await store.Update(s, r.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
            if (status == RequirementStatus.Approved) s = (await store.Update(s, s.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
            snapshots.Add(s);
        }
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        async Task Sql(string sql) { await using var c = new NpgsqlCommand(sql, connection); await c.ExecuteNonQueryAsync(); }
        await Sql("CREATE FUNCTION retravion_requirement.reject_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-provider-detail' USING ERRCODE = '42501'; END $$;");
        var baseline = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        foreach (var table in new[] { "retravion_audit.event", "retravion_requirement.review", "retravion_requirement.requirement" })
        {
            await Sql($"CREATE TRIGGER reject_write BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION retravion_requirement.reject_write();");
            if (table != "retravion_requirement.review")
            {
                Assert.True((await store.Create(RequirementState.Create(product.InternalId, "blocked", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).IsFailure);
                Assert.True((await store.Update(snapshots[0], snapshots[0].Requirement.Edit("Draft", "Changed", "Changed", RequirementType.Hardware, null).Value, RequirementActor, RequirementTime)).IsFailure);
            }
            foreach (var s in snapshots)
            {
                var history = (await store.History(s.Requirement.InternalId)).Value;
                foreach (var decision in s.Requirement.Status switch { RequirementStatus.Draft => new[] { "InReview" }, RequirementStatus.InReview => new[] { "Approved", "Draft" }, _ => new[] { "Reopened" } })
                {
                    var r = s.Requirement;
                    var next = decision switch { "InReview" => r.SubmitForReview().Value, "Approved" => r.Approve().Value, "Reopened" => r.Reopen("Comment").Value, _ => r.ReturnToDraft().Value };
                    Assert.True((await store.Update(s, next, RequirementActor, RequirementTime, decision, "Comment")).IsFailure);
                    Assert.Equal(s, (await store.Find(r.InternalId)).Value.Value);
                    Assert.Equal(history, (await store.History(r.InternalId)).Value);
                }
            }
            Assert.Equal(baseline, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            Assert.Equal(3, (await store.List(product.InternalId)).Value.Count);
            await Sql($"DROP TRIGGER reject_write ON {table};");
        }
        // One attempt only, with either the entire transaction committed or none of it.
        foreach (var commits in new[] { false, true })
        foreach (var path in new[] { "create", "edit", "InReview", "Approved", "DraftFromReview", "DraftFromApproved" })
        {
            var attempts = 0;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            { attempts++; if (commits) await transaction.CommitAsync(token); throw new IOException("private"); });
            var beforeCount = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
            var source = path == "Approved" || path == "DraftFromReview" ? snapshots[1] : path == "DraftFromApproved" ? snapshots[2] : snapshots[0];
            source = (await store.Find(source.Requirement.InternalId)).Value.Value;
            // Restore the source state after each committed scenario below.
            var decision = path switch { "InReview" => "InReview", "Approved" => "Approved", "DraftFromReview" => "Draft", "DraftFromApproved" => "Reopened", _ => null };
            var r = source.Requirement;
            var next = path switch { "InReview" => r.SubmitForReview().Value, "Approved" => r.Approve().Value,
                "DraftFromReview" => r.ReturnToDraft().Value, "DraftFromApproved" => r.Reopen("Correction").Value,
                _ => r.Edit(r.Id, "Changed", r.Statement, RequirementType.Hardware, null).Value };
            var historyBefore = (await store.History(r.InternalId)).Value.Count;
            var created = RequirementState.Create(product.InternalId, "uncertain-" + commits, "Title", "Statement", RequirementType.System).Value;
            var result = path == "create" ? await uncertain.Create(created, RequirementActor, RequirementTime)
                : await uncertain.Update(source, next, RequirementActor, RequirementTime, decision, decision == "Reopened" ? "Correction" : null);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("Retrieve", result.Error); Assert.DoesNotContain("private", result.Error); Assert.Equal(1, attempts);
            Assert.Equal(beforeCount + (commits ? 1 : 0), (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            if (path == "create") Assert.Equal(commits, (await store.Find(created.InternalId)).Value.HasValue);
            else
            {
                var actual = (await store.Find(r.InternalId)).Value.Value;
                Assert.Equal(commits ? next : r, actual.Requirement);
                Assert.Equal(historyBefore + (commits && decision is not null ? 1 : 0), (await store.History(r.InternalId)).Value.Count);
                if (commits)
                {
                    // Re-seed each original state for the next independent uncertain path.
                    var replacement = RequirementState.Create(product.InternalId, "reset-" + path, "Title", "Statement", RequirementType.System, "Reason").Value;
                    var reset = (await store.Create(replacement, RequirementActor, RequirementTime)).Value;
                    if (r.Status != RequirementStatus.Draft) reset = (await store.Update(reset, replacement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
                    if (r.Status == RequirementStatus.Approved) reset = (await store.Update(reset, reset.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
                    snapshots[(int)r.Status] = reset;
                }
            }
        }
    }
}
