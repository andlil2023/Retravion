using System.Text.Json;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Feature;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.Revision;
using Retravion.Infrastructure.UserNeed;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task FeatureOnlyRootResolvesExactOutgoingClosureAndBlocksIndirectUnapprovedVersions()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var revisions = new PostgresRevisionStore(db.ConnectionString);
        var features = new PostgresFeatureStore(db.ConnectionString);
        var endpoint = new RevisionSelection(RevisionEntityKind.Feature, g.Feature.Feature.InternalId, g.Feature.Feature.VersionId, 1);
        var row = await RevCreate(revisions, g.Product.InternalId, "Feature only");
        row = (await revisions.Select(Guid.NewGuid(), row, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
        var preview = await ReleasePreview(revisions, row);
        Assert.Equal(5, preview.Members.Count);
        Assert.Equal(endpoint, Assert.Single(preview.Members, m => m.Explicit).Endpoint);
        Assert.Contains(preview.Members, m => m.Endpoint.VersionId == g.Parent1.Requirement.VersionId);
        Assert.DoesNotContain(preview.Members, m => m.Endpoint.VersionId == g.Parent2.Requirement.VersionId);
        Assert.Equal(2, preview.Members.Count(m => m.Endpoint.Kind == RevisionEntityKind.UserNeed));
        var needs = new PostgresUserNeedStore(db.ConnectionString);
        var need = (await needs.Update(g.Need2, g.Need2.Need.Reopen("Correct indirect evidence").Value, RevActor, RevTime, "Reopened", "Correct indirect evidence")).Value;
        Assert.Equal("Approved", (await features.Find(endpoint.InternalId)).Value.Value.Feature.Status);
        var count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        Assert.Contains("entirely Approved", (await ReleaseNow(revisions, await ReleasePreview(revisions, row))).Error);
        Assert.Equal(count, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
        need = (await needs.Update(need, need.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
        Assert.True((await needs.Update(need, need.Need.Approve().Value, RevActor, RevTime, "Approved")).IsSuccess);
        Assert.Contains("baseline changed", (await ReleaseNow(revisions, preview)).Error);

        // A newer Feature version never substitutes for the selected, still Approved version.
        var successor = (await features.CreateSuccessor(g.Feature, "Next capability", RevActor, RevTime)).Value;
        successor = (await features.ChangeLink(successor, "remove", g.Feature.Feature.Links[0], null, RevActor, RevTime)).Value;
        var newer = endpoint with { VersionId = successor.Feature.VersionId, VersionNumber = 2 };
        var candidates = (await revisions.Candidates(g.Product.InternalId)).Value;
        Assert.Contains(candidates, m => m.Endpoint == endpoint);
        Assert.Contains(candidates, m => m.Endpoint == newer && m.Status == "Draft");
        preview = await ReleasePreview(revisions, row);
        Assert.Equal(endpoint, Assert.Single(preview.Members, m => m.Explicit).Endpoint);
        Assert.True((await ReleaseNow(revisions, preview)).IsSuccess);
        Assert.True((await features.Reopen(g.Feature, "Forbidden", RevActor, RevTime)).IsFailure);
        var frozen = (await revisions.Baseline(g.Product.InternalId, row.Value.InternalId)).Value;
        Assert.Equal(JsonSerializer.Serialize(preview.Members), JsonSerializer.Serialize(frozen.Members));
        // Frozen inspection does not deserialize or traverse the current Feature links.
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        await using (var corrupt = new NpgsqlCommand("UPDATE retravion_feature.feature_version SET links='[null]' WHERE version_id=$1", c))
        { corrupt.Parameters.AddWithValue(endpoint.VersionId); await corrupt.ExecuteNonQueryAsync(); }
        Assert.Equal(JsonSerializer.Serialize(frozen.Members), JsonSerializer.Serialize((await revisions.Baseline(g.Product.InternalId, row.Value.InternalId)).Value.Members));
    }

    [PostgresFact]
    public async Task FeatureSelectionReplacementRetainsExactOldNewAuditAndValidatesProductAndVersionIdentity()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var revisions = new PostgresRevisionStore(db.ConnectionString); var features = new PostgresFeatureStore(db.ConnectionString);
        var e1 = new RevisionSelection(RevisionEntityKind.Feature, g.Feature.Feature.InternalId, g.Feature.Feature.VersionId, 1);
        var successor = (await features.CreateSuccessor(g.Feature, "Next", RevActor, RevTime)).Value;
        var e2 = e1 with { VersionId = successor.Feature.VersionId, VersionNumber = 2 };
        var row = await RevCreate(revisions, g.Product.InternalId, "Feature selection");
        row = (await revisions.Select(Guid.NewGuid(), row, "add", e1, null, RevActor, RevTime, RevAllowed)).Value;
        var audit = new PostgresAuditQuery(db.ConnectionString); var count = (await audit.FindAsync(new())).Count;
        Assert.Equal(row.Revision, (await revisions.Select(Guid.NewGuid(), row, "replace", e1, e1, RevActor, RevTime, RevAllowed)).Value.Revision);
        foreach (var invalid in new[] { e2 with { VersionNumber = 8 }, e2 with { InternalId = Guid.NewGuid() }, e2 with { VersionId = Guid.NewGuid() } })
            Assert.True((await revisions.Select(Guid.NewGuid(), row, "replace", e1, invalid, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Equal(count, (await audit.FindAsync(new())).Count);
        var foreign = await PrepareRelease(db.ConnectionString);
        count = (await audit.FindAsync(new())).Count;
        var wrongProduct = new RevisionSelection(RevisionEntityKind.Feature, foreign.Feature.Feature.InternalId, foreign.Feature.Feature.VersionId, 1);
        Assert.True((await revisions.Select(Guid.NewGuid(), row, "add", wrongProduct, null, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Equal(count, (await audit.FindAsync(new())).Count);
        var operation = Guid.NewGuid();
        row = (await revisions.Select(operation, row, "replace", e1, e2, RevActor, RevTime, RevAllowed)).Value;
        var change = Assert.Single(await audit.FindAsync(new()), e => e.RevisionChange?.OperationId == operation);
        Assert.Equal(RevActor, change.Actor); Assert.Equal(RevTime, change.OccurredAt);
        Assert.Equal(AuditActionOutcome.Success, change.Outcome);
        Assert.Equal(new AuditTarget.Identified(row.Value.InternalId.ToString()), change.Target);
        Assert.Equal(e1, Assert.Single(change.RevisionChange!.Before!.Selections));
        Assert.Equal(e2, Assert.Single(change.RevisionChange.After!.Selections));
        Assert.Equal(e2, Assert.Single(row.Value.Selections));
        Assert.Equal(successor.Revision, (await features.Find(e2.InternalId)).Value.Value.Revision);
        Assert.Contains("entirely Approved", (await ReleaseNow(revisions, await ReleasePreview(revisions, row))).Error);
    }

    [PostgresFact]
    public async Task FeatureContentLinkAndReviewChangesInvalidateReleaseConfirmationEvenAfterReapproval()
    {
        await using var db = await Database.Create(); var g = await PrepareRelease(db.ConnectionString);
        var revisions = new PostgresRevisionStore(db.ConnectionString); var features = new PostgresFeatureStore(db.ConnectionString);
        var preview = await ReleasePreview(revisions, g.Row);
        var feature = (await features.Reopen(g.Feature, "Correct capability", RevActor, RevTime)).Value;
        feature = (await features.Update(feature, FeatureContent.Create("Corrected capability", "New description"), RevActor, RevTime)).Value;
        feature = (await features.ChangeLink(feature, "remove", g.Feature.Feature.Links[0], null, RevActor, RevTime)).Value;
        Assert.Contains("entirely Approved", (await ReleaseNow(revisions, await ReleasePreview(revisions, g.Row))).Error);
        feature = (await features.Review(feature, "InReview", null, RevActor, RevTime)).Value;
        feature = (await features.Review(feature, "Approved", null, RevActor, RevTime)).Value;
        Assert.Contains("baseline changed", (await ReleaseNow(revisions, preview)).Error);
        Assert.True((await ReleaseNow(revisions, await ReleasePreview(revisions, g.Row))).IsSuccess);
        var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Where(e => e.FeatureChange?.InternalId == feature.Feature.InternalId).ToArray();
        Assert.Contains(events, e => e.Action == "feature.edit" && e.FeatureChange!.Before!.Title == "Release capability" && e.FeatureChange.After.Title == "Corrected capability");
        Assert.Contains(events, e => e.Action == "feature.link.remove" && e.FeatureChange!.Before!.Links!.Length == 1 && e.FeatureChange!.After.Links!.Length == 0);
        Assert.True((await features.Reopen(feature, "Protected", RevActor, RevTime)).IsFailure);
    }
}
