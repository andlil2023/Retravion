using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Requirement;

public enum RequirementStatus { Draft, InReview, Approved }
public enum RequirementType { System, Software, Hardware, Safety, Usability, Regulatory }

public abstract record Requirement
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public string Id { get; }
    public string Title { get; }
    public string Statement { get; }
    public RequirementType Type { get; }
    public string? Rationale { get; }
    public abstract RequirementStatus Status { get; }

    private protected Requirement(Guid internalId, Guid productInternalId, string id, string title,
        string statement, RequirementType type, string? rationale) =>
        (InternalId, ProductInternalId, Id, Title, Statement, Type, Rationale) =
        (internalId, productInternalId, id, title, statement, type, rationale);

    public static Result<Requirement> Create(Guid productInternalId, string? id, string? title, string? statement,
        RequirementType? type, string? rationale = null) =>
        Construct(Guid.NewGuid(), productInternalId, id, title, statement, type, rationale, RequirementStatus.Draft);

    public static Result<Requirement> Restore(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, RequirementType? type, string? rationale, RequirementStatus status) =>
        Construct(internalId, productInternalId, id, title, statement, type, rationale, status);

    private static Result<Requirement> Construct(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, RequirementType? type, string? rationale, RequirementStatus status)
    {
        if (internalId == Guid.Empty || productInternalId == Guid.Empty)
            return Result.Failure<Requirement>("The Requirement identity is invalid.");
        if (string.IsNullOrWhiteSpace(id)) return Result.Failure<Requirement>("A Requirement ID is required.");
        id = id.Trim();
        if (id.EnumerateRunes().Count() > 100)
            return Result.Failure<Requirement>("A Requirement ID must contain at most 100 Unicode characters.");
        if (string.IsNullOrWhiteSpace(title)) return Result.Failure<Requirement>("A title is required.");
        if (string.IsNullOrWhiteSpace(statement)) return Result.Failure<Requirement>("A statement is required.");
        if (type is null || !Enum.IsDefined(type.Value))
            return Result.Failure<Requirement>("A valid Requirement type is required.");
        return status switch
        {
            RequirementStatus.Draft => new Draft(internalId, productInternalId, id, title, statement, type.Value, rationale),
            RequirementStatus.InReview => new InReview(internalId, productInternalId, id, title, statement, type.Value, rationale),
            RequirementStatus.Approved => new Approved(internalId, productInternalId, id, title, statement, type.Value, rationale),
            _ => Result.Failure<Requirement>("The Requirement status is invalid.")
        };
    }

    public virtual Result<Requirement> Edit(string? id, string? title, string? statement, RequirementType? type, string? rationale) =>
        Result.Failure<Requirement>("Only Draft Requirements can be edited.");
    public virtual Result<Requirement> SubmitForReview() => Result.Failure<Requirement>("Only Draft Requirements can be submitted for review.");
    public virtual Result<Requirement> Approve() => Result.Failure<Requirement>("Only Requirements in review can be approved.");
    public virtual Result<Requirement> ReturnToDraft() => Result.Failure<Requirement>("This Requirement is already Draft.");

    private Result<Requirement> Transition(RequirementStatus status) =>
        Construct(InternalId, ProductInternalId, Id, Title, Statement, Type, Rationale, status);

    private sealed record Draft(Guid Identity, Guid ProductIdentity, string ManualId, string RequirementTitle,
        string RequirementStatement, RequirementType RequirementKind, string? RequirementRationale)
        : Requirement(Identity, ProductIdentity, ManualId, RequirementTitle, RequirementStatement, RequirementKind, RequirementRationale)
    {
        public override RequirementStatus Status => RequirementStatus.Draft;
        public override Result<Requirement> Edit(string? id, string? title, string? statement, RequirementType? type, string? rationale) =>
            Construct(InternalId, ProductInternalId, id, title, statement, type, rationale, RequirementStatus.Draft);
        public override Result<Requirement> SubmitForReview() => Transition(RequirementStatus.InReview);
    }

    private sealed record InReview(Guid Identity, Guid ProductIdentity, string ManualId, string RequirementTitle,
        string RequirementStatement, RequirementType RequirementKind, string? RequirementRationale)
        : Requirement(Identity, ProductIdentity, ManualId, RequirementTitle, RequirementStatement, RequirementKind, RequirementRationale)
    {
        public override RequirementStatus Status => RequirementStatus.InReview;
        public override Result<Requirement> Approve() => Transition(RequirementStatus.Approved);
        public override Result<Requirement> ReturnToDraft() => Transition(RequirementStatus.Draft);
    }

    private sealed record Approved(Guid Identity, Guid ProductIdentity, string ManualId, string RequirementTitle,
        string RequirementStatement, RequirementType RequirementKind, string? RequirementRationale)
        : Requirement(Identity, ProductIdentity, ManualId, RequirementTitle, RequirementStatement, RequirementKind, RequirementRationale)
    {
        public override RequirementStatus Status => RequirementStatus.Approved;
        public override Result<Requirement> ReturnToDraft() => Transition(RequirementStatus.Draft);
    }
}
