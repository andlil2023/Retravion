using CSharpFunctionalExtensions;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product;

public interface IProductStore
{
    Task<Result> Create(ProductState product, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<ProductState>>> List(CancellationToken cancellationToken = default);
    Task<Result<Maybe<ProductState>>> Find(string id, CancellationToken cancellationToken = default);
    Task<Result> Update(ProductState expected, ProductState replacement, CancellationToken cancellationToken = default);
}
