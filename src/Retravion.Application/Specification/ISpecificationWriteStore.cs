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

    /// <summary>Assigns an active account or clears responsibility with null, committing history and audit together.</summary>
    Task<Result> ChangeAssignment(Guid productInternalId, Guid internalId, string? assignee,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Submits or replaces the user-approved current AI review and commits its audit event with the change.</summary>
    Task<Result<SpecificationAiReview>> SubmitAiReview(Guid productInternalId, Guid internalId, SpecificationAiReviewInput input,
        bool userApproved, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the single Approved evidence report and optional PR URL (null clears it), making the evidence current.</summary>
    Task<Result<SpecificationEvidence>> SaveEvidence(Guid productInternalId, Guid internalId, string? markdown,
        string? pullRequestUrl, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Explicitly confirms reviewed evidence after approval without replacing the report or PR URL.</summary>
    Task<Result<SpecificationEvidence>> ConfirmEvidenceCurrent(Guid productInternalId, Guid internalId, SpecificationEvidence? reviewedEvidence,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Submits a persisted Draft for review and commits the state change with its audit event.</summary>
    Task<Result<InReviewSpecification>> SubmitForReview(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default);

    /// <summary>Approves an In Review specification and reports missing link types after commit. Exact versions are locked through commit.</summary>
    Task<Result<SpecificationApproval>> Approve(Guid productInternalId, Guid internalId, AuditActor.Identified actor,
        DateTimeOffset occurredAt, Func<Task<bool>> authorized, CancellationToken cancellationToken = default);

    /// <summary>Finalizes an Approved specification only when the human-attested PR URL matches the current evidence
    /// under the Specification lock, committing the transition, any assignment clearing and audit together.</summary>
    Task<Result<ImplementedSpecification>> Implement(Guid productInternalId, Guid internalId, bool mergeAttested,
        string attestedPullRequestUrl, AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Returns an Approved specification to Draft and audits the reason and affirmative human run attestation.</summary>
    Task<Result<DraftSpecification>> Reopen(Guid productInternalId, Guid internalId, string? reason,
        bool noActiveOrUncertainRunAttested, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default);

    /// <summary>Deletes an eligible Specification and its owned rows with a durable audit snapshot.
    /// A matching confirmed identity and affirmative human run attestation are both required.</summary>
    Task<Result> Delete(Guid productInternalId, Guid internalId, Guid? confirmedInternalId, bool noActiveOrUncertainRunAttested,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a persisted In Review specification to Draft and audits the state change and optional reason.</summary>
    Task<Result<DraftSpecification>> ReturnToDraft(Guid productInternalId, Guid internalId, string? reason,
        AuditActor.Identified actor, DateTimeOffset occurredAt, Func<Task<bool>> authorized,
        CancellationToken cancellationToken = default);
}
