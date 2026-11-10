namespace Retravion.Application.AuditTrail;

public sealed record ProductAreaAuditDetails(Guid ProductInternalId, Guid InternalId, Guid OperationId,
    ProductAreaAuditValues? Before, ProductAreaAuditValues After);

public sealed record ProductAreaAuditValues(string Code, string Label, bool IsActive);
