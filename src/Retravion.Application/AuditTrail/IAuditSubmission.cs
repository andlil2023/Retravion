namespace Retravion.Application.AuditTrail;

/// <summary>The common boundary for submitting audit information.</summary>
public interface IAuditSubmission
{
    /// <summary>
    /// Reports submission success or failure separately from the audited action's outcome.
    /// This contract alone provides no storage or delivery implementation.
    /// </summary>
    Task<AuditSubmissionResult> SubmitAsync(
        AuditInformation information,
        CancellationToken cancellationToken = default);
}
