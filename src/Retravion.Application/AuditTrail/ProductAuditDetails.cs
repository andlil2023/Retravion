namespace Retravion.Application.AuditTrail;

/// <summary>Exact persisted values; creation has no previous state. Absent on denied attempts.</summary>
public sealed record ProductAuditDetails(Guid InternalId, ProductAuditValues? Before, ProductAuditValues After);

public sealed record ProductAuditValues(string Id, string Name, string Kind, string Description, bool IsActive);
