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

    public async Task<Result<IReadOnlyList<DraftSpecification>>> List(ClaimsPrincipal actor, Guid productInternalId,
        CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.List(productInternalId, cancellationToken)
        : Result.Failure<IReadOnlyList<DraftSpecification>>("Access denied.");

    public async Task<Result<Maybe<DraftSpecification>>> Find(ClaimsPrincipal actor, Guid productInternalId, Guid internalId,
        CancellationToken cancellationToken = default) => await products.CanView(actor)
        ? await store.Find(productInternalId, internalId, cancellationToken)
        : Result.Failure<Maybe<DraftSpecification>>("Access denied.");
}
