using CSharpFunctionalExtensions;
using Retravion.Domain.Product;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product.Create;

public sealed class CreateProduct(IProductStore store)
{
    public async Task<Result<ProductState>> Execute(string? id, string? name, ProductKind? kind, string? description,
        CancellationToken cancellationToken = default)
    {
        var product = ProductState.Create(id, name, kind, description);
        if (product.IsFailure) return product;
        var saved = await store.Create(product.Value, cancellationToken);
        return saved.IsSuccess ? product : Result.Failure<ProductState>(saved.Error);
    }
}
