using System.Collections.Immutable;
using Retravion.Domain.Revision;

namespace Retravion.Application.AuditTrail;

public sealed record RevisionAuditValues(string Identifier, string Description, string State, ImmutableArray<RevisionSelection> Selections,
    RevisionReleaseEvidence? Release = null);
public sealed record RevisionReleaseEvidence(string Actor, DateTimeOffset OccurredAt, ImmutableArray<ReleasedRevisionMember> Members);
public sealed record RevisionAuditDetails(Guid ProductInternalId, Guid InternalId, Guid OperationId,
    RevisionAuditValues? Before, RevisionAuditValues? After);
