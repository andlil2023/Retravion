using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Description;
using Retravion.Application.Product.Retrieve;
using Retravion.Application.Product.Status;
using Retravion.Domain.Product;
using Retravion.Web.Features.Identity.Logout;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.Web.Features.Product;

// Recheck live session and authorization on every circuit callback, not only page entry.
internal sealed class ProductOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    CreateProduct create, RetrieveProduct retrieve, ChangeProductStatus status, UpdateProductDescription description)
{
    private async Task<bool> Allowed(ClaimsPrincipal actor) =>
        sessions.IsActive(actor.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(actor, "CompletedPasswordReplacement")).Succeeded;

    public async Task<Result<ProductState>> Create(ClaimsPrincipal actor, string? id, string? name, ProductKind? kind, string? description,
        CancellationToken cancellationToken = default) => await Allowed(actor)
        ? await create.Execute(id, name, kind, description, cancellationToken)
        : Result.Failure<ProductState>("Access denied.");

    public async Task<Result<ProductState>> Retrieve(ClaimsPrincipal actor, string? id,
        CancellationToken cancellationToken = default) => await Allowed(actor)
        ? await retrieve.Execute(id, cancellationToken)
        : Result.Failure<ProductState>("Access denied.");

    public async Task<Result<ProductState>> UpdateDescription(ClaimsPrincipal actor, string id, Guid expectedInternalId, string expectedDescription,
        string? markdown, CancellationToken cancellationToken = default) => await Allowed(actor)
        ? await description.Execute(id, expectedInternalId, expectedDescription, markdown, cancellationToken)
        : Result.Failure<ProductState>("Access denied.");

    public async Task<Result<ProductState>> ChangeStatus(ClaimsPrincipal actor, string id, Guid expectedInternalId, bool active,
        CancellationToken cancellationToken = default) => await Allowed(actor)
        ? await status.Execute(id, expectedInternalId, active, cancellationToken)
        : Result.Failure<ProductState>("Access denied.");
}
