using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.UserNeed;
using Retravion.Web.Features.Requirement;

namespace Retravion.Web.Features.Mcp;

internal static class McpEndpoints
{
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly Dictionary<string, string[]> Arguments = new(StringComparer.Ordinal)
    {
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

    internal static void MapMcpCredentials(this WebApplication app)
    {
        app.MapPost("/identity/mcp-token", async (HttpContext context, IAntiforgery antiforgery,
            McpCredentials credentials, IAuthorizationService authorization) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var issued = await credentials.Issue(context.User, authorization, context.RequestAborted);
            return issued is null ? Results.StatusCode(403) : Results.Json(issued);
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

    internal static void UseMcpAccessGate(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/mcp-access") || context.Request.Path.StartsWithSegments("/settings/ai-workflow"))
            context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.Path.StartsWithSegments("/mcp")) { await next(context); return; }
        context.Response.Headers.CacheControl = "no-store";
        // Terminal clients send no Origin. Reject browser-origin traffic and unexpected hosts (DNS rebinding).
        var host = context.Request.Host.Host;
        var transportAllowed = !context.Request.Headers.ContainsKey("Origin") &&
            (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]");
        var header = context.Request.Headers.Authorization.ToString();
        var credentials = context.RequestServices.GetRequiredService<McpCredentials>();
        context.User = credentials.Resolve(header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null);
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
            context.RequestServices.GetRequiredService<SessionTicketStore>()
                .RecordActivity(context.User.FindFirstValue(SessionTicketStore.SessionClaim));
            var original = context.Request.Body;
            if (document is not null) context.Request.Body = body;
            try { await next(context); }
            finally { context.Request.Body = original; }
        }
    });
}
