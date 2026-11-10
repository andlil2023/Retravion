using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Retravion.Application.AiWorkflow;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.Mcp;

internal static class AiWorkflowEndpoints
{
    internal static void MapAiWorkflow(this WebApplication app)
    {
        app.MapPost("/settings/ai-workflow/save", async (HttpContext context, AiWorkflowOperations operations, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // Own this gate so every unauthorized settings mutation is audited, including anonymous/revoked callbacks.
            if (!await operations.CanConfigure(context.User))
            {
                await operations.Save(context.User, new(false, 1), false, context.RequestAborted);
                return Results.StatusCode(403);
            }
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!bool.TryParse(form["enabled"], out var enabled) || !bool.TryParse(form["before"], out var before) ||
                !long.TryParse(form["revision"], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision <= 0 ||
                form["confirm"] != "true") return Results.BadRequest();
            context.RequestServices.GetRequiredService<SessionTicketStore>().RecordActivity(context.User.FindFirstValue(SessionTicketStore.SessionClaim));
            var result = await operations.Save(context.User, new(before, revision), enabled, context.RequestAborted);
            return result.IsSuccess ? Results.LocalRedirect("/settings/mcp?saved=true") : Results.BadRequest(new { error = result.Error });
        }).AllowAnonymous(); // All access checks live in operations; never trust endpoint anonymity as permission.

        app.MapGet("/settings/ai-workflow/packages/{version}/{client}", (string version, string client, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-cache";
            if (version != TerminalPackages.Version) return Results.NotFound();
            try
            {
                var bundle = TerminalPackages.Get(client);
                return bundle is null ? Results.NotFound() : Results.File(bundle.Bytes, "application/zip", bundle.FileName);
            }
            catch (IOException) { return Results.Problem("Terminal skill files are unavailable.", statusCode: 503); }
        }).RequireAuthorization("CompletedPasswordReplacement");
    }
}
