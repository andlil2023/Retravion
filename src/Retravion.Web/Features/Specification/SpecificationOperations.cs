using System.Security.Claims;
using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Product;

namespace Retravion.Web.Features.Specification;

internal sealed class SpecificationOperations(ProductOperations products, ISpecificationReadStore store,
    ISpecificationWriteStore writes, SessionTicketStore sessions, IAuditSubmission submission, TimeProvider time,
    ILogger<SpecificationOperations> logger)
{
    public Task<Result<DraftSpecification>> Create(ClaimsPrincipal principal, Guid productInternalId, string? id,
        string? title, string? description, CancellationToken cancellationToken = default) =>
        Change(principal, "specification.create", async (actor, now, authorized) =>
        {
            var draft = DraftSpecification.Create(productInternalId, id, title, description);
            return draft.IsSuccess ? await writes.Create(draft.Value, actor, now, authorized, cancellationToken) : draft;
        }, cancellationToken);

    public Task<Result<DraftSpecification>> EditContent(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        string? id, string? title, string? description, CancellationToken cancellationToken = default) =>
        Change(principal, "specification.edit-content", (actor, now, authorized) =>
            writes.EditContent(productInternalId, internalId, id, title, description, actor, now, authorized, cancellationToken),
            cancellationToken);

    public Task<Result<DraftSpecification>> EditLinks(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        IEnumerable<SpecificationLink> targets, CancellationToken cancellationToken = default) =>
        Change(principal, "specification.edit-links", (actor, now, authorized) =>
            writes.EditLinks(productInternalId, internalId, targets, actor, now, authorized, cancellationToken), cancellationToken);

    public async Task<Result> ChangeAssignment(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        string? assignee, CancellationToken cancellationToken = default) =>
        await Change(principal, "specification.change-assignment", async (actor, now, authorized) =>
        {
            var result = await writes.ChangeAssignment(productInternalId, internalId, assignee, actor, now, authorized, cancellationToken);
            return result.IsSuccess ? Result.Success(true) : Result.Failure<bool>(result.Error);
        }, cancellationToken);

    public Task<Result<SpecificationAiReview>> SubmitAiReview(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        SpecificationAiReviewInput input, bool userApproved, CancellationToken cancellationToken = default) =>
        Change(principal, "specification.submit-ai-review", (actor, now, authorized) =>
            writes.SubmitAiReview(productInternalId, internalId, input, userApproved, actor, now, authorized, cancellationToken),
            cancellationToken);

    public Task<Result<InReviewSpecification>> SubmitForReview(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default) =>
        Change(principal, "specification.submit-for-review", (actor, now, authorized) =>
            writes.SubmitForReview(productInternalId, internalId, actor, now, authorized, cancellationToken), cancellationToken);

    public Task<Result<SpecificationApproval>> Approve(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default) =>
        Change(principal, "specification.approve", (actor, now, authorized) =>
            writes.Approve(productInternalId, internalId, actor, now, authorized, cancellationToken), cancellationToken);

    public Task<Result<DraftSpecification>> Reopen(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        string? reason, bool noActiveOrUncertainRunAttested, CancellationToken cancellationToken = default) =>
        Change(principal, "specification.reopen", (actor, now, authorized) =>
            writes.Reopen(productInternalId, internalId, reason, noActiveOrUncertainRunAttested, actor, now, authorized, cancellationToken),
            cancellationToken);

    public Task<Result<DraftSpecification>> ReturnToDraft(ClaimsPrincipal principal, Guid productInternalId, Guid internalId,
        string? reason = null, CancellationToken cancellationToken = default) =>
        Change(principal, "specification.return-to-draft", (actor, now, authorized) =>
            writes.ReturnToDraft(productInternalId, internalId, reason, actor, now, authorized, cancellationToken), cancellationToken);

    public async Task<Result<IReadOnlyList<SpecificationVersionChoice>>> ListVersions(ClaimsPrincipal actor, Guid productInternalId,
        SpecificationLinkKind kind, CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.ListVersions(productInternalId, kind, cancellationToken)
        : Result.Failure<IReadOnlyList<SpecificationVersionChoice>>("Access denied.");

    public async Task<Result<Maybe<SpecificationVersionChoice>>> ResolveVersion(ClaimsPrincipal actor, Guid productInternalId,
        SpecificationLink target, CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.ResolveVersion(productInternalId, target, cancellationToken)
        : Result.Failure<Maybe<SpecificationVersionChoice>>("Access denied.");

    private async Task<Result<T>> Change<T>(ClaimsPrincipal principal, string action,
        Func<AuditActor.Identified, DateTimeOffset, Func<Task<bool>>, Task<Result<T>>> change, CancellationToken cancellationToken)
    {
        const string denied = "Access denied.";
        AuditActor actor = principal.Identity?.IsAuthenticated == true &&
            principal.FindFirstValue(ClaimTypes.NameIdentifier) is { } id && !string.IsNullOrWhiteSpace(id)
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();
        var now = time.GetUtcNow();
        Task<bool> Allowed() => products.CanView(principal);
        if (actor is AuditActor.Identified identified && await Allowed())
        {
            // Use the same gate as session revocation, and recheck live access inside the transaction.
            await sessions.MutationGate.WaitAsync(cancellationToken);
            try
            {
                if (await Allowed())
                {
                    var result = await change(identified, now, Allowed);
                    if (result.IsSuccess || result.Error != denied) return result;
                }
            }
            finally { sessions.MutationGate.Release(); }
        }

        try
        {
            var information = new AuditInformation(actor, now, action, new AuditTarget.Unknown(), AuditActionOutcome.Failure);
            if (await submission.SubmitAsync(information, cancellationToken) == AuditSubmissionResult.Success)
                return Result.Failure<T>(denied);
        }
        catch (Exception)
        {
            // Denials contain no submitted values; provider exceptions must not enter operational logs.
        }
        logger.LogError("Unauthorized Specification change audit submission failed.");
        return Result.Failure<T>(denied);
    }

    public async Task<Result<IReadOnlyList<DevelopmentSpecification>>> List(ClaimsPrincipal actor, Guid productInternalId,
        CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.List(productInternalId, cancellationToken)
        : Result.Failure<IReadOnlyList<DevelopmentSpecification>>("Access denied.");

    public async Task<Result<Maybe<DevelopmentSpecification>>> Find(ClaimsPrincipal actor, Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.Find(productInternalId, internalId, cancellationToken)
        : Result.Failure<Maybe<DevelopmentSpecification>>("Access denied.");

    public async Task<Result<Maybe<SpecificationDetail>>> FindDetail(ClaimsPrincipal actor, Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.FindDetail(productInternalId, internalId, cancellationToken)
        : Result.Failure<Maybe<SpecificationDetail>>("Access denied.");
}
