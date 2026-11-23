using CSharpFunctionalExtensions;
using Retravion.Domain.Specification;

namespace Retravion.Application.Specification;

public interface ISpecificationReadStore
{
    Task<Result<IReadOnlyList<DevelopmentSpecification>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<DevelopmentSpecification>>> Find(Guid productInternalId, Guid internalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<SpecificationDetail>>> FindDetail(Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<SpecificationVersionChoice>>> ListVersions(Guid productInternalId, SpecificationLinkKind kind,
        CancellationToken cancellationToken = default);
    Task<Result<Maybe<SpecificationVersionChoice>>> ResolveVersion(Guid productInternalId, SpecificationLink target,
        CancellationToken cancellationToken = default);
}
