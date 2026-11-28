using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public sealed record DraftSpecification : DevelopmentSpecification
{
    private const int MaximumDescriptionBytes = 1024 * 1024;

    private DraftSpecification(Guid internalId, Guid productInternalId, string id, string title, string description,
        SpecificationLinks links) : base(internalId, productInternalId, id, title, description, links) { }

    public Result<InReviewSpecification> SubmitForReview() => InReviewSpecification.FromDraft(this);

    internal static DraftSpecification FromReturned(DevelopmentSpecification specification) =>
        new(specification.InternalId, specification.ProductInternalId, specification.Id, specification.Title,
            specification.Description, specification.Links);

    public static Result<DraftSpecification> Create(Guid productInternalId, string? id, string? title, string? description) =>
        Restore(Guid.NewGuid(), productInternalId, id, title, description ?? string.Empty);

    public Result<DraftSpecification> EditContent(string? id, string? title, string? description) =>
        Restore(InternalId, ProductInternalId, id, title, description ?? string.Empty, Links);

    public Result<DraftSpecification> EditLinks(IEnumerable<SpecificationLink> targets) =>
        SpecificationLinks.Create(targets).Map(links => new DraftSpecification(InternalId, ProductInternalId, Id, Title, Description, links));

    public static Result<DraftSpecification> Restore(Guid internalId, Guid productInternalId, string? id, string? title,
        string description, SpecificationLinks? links = null)
    {
        if (internalId == Guid.Empty || productInternalId == Guid.Empty)
            return Result.Failure<DraftSpecification>("Specification and Product identities are required.");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return Result.Failure<DraftSpecification>("A Specification ID and title are required.");
        if (description is null)
            return Result.Failure<DraftSpecification>("A Specification description must be at most 1 MB of UTF-8 Markdown.");
        // PostgreSQL text cannot represent NUL or malformed Unicode. Do not silently replace either.
        if (id.Contains('\0') || title.Contains('\0') || description.Contains('\0'))
            return Result.Failure<DraftSpecification>("Specification fields must contain valid UTF-8 text without NUL characters.");
        try
        {
            _ = Utf8.GetByteCount(id);
            _ = Utf8.GetByteCount(title);
            if (Utf8.GetByteCount(description) > MaximumDescriptionBytes)
                return Result.Failure<DraftSpecification>("A Specification description must be at most 1 MB of UTF-8 Markdown.");
        }
        catch (EncoderFallbackException)
        {
            return Result.Failure<DraftSpecification>("Specification fields must contain valid UTF-8 text without NUL characters.");
        }
        return Result.Success(new DraftSpecification(internalId, productInternalId, id.Trim(), title, description, links ?? SpecificationLinks.Empty));
    }
}
