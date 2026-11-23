using System.Text;
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

    public Result<DraftSpecification> ReturnToDraft(string? reason = null)
    {
        const string invalidReason = "The return reason must contain valid UTF-8 text without NUL characters.";
        if (reason is not null)
        {
            if (reason.Contains('\0')) return Result.Failure<DraftSpecification>(invalidReason);
            try { _ = Utf8.GetByteCount(reason); }
            catch (EncoderFallbackException) { return Result.Failure<DraftSpecification>(invalidReason); }
        }
        return Result.Success(DraftSpecification.FromReview(this));
    }
}
