using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Edit;
using Retravion.Application.Product.Description;
using Retravion.Application.Product.Retrieve;
using Retravion.Application.Product.Status;
using Retravion.Domain.Product;
using Retravion.Web.Features.Identity.Logout;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Web.Features.Product;

// Recheck live session and authorization on every circuit callback, not only page entry.
internal sealed class ProductOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IProductStore store, IProductChangeTransaction changes, IAuditSubmission submission, TimeProvider time,
    ILogger<ProductOperations> logger)
{
    private async Task<bool> Allowed(ClaimsPrincipal actor) =>
        sessions.IsActive(actor.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(actor, "CompletedPasswordReplacement")).Succeeded;

    private static AuditActor Actor(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();

    private async Task<Result<ProductState>> Change(ClaimsPrincipal actor, string action,
        Func<IProductStore, Task<Result<ProductState>>> change, CancellationToken cancellationToken)
    {
        if (await Allowed(actor))
            return await changes.Execute(Actor(actor), time.GetUtcNow(), change, cancellationToken);
        try
        {
            // No submitted identifiers, expected snapshots, or field values are included in denied events.
            var information = new AuditInformation(Actor(actor), time.GetUtcNow(), action,
                new AuditTarget.Unknown(), AuditActionOutcome.Failure);
            if (await submission.SubmitAsync(information, cancellationToken) == AuditSubmissionResult.Success)
                return Result.Failure<ProductState>("Access denied.");
        }
        catch (Exception)
        {
            // Provider exceptions may contain sensitive values; never pass them to operational logging.
        }
        logger.LogError("Unauthorized Product-change audit submission failed.");
        return Result.Failure<ProductState>("Access denied.");
    }

    public Task<Result<ProductState>> Create(ClaimsPrincipal actor, string? id, string? name, ProductKind? kind, string? description,
        CancellationToken cancellationToken = default) => Change(actor, "product.create",
        s => new CreateProduct(s).Execute(id, name, kind, description, cancellationToken), cancellationToken);

    public async Task<Result<IReadOnlyList<ProductState>>> List(ClaimsPrincipal actor, CancellationToken cancellationToken = default) =>
        await Allowed(actor) ? await store.List(cancellationToken) : Result.Failure<IReadOnlyList<ProductState>>("Access denied.");

    public Task<Result<ProductState>> Retire(ClaimsPrincipal actor, ProductState expected, CancellationToken cancellationToken = default) =>
        Change(actor, "product.retire", async s =>
        {
            var retired = expected.Retire();
            if (retired.IsFailure) return retired;
            var saved = await s.Update(expected, retired.Value, cancellationToken);
            return saved.IsSuccess ? retired : Result.Failure<ProductState>(saved.Error);
        }, cancellationToken);

    public Task<Result<ProductState>> Edit(ClaimsPrincipal actor, ProductState expected, string? id, string? name,
        ProductKind? kind, string? markdown, bool reactivate, CancellationToken cancellationToken = default) =>
        Change(actor, reactivate ? "product.reactivate" : "product.edit",
            s => new EditProduct(s).Execute(expected, id, name, kind, markdown, reactivate, cancellationToken), cancellationToken);

    public async Task<Result<ProductState>> Retrieve(ClaimsPrincipal actor, string? id,
        CancellationToken cancellationToken = default) => await Allowed(actor)
        ? await new RetrieveProduct(store).Execute(id, cancellationToken)
        : Result.Failure<ProductState>("Access denied.");

    public Task<Result<ProductState>> UpdateDescription(ClaimsPrincipal actor, string id, Guid expectedInternalId, string expectedDescription,
        string? markdown, CancellationToken cancellationToken = default) => Change(actor, "product.edit",
        s => new UpdateProductDescription(s).Execute(id, expectedInternalId, expectedDescription, markdown, cancellationToken), cancellationToken);

    public Task<Result<ProductState>> ChangeStatus(ClaimsPrincipal actor, string id, Guid expectedInternalId, bool active,
        CancellationToken cancellationToken = default) => Change(actor, active ? "product.reactivate" : "product.retire",
        s => new ChangeProductStatus(s).Execute(id, expectedInternalId, active, cancellationToken), cancellationToken);
}
