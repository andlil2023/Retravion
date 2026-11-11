namespace Retravion.Application.AuditTrail;

public sealed record FeatureAuditDetails(Guid InternalId, Guid VersionId, int VersionNumber,
    FeatureAuditValues? Before, FeatureAuditValues After, string? Decision = null, string? Comment = null,
    Guid? SourceVersionId = null, string? Reason = null, Guid? OperationId = null);

public sealed record FeatureAuditValues(Guid ProductInternalId, string Id, string Title, string Description, string Status);
