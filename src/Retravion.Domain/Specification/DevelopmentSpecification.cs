using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public abstract record DevelopmentSpecification
{
    private protected static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public SpecificationLinks Links { get; }

    private protected DevelopmentSpecification(Guid internalId, Guid productInternalId, string id, string title,
        string description, SpecificationLinks links)
    {
        InternalId = internalId;
        ProductInternalId = productInternalId;
        Id = id;
        Title = title;
        Description = description;
        Links = links;
    }

    public Result ConfirmDeletion(Guid? confirmedInternalId, bool noActiveOrUncertainRunAttested)
    {
        if (this is not (DraftSpecification or InReviewSpecification or ApprovedSpecification))
            return Result.Failure("Implemented Specifications cannot be deleted.");
        if (confirmedInternalId != InternalId)
            return Result.Failure("Confirm deletion of this specific Specification.");
        if (!noActiveOrUncertainRunAttested)
            return Result.Failure("Explicitly attest that no run is active or uncertain before deleting the Specification.");
        return Result.Success();
    }

    private protected static Result ValidateReturnReason(string? reason)
    {
        const string invalidReason = "The return reason must contain valid UTF-8 text without NUL characters.";
        if (reason is not null)
        {
            if (reason.Contains('\0')) return Result.Failure(invalidReason);
            try { _ = Utf8.GetByteCount(reason); }
            catch (EncoderFallbackException) { return Result.Failure(invalidReason); }
        }
        return Result.Success();
    }
}
