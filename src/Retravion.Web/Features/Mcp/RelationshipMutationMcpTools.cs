using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Web.Features.Relationships;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class RelationshipMutationMcpTools(RelationshipOperations operations, IHttpContextAccessor http)
{
    internal const string Description = "Change one ordinary link owned by the current editable Draft. Never use in review mode. Requires explicit human approval of relationship and exact owner/old/new endpoints, then confirmed=true. ownerKind: requirement, test-case or feature. relationship: user-need, parent, verifies or addresses (respectively Requirement–User Need, child–parent Requirement, Test Case–Requirement, Feature–Requirement). operation: add, remove or retarget. Supply ownerId, ownerVersionId and positive expectedRevision from a prior read, plus targetId and targetVersionId (old endpoint for retarget). Retarget requires replacementVersionId of the same target entity; omit otherwise. No creation, provenance or lifecycle changes. Never automatically retry; after any uncertain outcome retrieve authorized current state before a human-directed retry.";

    [McpServerTool(Name = "change_draft_link", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(Description)]
    public async Task<CallToolResult> Change(string ownerKind, Guid ownerId, Guid ownerVersionId, long expectedRevision,
        string relationship, string operation, Guid targetId, Guid targetVersionId, bool confirmed,
        CancellationToken cancellationToken, Guid? replacementVersionId = null)
    {
        if (!Valid(ownerKind, ownerId, ownerVersionId, expectedRevision, relationship, operation, targetId, targetVersionId, replacementVersionId))
            return Error("Select a supported ordinary link, exact endpoints and positive owner revision; only retarget accepts a replacement version.");
        if (!confirmed) return Error("Explicit human confirmation of the relationship and exact owner/old/new endpoints is required.");
        var label = relationship switch
        {
            "user-need" => RelationshipOperations.NeedRelationship,
            "parent" => RelationshipOperations.ParentRelationship,
            "verifies" => RelationshipOperations.TestCaseRelationship,
            _ => RelationshipOperations.FeatureRelationship
        };
        var result = await operations.ChangeExact(http.HttpContext!.User, ownerKind, ownerId, ownerVersionId,
            expectedRevision, label, operation, targetId, targetVersionId, replacementVersionId, cancellationToken);
        if (result.IsFailure) return Error(result.Error);
        return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new
        {
            outcome = "Confirmed transaction completed; a current no-op creates no change event.",
            ownerKind, ownerId, ownerVersionId, relationship, operation, targetId, targetVersionId, replacementVersionId,
            nextStep = "Read the exact owner again before another action; never reuse the old revision."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }] };
    }

    private static bool Valid(string kind, Guid owner, Guid version, long revision, string relationship, string operation,
        Guid target, Guid targetVersion, Guid? replacement) =>
        (kind, relationship) is ("requirement", "user-need" or "parent") or ("test-case", "verifies") or ("feature", "addresses") &&
        owner != Guid.Empty && version != Guid.Empty && revision > 0 && target != Guid.Empty && targetVersion != Guid.Empty &&
        operation is "add" or "remove" or "retarget" &&
        (operation == "retarget" ? replacement is { } id && id != Guid.Empty : replacement is null);

    internal static bool ValidArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var fields = arguments.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) return false;
        string? String(string name) => arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        Guid Id(string name) => arguments.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetGuid(out var id) ? id : Guid.Empty;
        if (!arguments.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) ||
            !arguments.TryGetProperty("confirmed", out var confirmed) || confirmed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        Guid? replacement = null;
        if (arguments.TryGetProperty("replacementVersionId", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id)) return false;
            replacement = id;
        }
        return Valid(String("ownerKind")!, Id("ownerId"), Id("ownerVersionId"), number, String("relationship")!,
            String("operation")!, Id("targetId"), Id("targetVersionId"), replacement);
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
