using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.Product;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class ProductMcpTools(IProductStore products, McpCredentials credentials, IHttpContextAccessor http)
{
    [McpServerTool(Name = "list_products", ReadOnly = true, OpenWorld = false)]
    [Description("List Products, including retired Products. Product information is read-only through MCP." + CollectionDiscovery.Description)]
    public async Task<CallToolResult> List(CancellationToken cancellationToken,
        string? search = null, string? status = null, int? offset = null, int? limit = null)
    {
        if (!await credentials.IsAllowed(http.HttpContext!.User, cancellationToken)) return Error("Access denied.");
        var result = await products.List(cancellationToken);
        return result.IsFailure ? Error(result.Error) : CollectionDiscovery.Result(result.Value,
            p => p.Id, p => p.Name, p => p.IsActive ? "Active" : "Retired", p => p.InternalId, Values,
            ["Active", "Retired"], search, status, offset, limit);
    }

    [McpServerTool(Name = "read_product", ReadOnly = true, OpenWorld = false)]
    [Description("Retrieve a Product by its manual productId, including its full Markdown description and active/retired status. Cannot modify Products.")]
    public async Task<CallToolResult> Read(string productId, CancellationToken cancellationToken)
    {
        if (!await credentials.IsAllowed(http.HttpContext!.User, cancellationToken)) return Error("Access denied.");
        var result = await products.Find(productId, cancellationToken);
        return result.IsFailure ? Error(result.Error) : result.Value.HasNoValue ? Error("Product not found.") : Success(Values(result.Value.Value));
    }

    private static object Values(ProductState product) => new
    { product.Id, product.Name, kind = product.Kind.ToString(), product.Description, status = product.IsActive ? "Active" : "Retired" };
    private static CallToolResult Success(object value) => new()
    { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }] };
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
