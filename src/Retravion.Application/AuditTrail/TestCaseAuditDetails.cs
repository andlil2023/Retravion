using System.Collections.Immutable;
using Retravion.Domain.TestCase;

namespace Retravion.Application.AuditTrail;

public sealed record TestCaseAuditDetails(Guid InternalId, Guid VersionId, int VersionNumber,
    TestCaseAuditValues? Before, TestCaseAuditValues After);

public sealed record TestCaseAuditValues(Guid ProductInternalId, string Id, string Title, string Description,
    string Preconditions, string Status, ImmutableArray<TestCaseStep> Steps);
