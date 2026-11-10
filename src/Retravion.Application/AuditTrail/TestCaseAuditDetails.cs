using System.Collections.Immutable;
using Retravion.Domain.TestCase;

namespace Retravion.Application.AuditTrail;

public sealed record TestCaseAuditDetails(Guid InternalId, Guid VersionId, int VersionNumber,
    TestCaseAuditValues? Before, TestCaseAuditValues After, string? Decision = null, string? Comment = null,
    Guid? SourceVersionId = null, string? Reason = null, Guid? OperationId = null);

public sealed record TestCaseAuditValues(Guid ProductInternalId, string Id, string Title, string Description,
    string Preconditions, string Status, ImmutableArray<TestCaseStep> Steps,
    IReadOnlyList<TestCaseRequirementLink>? Links = null, Retravion.Domain.Product.AreaEvidence? Area = null);
