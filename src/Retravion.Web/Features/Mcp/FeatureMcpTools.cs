using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Web.Features.Feature;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class FeatureMcpTools(FeatureOperations operations, IProductStore products, IHttpContextAccessor http)
{
    private System.Security.Claims.ClaimsPrincipal Principal => http.HttpContext!.User;

    [McpServerTool(Name = "list_features", ReadOnly = true, OpenWorld = false)]
    [Description("List all Features for a Product manual ID, including retired Products. Selects working version otherwise latest Approved; ordered by ID and immutable identity." + CollectionDiscovery.Description)]
    public async Task<CallToolResult> List(string productId, CancellationToken cancellationToken,
        string? search = null, string? status = null, int? offset = null, int? limit = null)
    {
        if (!await operations.CanRead(Principal)) return Error("Access denied.");
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await operations.List(Principal, product.Value.Value.InternalId, cancellationToken);
        return result.IsFailure ? Error(result.Error) : CollectionDiscovery.Result(result.Value,
            s => s.Feature.Id, s => s.Feature.Content.Title, s => s.Feature.Status.ToString(), s => s.Feature.InternalId, Values,
            ["Draft", "InReview", "Approved"], search, status, offset, limit);
    }

    [McpServerTool(Name = "read_feature", ReadOnly = true, OpenWorld = false)]
    [Description("Read a Feature, all version summaries and retained entity history. Optional versionId selects exactly that version; otherwise working or latest Approved. Retrieve before retrying uncertain writes.")]
    public async Task<CallToolResult> Read(Guid internalId, CancellationToken cancellationToken, Guid? versionId = null)
    {
        var result = await operations.Find(Principal, internalId, versionId, cancellationToken);
        if (result.IsFailure) return Error(result.Error);
        if (result.Value.HasNoValue) return Error("Feature not found.");
        var versions = await operations.Versions(Principal, internalId, cancellationToken);
        if (versions.IsFailure) return Error(versions.Error);
        var history = await operations.History(Principal, internalId, cancellationToken);
        return history.IsFailure ? Error(history.Error) : Success(new
        {
            feature = Values(result.Value.Value),
            versions = versions.Value.OrderBy(v => v.Feature.VersionNumber).Select(v => new
            {
                v.Feature.InternalId, v.Feature.VersionId, v.Feature.VersionNumber,
                status = v.Feature.Status.ToString(), v.Revision, v.Feature.CreationReason, v.Feature.SourceVersionId
            }),
            history = history.Value.Select(h => new
            {
                actor = h.Actor is AuditActor.Identified actor ? actor.Identifier : null,
                h.OccurredAt, h.Action,
                change = new
                {
                    h.Change.InternalId, h.Change.VersionId, h.Change.VersionNumber,
                    before = AuditValues(h.Change.Before), after = AuditValues(h.Change.After),
                    h.Change.Decision, h.Change.Comment, h.Change.SourceVersionId, h.Change.Reason
                }
            })
        });
    }

    [McpServerTool(Name = "create_draft_feature", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create an incomplete Draft v1 with no links. Optional null title/description default to empty. Requires explicit human approval of initial values. Never automatically retry; list/read candidates after uncertain creation, and do not blindly recreate an ambiguous result.")]
    public async Task<CallToolResult> Create(string productId, CancellationToken cancellationToken,
        string? title = null, string? description = null)
    {
        var result = await operations.CreateMcp(Principal, products, productId, title, description, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    [McpServerTool(Name = "edit_draft_feature", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Fully replace exact Draft content using its positive expectedRevision. Requires explicit human approval of old/new content. Preserves generated identities and links. No lifecycle/link changes. Never automatically retry; retrieve before user-directed retry after uncertain outcomes.")]
    public async Task<CallToolResult> Edit(Guid internalId, Guid versionId, long expectedRevision, string title,
        string description, CancellationToken cancellationToken)
    {
        var result = await operations.EditMcp(Principal, internalId, versionId, expectedRevision, title, description, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    private static object Values(FeatureSnapshot snapshot) => new
    {
        snapshot.Feature.InternalId, snapshot.Feature.ProductInternalId, snapshot.Feature.Id,
        snapshot.Feature.VersionId, snapshot.Feature.VersionNumber, status = snapshot.Feature.Status.ToString(),
        snapshot.Feature.Content.Title, snapshot.Feature.Content.Description,
        links = Links(snapshot.Feature.Links), snapshot.Revision,
        snapshot.Feature.CreationReason, snapshot.Feature.SourceVersionId, snapshot.LatestApprovedVersionNumber
    };
    private static object? AuditValues(FeatureAuditValues? value) => value is null ? null : new
    {
        value.ProductInternalId, value.Id, value.Title, value.Description, value.Status,
        links = value.Links is null ? null : Links(value.Links)
    };
    private static object Links(IEnumerable<FeatureRequirementLink> links) => links.Select(l => new { l.RequirementInternalId, l.RequirementVersionId });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CallToolResult Success(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message.Contains("could not be confirmed", StringComparison.Ordinal)
            ? "The write outcome could not be confirmed. Retrieve authorized list/read records before any user-directed retry; do not retry automatically. For creation without an identity, inspect candidate records; if ambiguous, do not blindly recreate." : message }]
    };
}
