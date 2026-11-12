using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.TestCase;
using Retravion.Domain.TestCase;
using Retravion.Web.Features.TestCase;

namespace Retravion.Web.Features.Mcp;

[McpServerToolType]
internal sealed class TestCaseMcpTools(TestCaseOperations operations, IProductStore products, IHttpContextAccessor http)
{
    private System.Security.Claims.ClaimsPrincipal Principal => http.HttpContext!.User;

    [McpServerTool(Name = "list_test_cases", ReadOnly = true, OpenWorld = false)]
    [Description("List all Test Cases for a Product manual ID, including retired Products. Selects working version otherwise latest Approved; ordered by ID and immutable identity." + CollectionDiscovery.Description)]
    public async Task<CallToolResult> List(string productId, CancellationToken cancellationToken,
        string? search = null, string? status = null, int? offset = null, int? limit = null)
    {
        if (!await operations.CanRead(Principal)) return Error("Access denied.");
        var product = await products.Find(productId, cancellationToken);
        if (product.IsFailure) return Error(product.Error);
        if (product.Value.HasNoValue) return Error("Product not found.");
        var result = await operations.List(Principal, product.Value.Value.InternalId, cancellationToken);
        return result.IsFailure ? Error(result.Error) : CollectionDiscovery.Result(result.Value,
            s => s.TestCase.Id, s => s.TestCase.Content.Title, s => s.TestCase.Status.ToString(), s => s.TestCase.InternalId, Values,
            ["Draft", "InReview", "Approved"], search, status, offset, limit);
    }

    [McpServerTool(Name = "read_test_case", ReadOnly = true, OpenWorld = false)]
    [Description("Read a Test Case, all version summaries and retained entity history. Optional versionId selects exactly that version; otherwise working or latest Approved. Retrieve before retrying uncertain writes.")]
    public async Task<CallToolResult> Read(Guid internalId, CancellationToken cancellationToken, Guid? versionId = null)
    {
        var result = await operations.Find(Principal, internalId, versionId, cancellationToken);
        if (result.IsFailure) return Error(result.Error);
        if (result.Value.HasNoValue) return Error("Test Case not found.");
        var versions = await operations.Versions(Principal, internalId, cancellationToken);
        if (versions.IsFailure) return Error(versions.Error);
        var history = await operations.History(Principal, internalId, cancellationToken);
        return history.IsFailure ? Error(history.Error) : Success(new
        {
            testCase = Values(result.Value.Value),
            versions = versions.Value.OrderBy(v => v.TestCase.VersionNumber).Select(v => new
            {
                v.TestCase.InternalId, v.TestCase.VersionId, v.TestCase.VersionNumber,
                status = v.TestCase.Status.ToString(), v.Revision, v.TestCase.CreationReason, v.TestCase.SourceVersionId
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

    [McpServerTool(Name = "create_draft_test_case", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create an incomplete unclassified Draft v1 with no links. Optional null content defaults to empty. Never automatically retry; list/read candidates after uncertain creation, and do not blindly recreate an ambiguous result.")]
    public async Task<CallToolResult> Create(string productId, CancellationToken cancellationToken,
        string? title = null, string? description = null, string? preconditions = null, TestCaseStep[]? steps = null)
    {
        var result = await operations.CreateMcp(Principal, products, productId, title, description, preconditions, steps, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    [McpServerTool(Name = "edit_draft_test_case", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Fully replace exact Draft content using its positive expectedRevision. Preserves identities, links and classification. No lifecycle/link changes. Never automatically retry; retrieve before user-directed retry after uncertain outcomes.")]
    public async Task<CallToolResult> Edit(Guid internalId, Guid versionId, long expectedRevision, string title,
        string description, string preconditions, TestCaseStep[] steps, CancellationToken cancellationToken)
    {
        var result = await operations.EditMcp(Principal, internalId, versionId, expectedRevision, title, description, preconditions, steps, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    [McpServerTool(Name = "edit_draft_test_case_classification", Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Change only the exact eligible Draft's classification after explicit human approval of old/new values. Supply internalId, versionId, positive expectedRevision, areaId (active same-Product area identity, or explicit null to clear), and confirmed=true. Preserves content and links. Never automatically retry; after uncertainty retrieve authorized current state before a human-directed retry.")]
    public async Task<CallToolResult> Classify(Guid internalId, Guid versionId, long expectedRevision, Guid? areaId,
        bool confirmed, CancellationToken cancellationToken)
    {
        var result = await operations.ClassifyMcp(Principal, internalId, versionId, expectedRevision, areaId, confirmed, cancellationToken);
        return result.IsFailure ? Error(result.Error) : Success(Values(result.Value));
    }

    private static object Values(TestCaseSnapshot snapshot) => new
    {
        snapshot.TestCase.InternalId, snapshot.TestCase.ProductInternalId, snapshot.TestCase.Id,
        snapshot.TestCase.VersionId, snapshot.TestCase.VersionNumber, status = snapshot.TestCase.Status.ToString(),
        snapshot.TestCase.Content.Title, snapshot.TestCase.Content.Description, snapshot.TestCase.Content.Preconditions,
        steps = Steps(snapshot.TestCase.Content.Steps), links = Links(snapshot.TestCase.Links), snapshot.Revision,
        snapshot.TestCase.CreationReason, snapshot.TestCase.SourceVersionId, snapshot.LatestApprovedVersionNumber, snapshot.TestCase.AreaId
    };
    private static object? AuditValues(TestCaseAuditValues? value) => value is null ? null : new
    {
        value.ProductInternalId, value.Id, value.Title, value.Description, value.Preconditions, value.Status,
        steps = Steps(value.Steps), links = value.Links is null ? null : Links(value.Links)
    };
    private static object Steps(IEnumerable<TestCaseStep> steps) => steps.Select(s => new { s.Action, s.ExpectedResult });
    private static object Links(IEnumerable<TestCaseRequirementLink> links) => links.Select(l => new { l.RequirementInternalId, l.RequirementVersionId });
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CallToolResult Success(object value) => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };
    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message.Contains("could not be confirmed", StringComparison.Ordinal)
            ? "The write outcome could not be confirmed. Retrieve authorized list/read records before any user-directed retry; do not retry automatically. For creation without an identity, inspect candidate records; if ambiguous, do not blindly recreate." : message }]
    };
}
