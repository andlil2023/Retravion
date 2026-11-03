namespace Retravion.Application.AuditTrail;

public sealed record RequirementAuditValues(Guid ProductInternalId, string Id, string Title, string Statement,
    string Type, string? Rationale, string Status);

public sealed record RequirementAuditDetails(Guid InternalId, RequirementAuditValues? Before, RequirementAuditValues After,
    string? Decision, string? Comment);
