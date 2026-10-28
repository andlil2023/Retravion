using System.Security.Claims;
using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;

namespace Retravion.Web.Features.Identity.Users;

// Only the composition root supplies an implementation that shares the PostgreSQL transaction.
internal interface IAccountAuditTransaction
{
    Task<AccountTransactionResult<T>> Execute<T>(Func<IAdministratorStore, Task<Result<T>>> change,
        AuditInformation success, CancellationToken cancellationToken);
}

internal sealed record AccountTransactionResult<T>(Result<T> Result, bool Uncertain = false);

internal sealed class AccountAudit(IAuditSubmission submission, IAdministratorStore store, TimeProvider time, ILogger<AccountAudit> logger)
{
    internal AuditInformation Information(ClaimsPrincipal actor, string action, string? username, AuditActionOutcome outcome,
        bool targetKnown = false)
    {
        var actorName = actor.FindFirstValue(ClaimTypes.NameIdentifier);
        AuditActor auditActor = actor.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(actorName)
            ? new AuditActor.Identified(actorName) : new AuditActor.Unknown();
        // Request text is never an identifier until the operation or a store lookup establishes it.
        AuditTarget target = targetKnown && !string.IsNullOrWhiteSpace(username)
            ? new AuditTarget.Identified(username) : new AuditTarget.Unknown();
        return new AuditInformation(auditActor, time.GetUtcNow(), action, target, outcome);
    }

    // Called only for requests already denied by authorization or validation; it never permits an effect.
    internal async Task RejectedRequest(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) return;
        var path = context.Request.Path.Value?.TrimEnd('/') ?? "";
        string? action = null;
        string? username = null;
        if (path.Equals("/administration/accounts", StringComparison.OrdinalIgnoreCase))
            action = "account.create";
        else if (context.Request.RouteValues.TryGetValue("username", out var routeName))
        {
            username = routeName?.ToString();
            if (path.EndsWith("/password-reset", StringComparison.OrdinalIgnoreCase)) action = "account.password-reset";
            else if (path.EndsWith("/availability", StringComparison.OrdinalIgnoreCase)) action = "account.availability";
        }
        if (action is null) return;
        if ((action == "account.create" || action == "account.availability") && context.Request.HasFormContentType)
        {
            try
            {
                var form = await context.Request.ReadFormAsync();
                if (action == "account.create") username = form["username"].ToString();
                else if (bool.TryParse(form["active"], out var active))
                    action = active ? "account.reactivate" : "account.inactivate";
            }
            catch (Exception) { /* Invalid form: record only the available safe information. */ }
        }
        await Failure(context.User, action, username);
    }

    internal async Task Failure(ClaimsPrincipal actor, string action, string? username)
    {
        string? knownUsername = null;
        if (!string.IsNullOrWhiteSpace(username))
        {
            try
            {
                var loaded = await store.LoadForSignIn(username);
                if (loaded.IsSuccess && loaded.Value.HasValue &&
                    string.Equals(loaded.Value.Value.Username, username, StringComparison.OrdinalIgnoreCase))
                    knownUsername = loaded.Value.Value.Username;
            }
            catch (Exception) { /* Failure to identify a target must not prevent the audit attempt. */ }
        }
        try
        {
            if (await submission.SubmitAsync(Information(actor, action, knownUsername, AuditActionOutcome.Failure,
                    targetKnown: knownUsername is not null))
                == AuditSubmissionResult.Success) return;
        }
        catch (Exception)
        {
            // Provider exceptions may contain credentials or connection details; do not log them.
        }
        logger.LogError("Account-administration failure audit submission failed.");
    }
}
