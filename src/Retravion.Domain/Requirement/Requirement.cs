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
    public Guid VersionId { get; }
    public int VersionNumber { get; }
    public bool HasBeenApproved { get; }
    public string? CreationReason { get; }
    public Guid? SourceVersionId { get; }
    public abstract RequirementStatus Status { get; }

    private protected Requirement(Guid internalId, Guid productInternalId, string id, string title,
        string statement, RequirementType type, string? rationale, VersionIdentity version) =>
        (InternalId, ProductInternalId, Id, Title, Statement, Type, Rationale, VersionId, VersionNumber, HasBeenApproved, CreationReason, SourceVersionId) =
        (internalId, productInternalId, id, title, statement, type, rationale, version.Id, version.Number, version.Approved, version.Reason, version.Source);

    private protected sealed record VersionIdentity(Guid Id, int Number, bool Approved, string? Reason, Guid? Source);
    private VersionIdentity Version => new(VersionId, VersionNumber, HasBeenApproved, CreationReason, SourceVersionId);

    public static Result<Requirement> Create(Guid productInternalId, string? id, string? title, string? statement,
        RequirementType? type, string? rationale = null) =>
        Construct(Guid.NewGuid(), productInternalId, id, title, statement, type, rationale, RequirementStatus.Draft,
            new(Guid.NewGuid(), 1, false, null, null));

    public static Result<Requirement> Restore(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, RequirementType? type, string? rationale, RequirementStatus status,
        Guid? versionId = null, int versionNumber = 1, bool hasBeenApproved = false,
        string? creationReason = null, Guid? sourceVersionId = null) =>
        Construct(internalId, productInternalId, id, title, statement, type, rationale, status,
            new(versionId ?? internalId, versionNumber, hasBeenApproved || status == RequirementStatus.Approved, creationReason, sourceVersionId));

    private static Result<Requirement> Construct(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, RequirementType? type, string? rationale, RequirementStatus status, VersionIdentity version)
    {
        if (internalId == Guid.Empty || productInternalId == Guid.Empty || version.Id == Guid.Empty || version.Number < 1 ||
            (version.Number == 1 ? version.Reason is not null || version.Source is not null :
                !version.Approved || string.IsNullOrWhiteSpace(version.Reason) || version.Source is null || version.Source == Guid.Empty || version.Source == version.Id))
            return Result.Failure<Requirement>("The Requirement identity or version is invalid.");
        if (string.IsNullOrWhiteSpace(id)) return Result.Failure<Requirement>("A Requirement ID is required.");
        id = id.Trim();
        if (id.EnumerateRunes().Count() > 100)
            return Result.Failure<Requirement>("A Requirement ID must contain at most 100 Unicode characters.");
        if (string.IsNullOrWhiteSpace(title)) return Result.Failure<Requirement>("A title is required.");
        if (string.IsNullOrWhiteSpace(statement)) return Result.Failure<Requirement>("A statement is required.");
        if (type is null || !Enum.IsDefined(type.Value)) return Result.Failure<Requirement>("A valid Requirement type is required.");
        return status switch
        {
            RequirementStatus.Draft => new Draft(internalId, productInternalId, id, title, statement, type.Value, rationale, version),
            RequirementStatus.InReview => new InReview(internalId, productInternalId, id, title, statement, type.Value, rationale, version),
            RequirementStatus.Approved when version.Approved => new Approved(internalId, productInternalId, id, title, statement, type.Value, rationale, version),
            _ => Result.Failure<Requirement>("The Requirement status is invalid.")
        };
    }

    public virtual Result<Requirement> Edit(string? id, string? title, string? statement, RequirementType? type, string? rationale) =>
        Result.Failure<Requirement>("Only Draft Requirements can be edited.");
    public virtual Result<Requirement> SubmitForReview() => Result.Failure<Requirement>("Only Draft Requirements can be submitted for review.");
    public virtual Result<Requirement> Approve() => Result.Failure<Requirement>("Only Requirements in review can be approved.");
    public virtual Result<Requirement> ReturnToDraft() => Result.Failure<Requirement>("Only Requirements in review can be returned to Draft.");
    public virtual Result<Requirement> Reopen(string? reason) => Result.Failure<Requirement>("Only Approved Requirements can be reopened.");
    public virtual Result<Requirement> CreateSuccessor(string? reason) => Result.Failure<Requirement>("Only Approved Requirements can have a successor.");

    private Result<Requirement> Transition(RequirementStatus status) =>
        Construct(InternalId, ProductInternalId, Id, Title, Statement, Type, Rationale, status,
            Version with { Approved = HasBeenApproved || status == RequirementStatus.Approved });

    private sealed record Draft(Guid Identity, Guid ProductIdentity, string ManualId, string RequirementTitle,
        string RequirementStatement, RequirementType RequirementKind, string? RequirementRationale, VersionIdentity VersionInfo)
        : Requirement(Identity, ProductIdentity, ManualId, RequirementTitle, RequirementStatement, RequirementKind, RequirementRationale, VersionInfo)
    {
        public override RequirementStatus Status => RequirementStatus.Draft;
        public override Result<Requirement> Edit(string? id, string? title, string? statement, RequirementType? type, string? rationale) =>
            HasBeenApproved && id?.Trim() != Id ? Result.Failure<Requirement>("The Requirement ID is fixed after first approval.") :
                Construct(InternalId, ProductInternalId, id, title, statement, type, rationale, RequirementStatus.Draft, Version);
        public override Result<Requirement> SubmitForReview() => Transition(RequirementStatus.InReview);
    }

    private sealed record InReview(Guid Identity, Guid ProductIdentity, string ManualId, string RequirementTitle,
        string RequirementStatement, RequirementType RequirementKind, string? RequirementRationale, VersionIdentity VersionInfo)
        : Requirement(Identity, ProductIdentity, ManualId, RequirementTitle, RequirementStatement, RequirementKind, RequirementRationale, VersionInfo)
    {
        public override RequirementStatus Status => RequirementStatus.InReview;
        public override Result<Requirement> Approve() => Transition(RequirementStatus.Approved);
        public override Result<Requirement> ReturnToDraft() => Transition(RequirementStatus.Draft);
    }

    private sealed record Approved(Guid Identity, Guid ProductIdentity, string ManualId, string RequirementTitle,
        string RequirementStatement, RequirementType RequirementKind, string? RequirementRationale, VersionIdentity VersionInfo)
        : Requirement(Identity, ProductIdentity, ManualId, RequirementTitle, RequirementStatement, RequirementKind, RequirementRationale, VersionInfo)
    {
        public override RequirementStatus Status => RequirementStatus.Approved;
        public override Result<Requirement> Reopen(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<Requirement>("A reopening reason is required.") : Transition(RequirementStatus.Draft);
        public override Result<Requirement> CreateSuccessor(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<Requirement>("A change reason is required.") : VersionNumber == int.MaxValue
                ? Result.Failure<Requirement>("The version number limit has been reached.")
                : Construct(InternalId, ProductInternalId, Id, Title, Statement, Type, Rationale, RequirementStatus.Draft,
                    new(Guid.NewGuid(), VersionNumber + 1, true, reason, VersionId));
    }
}
