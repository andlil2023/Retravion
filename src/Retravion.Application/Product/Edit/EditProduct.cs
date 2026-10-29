using CSharpFunctionalExtensions;
using Retravion.Domain.Product;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product.Edit;

public sealed class EditProduct(IProductStore store)
{
    public async Task<Result<ProductState>> Execute(ProductState expected, string? id, string? name, ProductKind? kind,
        string? description, bool reactivate, CancellationToken cancellationToken = default)
    {
        var changed = expected.Edit(id, name, kind, description, reactivate);
        if (changed.IsFailure) return changed;
        var saved = await store.Update(expected, changed.Value, cancellationToken);
        return saved.IsSuccess ? changed : Result.Failure<ProductState>(saved.Error);
    }
}
