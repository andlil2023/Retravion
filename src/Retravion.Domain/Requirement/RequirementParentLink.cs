using CSharpFunctionalExtensions;

namespace Retravion.Domain.Requirement;

public sealed record RequirementParentLink(Guid RequirementInternalId, Guid VersionId);

/// <summary>A retained exact-version edge, projected onto stable identities for cycle detection.</summary>
public sealed record RequirementParentEdge(Guid OwnerVersionId, Guid ChildInternalId, Guid ParentInternalId);

public static class RequirementParentLinks
{
    public static Result CanChange(Requirement child) => child.Status == RequirementStatus.Draft
        ? Result.Success() : Result.Failure("Only Draft Requirements can change their parent.");

    public static Result<RequirementParentLink> Set(Requirement child, Requirement parent,
        IEnumerable<RequirementParentEdge> retainedEdges)
    {
        var editable = CanChange(child);
        if (editable.IsFailure) return Result.Failure<RequirementParentLink>(editable.Error);
        if (child.ProductInternalId != parent.ProductInternalId)
            return Result.Failure<RequirementParentLink>("Ordinary relationships must stay within one Product.");
        // All retained versions participate. Only the edge being replaced may be omitted.
        var ancestry = retainedEdges.Where(e => e.OwnerVersionId != child.VersionId)
            .ToLookup(e => e.ChildInternalId, e => e.ParentInternalId);
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        pending.Push(parent.InternalId);
        while (pending.TryPop(out var identity))
        {
            if (identity == child.InternalId)
                return Result.Failure<RequirementParentLink>("A Requirement cannot be its own ancestor, including across versions.");
            if (!visited.Add(identity)) continue;
            foreach (var ancestor in ancestry[identity]) pending.Push(ancestor);
        }
        return Result.Success(new RequirementParentLink(parent.InternalId, parent.VersionId));
    }

    public static Result CanApprove(Requirement parent) => parent.Status == RequirementStatus.Approved
        ? Result.Success() : Result.Failure("The exact parent Requirement version must be Approved before approving the child.");
}
