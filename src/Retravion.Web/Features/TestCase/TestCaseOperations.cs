using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.TestCase;
using Retravion.Domain.TestCase;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;

namespace Retravion.Web.Features.TestCase;

internal sealed class TestCaseOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    ITestCaseStore store, ITestCaseChangeTransaction changes, IAuditSubmission audit, TimeProvider time,
    ILogger<TestCaseOperations> logger, McpCredentials mcpCredentials)
{
    internal Task<bool> CanRead(ClaimsPrincipal principal) => Allowed(principal);

    internal Task<Result<TestCaseSnapshot>> DeniedMcpMutation(ClaimsPrincipal principal, string action, CancellationToken token) =>
        Change(principal, action, (_, _) => Task.FromResult(Result.Failure<TestCaseSnapshot>("Access denied.")), token);

    internal Task<Result<TestCaseSnapshot>> CreateMcp(ClaimsPrincipal principal, Retravion.Application.Product.IProductStore products,
        string productId, string? title, string? description, string? preconditions, IEnumerable<TestCaseStep>? steps, CancellationToken token) =>
        Change(principal, "test-case.create", async (actor, now) =>
        {
            var product = await products.Find(productId, token);
            if (product.IsFailure) return Result.Failure<TestCaseSnapshot>(product.Error);
            if (product.Value.HasNoValue) return Result.Failure<TestCaseSnapshot>("Product not found.");
            var content = TestCaseContent.Create(title, description, preconditions, steps);
            return content.IsFailure ? Result.Failure<TestCaseSnapshot>(content.Error) :
                await changes.Create(product.Value.Value.InternalId, content.Value, actor, now, token, authorized: () => LiveAuthority(principal));
        }, token);

    internal Task<Result<TestCaseSnapshot>> EditMcp(ClaimsPrincipal principal, Guid internalId, Guid versionId, long expectedRevision,
        string title, string description, string preconditions, IEnumerable<TestCaseStep> steps, CancellationToken token) =>
        Change(principal, "test-case.edit", async (actor, now) =>
        {
            var found = await store.Find(internalId, versionId, token);
            if (found.IsFailure) return Result.Failure<TestCaseSnapshot>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<TestCaseSnapshot>("Test Case not found.");
            var content = TestCaseContent.Create(title, description, preconditions, steps);
            return content.IsFailure ? Result.Failure<TestCaseSnapshot>(content.Error) :
                await changes.Update(found.Value.Value with { Revision = expectedRevision }, content.Value, actor, now, token,
                    authorized: () => LiveAuthority(principal));
        }, token);

    internal Task<Result<TestCaseSnapshot>> ClassifyMcp(ClaimsPrincipal principal, Guid internalId, Guid versionId,
        long expectedRevision, Guid? areaId, bool confirmed, CancellationToken token) =>
        Change(principal, "test-case.edit", async (actor, now) =>
        {
            if (!confirmed) return Result.Failure<TestCaseSnapshot>("Explicit human confirmation of old/new classification is required.");
            if (internalId == Guid.Empty || versionId == Guid.Empty || expectedRevision <= 0 || areaId == Guid.Empty)
                return Result.Failure<TestCaseSnapshot>("Exact identities and a positive expectedRevision are required.");
            var found = await store.Find(internalId, versionId, token);
            if (found.IsFailure) return Result.Failure<TestCaseSnapshot>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<TestCaseSnapshot>("Test Case not found.");
            var expected = found.Value.Value;
            if (expected.Revision != expectedRevision)
                return Result.Failure<TestCaseSnapshot>("The version changed. Retrieve it again before editing.");
            return await changes.Update(expected, expected.TestCase.Content, actor, now, token,
                changeArea: true, areaId: areaId, authorized: () => LiveAuthority(principal));
        }, token);

    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || await mcpCredentials.IsAllowed(principal)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    // Settings share database locks with business writes; do not reopen that store inside a transaction.
    // Allowed checks configuration at the operation boundary; callbacks recheck live credential authority.
    private async Task<bool> LiveAuthority(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || mcpCredentials.IsActive(principal)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    private async Task<Result<TestCaseSnapshot>> Change(ClaimsPrincipal principal, string action,
        Func<AuditActor, DateTimeOffset, Task<Result<TestCaseSnapshot>>> operation, CancellationToken token)
    {
        AuditActor actor = principal.Identity?.IsAuthenticated == true &&
            principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
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
            if (await audit.SubmitAsync(new AuditInformation(actor, now, action, new AuditTarget.Unknown(), AuditActionOutcome.Failure), token)
                == AuditSubmissionResult.Success) return Result.Failure<TestCaseSnapshot>("Access denied.");
        }
        catch (Exception) { /* Denial remains effective; provider errors and submitted values are not logged. */ }
        logger.LogError("Unauthorized Test Case change audit submission failed.");
        return Result.Failure<TestCaseSnapshot>("Access denied.");
    }

    public Task<Result<TestCaseSnapshot>> Create(ClaimsPrincipal principal, Guid productInternalId,
        string? title, string? description, string? preconditions, IEnumerable<TestCaseStep>? steps, CancellationToken token = default, Guid? areaId = null, Guid? operationId = null) =>
        Change(principal, "test-case.create", (actor, now) =>
        {
            var content = TestCaseContent.Create(title, description, preconditions, steps);
            return content.IsSuccess ? changes.Create(productInternalId, content.Value, actor, now, token, areaId, operationId, () => LiveAuthority(principal))
                : Task.FromResult(Result.Failure<TestCaseSnapshot>(content.Error));
        }, token);

    public Task<Result<TestCaseSnapshot>> Edit(ClaimsPrincipal principal, TestCaseSnapshot expected,
        string? title, string? description, string? preconditions, IEnumerable<TestCaseStep>? steps, CancellationToken token = default,
        bool changeArea = false, Guid? areaId = null, Guid? operationId = null) =>
        Change(principal, "test-case.edit", (actor, now) =>
        {
            var content = TestCaseContent.Create(title, description, preconditions, steps);
            return content.IsSuccess ? changes.Update(expected, content.Value, actor, now, token, changeArea, areaId, operationId, () => LiveAuthority(principal))
                : Task.FromResult(Result.Failure<TestCaseSnapshot>(content.Error));
        }, token);

    public Task<Result<TestCaseSnapshot>> Review(ClaimsPrincipal principal, TestCaseSnapshot expected, string decision,
        string? comment, CancellationToken token = default) => Change(principal, decision switch
        {
            "InReview" => "test-case.review.InReview", "Approved" => "test-case.review.Approved",
            "Draft" => "test-case.review.Draft", _ => "test-case.review"
        }, (actor, now) => changes.Review(expected, decision, comment, actor, now, token), token);

    public Task<Result<TestCaseSnapshot>> CreateSuccessor(ClaimsPrincipal principal, TestCaseSnapshot expected, string? reason,
        CancellationToken token = default, Guid? operationId = null) => Change(principal, "test-case.version.create",
        (actor, now) => changes.CreateSuccessor(expected, reason, actor, now, token, operationId, () => LiveAuthority(principal)), token);

    public Task<Result<TestCaseSnapshot>> Reopen(ClaimsPrincipal principal, TestCaseSnapshot expected, string? reason,
        CancellationToken token = default) => Change(principal, "test-case.reopen",
            (actor, now) => changes.Reopen(expected, reason, actor, now, token), token);

    public Task<Result<TestCaseSnapshot>> ChangeLink(ClaimsPrincipal principal, TestCaseSnapshot expected, string operation,
        TestCaseRequirementLink target, TestCaseRequirementLink? replacement = null, bool confirmed = false, CancellationToken token = default) =>
        Change(principal, operation switch
        {
            "add" => "test-case.link.add", "remove" => "test-case.link.remove", "retarget" => "test-case.link.retarget", _ => "test-case.link"
        }, (actor, now) => operation is "remove" or "retarget" && !confirmed
            ? Task.FromResult(Result.Failure<TestCaseSnapshot>("Explicit confirmation of both exact endpoints is required."))
            : changes.ChangeLink(expected, operation, target, replacement, actor, now, token, () => LiveAuthority(principal)), token);

    public async Task<Result<IReadOnlyList<TestCaseSnapshot>>> Versions(ClaimsPrincipal principal, Guid internalId, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Versions(internalId, token) : Result.Failure<IReadOnlyList<TestCaseSnapshot>>("Access denied.");

    public async Task<Result<IReadOnlyList<TestCaseHistory>>> History(ClaimsPrincipal principal, Guid internalId, CancellationToken token = default) =>
        await Allowed(principal) ? await store.History(internalId, token) : Result.Failure<IReadOnlyList<TestCaseHistory>>("Access denied.");

    public async Task<Result<Maybe<TestCaseAuditDetails>>> Outcome(ClaimsPrincipal principal, Guid product, Guid operationId, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Outcome(product, operationId, token) : Result.Failure<Maybe<TestCaseAuditDetails>>("Access denied.");

    public async Task<Result<IReadOnlyList<TestCaseSnapshot>>> List(ClaimsPrincipal principal, Guid productInternalId, CancellationToken token = default) =>
        await Allowed(principal) ? await store.List(productInternalId, token) : Result.Failure<IReadOnlyList<TestCaseSnapshot>>("Access denied.");

    public async Task<Result<Maybe<TestCaseSnapshot>>> Find(ClaimsPrincipal principal, Guid internalId, Guid? versionId = null, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Find(internalId, versionId, token) : Result.Failure<Maybe<TestCaseSnapshot>>("Access denied.");
}
