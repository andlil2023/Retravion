namespace Retravion.Domain.TestCase;

/// <summary>An owned endpoint: both the Requirement entity and its exact version are immutable identities.</summary>
public sealed record TestCaseRequirementLink(Guid RequirementInternalId, Guid RequirementVersionId);
