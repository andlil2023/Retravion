using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.Product;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class AreaMcpTools(IProductStore products, IProductAreaStore areas, McpCredentials credentials, IHttpContextAccessor http)
{
    [McpServerTool(Name = "list_product_areas", ReadOnly = true, OpenWorld = false)]
    [Description("List active and retired areas for a Product manual productId, including retired Products. Returns immutable area identity, Product identity, stable code, current label and status. Area administration remains web-only." + CollectionDiscovery.Description)]
    public async Task<CallToolResult> Areas(string productId, CancellationToken cancellationToken,
        string? search = null, string? status = null, int? offset = null, int? limit = null)
    {
        if (!await credentials.IsAllowed(http.HttpContext!.User, cancellationToken)) return Error("Access denied.");
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await areas.Retrieve(product.Value.Value.InternalId, token: cancellationToken);
        return result.IsFailure ? Error(result.Error) : CollectionDiscovery.Result(result.Value.Areas,
            a => a.Area.Code, a => a.Area.Label, a => a.Area.IsActive ? "Active" : "Retired", a => a.Area.InternalId,
            a => new { a.Area.InternalId, a.Area.ProductInternalId, a.Area.Code, a.Area.Label,
                status = a.Area.IsActive ? "Active" : "Retired" },
            ["Active", "Retired"], search, status, offset, limit);
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
