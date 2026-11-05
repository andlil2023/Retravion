using System.Collections.Immutable;

namespace Retravion.Domain.Revision;

public abstract record Revision
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public string Identifier { get; }
    public string Description { get; }
    public ImmutableArray<RevisionSelection> Selections { get; }
    public abstract string State { get; }

    private protected Revision(Guid id, Guid product, string identifier, string description, ImmutableArray<RevisionSelection> selections) =>
        (InternalId, ProductInternalId, Identifier, Description, Selections) = (id, product, identifier, description, selections);

    public bool SameValues(Revision other) => InternalId == other.InternalId && ProductInternalId == other.ProductInternalId &&
        Identifier == other.Identifier && Description == other.Description && State == other.State && Selections.SequenceEqual(other.Selections);
}
