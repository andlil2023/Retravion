using Retravion.Domain.Specification;

namespace Retravion.Application.Specification;

public sealed record SpecificationDetail(DevelopmentSpecification Specification,
    IReadOnlyList<SpecificationAssignmentEntry> AssignmentHistory)
{
    public string? Assignee => AssignmentHistory.Count == 0 ? null : AssignmentHistory[^1].NewAssignee;
}

public sealed record SpecificationAssignmentEntry(string Actor, DateTimeOffset OccurredAt, string? OldAssignee, string? NewAssignee);
