using CSharpFunctionalExtensions;

namespace Retravion.Domain.Product;

public sealed record ProductArea
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public string Code { get; }
    public string Label { get; }
    public bool IsActive { get; }

    private ProductArea(Guid id, Guid product, string code, string label, bool active) =>
        (InternalId, ProductInternalId, Code, Label, IsActive) = (id, product, code, label, active);

    public static Result<ProductArea> Create(Guid product, string? code, string? label) =>
        Restore(Guid.NewGuid(), product, code, label, true);

    public static Result<ProductArea> Restore(Guid id, Guid product, string? code, string? label, bool active)
    {
        code = code?.Trim();
        if (code is null || code.Length is 0 or > 100 || code.Any(c => c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            return Result.Failure<ProductArea>("An area code must contain 1–100 ASCII letters, digits or hyphens.");
        code = code.ToUpperInvariant();
        if (id == Guid.Empty || product == Guid.Empty) return Result.Failure<ProductArea>("An area requires immutable area and Product identities.");
        if (string.IsNullOrWhiteSpace(label)) return Result.Failure<ProductArea>("An area label is required.");
        return new ProductArea(id, product, code, label.Trim(), active);
    }

    public Result<ProductArea> Rename(string? label) => Restore(InternalId, ProductInternalId, Code, label, IsActive);
    public ProductArea Retire() => new(InternalId, ProductInternalId, Code, Label, false);
}

/// <summary>Event-time classification; null evidence means not recorded, null Area means explicitly unclassified.</summary>
public sealed record AreaEvidence(AreaValue? Area)
{
    public static AreaEvidence Unclassified => new((AreaValue?)null);
    public static AreaEvidence From(ProductArea area) => new(new AreaValue(area.InternalId, area.Code, area.Label));
    public string Display => Area is { } area ? $"{area.Code} — {area.Label}" : "Unclassified";
    public bool IsValid => Area is null || Area.InternalId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(Area.Code) && !string.IsNullOrWhiteSpace(Area.Label);
}

public sealed record AreaValue(Guid InternalId, string Code, string Label);
