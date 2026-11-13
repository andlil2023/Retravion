using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authorization;
using Retravion.Application.AuditTrail;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.Web.Features.Requirement;

internal sealed class RequirementOperations(IAuthorizationService authorization, SessionTicketStore sessions,
    IRequirementStore store, IRequirementChangeTransaction changes, IAuditSubmission audit, TimeProvider time,
    ILogger<RequirementOperations> logger, McpCredentials mcpCredentials, IRequirementLinkStore links)
{
    internal Task<Result<RequirementSnapshot>> ClassifyMcp(ClaimsPrincipal principal, Guid internalId, Guid versionId,
        long expectedRevision, Guid? areaId, bool confirmed, CancellationToken token) =>
        Change(principal, "requirement.edit", async (actor, now) =>
        {
            if (!confirmed) return Result.Failure<RequirementSnapshot>("Explicit human confirmation of old/new classification is required.");
            if (internalId == Guid.Empty || versionId == Guid.Empty || expectedRevision <= 0 || areaId == Guid.Empty)
                return Result.Failure<RequirementSnapshot>("Exact identities and a positive expectedRevision are required.");
            var found = await store.Find(internalId, token, versionId);
            if (found.IsFailure) return Result.Failure<RequirementSnapshot>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<RequirementSnapshot>("Requirement not found.");
            var expected = found.Value.Value;
            if (expected.Revision != expectedRevision)
                return Result.Failure<RequirementSnapshot>("The version changed. Retrieve it again before editing.");
            var classified = expected.Requirement.Classify(areaId);
            return classified.IsFailure ? Result.Failure<RequirementSnapshot>(classified.Error) :
                await changes.Update(expected, classified.Value, actor, now, cancellationToken: token,
                    authorized: () => LiveAuthority(principal));
        }, token);

    private async Task<bool> Allowed(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || await mcpCredentials.IsAllowed(principal)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    // Transaction callbacks must not open the settings store: it shares the Requirement lock.
    // MCP configuration remains checked by Allowed at its existing request boundary; live session,
    // credential and role authority can be rechecked without taking a second database connection.
    private async Task<bool> LiveAuthority(ClaimsPrincipal principal) =>
        sessions.IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)) &&
        (!principal.HasClaim(c => c.Type == McpCredentials.CredentialClaim) || mcpCredentials.IsActive(principal)) &&
        (await authorization.AuthorizeAsync(principal, "CompletedPasswordReplacement")).Succeeded;

    private static AuditActor Actor(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true && principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id
            ? new AuditActor.Identified(id) : new AuditActor.Unknown();

    private async Task<Result<RequirementSnapshot>> Change(ClaimsPrincipal principal, string action,
        Func<AuditActor, DateTimeOffset, Task<Result<RequirementSnapshot>>> operation, CancellationToken token)
    {
        var actor = Actor(principal);
        var now = time.GetUtcNow();
        await sessions.MutationGate.WaitAsync(token);
        try
        {
            if (await Allowed(principal) && actor is AuditActor.Identified)
            {
                var result = await operation(actor, now);
                return result.IsFailure && result.Error == "Access denied." ? await Deny(actor, now, action, token) : result;
            }
            return await Deny(actor, now, action, token);
        }
        finally { sessions.MutationGate.Release(); }
    }

    private async Task<Result<RequirementSnapshot>> Deny(AuditActor actor, DateTimeOffset now, string action, CancellationToken token)
    {
        try
        {
            if (await audit.SubmitAsync(new AuditInformation(actor, now, action, new AuditTarget.Unknown(),
                    AuditActionOutcome.Failure), token) == AuditSubmissionResult.Success)
                return Result.Failure<RequirementSnapshot>("Access denied.");
        }
        catch (Exception) { /* No submitted values or provider details in operational logging. */ }
        logger.LogError("Unauthorized Requirement change audit submission failed.");
        return Result.Failure<RequirementSnapshot>("Access denied.");
    }

    internal Task<Result<RequirementSnapshot>> DeniedMcpMutation(ClaimsPrincipal principal, string action, CancellationToken token) =>
        Deny(Actor(principal), time.GetUtcNow(), action, token);

    public Task<Result<RequirementSnapshot>> Create(ClaimsPrincipal principal, Guid product, string? id, string? title,
        string? statement, RequirementType? type, string? rationale, CancellationToken token = default, Guid? areaId = null, Guid? operationId = null) =>
        Change(principal, "requirement.create", (actor, now) =>
        {
            var requirement = RequirementState.Create(product, id, title, statement, type, rationale, areaId);
            return requirement.IsSuccess ? changes.Create(requirement.Value, actor, now, token, operationId, () => LiveAuthority(principal))
                : Task.FromResult(Result.Failure<RequirementSnapshot>(requirement.Error));
        }, token);

    public Task<Result<RequirementSnapshot>> Derive(ClaimsPrincipal principal, RequirementSnapshot source, Guid destinationProduct,
        string? id, string? title, string? statement, RequirementType? type, string? rationale, CancellationToken token = default) =>
        Change(principal, "requirement.derive", (actor, now) =>
        {
            var destination = RequirementState.Create(destinationProduct, id, title, statement, type, rationale);
            return destination.IsSuccess ? changes.Derive(source, destination.Value, actor, now, token)
                : Task.FromResult(Result.Failure<RequirementSnapshot>(destination.Error));
        }, token);

    public async Task<Result<Maybe<RequirementProvenance>>> Provenance(ClaimsPrincipal principal, Guid id, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Provenance(id, token) : Result.Failure<Maybe<RequirementProvenance>>("Access denied.");

    public async Task<Result<IReadOnlyList<RequirementSnapshot>>> CopiesOf(ClaimsPrincipal principal, Guid version, CancellationToken token = default) =>
        await Allowed(principal) ? await store.CopiesOf(version, token) : Result.Failure<IReadOnlyList<RequirementSnapshot>>("Access denied.");

    public Task<Result<RequirementSnapshot>> Edit(ClaimsPrincipal principal, RequirementSnapshot expected, string? id,
        string? title, string? statement, RequirementType? type, string? rationale, CancellationToken token = default,
        bool changeArea = false, Guid? areaId = null, Guid? operationId = null) =>
        Change(principal, "requirement.edit", (actor, now) =>
        {
            var requirement = expected.Requirement.Edit(id, title, statement, type, rationale)
                .Bind(value => changeArea ? value.Classify(areaId) : Result.Success(value));
            return requirement.IsSuccess ? changes.Update(expected, requirement.Value, actor, now, cancellationToken: token,
                    operationId: operationId, authorized: () => LiveAuthority(principal))
                : Task.FromResult(Result.Failure<RequirementSnapshot>(requirement.Error));
        }, token);

    internal Task<Result<RequirementSnapshot>> EditDraft(ClaimsPrincipal principal, Guid internalId, long expectedRevision,
        string? id, string? title, string? statement, RequirementType? type, string? rationale, CancellationToken token = default) =>
        Change(principal, "requirement.edit", async (actor, now) =>
        {
            var found = await store.Find(internalId, token);
            if (found.IsFailure) return Result.Failure<RequirementSnapshot>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<RequirementSnapshot>("Requirement not found.");
            var expected = found.Value.Value;
            if (expected.Revision != expectedRevision)
                return Result.Failure<RequirementSnapshot>("The Requirement changed. Retrieve it again before editing.");
            var requirement = expected.Requirement.Edit(id, title, statement, type, rationale);
            return requirement.IsSuccess ? await changes.Update(expected, requirement.Value, actor, now, cancellationToken: token)
                : Result.Failure<RequirementSnapshot>(requirement.Error);
        }, token);

    internal Task<Result<RequirementSnapshot>> AddParent(ClaimsPrincipal principal, RequirementSnapshot expected,
        RequirementParentLink target, CancellationToken token = default) =>
        Change(principal, "requirement.parent.add", (actor, now) => links.AddParent(expected, target, actor, now, token, () => LiveAuthority(principal)), token);

    internal Task<Result<RequirementSnapshot>> RemoveParent(ClaimsPrincipal principal, RequirementSnapshot expected,
        CancellationToken token = default) =>
        Change(principal, "requirement.parent.remove", (actor, now) => links.RemoveParent(expected, actor, now, token, () => LiveAuthority(principal)), token);

    internal Task<Result<RequirementSnapshot>> ReplaceParent(ClaimsPrincipal principal, RequirementSnapshot expected,
        RequirementParentLink target, CancellationToken token = default) =>
        Change(principal, "requirement.parent.replace", (actor, now) => links.ReplaceParent(expected, target, actor, now, token, () => LiveAuthority(principal)), token);

    internal Task<Result<RequirementSnapshot>> RetargetParent(ClaimsPrincipal principal, RequirementSnapshot expected,
        RequirementParentLink target, RequirementParentLink replacement, CancellationToken token = default) =>
        Change(principal, "requirement.parent.replace", async (actor, now) =>
        {
            if (target.RequirementInternalId != replacement.RequirementInternalId)
                return Result.Failure<RequirementSnapshot>("Retargeting must select a version of the same parent Requirement.");
            var current = await links.Parent(expected.Requirement.InternalId, expected.Requirement.VersionId, token);
            if (current.IsFailure) return Result.Failure<RequirementSnapshot>(current.Error);
            if (current.Value.HasNoValue || current.Value.Value != target)
                return Result.Failure<RequirementSnapshot>("The original parent link changed. Retrieve it again.");
            // ReplaceParent validates the captured owner revision again inside its atomic, cycle-safe transaction.
            return await links.ReplaceParent(expected, replacement, actor, now, token, () => LiveAuthority(principal));
        }, token);

    internal async Task<Result<IReadOnlyList<RequirementVersionReference>>> IncomingUserNeed(ClaimsPrincipal principal, Guid id,
        Guid versionId, CancellationToken token = default) => await Allowed(principal)
        ? await links.IncomingUserNeed(id, versionId, token) : Result.Failure<IReadOnlyList<RequirementVersionReference>>("Access denied.");

    internal async Task<Result<IReadOnlyList<RequirementVersionReference>>> Children(ClaimsPrincipal principal, Guid id,
        Guid versionId, CancellationToken token = default) => await Allowed(principal)
        ? await links.Children(id, versionId, token) : Result.Failure<IReadOnlyList<RequirementVersionReference>>("Access denied.");

    internal async Task<Result<Maybe<RequirementParentLink>>> Parent(ClaimsPrincipal principal, Guid id,
        Guid versionId, CancellationToken token = default) => await Allowed(principal)
        ? await links.Parent(id, versionId, token) : Result.Failure<Maybe<RequirementParentLink>>("Access denied.");

    internal Task<Result<RequirementSnapshot>> AddLink(ClaimsPrincipal principal, RequirementSnapshot expected,
        RequirementUserNeedLink target, CancellationToken token = default) =>
        Change(principal, "requirement.link.add", (actor, now) => links.ChangeLink(expected, target, true, actor, now, token, () => LiveAuthority(principal)), token);

    internal Task<Result<RequirementSnapshot>> RetargetLink(ClaimsPrincipal principal, RequirementSnapshot expected,
        RequirementUserNeedLink target, RequirementUserNeedLink replacement, CancellationToken token = default) =>
        Change(principal, "requirement.link.retarget", (actor, now) => links.RetargetLink(expected, target, replacement, actor, now, token, () => LiveAuthority(principal)), token);

    internal Task<Result<RequirementSnapshot>> RemoveLink(ClaimsPrincipal principal, RequirementSnapshot expected,
        RequirementUserNeedLink target, CancellationToken token = default) =>
        Change(principal, "requirement.link.remove", (actor, now) => links.ChangeLink(expected, target, false, actor, now, token, () => LiveAuthority(principal)), token);

    internal async Task<Result<IReadOnlyList<RequirementUserNeedLink>>> Links(ClaimsPrincipal principal, Guid id,
        Guid versionId, CancellationToken token = default) => await Allowed(principal)
        ? await links.Links(id, versionId, token) : Result.Failure<IReadOnlyList<RequirementUserNeedLink>>("Access denied.");

    public Task<Result<RequirementSnapshot>> CreateSuccessor(ClaimsPrincipal principal, RequirementSnapshot expected, string? reason,
        CancellationToken token = default, Guid? operationId = null) => Change(principal, "requirement.version.create", (actor, now) =>
            changes.CreateSuccessor(expected, reason, actor, now, token, operationId, () => LiveAuthority(principal)), token);

    public Task<Result<RequirementSnapshot>> Reopen(ClaimsPrincipal principal, RequirementSnapshot expected, string? reason,
        CancellationToken token = default) => Change(principal, "requirement.reopen", (actor, now) =>
    {
        var requirement = expected.Requirement.Reopen(reason);
        return requirement.IsSuccess ? changes.Update(expected, requirement.Value, actor, now, "Reopened", reason, token)
            : Task.FromResult(Result.Failure<RequirementSnapshot>(requirement.Error));
    }, token);

    public Task<Result<RequirementSnapshot>> Review(ClaimsPrincipal principal, RequirementSnapshot expected, string decision,
        string? comment, CancellationToken token = default) => Change(principal, decision switch
        {
            "InReview" => "requirement.review.InReview", "Approved" => "requirement.review.Approved",
            "Draft" => "requirement.review.Draft", _ => "requirement.review"
        }, (actor, now) =>
        {
            var requirement = decision switch
            {
                "InReview" => expected.Requirement.SubmitForReview(),
                "Approved" => expected.Requirement.Approve(),
                "Draft" => expected.Requirement.ReturnToDraft(),
                _ => Result.Failure<RequirementState>("Invalid review decision.")
            };
            return requirement.IsSuccess ? changes.Update(expected, requirement.Value, actor, now, decision, comment, token)
                : Task.FromResult(Result.Failure<RequirementSnapshot>(requirement.Error));
        }, token);

    public async Task<Result<Maybe<RequirementAuditDetails>>> Outcome(ClaimsPrincipal principal, Guid product, Guid operationId, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Outcome(product, operationId, token) : Result.Failure<Maybe<RequirementAuditDetails>>("Access denied.");

    internal Task<bool> CanRead(ClaimsPrincipal principal) => Allowed(principal);

    internal async Task<Result<RequirementHierarchy>> Hierarchy(ClaimsPrincipal principal, Guid product, CancellationToken token = default)
    {
        var selected = await List(principal, product, token);
        if (selected.IsFailure) return Result.Failure<RequirementHierarchy>(selected.Error);
        var byId = selected.Value.ToDictionary(row => row.Requirement.InternalId);
        var exactParents = new Dictionary<Guid, RequirementSnapshot>();
        var items = new List<RequirementHierarchyItem>();
        foreach (var row in selected.Value)
        {
            var parent = await Parent(principal, row.Requirement.InternalId, row.Requirement.VersionId, token);
            if (parent.IsFailure) return Result.Failure<RequirementHierarchy>(parent.Error);
            RequirementSnapshot? reference = null;
            if (parent.Value.HasValue)
            {
                var endpoint = parent.Value.Value;
                if (!byId.TryGetValue(endpoint.RequirementInternalId, out var displayed))
                    return Result.Failure<RequirementHierarchy>("The Requirement hierarchy is incomplete. Reload the list.");
                if (displayed.Requirement.VersionId == endpoint.VersionId) reference = displayed;
                else if (!exactParents.TryGetValue(endpoint.VersionId, out reference))
                {
                    var found = await Find(principal, endpoint.RequirementInternalId, token, endpoint.VersionId);
                    if (found.IsFailure) return Result.Failure<RequirementHierarchy>(found.Error);
                    if (found.Value.HasNoValue)
                        return Result.Failure<RequirementHierarchy>("A linked parent Requirement version is unavailable. Reload the list.");
                    reference = found.Value.Value;
                    exactParents.Add(endpoint.VersionId, reference);
                }
            }
            items.Add(new(row, reference));
        }
        // Do not publish a partially read hierarchy or data fetched after session revocation.
        return await Allowed(principal) ? RequirementHierarchy.Create(items) : Result.Failure<RequirementHierarchy>("Access denied.");
    }

    public async Task<Result<IReadOnlyList<RequirementSnapshot>>> List(ClaimsPrincipal principal, Guid product, CancellationToken token = default) =>
        await Allowed(principal) ? await store.List(product, token) : Result.Failure<IReadOnlyList<RequirementSnapshot>>("Access denied.");
    public async Task<Result<Maybe<RequirementSnapshot>>> Find(ClaimsPrincipal principal, Guid id, CancellationToken token = default, Guid? versionId = null) =>
        await Allowed(principal) ? await store.Find(id, token, versionId) : Result.Failure<Maybe<RequirementSnapshot>>("Access denied.");
    public async Task<Result<IReadOnlyList<RequirementSnapshot>>> Versions(ClaimsPrincipal principal, Guid id, CancellationToken token = default) =>
        await Allowed(principal) ? await store.Versions(id, token) : Result.Failure<IReadOnlyList<RequirementSnapshot>>("Access denied.");
    public async Task<Result<IReadOnlyList<RequirementReview>>> History(ClaimsPrincipal principal, Guid id, CancellationToken token = default) =>
        await Allowed(principal) ? await store.History(id, token) : Result.Failure<IReadOnlyList<RequirementReview>>("Access denied.");
}
