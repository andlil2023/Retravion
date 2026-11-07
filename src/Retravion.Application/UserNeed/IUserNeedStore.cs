using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.Application.UserNeed;

/// <summary>A stored snapshot and concurrency token. Tokens change on every actual mutation.</summary>
public sealed record UserNeedSnapshot(Need Need, long Revision, int? LatestApprovedVersionNumber = null);

/// <summary>Immutable decision-time evidence, separate from the audit event.</summary>
public sealed record UserNeedReview(Guid NeedInternalId, AuditActor Actor, DateTimeOffset OccurredAt,
    string Decision, string? Comment, string Title, string Statement, Guid? VersionId = null, int? VersionNumber = null,
    string? ManualId = null, Guid? SourceVersionId = null);

/// <summary>Immutable source evidence captured when an independent User Need is created.</summary>
public sealed record UserNeedProvenance(Guid SourceProductId, Guid SourceNeedId, Guid SourceVersionId,
    int SourceVersionNumber, string SourceStatus, string SourceManualId, string SourceTitle, string SourceStatement);

public interface IUserNeedStore
{
    Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<UserNeedSnapshot>>> Find(Guid internalId, CancellationToken cancellationToken = default, Guid? versionId = null);
    Task<Result<IReadOnlyList<UserNeedSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<UserNeedReview>>> History(Guid internalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<UserNeedProvenance>>> Provenance(Guid internalId, CancellationToken cancellationToken = default);
}

/// <summary>Owns atomic Product eligibility, optimistic concurrency, change, history and audit writes.</summary>
public interface IUserNeedChangeTransaction
{
    Task<Result<UserNeedSnapshot>> CreateSuccessor(UserNeedSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<UserNeedSnapshot>> Create(Need need, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
    Task<Result<UserNeedSnapshot>> Derive(UserNeedSnapshot expectedSource, Need destination, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<UserNeedSnapshot>> Update(UserNeedSnapshot expected, Need replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision = null, string? comment = null,
        CancellationToken cancellationToken = default);
}
