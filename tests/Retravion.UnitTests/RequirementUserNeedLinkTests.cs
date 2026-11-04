using Retravion.Domain.Requirement;
using Retravion.Domain.UserNeed;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.UnitTests;

public sealed class RequirementUserNeedLinkTests
{
    [Fact]
    public void DraftOwnersCanLinkEveryTargetStateButOtherOwnersAndProductsCannot()
    {
        var product = Guid.NewGuid();
        var owner = RequirementState.Create(product, "R", "Title", "Statement", RequirementType.System).Value;
        var draft = Need.Create(product, "N", "Title", "Statement").Value;
        var review = draft.SubmitForReview().Value;
        var approved = review.Approve().Value;
        foreach (var target in new[] { draft, review, approved })
        {
            Assert.Equal(new RequirementUserNeedLink(target.InternalId, target.VersionId), RequirementUserNeedLinks.Add(owner, target).Value);
            Assert.True(RequirementUserNeedLinks.Add(owner.SubmitForReview().Value, target).IsFailure);
            Assert.True(RequirementUserNeedLinks.Add(owner.SubmitForReview().Value.Approve().Value, target).IsFailure);
        }
        Assert.True(RequirementUserNeedLinks.Add(owner, Need.Create(Guid.NewGuid(), "N", "Title", "Statement").Value).IsFailure);
        Assert.True(RequirementUserNeedLinks.CanApprove([]).IsSuccess);
        Assert.True(RequirementUserNeedLinks.CanApprove([approved]).IsSuccess);
        Assert.True(RequirementUserNeedLinks.CanApprove([draft, approved]).IsFailure);
        Assert.True(RequirementUserNeedLinks.CanApprove([review]).IsFailure);
    }
}
