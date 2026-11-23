using System.Text;

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
}
