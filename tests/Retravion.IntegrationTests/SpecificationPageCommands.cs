using System.Security.Claims;
using CSharpFunctionalExtensions;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Web.Features.Specification;

namespace Retravion.IntegrationTests;

// Ordinary test callers act from a freshly loaded page. Concurrency tests explicitly retain the earlier snapshot.
internal static class SpecificationPageCommands
{
    internal static async Task<Result<SpecificationApproval>> Approve(this SpecificationOperations operations,
        ClaimsPrincipal principal, Guid product, Guid id)
    {
        var page = await operations.Find(principal, product, id);
        return await operations.Approve(principal, product, id, page.IsSuccess ? page.Value.GetValueOrDefault() as InReviewSpecification : null);
    }

    internal static async Task<Result<DraftSpecification>> EditLinks(this SpecificationOperations operations,
        ClaimsPrincipal principal, Guid product, Guid id, IEnumerable<SpecificationLink> targets)
    {
        var page = await operations.Find(principal, product, id);
        return await operations.EditLinks(principal, product, id,
            page.IsSuccess && page.Value.HasValue ? page.Value.Value.Links : SpecificationLinks.Empty, targets);
    }
}
