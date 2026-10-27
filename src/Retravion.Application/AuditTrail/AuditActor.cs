namespace Retravion.Application.AuditTrail;

/// <summary>The actor of an audited action, which may be unknown.</summary>
public abstract record AuditActor
{
    private AuditActor() { }

    public sealed record Identified : AuditActor
    {
        public string Identifier { get; }

        public Identified(string identifier)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
            Identifier = identifier;
        }
    }

    public sealed record Unknown : AuditActor;
}
