using CSharpFunctionalExtensions;
using Retravion.Application.Product.Retrieve;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product.Description;

public sealed class UpdateProductDescription(IProductStore store)
{
    public async Task<Result<ProductState>> Execute(string id, Guid expectedInternalId, string expectedDescription, string? description,
        CancellationToken cancellationToken = default)
    {
        var current = await new RetrieveProduct(store).Execute(id, cancellationToken);
        if (current.IsFailure) return current;
        if (current.Value.InternalId != expectedInternalId)
            return Result.Failure<ProductState>("The Product changed or no longer exists. Retrieve it again.");
        if (current.Value.Description != expectedDescription)
            return Result.Failure<ProductState>("The description changed. Retrieve the Product before editing again.");
        var changed = current.Value.Edit(current.Value.Id, current.Value.Name, current.Value.Kind, description);
        if (changed.IsFailure) return changed;
        var saved = await store.Update(current.Value, changed.Value, cancellationToken);
        return saved.IsSuccess ? changed : Result.Failure<ProductState>(saved.Error);
    }
}
