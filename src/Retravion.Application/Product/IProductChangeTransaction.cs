using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product;

/// <summary>Runs a Product use case with a store whose successful writes are audited atomically.</summary>
public interface IProductChangeTransaction
{
    Task<Result<ProductState>> Execute(AuditActor actor, DateTimeOffset occurredAt,
        Func<IProductStore, Task<Result<ProductState>>> change, CancellationToken cancellationToken = default);
}
