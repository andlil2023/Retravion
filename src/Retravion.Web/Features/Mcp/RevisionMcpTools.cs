using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.Product;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Web.Features.Revision;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class RevisionMcpTools(RevisionOperations operations, IProductStore products, IHttpContextAccessor http)
{
    private System.Security.Claims.ClaimsPrincipal Principal => http.HttpContext!.User;

    [McpServerTool(Name = "list_revisions", ReadOnly = true, OpenWorld = false)]
    [Description("List Draft and Released Revisions for a Product manual ID, including retired Products. Returns immutable identities and exact explicit selections. Read-only; no preparation, selection or lifecycle changes.")]
    public async Task<CallToolResult> List(string productId, CancellationToken cancellationToken)
    {
        if (!await operations.CanRead(Principal)) return Error("Access denied.");
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await operations.List(Principal, product.Value.Value.InternalId, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(result.Value.Select(Values));
    }

    [McpServerTool(Name = "read_revision", ReadOnly = true, OpenWorld = false)]
    [Description("Read a Revision by Product manual ID and Revision immutable internalId. Draft members are the complete calculated outgoing closure; Released members are frozen retained evidence, including recorded area information. Explicit roots and indirect members remain distinct; multiple exact versions of one entity are preserved. Read-only; never substitutes current versions or changes a baseline.")]
    public async Task<CallToolResult> Read(string productId, Guid internalId, CancellationToken cancellationToken)
    {
        if (!await operations.CanRead(Principal)) return Error("Access denied.");
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        // One authorized baseline read provides both metadata and members from the same snapshot.
        var result = await operations.Baseline(Principal, product.Value.Value.InternalId, internalId, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(new
        {
            revision = Values(result.Value.Revision),
            evidence = result.Value.Revision.Value is ReleasedRevision ? "frozen-released" : "calculated-draft",
            members = result.Value.Members.Select(m => new
            {
                endpoint = Endpoint(m.Endpoint), m.Identifier, m.Title, m.Status, m.Explicit, m.Area
            })
        });
    }

    private static object Values(RevisionSnapshot snapshot) => new
    {
        snapshot.Value.InternalId, snapshot.Value.ProductInternalId, snapshot.Value.Identifier,
        snapshot.Value.Description, snapshot.Value.State, snapshot.Revision,
        selections = snapshot.Value.Selections.Select(Endpoint),
        releasedBy = (snapshot.Value as ReleasedRevision)?.ReleasedBy,
        releasedAt = (snapshot.Value as ReleasedRevision)?.ReleasedAt
    };

    private static object Endpoint(RevisionSelection endpoint) => new
    {
        kind = endpoint.Kind.ToString(), endpoint.InternalId, endpoint.VersionId, endpoint.VersionNumber
    };

    internal static bool ValidArguments(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var fields = arguments.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            !arguments.TryGetProperty("productId", out var product) || product.ValueKind != JsonValueKind.String) return false;
        return tool == "list_revisions" || arguments.TryGetProperty("internalId", out var id) &&
            id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var guid) && guid != Guid.Empty;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CallToolResult Success(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
