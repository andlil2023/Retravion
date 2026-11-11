using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.Feature;

internal sealed class FeatureOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IFeatureStore store, IFeatureChangeTransaction changes, IAuditSubmission audit, TimeProvider time, ILogger<FeatureOperations> logger)
{
    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    private async Task<Result<FeatureSnapshot>> Change(ClaimsPrincipal principal, string action,
        Func<AuditActor, DateTimeOffset, Task<Result<FeatureSnapshot>>> operation, CancellationToken token)
    {
        AuditActor actor = principal.Identity?.IsAuthenticated == true && principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();
        var now = time.GetUtcNow();
        await sessions.MutationGate.WaitAsync(token);
        try
        {
            if (await Allowed(principal) && actor is AuditActor.Identified)
            {
                var result = await operation(actor, now);
                if (result.IsSuccess || result.Error != "Access denied.") return result;
            }
        }
        finally { sessions.MutationGate.Release(); }
        try
        {
            if (await audit.SubmitAsync(new AuditInformation(actor, now, action, new AuditTarget.Unknown(), AuditActionOutcome.Failure), token) == AuditSubmissionResult.Success)
                return Result.Failure<FeatureSnapshot>("Access denied.");
        }
        catch (Exception) { }
        logger.LogError("Unauthorized Feature change audit submission failed.");
        return Result.Failure<FeatureSnapshot>("Access denied.");
    }
    public Task<Result<FeatureSnapshot>> Create(ClaimsPrincipal principal, Guid product, string? title, string? description, CancellationToken token = default, Guid? operationId = null) =>
        Change(principal, "feature.create", (actor, now) => changes.Create(product, FeatureContent.Create(title, description), actor, now, token, operationId, () => Allowed(principal)), token);
    public Task<Result<FeatureSnapshot>> Edit(ClaimsPrincipal principal, FeatureSnapshot expected, string? title, string? description, CancellationToken token = default, Guid? operationId = null) =>
        Change(principal, "feature.edit", (actor, now) => changes.Update(expected, FeatureContent.Create(title, description), actor, now, token, operationId, () => Allowed(principal)), token);
    public Task<Result<FeatureSnapshot>> Review(ClaimsPrincipal principal, FeatureSnapshot expected, string decision, string? comment, CancellationToken token = default) =>
        Change(principal, decision switch { "InReview" => "feature.review.InReview", "Approved" => "feature.review.Approved", "Draft" => "feature.review.Draft", _ => "feature.review" },
            (actor, now) => changes.Review(expected, decision, comment, actor, now, token, () => Allowed(principal)), token);
    public Task<Result<FeatureSnapshot>> Reopen(ClaimsPrincipal principal, FeatureSnapshot expected, string? reason, CancellationToken token = default) =>
        Change(principal, "feature.reopen", (actor, now) => changes.Reopen(expected, reason, actor, now, token, () => Allowed(principal)), token);
    public Task<Result<FeatureSnapshot>> CreateSuccessor(ClaimsPrincipal principal, FeatureSnapshot expected, string? reason, CancellationToken token = default, Guid? operationId = null) =>
        Change(principal, "feature.version.create", (actor, now) => changes.CreateSuccessor(expected, reason, actor, now, token, operationId, () => Allowed(principal)), token);
    public async Task<Result<IReadOnlyList<FeatureSnapshot>>> List(ClaimsPrincipal principal, Guid product, CancellationToken token = default) =>
        await Allowed(principal) ? await store.List(product, token) : Result.Failure<IReadOnlyList<FeatureSnapshot>>("Access denied.");
    public async Task<Result<Maybe<FeatureSnapshot>>> Find(ClaimsPrincipal principal, Guid id, Guid? version = null, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Find(id, version, token) : Result.Failure<Maybe<FeatureSnapshot>>("Access denied.");
    public async Task<Result<IReadOnlyList<FeatureSnapshot>>> Versions(ClaimsPrincipal principal, Guid id, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Versions(id, token) : Result.Failure<IReadOnlyList<FeatureSnapshot>>("Access denied.");
    public async Task<Result<IReadOnlyList<FeatureHistory>>> History(ClaimsPrincipal principal, Guid id, CancellationToken token = default) =>
        await Allowed(principal) ? await store.History(id, token) : Result.Failure<IReadOnlyList<FeatureHistory>>("Access denied.");
    public async Task<Result<Maybe<FeatureAuditDetails>>> Outcome(ClaimsPrincipal principal, Guid product, Guid operationId, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Outcome(product, operationId, token) : Result.Failure<Maybe<FeatureAuditDetails>>("Access denied.");
}
