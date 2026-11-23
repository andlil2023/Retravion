namespace Retravion.Application.AuditTrail;

/// <summary>Audit-owned snapshots keyed by the immutable Specification identity, not the editable ID.</summary>
public sealed record SpecificationAuditDetails(Guid InternalId, SpecificationAuditValues? Before, SpecificationAuditValues? After);

/// <summary>Specification change values. Attachments are represented only by their immutable identifiers.</summary>
public sealed record SpecificationAuditValues(Guid ProductInternalId, string Id, string Title, string Description, string Status,
    Guid[]? AttachmentIds = null);
