namespace Retravion.Application.AuditTrail;

/// <summary>Event-time User Need values and optional review decision; absent on denied attempts.</summary>
public sealed record UserNeedAuditDetails(Guid InternalId, UserNeedAuditValues? Before, UserNeedAuditValues After,
    string? Decision, string? Comment);

public sealed record UserNeedAuditValues(Guid ProductInternalId, string Id, string Title, string Statement, string Status);
