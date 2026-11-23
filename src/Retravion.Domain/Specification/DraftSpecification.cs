using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public sealed record DraftSpecification
{
    private const int MaximumDescriptionBytes = 1024 * 1024;

    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }

    private DraftSpecification(Guid internalId, Guid productInternalId, string id, string title, string description)
    {
        InternalId = internalId;
        ProductInternalId = productInternalId;
        Id = id;
        Title = title;
        Description = description;
    }

    public static Result<DraftSpecification> Restore(Guid internalId, Guid productInternalId, string id, string title, string description)
    {
        if (internalId == Guid.Empty || productInternalId == Guid.Empty)
            return Result.Failure<DraftSpecification>("Specification and Product identities are required.");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return Result.Failure<DraftSpecification>("A Specification ID and title are required.");
        if (description is null || Encoding.UTF8.GetByteCount(description) > MaximumDescriptionBytes)
            return Result.Failure<DraftSpecification>("A Specification description must be at most 1 MB of UTF-8 Markdown.");
        return Result.Success(new DraftSpecification(internalId, productInternalId, id.Trim(), title, description));
    }
}
