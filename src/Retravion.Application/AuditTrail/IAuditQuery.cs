namespace Retravion.Application.AuditTrail;

/// <summary>Read-only selection of recorded audit events. Null filters match all values.</summary>
public sealed record AuditFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    AuditActor? Actor = null,
    string? Action = null,
    AuditActionOutcome? Outcome = null,
    AuditTarget? Target = null);

public interface IAuditQuery
{
    Task<IReadOnlyList<AuditInformation>> FindAsync(AuditFilter filter, CancellationToken cancellationToken = default);
}
