using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Feature;
using FeatureRecord = Retravion.Domain.Feature.Feature;

namespace Retravion.Application.Feature;

public sealed record FeatureSnapshot(FeatureRecord Feature, long Revision, int? LatestApprovedVersionNumber = null);
public sealed record FeatureHistory(AuditActor Actor, DateTimeOffset OccurredAt, string Action, FeatureAuditDetails Change);

public interface IFeatureStore
{
    Task<Result<Maybe<FeatureAuditDetails>>> Outcome(Guid productInternalId, Guid operationId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<FeatureSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<FeatureSnapshot>>> Find(Guid internalId, Guid? versionId = null, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<FeatureSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<FeatureHistory>>> History(Guid internalId, CancellationToken cancellationToken = default);
}

public interface IFeatureChangeTransaction
{
    Task<Result<FeatureSnapshot>> Create(Guid productInternalId, FeatureContent content, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null);
    Task<Result<FeatureSnapshot>> Update(FeatureSnapshot expected, FeatureContent content, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null);
    Task<Result<FeatureSnapshot>> Review(FeatureSnapshot expected, string decision, string? comment, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Func<Task<bool>>? authorized = null);
    Task<Result<FeatureSnapshot>> Reopen(FeatureSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Func<Task<bool>>? authorized = null);
    Task<Result<FeatureSnapshot>> CreateSuccessor(FeatureSnapshot expected, string? reason, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null);
    Task<Result<FeatureSnapshot>> ChangeLink(FeatureSnapshot expected, string operation, FeatureRequirementLink target,
        FeatureRequirementLink? replacement, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default, Func<Task<bool>>? authorized = null);
}
