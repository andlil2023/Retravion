using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.UserNeed;
using Retravion.Web.Features.Requirement;
using Retravion.Web.Features.TestCase;

namespace Retravion.Web.Features.Mcp;

internal static class McpEndpoints
{
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly Dictionary<string, string[]> Arguments = new(StringComparer.Ordinal)
    {
        ["list_features"] = ["productId"],
        ["read_feature"] = ["internalId", "versionId"],
        ["create_draft_feature"] = ["productId", "title", "description"],
        ["edit_draft_feature"] = ["internalId", "versionId", "expectedRevision", "title", "description"],
        ["list_test_cases"] = ["productId"],
        ["read_test_case"] = ["internalId", "versionId"],
        ["create_draft_test_case"] = ["productId", "title", "description", "preconditions", "steps"],
        ["edit_draft_test_case"] = ["internalId", "versionId", "expectedRevision", "title", "description", "preconditions", "steps"],
        ["list_products"] = [],
        ["read_product"] = ["productId"],
        ["list_user_needs"] = ["productId"],
        ["read_user_need"] = ["internalId", "versionId"],
        ["create_draft_user_need"] = ["productId", "id", "title", "statement"],
        ["edit_draft_user_need"] = ["internalId", "expectedRevision", "id", "title", "statement"],
        ["list_requirements"] = ["productId"],
        ["read_requirement"] = ["internalId", "versionId"],
        ["create_draft_requirement"] = ["productId", "id", "title", "statement", "type", "rationale"],
        ["edit_draft_requirement"] = ["internalId", "expectedRevision", "id", "title", "statement", "type", "rationale"]
    };

    private static bool ValidTestCaseArguments(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var fields = arguments.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) return false;
        var create = tool == "create_draft_test_case";
        var edit = tool == "edit_draft_test_case";
        bool Has(string name, JsonValueKind kind) => arguments.TryGetProperty(name, out var value) && value.ValueKind == kind;
        bool GuidValue(string name, bool optional = false) =>
            !arguments.TryGetProperty(name, out var value) ? optional :
            optional && value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.TryGetGuid(out _);
        if (tool is "list_test_cases" or "create_draft_test_case")
        {
            if (!Has("productId", JsonValueKind.String)) return false;
        }
        else if (!GuidValue("internalId") || !GuidValue("versionId", !edit)) return false;
        if (!create && !edit) return true;
        if (edit && (!arguments.TryGetProperty("expectedRevision", out var revision) ||
            revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) || number <= 0)) return false;
        foreach (var name in new[] { "title", "description", "preconditions", "steps" })
        {
            if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (edit) return false;
                continue;
            }
            if (name != "steps") { if (value.ValueKind != JsonValueKind.String) return false; continue; }
            if (value.ValueKind != JsonValueKind.Array) return false;
            foreach (var step in value.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object) return false;
                var properties = step.EnumerateObject().ToArray();
                if (properties.Length != 2 || !step.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                    !step.TryGetProperty("expectedResult", out var result) || result.ValueKind != JsonValueKind.String) return false;
            }
        }
        return true;
    }

    private static bool ValidFeatureArguments(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        var fields = arguments.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) return false;
        var edit = tool == "edit_draft_feature";
        bool GuidValue(string name, bool optional = false) =>
            !arguments.TryGetProperty(name, out var value) ? optional :
            optional && value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.TryGetGuid(out _);
        if (tool is "list_features" or "create_draft_feature")
        {
            if (!arguments.TryGetProperty("productId", out var product) || product.ValueKind != JsonValueKind.String) return false;
        }
        else if (!GuidValue("internalId") || !GuidValue("versionId", !edit)) return false;
        if (tool is "list_features" or "read_feature") return true;
        if (edit && (!arguments.TryGetProperty("expectedRevision", out var revision) ||
            revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var number) || number <= 0)) return false;
        foreach (var name in new[] { "title", "description" })
        {
            if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (edit) return false;
            }
            else if (value.ValueKind != JsonValueKind.String) return false;
        }
        return true;
    }

    internal static void MapMcpCredentials(this WebApplication app)
    {
        // Terminal bridge handoff. The bridge keeps the device code; a signed-in user approves the short user code.
        app.MapPost("/mcp-connect/start", async (HttpContext context, McpConnections connections,
            Retravion.Application.AiWorkflow.IAiWorkflowSettingsStore settings) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TransportAllowed(context)) return Results.StatusCode(403);
            var configuration = await settings.Load(context.RequestAborted);
            if (configuration.IsFailure || !configuration.Value.Enabled) return Results.StatusCode(403);
            var started = connections.Start();
            if (started is null) return Results.StatusCode(429);
            return Results.Json(new
            {
                deviceCode = started.DeviceCode,
                userCode = started.UserCode,
                verificationUri = $"https://{context.Request.Host}/settings/ai-workflow?connect={started.UserCode}",
                expiresIn = (int)McpConnections.Lifetime.TotalSeconds,
                interval = 2
            });
        }).AllowAnonymous();
        app.MapPost("/mcp-connect/status", (HttpContext context, McpConnections connections) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TransportAllowed(context)) return Results.StatusCode(403);
            return Results.Json(new { status = connections.Status(Bearer(context)).ToString().ToLowerInvariant() });
        }).AllowAnonymous();
        app.MapPost("/mcp-connect/disconnect", (HttpContext context, McpCredentials credentials) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TransportAllowed(context)) return Results.StatusCode(403);
            var principal = credentials.Resolve(Bearer(context));
            if (credentials.IsActive(principal)) credentials.Revoke(principal);
            return Results.NoContent();
        }).AllowAnonymous();
        app.MapPost("/mcp-connect/decide", async (HttpContext context, IAntiforgery antiforgery, McpConnections connections,
            McpCredentials credentials, IAuthorizationService authorization) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var approve = form["decision"] == "approve";
            if (!approve && form["decision"] != "deny") return Results.BadRequest();
            var digest = connections.Decide(form["code"], approve);
            if (digest is null) return Results.LocalRedirect("/settings/ai-workflow?connection=expired");
            if (!approve) return Results.LocalRedirect("/settings/ai-workflow?connection=denied");
            if (await credentials.Issue(context.User, authorization, digest, context.RequestAborted))
                return Results.LocalRedirect("/settings/ai-workflow?connection=approved");
            connections.Deny(digest);
            return Results.StatusCode(403);
        }).RequireAuthorization("CompletedPasswordReplacement");
        app.MapPost("/identity/mcp-token/revoke", async (HttpContext context, IAntiforgery antiforgery, McpCredentials credentials) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            credentials.Revoke(context.User);
            return Results.LocalRedirect("/settings/ai-workflow?revoked=true");
        }).RequireAuthorization("CompletedPasswordReplacement");
    }

    private static bool TransportAllowed(HttpContext context)
    {
        var host = context.Request.Host.Host;
        return !context.Request.Headers.ContainsKey("Origin") &&
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]");
    }

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
    }

    internal static void UseMcpAccessGate(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/mcp-access") || context.Request.Path.StartsWithSegments("/settings/ai-workflow"))
            context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.Path.StartsWithSegments("/mcp")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        // Terminal clients send no Origin. Reject browser-origin traffic and unexpected hosts (DNS rebinding).
        var transportAllowed = TransportAllowed(context);
        var credentials = context.RequestServices.GetRequiredService<McpCredentials>();
        context.User = credentials.Resolve(Bearer(context));
        var allowed = await credentials.IsAllowed(context.User, context.RequestAborted) &&
            (await context.RequestServices.GetRequiredService<IAuthorizationService>()
                .AuthorizeAsync(context.User, "CompletedPasswordReplacement")).Succeeded;

        // Bound input before parsing. Rewind only an in-memory copy; never log submitted content.
        using var body = new MemoryStream();
        JsonDocument? document = null;
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var buffer = new byte[16384];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, MaxBodyBytes + 1 - body.Length)), context.RequestAborted)) > 0)
            {
                body.Write(buffer, 0, count);
                if (body.Length > MaxBodyBytes) { context.Response.StatusCode = 413; return; }
            }
            body.Position = 0;
            try { document = await JsonDocument.ParseAsync(body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { context.Response.StatusCode = !transportAllowed ? 403 : allowed ? 400 : 401; return; }
            body.Position = 0;
        }
        using (document)
        {
            var root = document?.RootElement;
            string? tool = null;
            JsonElement parameters = default;
            if (root is { ValueKind: JsonValueKind.Object } value &&
                value.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String && method.GetString() == "tools/call" &&
                value.TryGetProperty("params", out parameters) && parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                tool = name.GetString();
            if (!allowed || !transportAllowed)
            {
                if (tool is "create_draft_user_need" or "edit_draft_user_need")
                    await context.RequestServices.GetRequiredService<UserNeedOperations>().DeniedMcpMutation(context.User,
                        tool == "create_draft_user_need" ? "need.create" : "need.edit", context.RequestAborted);
                if (tool is "create_draft_requirement" or "edit_draft_requirement")
                    await context.RequestServices.GetRequiredService<RequirementOperations>().DeniedMcpMutation(context.User,
                        tool == "create_draft_requirement" ? "requirement.create" : "requirement.edit", context.RequestAborted);
                if (tool is "create_draft_feature" or "edit_draft_feature")
                    await context.RequestServices.GetRequiredService<Retravion.Web.Features.Feature.FeatureOperations>().DeniedMcpMutation(context.User,
                        tool == "create_draft_feature" ? "feature.create" : "feature.edit", context.RequestAborted);
                if (tool is "create_draft_test_case" or "edit_draft_test_case")
                    await context.RequestServices.GetRequiredService<TestCaseOperations>().DeniedMcpMutation(context.User,
                        tool == "create_draft_test_case" ? "test-case.create" : "test-case.edit", context.RequestAborted);
                context.Response.StatusCode = transportAllowed ? 401 : 403;
                return;
            }
            if (tool is not null && Arguments.TryGetValue(tool, out var accepted) &&
                parameters.TryGetProperty("arguments", out var arguments) &&
                (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(p => !accepted.Contains(p.Name, StringComparer.Ordinal))))
            {
                // Reject lifecycle/actor injection rather than silently ignoring extra fields.
                context.Response.StatusCode = 400;
                return;
            }
            if (tool is "list_test_cases" or "read_test_case" or "create_draft_test_case" or "edit_draft_test_case" &&
                (!parameters.TryGetProperty("arguments", out var caseArguments) || !ValidTestCaseArguments(tool, caseArguments)))
            {
                context.Response.StatusCode = 400;
                return;
            }
            if (tool is "list_features" or "read_feature" or "create_draft_feature" or "edit_draft_feature" &&
                (!parameters.TryGetProperty("arguments", out var featureArguments) || !ValidFeatureArguments(tool, featureArguments)))
            {
                context.Response.StatusCode = 400;
                return;
            }
            context.RequestServices.GetRequiredService<SessionTicketStore>()
                .RecordActivity(context.User.FindFirstValue(SessionTicketStore.SessionClaim));
            credentials.RecordActivity(context.User);
            var original = context.Request.Body;
            if (document is not null) context.Request.Body = body;
            try { await next(context); }
            finally { context.Request.Body = original; }
        }
    });
}
