using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Specification;

namespace Retravion.Application.Specification;

public interface ISpecificationWriteStore
{
    /// <summary>Creates a Draft and its initial-value audit event together, rechecking live write access before commit.</summary>
    Task<Result<DraftSpecification>> Create(DraftSpecification draft, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default);

    /// <summary>Edits persisted Draft content in the given Product and commits its audit event with the change.</summary>
    Task<Result<DraftSpecification>> EditContent(Guid productInternalId, Guid internalId, string? id, string? title,
        string? description, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the Draft's exact target set and commits its audit event with the change.</summary>
    Task<Result<DraftSpecification>> EditLinks(Guid productInternalId, Guid internalId, IEnumerable<SpecificationLink> targets,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Submits a persisted Draft for review and commits the state change with its audit event.</summary>
    Task<Result<InReviewSpecification>> SubmitForReview(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default);

    /// <summary>Returns a persisted In Review specification to Draft and audits the state change and optional reason.</summary>
    Task<Result<DraftSpecification>> ReturnToDraft(Guid productInternalId, Guid internalId, string? reason,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);
}
