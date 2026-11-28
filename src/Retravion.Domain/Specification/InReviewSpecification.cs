using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public sealed record InReviewSpecification : DevelopmentSpecification
{
    private InReviewSpecification(DraftSpecification draft)
        : base(draft.InternalId, draft.ProductInternalId, draft.Id, draft.Title, draft.Description, draft.Links) { }

    internal static Result<InReviewSpecification> FromDraft(DraftSpecification draft) =>
        string.IsNullOrWhiteSpace(draft.Description)
            ? Result.Failure<InReviewSpecification>("A Specification description is required to submit for review.")
            : Result.Success(new InReviewSpecification(draft));

    // Link approval is a live cross-record invariant enforced by the write transaction.
    public ApprovedSpecification Approve() => new(this);

    public Result<DraftSpecification> ReturnToDraft(string? reason = null) =>
        ValidateReturnReason(reason).Map(() => DraftSpecification.FromReturned(this));
}
