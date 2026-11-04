namespace Retravion.Application.AuditTrail;

public sealed record RequirementAuditValues(Guid ProductInternalId, string Id, string Title, string Statement,
    string Type, string? Rationale, string Status);

public sealed record RequirementAuditDetails(Guid InternalId, RequirementAuditValues? Before, RequirementAuditValues After,
    string? Decision, string? Comment, Guid? VersionId = null, int? VersionNumber = null,
    Guid? SourceVersionId = null, string? Reason = null);
