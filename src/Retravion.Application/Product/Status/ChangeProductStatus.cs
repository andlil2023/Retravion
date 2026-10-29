using CSharpFunctionalExtensions;
using Retravion.Application.Product.Retrieve;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product.Status;

public sealed class ChangeProductStatus(IProductStore store)
{
    public async Task<Result<ProductState>> Execute(string id, bool active, CancellationToken cancellationToken = default)
    {
        var current = await new RetrieveProduct(store).Execute(id, cancellationToken);
        if (current.IsFailure) return current;
        var changed = active ? current.Value.Reactivate() : current.Value.Retire();
        if (changed.IsFailure) return changed;
        var saved = await store.ChangeStatus(current.Value, changed.Value, cancellationToken);
        return saved.IsSuccess ? changed : Result.Failure<ProductState>(saved.Error);
    }
}
