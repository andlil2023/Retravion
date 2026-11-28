using Retravion.Domain.Specification;

namespace Retravion.Application.Specification;

public sealed record SpecificationApproval(ApprovedSpecification Specification, IReadOnlyList<SpecificationLinkKind> MissingLinkTypes);
