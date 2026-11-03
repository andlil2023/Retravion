using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;

namespace Retravion.Application.AiWorkflow;

public sealed record AiWorkflowSettings(bool Enabled, long Revision);

/// <summary>Application-wide eligibility and credential generation; changes and their audit are atomic.</summary>
public interface IAiWorkflowSettingsStore
{
    Task<Result<AiWorkflowSettings>> Load(CancellationToken cancellationToken = default);
    Task<Result<AiWorkflowSettings>> Save(AiWorkflowSettings expected, bool enabled, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}
