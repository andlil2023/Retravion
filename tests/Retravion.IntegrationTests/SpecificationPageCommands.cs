using System.Security.Claims;
using CSharpFunctionalExtensions;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Web.Features.Specification;

namespace Retravion.IntegrationTests;

// Ordinary test callers act from a freshly loaded page. Concurrency tests explicitly retain the earlier snapshot.
internal static class SpecificationPageCommands
{
    private static async Task<long> DisplayedRevision(SpecificationOperations operations, ClaimsPrincipal principal, Guid product, Guid id)
    {
        var page = await operations.FindDetail(principal, product, id);
        return page.IsSuccess && page.Value.HasValue ? page.Value.Value.ReviewBasisRevision : -1;
    }

    internal static async Task<Result<SpecificationAiReview>> SubmitAiReview(this SpecificationOperations ops, ClaimsPrincipal principal,
        Guid product, Guid id, SpecificationAiReviewInput input, bool approved) =>
        await ops.SubmitAiReview(principal, product, id, await DisplayedRevision(ops, principal, product, id), input, approved);

    internal static async Task<Result<SpecificationEvidence>> SaveEvidence(this SpecificationOperations ops, ClaimsPrincipal principal,
        Guid product, Guid id, SpecificationEvidence? expected, string? markdown, string? url) =>
        await ops.SaveEvidence(principal, product, id, await DisplayedRevision(ops, principal, product, id), expected, markdown, url);

    internal static async Task<Result<SpecificationEvidence>> ConfirmEvidenceCurrent(this SpecificationOperations ops, ClaimsPrincipal principal,
        Guid product, Guid id, SpecificationEvidence? expected) =>
        await ops.ConfirmEvidenceCurrent(principal, product, id, await DisplayedRevision(ops, principal, product, id), expected);

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
