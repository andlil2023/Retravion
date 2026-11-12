using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;

namespace Retravion.Web.Features.Revision;

internal sealed class RevisionOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IRevisionStore store, IRevisionChangeTransaction changes, IAuditSubmission audit, TimeProvider time,
    ILogger<RevisionOperations> logger, McpCredentials mcpCredentials)
{
    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    internal async Task<bool> CanRead(ClaimsPrincipal principal) =>
        await Allowed(principal) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || await mcpCredentials.IsAllowed(principal));

    private async Task<Result<T>> Read<T>(ClaimsPrincipal principal, Func<Task<Result<T>>> read)
    {
        if (!await CanRead(principal)) return Result.Failure<T>("Access denied.");
        var result = await read();
        return await CanRead(principal) ? result : Result.Failure<T>("Access denied.");
    }

    private async Task<Result<RevisionSnapshot>> Change(ClaimsPrincipal principal, string action,
        Func<AuditActor, DateTimeOffset, Func<Task<bool>>, Task<Result<RevisionSnapshot>>> operation, CancellationToken token)
    {
        AuditActor actor = principal.Identity?.IsAuthenticated == true &&
            principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();
        var now = time.GetUtcNow();
        Result<RevisionSnapshot> result;
        await sessions.MutationGate.WaitAsync(token);
        try
        {
            result = await Allowed(principal) && actor is AuditActor.Identified
                ? await operation(actor, now, () => Allowed(principal)) : Result.Failure<RevisionSnapshot>("Access denied.");
        }
        finally { sessions.MutationGate.Release(); }
        if (result.IsSuccess || result.Error != "Access denied.") return result;
        try
        {
            if (await audit.SubmitAsync(new AuditInformation(actor, now, action, new AuditTarget.Unknown(), AuditActionOutcome.Failure), token)
                == AuditSubmissionResult.Success) return result;
        }
        catch (Exception) { /* Deny even if audit submission fails; never log provider details. */ }
        logger.LogError("Unauthorized revision change audit submission failed.");
        return result;
    }

    public Task<Result<RevisionSnapshot>> Create(ClaimsPrincipal principal, Guid operation, Guid id, Guid product,
        string? identifier, string? description, CancellationToken token = default) =>
        Change(principal, "revision.create", (actor, now, allowed) =>
        {
            var draft = DraftRevision.Create(id, product, identifier, description);
            return draft.IsSuccess ? changes.Create(operation, draft.Value, actor, now, allowed, token)
                : Task.FromResult(Result.Failure<RevisionSnapshot>(draft.Error));
        }, token);

    public Task<Result<RevisionSnapshot>> Copy(ClaimsPrincipal principal, Guid operation, Guid id, Guid product, Guid sourceId,
        string? identifier, string? description, CancellationToken token = default) =>
        Change(principal, "revision.copy", (actor, now, allowed) =>
        {
            var destination = DraftRevision.Create(id, product, identifier, description);
            return destination.IsSuccess ? changes.Copy(operation, destination.Value, sourceId, actor, now, allowed, token)
                : Task.FromResult(Result.Failure<RevisionSnapshot>(destination.Error));
        }, token);

    public Task<Result<RevisionSnapshot>> Edit(ClaimsPrincipal principal, Guid operation, RevisionSnapshot expected,
        string? identifier, string? description, CancellationToken token = default) =>
        Change(principal, "revision.edit", (actor, now, allowed) => changes.Edit(operation, expected, identifier, description, actor, now, allowed, token), token);

    public Task<Result<RevisionSnapshot>> Select(ClaimsPrincipal principal, Guid operation, RevisionSnapshot expected, string action,
        RevisionSelection target, RevisionSelection? replacement = null, bool confirmed = false, CancellationToken token = default) =>
        Change(principal, action switch { "add" => "revision.selection.add", "remove" => "revision.selection.remove", "replace" => "revision.selection.replace", _ => "revision.selection" },
            (actor, now, allowed) => action is "remove" or "replace" && !confirmed
                ? Task.FromResult(Result.Failure<RevisionSnapshot>("Confirm the exact selection change first."))
                : changes.Select(operation, expected, action, target, replacement, actor, now, allowed, token), token);

    public Task<Result<RevisionSnapshot>> Release(ClaimsPrincipal principal, Guid operation, RevisionSnapshot expected,
        string baselineToken, bool confirmed, CancellationToken token = default) =>
        Change(principal, "revision.release", (actor, now, allowed) => confirmed
            ? changes.Release(operation, expected, baselineToken, actor, now, allowed, token)
            : Task.FromResult(Result.Failure<RevisionSnapshot>("Confirm the exact baseline before release.")), token);

    public Task<Result<RevisionSnapshot>> Delete(ClaimsPrincipal principal, Guid operation, RevisionSnapshot expected, bool confirmed,
        CancellationToken token = default) => Change(principal, "revision.delete", (actor, now, allowed) => confirmed
            ? changes.Delete(operation, expected, actor, now, allowed, token)
            : Task.FromResult(Result.Failure<RevisionSnapshot>("Confirm Draft deletion first.")), token);

    public Task<Result<IReadOnlyList<RevisionSnapshot>>> List(ClaimsPrincipal principal, Guid product, CancellationToken token = default) =>
        Read(principal, () => store.List(product, token));
    public Task<Result<RevisionRetrieval>> Retrieve(ClaimsPrincipal principal, Guid product, Guid id, Guid? operation = null, CancellationToken token = default) =>
        Read(principal, () => store.Retrieve(product, id, operation, token));
    public Task<Result<IReadOnlyList<RevisionHistory>>> History(ClaimsPrincipal principal, Guid product, Guid id, CancellationToken token = default) =>
        Read(principal, () => store.History(product, id, token));
    public Task<Result<IReadOnlyList<RevisionBaselineMember>>> Candidates(ClaimsPrincipal principal, Guid product, CancellationToken token = default) =>
        Read(principal, () => store.Candidates(product, token));
    public Task<Result<RevisionBaseline>> Baseline(ClaimsPrincipal principal, Guid product, Guid id, CancellationToken token = default) =>
        Read(principal, () => store.Baseline(product, id, token));
}
