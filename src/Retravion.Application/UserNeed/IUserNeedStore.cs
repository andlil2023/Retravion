using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.Application.UserNeed;

/// <summary>A stored snapshot and concurrency token. Tokens change on every actual mutation.</summary>
public sealed record UserNeedSnapshot(Need Need, long Revision);

/// <summary>Immutable decision-time evidence, separate from the audit event.</summary>
public sealed record UserNeedReview(Guid NeedInternalId, AuditActor Actor, DateTimeOffset OccurredAt,
    string Decision, string? Comment, string Title, string Statement);

public interface IUserNeedStore
{
    Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<UserNeedSnapshot>>> Find(Guid internalId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<UserNeedReview>>> History(Guid internalId, CancellationToken cancellationToken = default);
}

/// <summary>Owns atomic Product eligibility, optimistic concurrency, change, history and audit writes.</summary>
public interface IUserNeedChangeTransaction
{
    Task<Result<UserNeedSnapshot>> Create(Need need, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
    Task<Result<UserNeedSnapshot>> Update(UserNeedSnapshot expected, Need replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision = null, string? comment = null,
        CancellationToken cancellationToken = default);
}
