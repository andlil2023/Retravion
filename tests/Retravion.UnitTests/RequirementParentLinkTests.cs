using Retravion.Domain.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.UnitTests;

public sealed class RequirementParentLinkTests
{
    private static RequirementState Draft(Guid product, string id) =>
        RequirementState.Create(product, id, id, "Statement", RequirementType.System).Value;

    [Fact]
    public void OnlyDraftChildrenCanLinkSameProductParentsInEveryStatus()
    {
        var product = Guid.NewGuid();
        var child = Draft(product, "Child");
        var parent = Draft(product, "Parent");
        foreach (var target in new[] { parent, parent.SubmitForReview().Value, parent.SubmitForReview().Value.Approve().Value })
        {
            Assert.Equal(new RequirementParentLink(target.InternalId, target.VersionId), RequirementParentLinks.Set(child, target, []).Value);
            Assert.True(RequirementParentLinks.Set(child.SubmitForReview().Value, target, []).IsFailure);
            Assert.True(RequirementParentLinks.Set(child.SubmitForReview().Value.Approve().Value, target, []).IsFailure);
        }
        Assert.True(RequirementParentLinks.Set(child, Draft(Guid.NewGuid(), "Other"), []).IsFailure);
        Assert.True(RequirementParentLinks.CanApprove(parent).IsFailure);
        Assert.True(RequirementParentLinks.CanApprove(parent.SubmitForReview().Value).IsFailure);
        Assert.True(RequirementParentLinks.CanApprove(parent.SubmitForReview().Value.Approve().Value).IsSuccess);
    }

    [Fact]
    public void CyclesUseAllRetainedStableIdentitiesNotOnlyExactTargetChains()
    {
        var product = Guid.NewGuid();
        var a = Draft(product, "A"); var b = Draft(product, "B"); var c = Draft(product, "C");
        Assert.True(RequirementParentLinks.Set(a, a, []).IsFailure);
        var aApproved = a.SubmitForReview().Value.Approve().Value;
        var a2 = aApproved.CreateSuccessor("Next").Value;
        Assert.True(RequirementParentLinks.Set(a2, aApproved, []).IsFailure);
        Assert.True(RequirementParentLinks.Set(c, a,
            [new(a.VersionId, a.InternalId, b.InternalId), new(b.VersionId, b.InternalId, c.InternalId)]).IsFailure);
        var b2 = b.SubmitForReview().Value.Approve().Value.CreateSuccessor("Next").Value;
        // B v2 -> A v1 prohibits A v2 -> B v1 even though B v1 itself has no parent.
        Assert.True(RequirementParentLinks.Set(a2, b, [new(b2.VersionId, b.InternalId, a.InternalId)]).IsFailure);
        // Editing A v2 must not discard A v1's historical edge from ancestry checks.
        Assert.True(RequirementParentLinks.Set(b2, a2, [new(a.VersionId, a.InternalId, b.InternalId)]).IsFailure);
    }

    [Fact]
    public void AcyclicReplacementAllowsSharedAncestryAndDeepTraversalTerminates()
    {
        var product = Guid.NewGuid();
        var a = Draft(product, "A"); var b = Draft(product, "B"); var c = Draft(product, "C"); var root = Draft(product, "Root");
        RequirementParentEdge[] edges = [new(a.VersionId, a.InternalId, b.InternalId), new(b.VersionId, b.InternalId, root.InternalId),
            new(c.VersionId, c.InternalId, root.InternalId)];
        Assert.True(RequirementParentLinks.Set(a, c, edges).IsSuccess);
        Assert.True(RequirementParentLinks.Set(root, a, edges).IsFailure);
        var chain = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToArray();
        var deep = chain.Zip(chain.Skip(1), (child, parent) => new RequirementParentEdge(Guid.NewGuid(), child, parent)).ToList();
        deep.Add(new(Guid.NewGuid(), b.InternalId, chain[0]));
        deep.Add(new(Guid.NewGuid(), chain[^1], a.InternalId));
        Assert.True(RequirementParentLinks.Set(a, b, deep).IsFailure);
    }
}
