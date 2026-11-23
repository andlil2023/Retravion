using System.Security.Claims;
using CSharpFunctionalExtensions;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Web.Features.Product;

namespace Retravion.Web.Features.Specification;

internal sealed class SpecificationOperations(ProductOperations products, ISpecificationReadStore store)
{
    public async Task<Result<IReadOnlyList<DraftSpecification>>> List(ClaimsPrincipal actor, Guid productInternalId,
        CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.List(productInternalId, cancellationToken)
        : Result.Failure<IReadOnlyList<DraftSpecification>>("Access denied.");

    public async Task<Result<Maybe<DraftSpecification>>> Find(ClaimsPrincipal actor, Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.Find(productInternalId, internalId, cancellationToken)
        : Result.Failure<Maybe<DraftSpecification>>("Access denied.");
}
