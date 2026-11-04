using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Requirement;

namespace Retravion.Application.Requirement;

/// <summary>Requirement-owned exact links. Mutations include concurrency, history and audit in one commit.</summary>
public interface IRequirementLinkStore
{
    Task<Result<IReadOnlyList<RequirementUserNeedLink>>> Links(Guid requirementInternalId, Guid versionId,
        CancellationToken cancellationToken = default);
    Task<Result<Maybe<RequirementParentLink>>> Parent(Guid requirementInternalId, Guid versionId,
        CancellationToken cancellationToken = default);
    Task<Result<RequirementSnapshot>> AddParent(RequirementSnapshot expected, RequirementParentLink target,
        AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<RequirementSnapshot>> RemoveParent(RequirementSnapshot expected, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<RequirementSnapshot>> ReplaceParent(RequirementSnapshot expected, RequirementParentLink target,
        AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<RequirementSnapshot>> ChangeLink(RequirementSnapshot expected, RequirementUserNeedLink target, bool add,
        AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}
