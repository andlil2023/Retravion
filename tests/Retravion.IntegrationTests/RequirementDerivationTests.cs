using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task DerivationRejectsConcurrentSourceEditAndDestinationRetirementWithoutChangeEvents()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var source = (await store.Create(RequirementState.Create(from.InternalId, "SRC", "Source", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var events = new PostgresAuditQuery(database.ConnectionString);
        var baseline = (await events.FindAsync(new())).Count;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            foreach (var table in new[] { "requirement", "requirement_version" })
            {
                await using var change = new NpgsqlCommand($"UPDATE retravion_requirement.{table} SET title='Concurrent',revision=revision+1 WHERE internal_id=$1", connection, tx);
                change.Parameters.AddWithValue(source.Requirement.InternalId); await change.ExecuteNonQueryAsync();
            }
            var destination = RequirementState.Create(to.InternalId, "STALE", "Copy", "Statement", RequirementType.System).Value;
            var pending = store.Derive(source, destination, RequirementActor, RequirementTime);
            await Task.Delay(200); Assert.False(pending.IsCompleted);
            await tx.CommitAsync();
            Assert.True((await pending).IsFailure);
            Assert.True((await store.Find(destination.InternalId)).Value.HasNoValue);
        }
        Assert.Equal(baseline, (await events.FindAsync(new())).Count);
        var current = (await store.Find(source.Requirement.InternalId)).Value.Value;
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await using (var retire = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", connection, tx))
            { retire.Parameters.AddWithValue(to.InternalId); await retire.ExecuteNonQueryAsync(); }
            var destination = RequirementState.Create(to.InternalId, "RETIRED", "Copy", "Statement", RequirementType.System).Value;
            var pending = store.Derive(current, destination, RequirementActor, RequirementTime);
            await Task.Delay(200); Assert.False(pending.IsCompleted);
            await tx.CommitAsync();
            Assert.True((await pending).IsFailure);
            Assert.True((await store.Find(destination.InternalId)).Value.HasNoValue);
        }
        Assert.Equal(baseline, (await events.FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task ReleasedCopyProtectsDestinationButDoesNotTraverseOrFreezeDraftSource()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var source = (await requirements.Create(RequirementState.Create(from.InternalId, "SRC", "Source", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var copy = (await requirements.Derive(source, RequirementState.Create(to.InternalId, "COPY", "Copy", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        copy = (await requirements.Update(copy, copy.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        copy = (await requirements.Update(copy, copy.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var revisions = new PostgresRevisionStore(database.ConnectionString);
        var revision = await RevCreate(revisions, to.InternalId);
        var endpoint = new RevisionSelection(RevisionEntityKind.Requirement, copy.Requirement.InternalId, copy.Requirement.VersionId, 1);
        revision = (await revisions.Select(Guid.NewGuid(), revision, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
        var preview = await ReleasePreview(revisions, revision);
        Assert.Single(preview.Members); Assert.DoesNotContain(preview.Members, m => m.Endpoint.InternalId == source.Requirement.InternalId);
        var released = await ReleaseNow(revisions, preview);
        Assert.True(released.IsSuccess, released.IsFailure ? released.Error : "");
        Assert.True((await requirements.Update(copy, copy.Requirement.Reopen("Change").Value, RequirementActor, RequirementTime, "Reopened", "Change")).IsFailure);
        var changed = await requirements.Update(source, source.Requirement.Edit("SRC", "Changed", "Changed", RequirementType.System, null).Value, RequirementActor, RequirementTime);
        Assert.True(changed.IsSuccess, changed.IsFailure ? changed.Error : "");
        Assert.Equal("Source", (await requirements.Provenance(copy.Requirement.InternalId)).Value.Value.SourceTitle);
    }

    [PostgresFact]
    public async Task DerivationSupportsExactEarlierVersionsAndCoordinatesRacesAndRetirement()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var approved = await ApprovedRequirement(store, from.InternalId, "SRC");
        var successor = (await store.CreateSuccessor(approved, "Next", RequirementActor, RequirementTime)).Value;
        var inReview = (await store.Update(successor, successor.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var old = (await store.Find(approved.Requirement.InternalId, versionId: approved.Requirement.VersionId)).Value.Value;
        foreach (var source in new[] { old, inReview })
        {
            var destination = RequirementState.Create(to.InternalId, "copy-" + source.Requirement.VersionNumber, "Copy", "Content", RequirementType.System).Value;
            var copy = (await store.Derive(source, destination, RequirementActor, RequirementTime)).Value;
            var provenance = (await store.Provenance(copy.Requirement.InternalId)).Value.Value;
            Assert.Equal(source.Requirement.VersionId, provenance.SourceVersionId);
            Assert.Equal(source.Requirement.Status.ToString(), provenance.SourceStatus);
        }
        var racing = Enumerable.Range(0, 3).Select(_ => store.Derive(inReview,
            RequirementState.Create(to.InternalId, "raced", "Copy", "Content", RequirementType.System).Value, RequirementActor, RequirementTime));
        var results = await Task.WhenAll(racing);
        Assert.Single(results, r => r.IsSuccess); Assert.Equal(2, results.Count(r => r.IsFailure));
        Assert.True((await products.Update(to, to.Retire().Value)).IsSuccess);
        var rejected = RequirementState.Create(to.InternalId, "retired", "Copy", "Content", RequirementType.System).Value;
        Assert.True((await store.Derive(inReview, rejected, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Find(rejected.InternalId)).Value.HasNoValue);
        Assert.Equal(inReview.Requirement, (await store.Find(inReview.Requirement.InternalId, versionId: inReview.Requirement.VersionId)).Value.Value.Requirement);
    }

    [PostgresFact]
    public async Task DerivationFailuresRollBackAndUncertainCommitsRequireRetrieval()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var source = (await store.Create(RequirementState.Create(from.InternalId, "S", "Title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync(); }
        await Sql("CREATE FUNCTION retravion_requirement.reject_derivation() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE = '42501'; END $$;");
        var initialEvents = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        foreach (var table in new[] { "retravion_requirement.requirement", "retravion_requirement.provenance", "retravion_audit.event" })
        {
            await Sql($"CREATE TRIGGER reject_derivation BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION retravion_requirement.reject_derivation();");
            var destination = RequirementState.Create(to.InternalId, "blocked", "Title", "Statement", RequirementType.System).Value;
            Assert.True((await store.Derive(source, destination, RequirementActor, RequirementTime)).IsFailure);
            Assert.True((await store.Find(destination.InternalId)).Value.HasNoValue);
            Assert.True((await store.Provenance(destination.InternalId)).Value.HasNoValue);
            Assert.Equal(initialEvents, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            await Sql($"DROP TRIGGER reject_derivation ON {table};");
        }
        foreach (var commits in new[] { false, true })
        {
            var attempts = 0;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            { attempts++; if (commits) await transaction.CommitAsync(token); throw new IOException("private"); });
            var destination = RequirementState.Create(to.InternalId, "uncertain-" + commits, "Title", "Statement", RequirementType.System).Value;
            var result = await uncertain.Derive(source, destination, RequirementActor, RequirementTime);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.DoesNotContain("private", result.Error);
            Assert.Equal(1, attempts);
            Assert.Equal(commits, (await store.Find(destination.InternalId)).Value.HasValue);
            Assert.Equal(commits, (await store.Provenance(destination.InternalId)).Value.HasValue);
            Assert.Equal(initialEvents + (commits ? 1 : 0), (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        }
    }

    [PostgresFact]
    public async Task DerivationRetainsDraftSnapshotAndRejectsStaleAndDuplicateWrites()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var original = (await store.Create(RequirementState.Create(from.InternalId, "SRC", "Source", "Original statement", RequirementType.Software, "Why").Value, RequirementActor, RequirementTime)).Value;
        var replacement = RequirementState.Create(to.InternalId, "COPY", "Changed title", "Edited at creation", RequirementType.Safety, null).Value;
        var result = await store.Derive(original, replacement, RequirementActor, RequirementTime);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
        var copy = result.Value;
        Assert.NotEqual(original.Requirement.InternalId, copy.Requirement.InternalId);
        Assert.NotEqual(original.Requirement.VersionId, copy.Requirement.VersionId);
        Assert.Equal(RequirementStatus.Draft, copy.Requirement.Status);
        var provenance = (await store.Provenance(copy.Requirement.InternalId)).Value.Value;
        Assert.Equal(original.Requirement.VersionId, provenance.SourceVersionId);
        Assert.Equal("Draft", provenance.SourceStatus);
        Assert.Equal("Source", provenance.SourceTitle);
        Assert.Equal("Original statement", provenance.SourceStatement);
        Assert.Equal("Edited at creation", copy.Requirement.Statement);
        Assert.Empty((await store.Links(copy.Requirement.InternalId, copy.Requirement.VersionId)).Value);
        var changed = (await store.Update(original, original.Requirement.Edit("SRC", "Later", "Later", RequirementType.System, null).Value, RequirementActor, RequirementTime)).Value;
        var baseline = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        Assert.Equal(provenance, (await store.Provenance(copy.Requirement.InternalId)).Value.Value);
        Assert.True((await store.Derive(original, RequirementState.Create(to.InternalId, "STALE", "X", "Y", RequirementType.System).Value, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Derive(changed, RequirementState.Create(to.InternalId, "copy", "X", "Y", RequirementType.System).Value, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Derive(changed, RequirementState.Create(from.InternalId, "SAME", "X", "Y", RequirementType.System).Value, RequirementActor, RequirementTime)).IsFailure);
        Assert.Single((await store.List(to.InternalId)).Value);
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        Assert.Equal(baseline, events.Count);
        var audit = Assert.Single(events, e => e.Action == "requirement.derive");
        Assert.Equal(RequirementActor, audit.Actor); Assert.Equal(RequirementTime, audit.OccurredAt);
        Assert.Equal(AuditActionOutcome.Success, audit.Outcome);
        Assert.Equal(provenance, audit.RequirementChange!.Provenance);
        Assert.Equal(copy.Requirement.VersionId, audit.RequirementChange.VersionId);
    }
}
