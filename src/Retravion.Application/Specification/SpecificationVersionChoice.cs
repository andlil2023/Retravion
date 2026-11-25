using Retravion.Domain.Specification;

namespace Retravion.Application.Specification;

/// <summary>A selectable exact version, including the owning record identity needed for navigation.</summary>
public sealed record SpecificationVersionChoice(SpecificationLink Target, Guid InternalId, string Identifier,
    int VersionNumber, string Title, bool IsApproved);
