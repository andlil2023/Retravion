using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Revision;

public sealed record DraftRevision
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public string Identifier { get; }
    public string Description { get; }
    public ImmutableArray<RevisionSelection> Selections { get; }
    public string State => "Draft";

    private DraftRevision(Guid id, Guid product, string identifier, string description, ImmutableArray<RevisionSelection> selections) =>
        (InternalId, ProductInternalId, Identifier, Description, Selections) = (id, product, identifier, description, selections);

    public static Result<DraftRevision> Create(Guid id, Guid product, string? identifier, string? description) =>
        Restore(id, product, identifier, description ?? "", []);

    public static Result<DraftRevision> Restore(Guid id, Guid product, string? identifier, string? description,
        IEnumerable<RevisionSelection>? selections)
    {
        if (id == Guid.Empty || product == Guid.Empty) return Result.Failure<DraftRevision>("Immutable revision and Product identities are required.");
        if (string.IsNullOrWhiteSpace(identifier)) return Result.Failure<DraftRevision>("A revision identifier is required.");
        identifier = identifier.Trim();
        if (identifier.EnumerateRunes().Count() > 100) return Result.Failure<DraftRevision>("A revision identifier must contain at most 100 Unicode characters.");
        if (description is null || selections is null) return Result.Failure<DraftRevision>("The persisted revision values are invalid.");
        var endpoints = selections.ToArray();
        if (endpoints.Any(e => e is null || e.Validate().IsFailure) ||
            endpoints.DistinctBy(e => (e.Kind, e.VersionId)).Count() != endpoints.Length)
            return Result.Failure<DraftRevision>("Revision selections must contain distinct valid exact versions.");
        return new DraftRevision(id, product, identifier, description, endpoints.OrderBy(e => e.Kind).ThenBy(e => e.VersionId).ToImmutableArray());
    }

    public Result<DraftRevision> Edit(string? identifier, string? description) =>
        Restore(InternalId, ProductInternalId, identifier, description ?? "", Selections);

    public Result<DraftRevision> ChangeSelection(string operation, RevisionSelection target, RevisionSelection? replacement = null)
    {
        if (target is null || target.Validate().IsFailure || replacement?.Validate().IsFailure == true)
            return Result.Failure<DraftRevision>("A valid exact selection is required.");
        var selections = Selections.ToList();
        switch (operation)
        {
            case "add" when replacement is null:
                if (selections.Contains(target)) return this;
                selections.Add(target);
                break;
            case "remove" when replacement is null:
                selections.Remove(target);
                break;
            case "replace" when replacement is not null:
                var index = selections.IndexOf(target);
                if (index < 0) return Result.Failure<DraftRevision>("The old exact selection is no longer present.");
                selections[index] = replacement;
                break;
            default: return Result.Failure<DraftRevision>("Invalid selection operation.");
        }
        return Restore(InternalId, ProductInternalId, Identifier, Description, selections);
    }

    public bool SameValues(DraftRevision other) => InternalId == other.InternalId && ProductInternalId == other.ProductInternalId &&
        Identifier == other.Identifier && Description == other.Description && Selections.SequenceEqual(other.Selections);
}
