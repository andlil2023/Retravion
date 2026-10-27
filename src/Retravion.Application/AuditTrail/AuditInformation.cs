namespace Retravion.Application.AuditTrail;

/// <summary>Information about an action performed or attempted in the application.</summary>
public sealed record AuditInformation
{
    public AuditActor Actor { get; }
    public DateTimeOffset OccurredAt { get; }
    public string Action { get; }
    public AuditTarget Target { get; }
    public AuditActionOutcome Outcome { get; }

    public AuditInformation(
        AuditActor actor,
        DateTimeOffset occurredAt,
        string action,
        AuditTarget target,
        AuditActionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown action outcome.");
        }

        Actor = actor;
        OccurredAt = occurredAt;
        Action = action;
        Target = target;
        Outcome = outcome;
    }
}
