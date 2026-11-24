using Retravion.Domain.Specification;

namespace Retravion.Web.Features.Specification;

internal static class SpecificationPresentation
{
    internal static string ListPath(string? product) => $"/specifications?product={Uri.EscapeDataString(product ?? "")}";
    internal static string CreatePath(string product) => $"/specifications/create?product={Uri.EscapeDataString(product)}";
    internal static string DetailsPath(string? product, Guid id) => $"/specifications/details?product={Uri.EscapeDataString(product ?? "")}&id={id}";
    internal static string EditPath(string product, Guid id) => $"/specifications/edit?product={Uri.EscapeDataString(product)}&id={id}";

    internal static string Status(DevelopmentSpecification specification) => specification switch
    {
        DraftSpecification => "Draft",
        InReviewSpecification => "In Review",
        ApprovedSpecification => "Approved",
        ImplementedSpecification => "Implemented",
        _ => throw new ArgumentOutOfRangeException(nameof(specification))
    };
}
