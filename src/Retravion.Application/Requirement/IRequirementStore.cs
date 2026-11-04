using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Requirement;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.Application.Requirement;

/// <summary>An exact content version and entity-wide optimistic concurrency token.</summary>
public sealed record RequirementSnapshot(RequirementState Requirement, long Revision, int? LatestApprovedVersionNumber = null);

public sealed record RequirementReview(Guid RequirementInternalId, AuditActor Actor, DateTimeOffset OccurredAt,
    string Decision, string? Comment, string Id, string Title, string Statement, RequirementType Type, string? Rationale,
    Guid? VersionId = null, int? VersionNumber = null, Guid? SourceVersionId = null,
    string? RelationshipType = null, IReadOnlyList<RequirementUserNeedLink>? BeforeLinks = null,
    IReadOnlyList<RequirementUserNeedLink>? AfterLinks = null,
    string? ParentRelationshipType = null, RequirementParentLink? BeforeParent = null, RequirementParentLink? AfterParent = null);

public interface IRequirementStore
{
    Task<Result<IReadOnlyList<RequirementSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<RequirementSnapshot>>> Find(Guid internalId, CancellationToken cancellationToken = default, Guid? versionId = null);
    Task<Result<IReadOnlyList<RequirementSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<RequirementReview>>> History(Guid internalId, CancellationToken cancellationToken = default);
}

/// <summary>Owns atomic Product eligibility, concurrency, change, history and audit writes.</summary>
public interface IRequirementChangeTransaction
{
    Task<Result<RequirementSnapshot>> CreateSuccessor(RequirementSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<RequirementSnapshot>> Create(RequirementState requirement, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
    Task<Result<RequirementSnapshot>> Update(RequirementSnapshot expected, RequirementState replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision = null, string? comment = null, CancellationToken cancellationToken = default);
}

public static class RequirementList
{
    public static IReadOnlyList<RequirementSnapshot> Filter(IEnumerable<RequirementSnapshot> requirements,
        RequirementType? type, RequirementStatus? status, string? text) =>
        requirements.Where(row => (type is null || row.Requirement.Type == type) &&
            (status is null || row.Requirement.Status == status) &&
            (string.IsNullOrWhiteSpace(text) || row.Requirement.Id.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                row.Requirement.Title.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();
}
