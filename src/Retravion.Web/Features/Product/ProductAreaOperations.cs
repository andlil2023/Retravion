using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Domain.Product;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.Product;

internal sealed class ProductAreaOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IProductAreaStore areas, IAuditSubmission submission, TimeProvider time, ILogger<ProductAreaOperations> logger)
{
    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    private static AuditActor Actor(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();

    public async Task<Result<ProductAreaRetrieval>> Areas(ClaimsPrincipal principal, Guid product, Guid? operation = null, CancellationToken token = default) =>
        await Allowed(principal) ? await areas.Retrieve(product, operation, token) : Result.Failure<ProductAreaRetrieval>("Access denied.");

    public async Task<Result<ProductAreaSnapshot>> SaveArea(ClaimsPrincipal principal, Guid operation, Guid product,
        ProductAreaSnapshot? expected, string? code, string? label, bool retire = false, CancellationToken token = default)
    {
        var action = expected is null ? "product.area.create" : retire ? "product.area.retire" : "product.area.rename";
        var actor = Actor(principal);
        var now = time.GetUtcNow();
        async Task<Result<ProductAreaSnapshot>> Deny()
        {
            try
            {
                if (await submission.SubmitAsync(new AuditInformation(actor, now, action, new AuditTarget.Unknown(), AuditActionOutcome.Failure), token) == AuditSubmissionResult.Success)
                    return Result.Failure<ProductAreaSnapshot>("Access denied.");
            }
            catch (Exception) { /* Do not log provider details or submitted values. */ }
            logger.LogError("Unauthorized Product area audit submission failed.");
            return Result.Failure<ProductAreaSnapshot>("Access denied.");
        }
        if (!await Allowed(principal) || actor is not AuditActor.Identified) return await Deny();
        var value = expected is null ? ProductArea.Create(product, code, label)
            : expected.Area.ProductInternalId != product ? Result.Failure<ProductArea>("The area belongs to another Product.")
            : retire ? Result.Success(expected.Area.Retire()) : expected.Area.Rename(label);
        if (value.IsFailure) return Result.Failure<ProductAreaSnapshot>(value.Error);
        await sessions.MutationGate.WaitAsync(token);
        try
        {
            var result = await areas.Save(operation, expected, value.Value, actor, now, () => Allowed(principal), token);
            return result.IsFailure && result.Error == "Access denied." ? await Deny() : result;
        }
        finally { sessions.MutationGate.Release(); }
    }
}
