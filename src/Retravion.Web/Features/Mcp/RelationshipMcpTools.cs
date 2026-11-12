using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Web.Features.Relationships;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class RelationshipMcpTools(RelationshipOperations operations, IHttpContextAccessor http)
{
    internal const string ToolDescription = "Read incoming/outgoing relationships from an exact User Need, Requirement, Test Case or Feature version. kind: user-need, requirement, test-case or feature. Only explicitly requested reachable expandedVersionIds are expanded (at most 20); repeated versions load once. limit defaults to 100, range 1-200, independently caps edges and unavailable provenance snapshots. Truncation is disclosed; results are not a complete transitive closure. No mutations.";

    [McpServerTool(Name = "explore_relationships", ReadOnly = true, OpenWorld = false)]
    [Description(ToolDescription)]
    public async Task<CallToolResult> Explore(string kind, Guid internalId, Guid versionId,
        CancellationToken cancellationToken, Guid[]? expandedVersionIds = null, int limit = 100)
    {
        if (!ValidSelection(kind, internalId, versionId, expandedVersionIds, limit))
            return Error("Select an exact supported entity version, at most 20 expansion IDs, and a limit from 1 to 200.");
        var requested = (expandedVersionIds ?? []).Distinct().ToArray();
        var result = await operations.Explore(http.HttpContext!.User, kind, internalId, versionId, requested, cancellationToken);
        if (result.IsFailure) return Error(result.Error);
        var view = result.Value;
        var edges = view.Edges.OrderBy(e => e.Owner.VersionId).ThenBy(e => e.Target.VersionId)
            .ThenBy(e => e.Relationship, StringComparer.Ordinal).ToArray();
        var origins = view.UnavailableOrigins.OrderBy(o => o.Copy.VersionId).ThenBy(o => o.SourceVersionId).ToArray();
        object Node(RelationshipNode n) => new
        {
            n.Kind, n.InternalId, n.VersionId, n.ProductInternalId, n.Id, n.Title,
            n.VersionNumber, n.Status, n.HasNewerVersion
        };
        var shown = edges.Take(limit).ToArray();
        var productIds = shown.SelectMany(e => new[] { e.Owner.ProductInternalId, e.Target.ProductInternalId })
            .Append(view.Root.Center.ProductInternalId).ToHashSet();
        return Success(new
        {
            center = Node(view.Root.Center),
            scope = "Direct relationships of the root and explicitly expanded reachable exact versions only; not a complete transitive closure.",
            expandedVersionIds = view.Expanded,
            unexpandedVersionIds = requested.Except(view.Expanded).Where(v => v != versionId).ToArray(),
            limit, truncated = edges.Length > limit || origins.Length > limit,
            omittedEdges = Math.Max(0, edges.Length - limit), omittedUnavailableOrigins = Math.Max(0, origins.Length - limit),
            products = view.Products.Where(p => productIds.Contains(p.InternalId)).Select(p => new { p.InternalId, p.Id, p.Name, p.IsActive }),
            edges = shown.Select(e => new
            {
                owner = Node(e.Owner), target = Node(e.Target), e.Relationship, e.IsProvenance,
                direction = e.IsProvenance ? "copy-to-source" : "owner-to-target"
            }),
            unavailableOrigins = origins.Take(limit).Select(o => new
            {
                copy = Node(o.Copy), available = false, readOnly = true,
                context = "Stored copy-time snapshot only; live source unavailable. Not an expandable endpoint.",
                o.SourceProductId, o.SourceId, o.SourceVersionId, o.SourceVersionNumber, o.SourceStatus,
                o.SourceManualId, o.SourceTitle, o.SourceStatement, o.SourceType, o.SourceRationale
            })
        });
    }

    private static bool ValidSelection(string kind, Guid id, Guid version, Guid[]? expanded, int limit) =>
        kind is "user-need" or "requirement" or "test-case" or "feature" && id != Guid.Empty && version != Guid.Empty &&
        limit is >= 1 and <= 200 && (expanded is null || expanded.Length <= 20 && expanded.All(v => v != Guid.Empty));

    internal static bool ValidArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var fields = arguments.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) return false;
        if (!arguments.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
            !arguments.TryGetProperty("internalId", out var id) || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var entityId) ||
            !arguments.TryGetProperty("versionId", out var version) || version.ValueKind != JsonValueKind.String || !version.TryGetGuid(out var versionId)) return false;
        var limit = 100;
        if (arguments.TryGetProperty("limit", out var size) && (size.ValueKind != JsonValueKind.Number || !size.TryGetInt32(out limit))) return false;
        Guid[]? expanded = null;
        if (arguments.TryGetProperty("expandedVersionIds", out var values) && values.ValueKind != JsonValueKind.Null)
        {
            if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 20) return false;
            var ids = new List<Guid>();
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var parsed)) return false;
                ids.Add(parsed);
            }
            expanded = ids.ToArray();
        }
        return ValidSelection(kind.GetString()!, entityId, versionId, expanded, limit);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CallToolResult Success(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
