using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.Web.Features.UserNeed;

internal sealed class UserNeedOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IUserNeedStore store, IUserNeedChangeTransaction changes, IAuditSubmission audit, TimeProvider time,
    ILogger<UserNeedOperations> logger, McpCredentials mcpCredentials)
{
    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || mcpCredentials.IsActive(principal)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    private static AuditActor Actor(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();

    private async Task<Result<UserNeedSnapshot>> Change(ClaimsPrincipal principal, string action,
        Func<AuditActor, DateTimeOffset, Task<Result<UserNeedSnapshot>>> operation, CancellationToken cancellationToken)
    {
        var actor = Actor(principal);
        var now = time.GetUtcNow();
        if (await Allowed(principal) && actor is AuditActor.Identified)
            return await operation(actor, now);
        return await Deny(actor, now, action, cancellationToken);
    }

    private async Task<Result<UserNeedSnapshot>> Deny(AuditActor actor, DateTimeOffset now, string action,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await audit.SubmitAsync(new AuditInformation(actor, now, action, new AuditTarget.Unknown(),
                    AuditActionOutcome.Failure), cancellationToken) == AuditSubmissionResult.Success)
                return Result.Failure<UserNeedSnapshot>("Access denied.");
        }
        catch (Exception) { /* Do not expose provider errors or submitted values. */ }
        logger.LogError("Unauthorized User Need change audit submission failed.");
        return Result.Failure<UserNeedSnapshot>("Access denied.");
    }

    // Used by the MCP request gate before deserializing any submitted business values.
    internal Task<Result<UserNeedSnapshot>> DeniedMcpMutation(ClaimsPrincipal principal, string action,
        CancellationToken cancellationToken) => Deny(Actor(principal), time.GetUtcNow(), action, cancellationToken);

    public Task<Result<UserNeedSnapshot>> Create(ClaimsPrincipal principal, Guid product, string? id, string? title, string? statement,
        CancellationToken cancellationToken = default) => Change(principal, "need.create", (actor, now) =>
    {
        var need = Need.Create(product, id, title, statement);
        return need.IsSuccess ? changes.Create(need.Value, actor, now, cancellationToken)
            : Task.FromResult(Result.Failure<UserNeedSnapshot>(need.Error));
    }, cancellationToken);

    public Task<Result<UserNeedSnapshot>> Edit(ClaimsPrincipal principal, UserNeedSnapshot expected, string? id, string? title,
        string? statement, CancellationToken cancellationToken = default) => Change(principal, "need.edit", (actor, now) =>
    {
        var need = expected.Need.Edit(id, title, statement);
        return need.IsSuccess ? changes.Update(expected, need.Value, actor, now, cancellationToken: cancellationToken)
            : Task.FromResult(Result.Failure<UserNeedSnapshot>(need.Error));
    }, cancellationToken);

    internal Task<Result<UserNeedSnapshot>> EditDraft(ClaimsPrincipal principal, Guid internalId, long expectedRevision,
        string? id, string? title, string? statement, CancellationToken cancellationToken = default) =>
        Change(principal, "need.edit", async (actor, now) =>
        {
            var found = await store.Find(internalId, cancellationToken);
            if (found.IsFailure) return Result.Failure<UserNeedSnapshot>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<UserNeedSnapshot>("User Need not found.");
            var expected = found.Value.Value;
            if (expected.Revision != expectedRevision)
                return Result.Failure<UserNeedSnapshot>("The User Need changed. Retrieve it again before editing.");
            var need = expected.Need.Edit(id, title, statement);
            return need.IsSuccess ? await changes.Update(expected, need.Value, actor, now, cancellationToken: cancellationToken)
                : Result.Failure<UserNeedSnapshot>(need.Error);
        }, cancellationToken);

    public Task<Result<UserNeedSnapshot>> Review(ClaimsPrincipal principal, UserNeedSnapshot expected, string decision, string? comment,
        CancellationToken cancellationToken = default) => Change(principal, decision switch
        {
            "InReview" => "need.review.InReview", "Approved" => "need.review.Approved",
            "Draft" => "need.review.Draft", _ => "need.review"
        }, (actor, now) =>
    {
        var need = decision switch
        {
            "InReview" => expected.Need.SubmitForReview(),
            "Approved" => expected.Need.Approve(),
            "Draft" => expected.Need.ReturnToDraft(),
            _ => Result.Failure<Need>("Invalid review decision.")
        };
        return need.IsSuccess ? changes.Update(expected, need.Value, actor, now, decision, comment, cancellationToken)
            : Task.FromResult(Result.Failure<UserNeedSnapshot>(need.Error));
    }, cancellationToken);

    public async Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(ClaimsPrincipal principal, Guid product, CancellationToken token = default) =>
        await Allowed(principal) ? await store.List(product, token) : Result.Failure<IReadOnlyList<UserNeedSnapshot>>("Access denied.");
    public async Task<Result<Maybe<UserNeedSnapshot>>> Find(ClaimsPrincipal principal, Guid id, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Find(id, token) : Result.Failure<Maybe<UserNeedSnapshot>>("Access denied.");
    public async Task<Result<IReadOnlyList<UserNeedReview>>> History(ClaimsPrincipal principal, Guid id, CancellationToken token = default) =>
        await Allowed(principal) ? await store.History(id, token) : Result.Failure<IReadOnlyList<UserNeedReview>>("Access denied.");
}
