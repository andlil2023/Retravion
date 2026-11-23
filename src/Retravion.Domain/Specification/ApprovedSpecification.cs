using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public sealed record ApprovedSpecification : DevelopmentSpecification
{
    internal ApprovedSpecification(InReviewSpecification review)
        : base(review.InternalId, review.ProductInternalId, review.Id, review.Title, review.Description, review.Links) { }

    public Result<DraftSpecification> Reopen(string? reason, bool noActiveOrUncertainRunAttested)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure<DraftSpecification>("A reason is required to reopen an Approved Specification.");
        if (!noActiveOrUncertainRunAttested)
            return Result.Failure<DraftSpecification>("Explicitly attest that no run is active or uncertain before reopening.");
        return ValidateReturnReason(reason).Map(() => DraftSpecification.FromReturned(this));
    }
}
