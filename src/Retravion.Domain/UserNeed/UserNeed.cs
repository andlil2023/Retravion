using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.UserNeed;

public enum UserNeedStatus { Draft, InReview, Approved }

// The concrete states control which edits and transitions are available.
public abstract record UserNeed
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public string Id { get; }
    public string Title { get; }
    public string Statement { get; }
    public Guid VersionId { get; }
    public int VersionNumber { get; }
    public bool HasBeenApproved { get; }
    public string? CreationReason { get; }
    public Guid? SourceVersionId { get; }
    public abstract UserNeedStatus Status { get; }

    private protected UserNeed(Guid internalId, Guid productInternalId, string id, string title, string statement,
        VersionIdentity version) =>
        (InternalId, ProductInternalId, Id, Title, Statement, VersionId, VersionNumber, HasBeenApproved, CreationReason, SourceVersionId) =
        (internalId, productInternalId, id, title, statement, version.Id, version.Number, version.Approved, version.Reason, version.Source);

    private protected sealed record VersionIdentity(Guid Id, int Number, bool Approved, string? Reason, Guid? Source);
    private VersionIdentity Version => new(VersionId, VersionNumber, HasBeenApproved, CreationReason, SourceVersionId);

    public static Result<UserNeed> Create(Guid productInternalId, string? id, string? title, string? statement) =>
        Construct(Guid.NewGuid(), productInternalId, id, title, statement, UserNeedStatus.Draft,
            new(Guid.NewGuid(), 1, false, null, null));

    public static Result<UserNeed> Restore(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, UserNeedStatus status, Guid? versionId = null, int versionNumber = 1,
        bool hasBeenApproved = false, string? creationReason = null, Guid? sourceVersionId = null) =>
        Construct(internalId, productInternalId, id, title, statement, status,
            new(versionId ?? internalId, versionNumber, hasBeenApproved || status == UserNeedStatus.Approved, creationReason, sourceVersionId));

    private static Result<UserNeed> Construct(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, UserNeedStatus status, VersionIdentity version)
    {
        if (internalId == Guid.Empty || productInternalId == Guid.Empty || version.Id == Guid.Empty || version.Number < 1 ||
            (version.Number == 1 ? version.Reason is not null || version.Source is not null :
                !version.Approved || string.IsNullOrWhiteSpace(version.Reason) || version.Source is null || version.Source == Guid.Empty || version.Source == version.Id))
            return Result.Failure<UserNeed>("The User Need identity or version is invalid.");
        if (string.IsNullOrWhiteSpace(id)) return Result.Failure<UserNeed>("A User Need ID is required.");
        id = id.Trim();
        if (id.EnumerateRunes().Count() > 100)
            return Result.Failure<UserNeed>("A User Need ID must contain at most 100 Unicode characters.");
        if (string.IsNullOrWhiteSpace(title)) return Result.Failure<UserNeed>("A title is required.");
        if (string.IsNullOrWhiteSpace(statement)) return Result.Failure<UserNeed>("A statement is required.");
        return status switch
        {
            UserNeedStatus.Draft => new Draft(internalId, productInternalId, id, title, statement, version),
            UserNeedStatus.InReview => new InReview(internalId, productInternalId, id, title, statement, version),
            UserNeedStatus.Approved when version.Approved => new Approved(internalId, productInternalId, id, title, statement, version),
            _ => Result.Failure<UserNeed>("The User Need status is invalid.")
        };
    }

    public virtual Result<UserNeed> Edit(string? id, string? title, string? statement) =>
        Result.Failure<UserNeed>("Only Draft User Needs can be edited.");
    public virtual Result<UserNeed> SubmitForReview() => Result.Failure<UserNeed>("Only Draft User Needs can be submitted for review.");
    public virtual Result<UserNeed> Approve() => Result.Failure<UserNeed>("Only User Needs in review can be approved.");
    public virtual Result<UserNeed> ReturnToDraft() => Result.Failure<UserNeed>("Only User Needs in review can be returned to Draft.");
    public virtual Result<UserNeed> Reopen(string? reason) => Result.Failure<UserNeed>("Only Approved User Needs can be reopened.");
    public virtual Result<UserNeed> CreateSuccessor(string? reason) => Result.Failure<UserNeed>("Only Approved User Needs can have a successor.");

    private Result<UserNeed> Transition(UserNeedStatus status) => Construct(InternalId, ProductInternalId, Id, Title, Statement,
        status, Version with { Approved = HasBeenApproved || status == UserNeedStatus.Approved });

    private sealed record Draft(Guid Identity, Guid ProductIdentity, string ManualId, string NeedTitle, string NeedStatement, VersionIdentity VersionInfo)
        : UserNeed(Identity, ProductIdentity, ManualId, NeedTitle, NeedStatement, VersionInfo)
    {
        public override UserNeedStatus Status => UserNeedStatus.Draft;
        public override Result<UserNeed> Edit(string? id, string? title, string? statement) =>
            HasBeenApproved && id?.Trim() != Id ? Result.Failure<UserNeed>("The User Need ID is fixed after first approval.") :
                Construct(InternalId, ProductInternalId, id, title, statement, UserNeedStatus.Draft, Version);
        public override Result<UserNeed> SubmitForReview() => Transition(UserNeedStatus.InReview);
    }

    private sealed record InReview(Guid Identity, Guid ProductIdentity, string ManualId, string NeedTitle, string NeedStatement, VersionIdentity VersionInfo)
        : UserNeed(Identity, ProductIdentity, ManualId, NeedTitle, NeedStatement, VersionInfo)
    {
        public override UserNeedStatus Status => UserNeedStatus.InReview;
        public override Result<UserNeed> Approve() => Transition(UserNeedStatus.Approved);
        public override Result<UserNeed> ReturnToDraft() => Transition(UserNeedStatus.Draft);
    }

    private sealed record Approved(Guid Identity, Guid ProductIdentity, string ManualId, string NeedTitle, string NeedStatement, VersionIdentity VersionInfo)
        : UserNeed(Identity, ProductIdentity, ManualId, NeedTitle, NeedStatement, VersionInfo)
    {
        public override UserNeedStatus Status => UserNeedStatus.Approved;
        public override Result<UserNeed> Reopen(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<UserNeed>("A reopening reason is required.") : Transition(UserNeedStatus.Draft);
        public override Result<UserNeed> CreateSuccessor(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<UserNeed>("A change reason is required.") : VersionNumber == int.MaxValue
                ? Result.Failure<UserNeed>("The version number limit has been reached.")
                : Construct(InternalId, ProductInternalId, Id, Title, Statement, UserNeedStatus.Draft,
                    new(Guid.NewGuid(), VersionNumber + 1, true, reason, VersionId));
    }
}
