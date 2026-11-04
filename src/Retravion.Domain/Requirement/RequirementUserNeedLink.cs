using CSharpFunctionalExtensions;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.Domain.Requirement;

/// <summary>An exact target; the owning Requirement version is supplied by the operation.</summary>
public sealed record RequirementUserNeedLink(Guid UserNeedInternalId, Guid UserNeedVersionId);

public static class RequirementUserNeedLinks
{
    public static Result<RequirementUserNeedLink> Add(Requirement owner, Need target)
    {
        var editable = CanChange(owner);
        if (editable.IsFailure) return Result.Failure<RequirementUserNeedLink>(editable.Error);
        return owner.ProductInternalId != target.ProductInternalId
            ? Result.Failure<RequirementUserNeedLink>("Ordinary relationships must stay within one Product.")
            : Result.Success(new RequirementUserNeedLink(target.InternalId, target.VersionId));
    }

    public static Result CanChange(Requirement owner) => owner.Status == RequirementStatus.Draft
        ? Result.Success() : Result.Failure("Only Draft Requirements can change links.");

    public static Result CanApprove(IEnumerable<Need> targets) => targets.All(t => t.Status == UserNeed.UserNeedStatus.Approved)
        ? Result.Success() : Result.Failure("Every linked User Need version must be Approved before approving the Requirement.");
}
