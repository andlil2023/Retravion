using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public enum SpecificationLinkKind { UserNeed = 0, Requirement = 1, TestCase = 2 }

public readonly record struct SpecificationLink(SpecificationLinkKind Kind, Guid VersionId);

public sealed class SpecificationLinks : IEquatable<SpecificationLinks>
{
    public static SpecificationLinks Empty { get; } = new([]);
    public IReadOnlyList<SpecificationLink> Targets { get; }

    private SpecificationLinks(SpecificationLink[] targets) => Targets = Array.AsReadOnly(targets);

    public static Result<SpecificationLinks> Create(IEnumerable<SpecificationLink> targets)
    {
        var ordered = targets.OrderBy(link => link.Kind).ThenBy(link => link.VersionId).ToArray();
        return ordered.Any(link => !Enum.IsDefined(link.Kind) || link.VersionId == Guid.Empty) ||
            ordered.Distinct().Count() != ordered.Length
            ? Result.Failure<SpecificationLinks>("Select distinct, exact versions for Specification links.")
            : Result.Success(ordered.Length == 0 ? Empty : new SpecificationLinks(ordered));
    }

    public bool Equals(SpecificationLinks? other) => other is not null && Targets.SequenceEqual(other.Targets);
    public override bool Equals(object? obj) => obj is SpecificationLinks other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var target in Targets) hash.Add(target);
        return hash.ToHashCode();
    }
}
