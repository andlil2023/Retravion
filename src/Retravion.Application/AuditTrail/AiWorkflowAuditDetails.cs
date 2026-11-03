namespace Retravion.Application.AuditTrail;

/// <summary>Event-time global MCP switch values; absent on denied attempts.</summary>
public sealed record AiWorkflowAuditDetails(bool Before, bool After);
