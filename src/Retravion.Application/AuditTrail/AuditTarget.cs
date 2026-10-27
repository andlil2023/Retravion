namespace Retravion.Application.AuditTrail;

/// <summary>Distinguishes an identified target, an unknown target, and no applicable target.</summary>
public abstract record AuditTarget
{
    private AuditTarget() { }

    public sealed record Identified : AuditTarget
    {
        public string Identifier { get; }

        public Identified(string identifier)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            Identifier = identifier;
        }
    }

    public sealed record Unknown : AuditTarget;

    public sealed record NotApplicable : AuditTarget;
}
