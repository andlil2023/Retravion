using System.Collections.Immutable;
using Retravion.Domain.Revision;

namespace Retravion.Application.AuditTrail;

public sealed record RevisionAuditValues(string Identifier, string Description, string State, ImmutableArray<RevisionSelection> Selections);
public sealed record RevisionAuditDetails(Guid ProductInternalId, Guid InternalId, Guid OperationId,
    RevisionAuditValues? Before, RevisionAuditValues? After);
