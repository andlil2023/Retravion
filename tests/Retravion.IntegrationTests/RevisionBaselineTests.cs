using Npgsql;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Domain.Revision;
using Retravion.Domain.Requirement;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Revision;
using Retravion.Infrastructure.TestCase;
using Retravion.Infrastructure.UserNeed;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task RevisionBaselineTraversesCompleteOutgoingChainsSharedMembersAndMultipleExactVersionsOnly()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var needs = new PostgresUserNeedStore(db.ConnectionString);
        var n1 = await ApprovedNeed(needs, product.InternalId, "N");
        var n2 = (await needs.CreateSuccessor(n1, "next", RevActor, RevTime)).Value;
        n2 = (await needs.Update(n2, n2.Need.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var parent = await ParentDraft(requirements, product.InternalId, "Parent");
        parent = (await requirements.ChangeLink(parent, new(n1.Need.InternalId, n1.Need.VersionId), true, RevActor, RevTime)).Value;
        parent = await ParentApprove(requirements, parent);
        var nextParent = (await requirements.CreateSuccessor(parent, "next", RevActor, RevTime)).Value;
        var child = await ParentDraft(requirements, product.InternalId, "Child");
        child = (await requirements.AddParent(child, ParentEndpoint(parent), RevActor, RevTime)).Value;
        child = (await requirements.ChangeLink(child, new(n2.Need.InternalId, n2.Need.VersionId), true, RevActor, RevTime)).Value;
        var incomingChild = await ParentDraft(requirements, product.InternalId, "Incoming child");
        await requirements.AddParent(incomingChild, ParentEndpoint(parent), RevActor, RevTime);
        var cases = new PostgresTestCaseStore(db.ConnectionString);
        var c1 = (await cases.Create(product.InternalId, CaseContent("First root"), RevActor, RevTime)).Value;
        c1 = (await cases.ChangeLink(c1, "add", new(child.Requirement.InternalId, child.Requirement.VersionId), null, RevActor, RevTime)).Value;
        var c2 = (await cases.Create(product.InternalId, CaseContent("Shared root"), RevActor, RevTime)).Value;
        c2 = (await cases.ChangeLink(c2, "add", new(child.Requirement.InternalId, child.Requirement.VersionId), null, RevActor, RevTime)).Value;
        var incoming = (await cases.Create(product.InternalId, CaseContent("Incoming only"), RevActor, RevTime)).Value;
        await cases.ChangeLink(incoming, "add", new(parent.Requirement.InternalId, parent.Requirement.VersionId), null, RevActor, RevTime);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var row = await RevCreate(store, product.InternalId);
        Assert.Empty((await store.Baseline(product.InternalId, row.Value.InternalId)).Value.Members);
        var roots = new[]
        {
            new RevisionSelection(RevisionEntityKind.TestCase,c1.TestCase.InternalId,c1.TestCase.VersionId,1),
            new RevisionSelection(RevisionEntityKind.TestCase,c2.TestCase.InternalId,c2.TestCase.VersionId,1),
            new RevisionSelection(RevisionEntityKind.Requirement,nextParent.Requirement.InternalId,nextParent.Requirement.VersionId,2),
            new RevisionSelection(RevisionEntityKind.UserNeed,n2.Need.InternalId,n2.Need.VersionId,2)
        };
        foreach (var endpoint in roots) row = (await store.Select(Guid.NewGuid(), row, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
        var baseline = (await store.Baseline(product.InternalId, row.Value.InternalId)).Value;
        Assert.Equal(7, baseline.Members.Count); Assert.Equal(4, baseline.Members.Count(m => m.Explicit));
        Assert.All(roots, e => Assert.Contains(baseline.Members, m => m.Endpoint == e && m.Explicit));
        Assert.Contains(baseline.Members, m => m.Endpoint.VersionId == n1.Need.VersionId && !m.Explicit);
        Assert.Contains(baseline.Members, m => m.Endpoint.VersionId == parent.Requirement.VersionId && !m.Explicit);
        Assert.DoesNotContain(baseline.Members, m => m.Endpoint.InternalId == incoming.TestCase.InternalId || m.Endpoint.InternalId == incomingChild.Requirement.InternalId);
        Assert.Equal(new[] { 1, 2 }, baseline.Members.Where(m => m.Endpoint.InternalId == n1.Need.InternalId).Select(m => m.Endpoint.VersionNumber).Order());
        Assert.Equal(new[] { 1, 2 }, baseline.Members.Where(m => m.Endpoint.InternalId == parent.Requirement.InternalId).Select(m => m.Endpoint.VersionNumber).Order());
        Assert.Contains(baseline.Members, m => m.Status == "Draft"); Assert.Contains(baseline.Members, m => m.Status == "InReview"); Assert.Contains(baseline.Members, m => m.Status == "Approved");
        var foreign = await ParentDraft(requirements, other.InternalId, "Foreign");
        var foreignEndpoint = new RevisionSelection(RevisionEntityKind.Requirement, foreign.Requirement.InternalId, foreign.Requirement.VersionId, 1);
        Assert.True((await store.Select(Guid.NewGuid(), row, "add", foreignEndpoint, null, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.DoesNotContain((await store.Candidates(product.InternalId)).Value, m => m.Endpoint == foreignEndpoint);
        var approved = await ApprovedNeed(needs, product.InternalId, "Unrelated newer");
        await needs.CreateSuccessor(approved, "not a root", RevActor, RevTime);
        Assert.Equal(baseline.Members.Select(m => m.Endpoint), (await store.Baseline(product.InternalId, row.Value.InternalId)).Value.Members.Select(m => m.Endpoint));
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        await using (var cycle = new NpgsqlCommand("INSERT INTO retravion_requirement.parent_link VALUES($1,$2,$3)", c))
        {
            cycle.Parameters.AddWithValue(parent.Requirement.VersionId); cycle.Parameters.AddWithValue(child.Requirement.InternalId); cycle.Parameters.AddWithValue(child.Requirement.VersionId);
            await cycle.ExecuteNonQueryAsync();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(7, (await store.Baseline(product.InternalId, row.Value.InternalId, timeout.Token)).Value.Members.Count);
        // A broken/cross-Product endpoint fails the whole read, never silently omitting part of the closure.
        await using (var corrupt = new NpgsqlCommand("UPDATE retravion_requirement.parent_link SET target_internal_id=$1,target_version_id=$2 WHERE owner_version_id=$3", c))
        {
            corrupt.Parameters.AddWithValue(foreign.Requirement.InternalId); corrupt.Parameters.AddWithValue(foreign.Requirement.VersionId); corrupt.Parameters.AddWithValue(child.Requirement.VersionId);
            await corrupt.ExecuteNonQueryAsync();
        }
        Assert.True((await store.Baseline(product.InternalId, row.Value.InternalId)).IsFailure);
        Assert.True((await store.Baseline(other.InternalId, row.Value.InternalId)).IsFailure);
        await using var missing = new NpgsqlCommand("DROP TABLE retravion_requirement.parent_link", c); await missing.ExecuteNonQueryAsync();
        var incomplete = await store.Baseline(product.InternalId, row.Value.InternalId);
        Assert.True(incomplete.IsFailure); Assert.Contains("complete outgoing reference storage", incomplete.Error);
    }
}
