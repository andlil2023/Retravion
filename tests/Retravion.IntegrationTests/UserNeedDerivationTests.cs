using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Domain.UserNeed;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task NeedDerivationRetainsExactSnapshotsAndRejectsInvalidOrStaleCopies()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var draft = (await store.Create(Need.Create(from.InternalId, "SRC", "Original", "Original statement").Value, VersionActor, VersionTime)).Value;
        // User Needs have no owned ordinary links; verify an incoming Requirement link stays on the source.
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var owner = (await requirements.Create(Retravion.Domain.Requirement.Requirement.Create(from.InternalId, "REQ", "Title", "Statement", RequirementType.System).Value, VersionActor, VersionTime)).Value;
        Assert.True((await requirements.ChangeLink(owner, Endpoint(draft), true, VersionActor, VersionTime)).IsSuccess);
        var destination = Need.Create(to.InternalId, "COPY", "Edited", "Edited statement").Value;
        var result = await store.Derive(draft, destination, VersionActor, VersionTime);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        var copy = result.Value;
        Assert.NotEqual(draft.Need.InternalId, copy.Need.InternalId);
        Assert.NotEqual(draft.Need.VersionId, copy.Need.VersionId);
        Assert.Equal(UserNeedStatus.Draft, copy.Need.Status);
        Assert.Equal(1, copy.Need.VersionNumber);
        var provenance = (await store.Provenance(copy.Need.InternalId)).Value.Value;
        Assert.Equal(draft.Need.VersionId, provenance.SourceVersionId);
        Assert.Equal("Original", provenance.SourceTitle);
        Assert.Equal("Original statement", provenance.SourceStatement);
        Assert.Equal("Edited", copy.Need.Title);
        Assert.Single((await requirements.IncomingUserNeed(draft.Need.InternalId, draft.Need.VersionId)).Value);
        Assert.Empty((await requirements.IncomingUserNeed(copy.Need.InternalId, copy.Need.VersionId)).Value);
        var editedCopy = (await store.Update(copy, copy.Need.Edit("COPY", "Later copy", "Later copy statement").Value, VersionActor, VersionTime)).Value;
        Assert.Equal("Later copy", editedCopy.Need.Title);
        Assert.Equal(provenance, (await store.Provenance(copy.Need.InternalId)).Value.Value);
        var changed = (await store.Update(draft, draft.Need.Edit("SRC", "Changed", "Changed").Value, VersionActor, VersionTime)).Value;
        Assert.Equal(provenance, (await store.Provenance(copy.Need.InternalId)).Value.Value);
        var baseline = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        Assert.True((await store.Derive(draft, Need.Create(to.InternalId, "STALE", "X", "Y").Value, VersionActor, VersionTime)).IsFailure);
        Assert.True((await store.Derive(changed, Need.Create(to.InternalId, "copy", "X", "Y").Value, VersionActor, VersionTime)).IsFailure);
        Assert.True((await store.Derive(changed, Need.Create(from.InternalId, "SAME", "X", "Y").Value, VersionActor, VersionTime)).IsFailure);
        Assert.Equal(baseline, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        var audit = Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()), e => e.Action == "need.derive");
        Assert.Equal(VersionActor, audit.Actor); Assert.Equal(VersionTime, audit.OccurredAt);
        Assert.Equal(AuditActionOutcome.Success, audit.Outcome);
        Assert.Equal(new AuditTarget.Identified(copy.Need.InternalId.ToString()), audit.Target);
        Assert.Equal(copy.Need.InternalId, audit.UserNeedChange!.InternalId);
        Assert.Null(audit.UserNeedChange.Before);
        Assert.Equal(new UserNeedAuditValues(to.InternalId, "COPY", "Edited", "Edited statement", "Draft"), audit.UserNeedChange.After);
        Assert.Equal(provenance, audit.UserNeedChange.Provenance);
        Assert.Equal(copy.Need.VersionId, audit.UserNeedChange.VersionId);
        Assert.Equal(1, audit.UserNeedChange.VersionNumber);

        var review = (await store.Update(changed, changed.Need.SubmitForReview().Value, VersionActor, VersionTime, "InReview")).Value;
        var reviewCopy = await store.Derive(review, Need.Create(to.InternalId, "REVIEW", "Title", "Statement").Value, VersionActor, VersionTime);
        Assert.True(reviewCopy.IsSuccess, reviewCopy.IsFailure ? reviewCopy.Error : "");
        Assert.Equal("InReview", (await store.Provenance(reviewCopy.Value.Need.InternalId)).Value.Value.SourceStatus);
        var approved = (await store.Update(review, review.Need.Approve().Value, VersionActor, VersionTime, "Approved")).Value;
        var successor = (await store.CreateSuccessor(approved, "Next", VersionActor, VersionTime)).Value;
        var earlier = (await store.Find(approved.Need.InternalId, versionId: approved.Need.VersionId)).Value.Value;
        Assert.True((await products.Update(from, from.Retire().Value)).IsSuccess);
        foreach (var source in new[] { earlier, successor })
        {
            var copied = await store.Derive(source, Need.Create(to.InternalId, "v" + source.Need.VersionNumber, "Title", "Statement").Value, VersionActor, VersionTime);
            Assert.True(copied.IsSuccess, copied.IsFailure ? copied.Error : "");
            Assert.Equal(source.Need.VersionId, (await store.Provenance(copied.Value.Need.InternalId)).Value.Value.SourceVersionId);
        }
        Assert.True((await products.Update(to, to.Retire().Value)).IsSuccess);
        Assert.True((await store.Derive(successor, Need.Create(to.InternalId, "RETIRED", "X", "Y").Value, VersionActor, VersionTime)).IsFailure);
    }

    [PostgresFact]
    public async Task NeedDerivationSerializesSourceChangesRetirementAndDuplicateIds()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var source = (await store.Create(Need.Create(from.InternalId, "SRC", "Source", "Statement").Value, VersionActor, VersionTime)).Value;
        var baseline = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            foreach (var table in new[] { "need", "need_version" })
            {
                await using var change = new NpgsqlCommand($"UPDATE retravion_need.{table} SET title='Concurrent',revision=revision+1 WHERE internal_id=$1", connection, tx);
                change.Parameters.AddWithValue(source.Need.InternalId); await change.ExecuteNonQueryAsync();
            }
            var destination = Need.Create(to.InternalId, "STALE", "Copy", "Statement").Value;
            var pending = store.Derive(source, destination, VersionActor, VersionTime);
            await Task.Delay(200); Assert.False(pending.IsCompleted);
            await tx.CommitAsync();
            Assert.True((await pending).IsFailure);
            Assert.True((await store.Find(destination.InternalId)).Value.HasNoValue);
        }
        var current = (await store.Find(source.Need.InternalId)).Value.Value;
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await using (var retire = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", connection, tx))
            { retire.Parameters.AddWithValue(to.InternalId); await retire.ExecuteNonQueryAsync(); }
            var destination = Need.Create(to.InternalId, "RETIRED", "Copy", "Statement").Value;
            var pending = store.Derive(current, destination, VersionActor, VersionTime);
            await Task.Delay(200); Assert.False(pending.IsCompleted);
            await tx.CommitAsync();
            Assert.True((await pending).IsFailure);
            Assert.True((await store.Find(destination.InternalId)).Value.HasNoValue);
        }
        Assert.Equal(baseline, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        await using (var activate = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=true WHERE internal_id=$1", connection))
        { activate.Parameters.AddWithValue(to.InternalId); await activate.ExecuteNonQueryAsync(); }
        var attempts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.Derive(current,
            Need.Create(to.InternalId, "RACE", "Copy", "Statement").Value, VersionActor, VersionTime)));
        Assert.Single(attempts, r => r.IsSuccess);
        Assert.Equal(2, attempts.Count(r => r.IsFailure));
    }

    [PostgresFact]
    public async Task NeedDerivationRollsBackFailuresAndReportsUncertainCommits()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var source = (await store.Create(Need.Create(from.InternalId, "SRC", "Source", "Statement").Value, VersionActor, VersionTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
        await Sql("CREATE FUNCTION retravion_need.reject_derivation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE = '42501'; END $$;");
        var initial = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        foreach (var table in new[] { "retravion_need.need", "retravion_need.provenance", "retravion_audit.event" })
        {
            await Sql($"CREATE TRIGGER reject_derivation BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION retravion_need.reject_derivation();");
            var destination = Need.Create(to.InternalId, "blocked", "Title", "Statement").Value;
            Assert.True((await store.Derive(source, destination, VersionActor, VersionTime)).IsFailure);
            Assert.True((await store.Find(destination.InternalId)).Value.HasNoValue);
            Assert.True((await store.Provenance(destination.InternalId)).Value.HasNoValue);
            Assert.Equal(initial, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            await Sql($"DROP TRIGGER reject_derivation ON {table};");
        }
        foreach (var commits in new[] { false, true })
        {
            var attempts = 0;
            var uncertain = new PostgresUserNeedStore(database.ConnectionString, async (transaction, token) =>
            { attempts++; if (commits) await transaction.CommitAsync(token); throw new IOException("private"); });
            var destination = Need.Create(to.InternalId, "uncertain-" + commits, "Title", "Statement").Value;
            var result = await uncertain.Derive(source, destination, VersionActor, VersionTime);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.DoesNotContain("private", result.Error);
            Assert.Equal(1, attempts);
            Assert.Equal(commits, (await store.Find(destination.InternalId)).Value.HasValue);
            Assert.Equal(commits, (await store.Provenance(destination.InternalId)).Value.HasValue);
            Assert.Equal(initial + (commits ? 1 : 0), (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        }
    }
}
