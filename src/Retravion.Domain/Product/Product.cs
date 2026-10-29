using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Product;

public sealed record Product
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool IsActive { get; }

    private Product(string id, string name, string description, bool isActive) =>
        (Id, Name, Description, IsActive) = (id, name, description, isActive);

    public static Result<Product> Create(string? id, string? name, string? description) =>
        Restore(id, name, description ?? "", true);

    public static Result<Product> Restore(string? id, string? name, string? description, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(id)) return Result.Failure<Product>("A Product ID is required.");
        if (id.EnumerateRunes().Count() > 100)
            return Result.Failure<Product>("A Product ID must contain at most 100 Unicode characters.");
        if (string.IsNullOrWhiteSpace(name)) return Result.Failure<Product>("A Product name is required.");
        if (description is null) return Result.Failure<Product>("The persisted Product description is invalid.");
        return new Product(id, name, description, isActive);
    }

    public Result<Product> Retire() => IsActive
        ? new Product(Id, Name, Description, false)
        : Result.Failure<Product>("The Product is already retired.");

    public Result<Product> Reactivate() => !IsActive
        ? new Product(Id, Name, Description, true)
        : Result.Failure<Product>("The Product is already active.");
}
