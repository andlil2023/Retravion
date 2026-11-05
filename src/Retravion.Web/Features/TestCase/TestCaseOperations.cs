using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.TestCase;
using Retravion.Domain.TestCase;
using Retravion.Web.Features.Identity.Logout;

namespace Retravion.Web.Features.TestCase;

internal sealed class TestCaseOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    ITestCaseStore store, ITestCaseChangeTransaction changes, IAuditSubmission audit, TimeProvider time,
    ILogger<TestCaseOperations> logger)
{
    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    private async Task<Result<TestCaseSnapshot>> Change(ClaimsPrincipal principal, string action,
        Func<AuditActor, DateTimeOffset, Task<Result<TestCaseSnapshot>>> operation, CancellationToken token)
    {
        AuditActor actor = principal.Identity?.IsAuthenticated == true &&
            principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();
        var now = time.GetUtcNow();
        if (await Allowed(principal) && actor is AuditActor.Identified) return await operation(actor, now);
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
        string? title, string? description, string? preconditions, IEnumerable<TestCaseStep>? steps, CancellationToken token = default) =>
        Change(principal, "test-case.create", (actor, now) =>
        {
            var content = TestCaseContent.Create(title, description, preconditions, steps);
            return content.IsSuccess ? changes.Create(productInternalId, content.Value, actor, now, token)
                : Task.FromResult(Result.Failure<TestCaseSnapshot>(content.Error));
        }, token);

    public Task<Result<TestCaseSnapshot>> Edit(ClaimsPrincipal principal, TestCaseSnapshot expected,
        string? title, string? description, string? preconditions, IEnumerable<TestCaseStep>? steps, CancellationToken token = default) =>
        Change(principal, "test-case.edit", (actor, now) =>
        {
            var content = TestCaseContent.Create(title, description, preconditions, steps);
            return content.IsSuccess ? changes.Update(expected, content.Value, actor, now, token)
                : Task.FromResult(Result.Failure<TestCaseSnapshot>(content.Error));
        }, token);

    public async Task<Result<IReadOnlyList<TestCaseSnapshot>>> List(ClaimsPrincipal principal, Guid productInternalId, CancellationToken token = default) =>
        await Allowed(principal) ? await store.List(productInternalId, token) : Result.Failure<IReadOnlyList<TestCaseSnapshot>>("Access denied.");

    public async Task<Result<Maybe<TestCaseSnapshot>>> Find(ClaimsPrincipal principal, Guid internalId, Guid? versionId = null, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Find(internalId, versionId, token) : Result.Failure<Maybe<TestCaseSnapshot>>("Access denied.");
}
