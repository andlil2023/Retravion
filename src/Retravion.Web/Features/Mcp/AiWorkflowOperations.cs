using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AiWorkflow;
using Retravion.Application.AuditTrail;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.Mcp;

internal sealed class AiWorkflowOperations(IAiWorkflowSettingsStore store, IAuthorizationService authorization,
    SessionTicketStore sessions, IAuditSubmission audit, TimeProvider time, ILogger<AiWorkflowOperations> logger)
{
    internal async Task<bool> CanConfigure(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(principal, "Administrator")).Succeeded;

    internal async Task<Result<AiWorkflowSettings>> Read(ClaimsPrincipal principal, CancellationToken token = default) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded
            ? await store.Load(token) : Result.Failure<AiWorkflowSettings>("Access denied.");

    internal async Task<Result<AiWorkflowSettings>> Save(ClaimsPrincipal principal, AiWorkflowSettings expected,
        bool enabled, CancellationToken token = default)
    {
        var actor = principal.Identity?.IsAuthenticated == true && principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? (AuditActor)new AuditActor.Identified(id) : new AuditActor.Unknown();
        var now = time.GetUtcNow();
        if (await CanConfigure(principal) && actor is AuditActor.Identified)
        {
            var result = await store.Save(expected, enabled, actor, now, token);
            if (result.IsFailure) logger.LogError("AI Workflow settings change could not be confirmed.");
            return result;
        }
        try
        {
            if (await audit.SubmitAsync(new AuditInformation(actor, now, "ai-workflow.configure",
                    new AuditTarget.Unknown(), AuditActionOutcome.Failure), token) == AuditSubmissionResult.Success)
                return Result.Failure<AiWorkflowSettings>("Access denied.");
        }
        catch (Exception) { /* Never disclose provider details or submitted values. */ }
        logger.LogError("Unauthorized AI Workflow settings audit submission failed.");
        return Result.Failure<AiWorkflowSettings>("Access denied.");
    }
}
