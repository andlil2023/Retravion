using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.Product;
using Retravion.Domain.Revision;
using Retravion.Web.Features.Revision;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class RevisionMutationMcpTools(RevisionOperations operations, IProductStore products, IHttpContextAccessor http)
{
    internal const string CreateDescription = "Create an empty Draft Revision under an active Product. Never use in review mode. Requires explicit human approval of identifier, description and empty selections, then confirmed=true. No copy or lifecycle changes. Never automatically retry. After uncertain creation inspect authorized list/read candidates; if ambiguous, do not blindly recreate.";
    internal const string EditDescription = "Replace Draft Revision identifier and description only, preserving exact selections. Never use in review mode. Requires explicit human approval of old/new metadata, confirmed=true and positive expectedRevision from a prior read. No copy, deletion, release or entity lifecycle changes. Never automatically retry; retrieve authorized current state before a human-directed retry after uncertainty.";
    internal const string SelectionDescription = "Add, remove or replace one exact explicit Draft Revision selection. Never use in review mode. Requires explicit human approval of exact old/new endpoints, confirmed=true and positive expectedRevision from a prior read. target (old endpoint for replace) and replacement contain kind (UserNeed, Requirement, TestCase or Feature), internalId, versionId and positive versionNumber. Only replace accepts replacement; replacement is one atomic operation. Never silently advance versions or modify referenced entities/links. No copy, deletion or release. Never automatically retry; retrieve authorized current state before a human-directed retry after uncertainty.";

    public sealed record Endpoint(string Kind, Guid InternalId, Guid VersionId, int VersionNumber)
    {
        internal RevisionSelection? Selection() => Enum.TryParse<RevisionEntityKind>(Kind, out var kind) &&
            Enum.IsDefined(kind) && Kind == kind.ToString() && InternalId != Guid.Empty && VersionId != Guid.Empty && VersionNumber > 0
            ? new(kind, InternalId, VersionId, VersionNumber) : null;
    }

    [McpServerTool(Name = "create_draft_revision", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(CreateDescription)]
    public Task<CallToolResult> Create(string productId, string identifier, string description, bool confirmed, CancellationToken cancellationToken) =>
        Prepare(productId, "revision.create", confirmed, Guid.Empty, 0, identifier, description, null, null, cancellationToken);

    [McpServerTool(Name = "edit_draft_revision", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(EditDescription)]
    public Task<CallToolResult> Edit(string productId, Guid internalId, long expectedRevision, string identifier, string description,
        bool confirmed, CancellationToken cancellationToken) =>
        Prepare(productId, "revision.edit", confirmed, internalId, expectedRevision, identifier, description, null, null, cancellationToken);

    [McpServerTool(Name = "change_draft_revision_selection", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(SelectionDescription)]
    public async Task<CallToolResult> Select(string productId, Guid internalId, long expectedRevision, string operation, Endpoint target,
        bool confirmed, CancellationToken cancellationToken, Endpoint? replacement = null)
    {
        // Direct callers receive the same authorization-first denial as HTTP callers, even for invalid endpoints.
        if (!await operations.CanRead(http.HttpContext!.User))
        {
            await operations.DeniedMcpMutation(http.HttpContext.User, "revision.selection", cancellationToken);
            return Error("Access denied.");
        }
        if (operation is not ("add" or "remove" or "replace") || target?.Selection() is null ||
            (operation == "replace" ? replacement?.Selection() is null : replacement is not null))
            return Error("Supply a supported operation and exact endpoints; only replace requires a replacement.");
        return await Prepare(productId, "revision.selection." + operation, confirmed, internalId, expectedRevision,
            null, null, target.Selection(), replacement?.Selection(), cancellationToken);
    }

    private async Task<CallToolResult> Prepare(string productId, string action, bool confirmed, Guid id, long revision,
        string? identifier, string? description, RevisionSelection? target, RevisionSelection? replacement, CancellationToken token)
    {
        var result = await operations.PrepareMcp(http.HttpContext!.User, products, productId, action, confirmed, id, revision,
            identifier, description, target, replacement, token);
        return result.IsFailure ? Error(result.Error) : new() { Content = [new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new
            {
                result.Value.Value.InternalId, result.Value.Value.ProductInternalId, result.Value.Value.Identifier,
                result.Value.Value.Description, result.Value.Value.State, result.Value.Revision,
                selections = result.Value.Value.Selections.Select(s => new { kind = s.Kind.ToString(), s.InternalId, s.VersionId, s.VersionNumber })
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        }] };
    }

    internal static bool ValidArguments(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != arguments.EnumerateObject().Count()) return false;
        bool String(string key) => arguments.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String;
        if (!String("productId") || !arguments.TryGetProperty("confirmed", out var confirmation) ||
            confirmation.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        if (tool != "create_draft_revision" &&
            (!arguments.TryGetProperty("internalId", out var id) || id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var guid) || guid == Guid.Empty ||
             !arguments.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var n) || n <= 0)) return false;
        if (tool != "change_draft_revision_selection") return String("identifier") && String("description");
        bool EndpointValue(string name)
        {
            if (!arguments.TryGetProperty(name, out var endpoint) || endpoint.ValueKind != JsonValueKind.Object) return false;
            var fields = endpoint.EnumerateObject().ToArray();
            if (fields.Length != 4 || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 4 ||
                fields.Any(p => p.Name is not ("kind" or "internalId" or "versionId" or "versionNumber"))) return false;
            try { return endpoint.Deserialize<Endpoint>(new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Selection() is not null; }
            catch (JsonException) { return false; }
        }
        if (!String("operation") || !EndpointValue("target")) return false;
        var operation = arguments.GetProperty("operation").GetString();
        return operation == "replace" ? EndpointValue("replacement") : operation is "add" or "remove" &&
            (!arguments.TryGetProperty("replacement", out var replacement) || replacement.ValueKind == JsonValueKind.Null);
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock
    {
        Text = message.Contains("could not be confirmed", StringComparison.Ordinal)
            ? "The write outcome could not be confirmed. Retrieve authorized list/read records before any human-directed retry; never retry automatically. After uncertain creation without an identity, inspect candidate records; if ambiguous, do not blindly recreate."
            : message
    }] };
}
