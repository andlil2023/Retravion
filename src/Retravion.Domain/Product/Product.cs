using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Product;

public enum ProductKind { System, SoftwareApplication, HardwareDevice, Firmware }

// The concrete lifecycle types determine which transitions are permitted.
public abstract record Product
{
    public Guid InternalId { get; }
    public string Id { get; }
    public string Name { get; }
    public ProductKind Kind { get; }
    public string Description { get; }
    public abstract bool IsActive { get; }

    private protected Product(Guid internalId, string id, string name, ProductKind kind, string description) =>
        (InternalId, Id, Name, Kind, Description) = (internalId, id, name, kind, description);

    public static Result<Product> Create(string? id, string? name, ProductKind? kind, string? description) =>
        Construct(Guid.NewGuid(), id, name, kind, description ?? "", true);

    public static Result<Product> Restore(Guid internalId, string? id, string? name, ProductKind? kind, string? description, bool isActive) =>
        Construct(internalId, id, name, kind, description, isActive);

    private static Result<Product> Construct(Guid internalId, string? id, string? name, ProductKind? kind, string? description, bool active)
    {
        if (internalId == Guid.Empty) return Result.Failure<Product>("The internal Product identity is invalid.");
        if (string.IsNullOrWhiteSpace(id)) return Result.Failure<Product>("A Product ID is required.");
        id = id.Trim();
        if (id.EnumerateRunes().Count() > 100)
            return Result.Failure<Product>("A Product ID must contain at most 100 Unicode characters.");
        if (string.IsNullOrWhiteSpace(name)) return Result.Failure<Product>("A Product name is required.");
        if (kind is null || !Enum.IsDefined(kind.Value)) return Result.Failure<Product>("A Product kind is required.");
        if (description is null) return Result.Failure<Product>("The persisted Product description is invalid.");
        return active ? new ActiveProduct(internalId, id, name.Trim(), kind.Value, description)
            : new RetiredProduct(internalId, id, name.Trim(), kind.Value, description);
    }

    public abstract Result<Product> Edit(string? id, string? name, ProductKind? kind, string? description, bool reactivate = false);
    public abstract Result<Product> Retire();
    public abstract Result<Product> Reactivate();

    private sealed record ActiveProduct : Product
    {
        public ActiveProduct(Guid identity, string id, string name, ProductKind kind, string description)
            : base(identity, id, name, kind, description) { }
        public override bool IsActive => true;
        public override Result<Product> Edit(string? id, string? name, ProductKind? kind, string? description, bool reactivate = false) =>
            reactivate ? Result.Failure<Product>("The Product is already active.") : Construct(InternalId, id, name, kind, description ?? "", true);
        public override Result<Product> Retire() => Construct(InternalId, Id, Name, Kind, Description, false);
        public override Result<Product> Reactivate() => Result.Failure<Product>("The Product is already active.");
    }

    private sealed record RetiredProduct : Product
    {
        public RetiredProduct(Guid identity, string id, string name, ProductKind kind, string description)
            : base(identity, id, name, kind, description) { }
        public override bool IsActive => false;
        public override Result<Product> Edit(string? id, string? name, ProductKind? kind, string? description, bool reactivate = false) =>
            reactivate ? Construct(InternalId, id, name, kind, description ?? "", true)
                : Result.Failure<Product>("Reactivate the Product before editing its details.");
        public override Result<Product> Retire() => Result.Failure<Product>("The Product is already retired.");
        public override Result<Product> Reactivate() => Construct(InternalId, Id, Name, Kind, Description, true);
    }
}
