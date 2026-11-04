using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.UserNeed;
using Retravion.Web.Features.UserNeed;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class UserNeedMcpTools(UserNeedOperations operations, IProductStore products, IHttpContextAccessor http)
{
    private System.Security.Claims.ClaimsPrincipal Principal => http.HttpContext!.User;

    [McpServerTool(Name = "list_user_needs", ReadOnly = true, OpenWorld = false)]
    [Description("List User Needs in all statuses for a Product's manual ID, including retired Products. Returns immutable identities and revision tokens.")]
    public async Task<CallToolResult> List(string productId, CancellationToken cancellationToken)
    {
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await operations.List(Principal, product.Value.Value.InternalId, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(result.Value.Select(Values));
    }

    [McpServerTool(Name = "read_user_need", ReadOnly = true, OpenWorld = false)]
    [Description("Read User Need content, versions and decision history by immutable internalId. Optional versionId selects exactly that version, never latest substitution. Otherwise returns working version or latest Approved. Retrieve before retrying uncertain writes.")]
    public async Task<CallToolResult> Read(Guid internalId, CancellationToken cancellationToken, Guid? versionId = null)
    {
        var result = await operations.Find(Principal, internalId, cancellationToken, versionId);
        if (result.IsFailure) return Error(result.Error);
        if (result.Value.HasNoValue) return Error("User Need not found.");
        var history = await operations.History(Principal, internalId, cancellationToken);
        if (history.IsFailure) return Error(history.Error);
        var versions = await operations.Versions(Principal, internalId, cancellationToken);
        return versions.IsFailure ? Error(versions.Error) : Success(new
        {
            need = Values(result.Value.Value),
            versions = versions.Value.Select(Values),
            history = history.Value.Select(h => new { h.Decision, h.OccurredAt, actor = h.Actor is AuditActor.Identified actor ? actor.Identifier : null,
                h.Comment, h.Title, h.Statement, h.VersionId, h.VersionNumber, h.ManualId, h.SourceVersionId })
        });
    }

    [McpServerTool(Name = "create_draft_user_need", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a Draft User Need under an active Product's manual ID. Required: Product-scoped ID, title and Markdown statement. Never automatically retry a write; after an uncertain result, list/read before manually retrying.")]
    public async Task<CallToolResult> Create(string productId, string id, string title, string statement, CancellationToken cancellationToken)
    {
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await operations.Create(Principal, product.Value.Value.InternalId, id, title, statement, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    [McpServerTool(Name = "edit_draft_user_need", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Edit Draft content only. Supply immutable internalId and expectedRevision from a prior read. Stale writes, non-Draft content and retired Products are rejected. No status/review/version changes. Never automatically retry; retrieve before manually retrying uncertain outcomes.")]
    public async Task<CallToolResult> Edit(Guid internalId, long expectedRevision, string id, string title, string statement,
        CancellationToken cancellationToken)
    {
        var result = await operations.EditDraft(Principal, internalId, expectedRevision, id, title, statement, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    private static object Values(UserNeedSnapshot snapshot) => new
    {
        snapshot.Need.InternalId, snapshot.Need.ProductInternalId, snapshot.Need.Id, snapshot.Need.Title,
        snapshot.Need.Statement, status = snapshot.Need.Status.ToString(), snapshot.Revision,
        snapshot.Need.VersionId, snapshot.Need.VersionNumber, snapshot.Need.CreationReason, snapshot.Need.SourceVersionId,
        snapshot.LatestApprovedVersionNumber
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CallToolResult Success(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
