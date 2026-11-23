using CSharpFunctionalExtensions;
using Retravion.Domain.Specification;

namespace Retravion.Application.Specification;

public interface ISpecificationReadStore
{
    Task<Result<IReadOnlyList<DraftSpecification>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<DraftSpecification>>> Find(Guid productInternalId, Guid internalId, CancellationToken cancellationToken = default);
}
