using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;

namespace Retravion.Web.Features.Feature;

internal sealed class FeatureOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IFeatureStore store, IFeatureChangeTransaction changes, IAuditSubmission audit, TimeProvider time, ILogger<FeatureOperations> logger, McpCredentials mcpCredentials)
{
    internal Task<bool> CanRead(ClaimsPrincipal principal) => Allowed(principal);

    internal Task<Result<FeatureSnapshot>> DeniedMcpMutation(ClaimsPrincipal principal, string action, CancellationToken token) =>
        Change(principal, action, (_, _) => Task.FromResult(Result.Failure<FeatureSnapshot>("Access denied.")), token);

    internal Task<Result<FeatureSnapshot>> CreateMcp(ClaimsPrincipal principal, Retravion.Application.Product.IProductStore products,
        string productId, string? title, string? description, CancellationToken token) =>
        Change(principal, "feature.create", async (actor, now) =>
        {
            var product = await products.Find(productId, token);
            if (product.IsFailure) return Result.Failure<FeatureSnapshot>(product.Error);
            if (product.Value.HasNoValue) return Result.Failure<FeatureSnapshot>("Product not found.");
            return await changes.Create(product.Value.Value.InternalId, FeatureContent.Create(title, description), actor, now, token,
                authorized: () => LiveAuthority(principal));
        }, token);

    internal Task<Result<FeatureSnapshot>> EditMcp(ClaimsPrincipal principal, Guid internalId, Guid versionId, long expectedRevision,
        string title, string description, CancellationToken token) =>
        Change(principal, "feature.edit", async (actor, now) =>
        {
            var found = await store.Find(internalId, versionId, token);
            if (found.IsFailure) return Result.Failure<FeatureSnapshot>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<FeatureSnapshot>("Feature not found.");
            return await changes.Update(found.Value.Value with { Revision = expectedRevision }, FeatureContent.Create(title, description),
                actor, now, token, authorized: () => LiveAuthority(principal));
        }, token);

    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || await mcpCredentials.IsAllowed(principal)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    // Configuration is checked before transactions; callbacks only recheck live authority to avoid reopening settings under locks.
    private async Task<bool> LiveAuthority(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || mcpCredentials.IsActive(principal)) &&
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
    public Task<Result<FeatureSnapshot>> ChangeLink(ClaimsPrincipal principal, FeatureSnapshot expected, string operation, FeatureRequirementLink target, FeatureRequirementLink? replacement, CancellationToken token = default) =>
        Change(principal, operation switch { "add" => "feature.link.add", "remove" => "feature.link.remove", "retarget" => "feature.link.retarget", _ => "feature.link" },
            (actor, now) => changes.ChangeLink(expected, operation, target, replacement, actor, now, token, () => LiveAuthority(principal)), token);
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
