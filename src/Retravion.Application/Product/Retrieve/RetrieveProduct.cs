using CSharpFunctionalExtensions;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Application.Product.Retrieve;

public sealed class RetrieveProduct(IProductStore store)
{
    public async Task<Result<ProductState>> Execute(string? id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Result.Failure<ProductState>("A Product ID is required.");
        var found = await store.Find(id, cancellationToken);
        if (found.IsFailure) return Result.Failure<ProductState>(found.Error);
        return found.Value.HasValue ? found.Value.Value : Result.Failure<ProductState>("Product not found.");
    }
}
