using System.Text.Json.Serialization;

namespace Retravion.Application.AuditTrail;

/// <summary>Audit-owned snapshots keyed by the immutable Specification identity, not the editable ID.</summary>
public sealed record SpecificationAuditDetails(Guid InternalId, SpecificationAuditValues? Before, SpecificationAuditValues? After,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NoActiveOrUncertainRunAttested = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpecificationAuditAssignment? Assignment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpecificationAuditAiReviewChange? AiReview = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpecificationAuditEvidenceChange? Evidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? MergeAttested = null);

public sealed record SpecificationAuditAssignment(string? OldAssignee, string? NewAssignee);

public sealed record SpecificationAuditAiReviewChange(SpecificationAuditAiReviewValue? Before, SpecificationAuditAiReviewValue After,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? UserApproved = null);

public sealed record SpecificationAuditAiReviewValue(string Markdown, bool IsOutdated);

public sealed record SpecificationAuditEvidenceChange(SpecificationAuditEvidenceValue? Before, SpecificationAuditEvidenceValue After);

public sealed record SpecificationAuditEvidenceValue(string Markdown, string? PullRequestUrl, bool IsOutdated);

/// <summary>Specification change values. Attachments are represented only by their immutable identifiers.</summary>
public sealed record SpecificationAuditValues(Guid ProductInternalId, string Id, string Title, string Description, string Status,
    Guid[]? AttachmentIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpecificationAuditLink[]? Links = null);

public sealed record SpecificationAuditLink(string Kind, Guid VersionId);
