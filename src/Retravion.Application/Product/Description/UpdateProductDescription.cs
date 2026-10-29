using CSharpFunctionalExtensions;
using Retravion.Application.Product.Retrieve;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product.Description;

public sealed class UpdateProductDescription(IProductStore store)
{
    public async Task<Result<ProductState>> Execute(string id, string expectedDescription, string? description,
        CancellationToken cancellationToken = default)
    {
        var current = await new RetrieveProduct(store).Execute(id, cancellationToken);
        if (current.IsFailure) return current;
        if (current.Value.Description != expectedDescription)
            return Result.Failure<ProductState>("The description changed. Retrieve the Product before editing again.");
        var changed = current.Value.UpdateDescription(description);
        var saved = await store.UpdateDescription(current.Value, changed, cancellationToken);
        return saved.IsSuccess ? changed : Result.Failure<ProductState>(saved.Error);
    }
}
