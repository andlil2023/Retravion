using CSharpFunctionalExtensions;
using Retravion.Application.Product.Retrieve;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product.Status;

public sealed class ChangeProductStatus(IProductStore store)
{
    public async Task<Result<ProductState>> Execute(string id, Guid expectedInternalId, bool active, CancellationToken cancellationToken = default)
    {
        var current = await new RetrieveProduct(store).Execute(id, cancellationToken);
        if (current.IsFailure) return current;
        if (current.Value.InternalId != expectedInternalId)
            return Result.Failure<ProductState>("The Product changed or no longer exists. Retrieve it again.");
        var changed = active ? current.Value.Reactivate() : current.Value.Retire();
        if (changed.IsFailure) return changed;
        var saved = await store.Update(current.Value, changed.Value, cancellationToken);
        return saved.IsSuccess ? changed : Result.Failure<ProductState>(saved.Error);
    }
}
