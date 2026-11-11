namespace Retravion.Application.AuditTrail;

/// <summary>Information about an action performed or attempted in the application.</summary>
public sealed record AuditInformation
{
    public AuditActor Actor { get; }
    public DateTimeOffset OccurredAt { get; }
    public string Action { get; }
    public AuditTarget Target { get; }
    public AuditActionOutcome Outcome { get; }
    public ProductAuditDetails? ProductChange { get; }
    public UserNeedAuditDetails? UserNeedChange { get; }
    public RequirementAuditDetails? RequirementChange { get; }
    public AiWorkflowAuditDetails? AiWorkflowChange { get; }
    public TestCaseAuditDetails? TestCaseChange { get; }
    public RevisionAuditDetails? RevisionChange { get; }
    public ProductAreaAuditDetails? ProductAreaChange { get; }
    public FeatureAuditDetails? FeatureChange { get; }

    public AuditInformation(
        AuditActor actor,
        DateTimeOffset occurredAt,
        string action,
        AuditTarget target,
        AuditActionOutcome outcome,
        ProductAuditDetails? productChange = null,
        UserNeedAuditDetails? userNeedChange = null,
        RequirementAuditDetails? requirementChange = null,
        AiWorkflowAuditDetails? aiWorkflowChange = null,
        TestCaseAuditDetails? testCaseChange = null,
        RevisionAuditDetails? revisionChange = null,
        ProductAreaAuditDetails? productAreaChange = null,
        FeatureAuditDetails? featureChange = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(target);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown action outcome.");
        }

        Actor = actor;
        OccurredAt = occurredAt;
        Action = action;
        Target = target;
        Outcome = outcome;
        ProductChange = productChange;
        UserNeedChange = userNeedChange;
        RequirementChange = requirementChange;
        AiWorkflowChange = aiWorkflowChange;
        TestCaseChange = testCaseChange;
        RevisionChange = revisionChange;
        ProductAreaChange = productAreaChange;
        FeatureChange = featureChange;
    }
}
