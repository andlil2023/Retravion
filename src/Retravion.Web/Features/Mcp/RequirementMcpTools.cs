using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Web.Features.Requirement;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class RequirementMcpTools(RequirementOperations operations, IProductStore products, IHttpContextAccessor http)
{
    private System.Security.Claims.ClaimsPrincipal Principal => http.HttpContext!.User;

    [McpServerTool(Name = "list_requirements", ReadOnly = true, OpenWorld = false)]
    [Description("List Requirements in all statuses for a Product manual ID, including retired Products. Returns immutable entity/version identities, working/latest-Approved designations and concurrency revision tokens.")]
    public async Task<CallToolResult> List(string productId, CancellationToken cancellationToken)
    {
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await operations.List(Principal, product.Value.Value.InternalId, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(result.Value.Select(Values));
    }

    [McpServerTool(Name = "read_requirement", ReadOnly = true, OpenWorld = false)]
    [Description("Read Requirement content, versions and retained decision history by immutable internalId. Optional versionId selects exactly that version, never latest substitution. Otherwise returns working version or latest Approved. Retrieve before retrying uncertain writes.")]
    public async Task<CallToolResult> Read(Guid internalId, CancellationToken cancellationToken, Guid? versionId = null)
    {
        var result = await operations.Find(Principal, internalId, cancellationToken, versionId);
        if (result.IsFailure) return Error(result.Error);
        if (result.Value.HasNoValue) return Error("Requirement not found.");
        var history = await operations.History(Principal, internalId, cancellationToken);
        if (history.IsFailure) return Error(history.Error);
        var versions = await operations.Versions(Principal, internalId, cancellationToken);
        return versions.IsFailure ? Error(versions.Error) : Success(new
        {
            requirement = Values(result.Value.Value),
            versions = versions.Value.Select(Values),
            history = history.Value.Select(h => new { h.Decision, h.OccurredAt,
                actor = h.Actor is AuditActor.Identified actor ? actor.Identifier : null,
                h.Comment, h.Id, h.Title, h.Statement, type = h.Type.ToString(), h.Rationale,
                h.VersionId, h.VersionNumber, h.SourceVersionId })
        });
    }

    [McpServerTool(Name = "create_draft_requirement", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a Draft Requirement under an active Product manual ID. Requires a Product-unique manual ID (at most 100 Unicode characters), title, Markdown statement and explicit type: System, Software, Hardware, Safety, Usability or Regulatory. Rationale is optional plain text. Never automatically retry; retrieve before manually retrying uncertain outcomes.")]
    public async Task<CallToolResult> Create(string productId, string id, string title, string statement, string type,
        CancellationToken cancellationToken, string? rationale = null)
    {
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await operations.Create(Principal, product.Value.Value.InternalId, id, title, statement, ParseType(type), rationale, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    [McpServerTool(Name = "edit_draft_requirement", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Replace Draft Requirement content only. Supply immutable internalId and expectedRevision from a prior read, manual ID, title, Markdown statement and explicit type: System, Software, Hardware, Safety, Usability or Regulatory. Omitted/null rationale clears it. Rejects stale writes, non-Draft states and retired Products. No status/review changes. Never automatically retry; retrieve before manually retrying uncertain outcomes.")]
    public async Task<CallToolResult> Edit(Guid internalId, long expectedRevision, string id, string title, string statement, string type,
        CancellationToken cancellationToken, string? rationale = null)
    {
        var result = await operations.EditDraft(Principal, internalId, expectedRevision, id, title, statement, ParseType(type), rationale, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    private static RequirementType? ParseType(string type) => Enum.GetValues<RequirementType>()
        .Where(value => value.ToString().Equals(type, StringComparison.OrdinalIgnoreCase))
        .Select(value => (RequirementType?)value).FirstOrDefault();
    [McpServerTool(Name = "edit_draft_requirement_classification", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Change only the exact eligible Draft's classification after explicit human approval of old/new values. Supply internalId, versionId, positive expectedRevision, areaId (active same-Product area identity, or explicit null to clear), and confirmed=true. Preserves content and links. Never automatically retry; after uncertainty retrieve authorized current state before a human-directed retry.")]
    public async Task<CallToolResult> Classify(Guid internalId, Guid versionId, long expectedRevision, Guid? areaId,
        bool confirmed, CancellationToken cancellationToken)
    {
        var result = await operations.ClassifyMcp(Principal, internalId, versionId, expectedRevision, areaId, confirmed, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    private static object Values(RequirementSnapshot snapshot) => new
    {
        snapshot.Requirement.InternalId, snapshot.Requirement.ProductInternalId, snapshot.Requirement.Id, snapshot.Requirement.Title,
        snapshot.Requirement.Statement, type = snapshot.Requirement.Type.ToString(), snapshot.Requirement.Rationale,
        status = snapshot.Requirement.Status.ToString(), snapshot.Revision,
        snapshot.Requirement.VersionId, snapshot.Requirement.VersionNumber, snapshot.Requirement.CreationReason,
        snapshot.Requirement.SourceVersionId, snapshot.LatestApprovedVersionNumber, snapshot.Requirement.AreaId
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CallToolResult Success(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
