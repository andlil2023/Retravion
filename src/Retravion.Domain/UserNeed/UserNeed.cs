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
    public abstract UserNeedStatus Status { get; }

    private protected UserNeed(Guid internalId, Guid productInternalId, string id, string title, string statement) =>
        (InternalId, ProductInternalId, Id, Title, Statement) = (internalId, productInternalId, id, title, statement);

    public static Result<UserNeed> Create(Guid productInternalId, string? id, string? title, string? statement) =>
        Construct(Guid.NewGuid(), productInternalId, id, title, statement, UserNeedStatus.Draft);

    public static Result<UserNeed> Restore(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, UserNeedStatus status) => Construct(internalId, productInternalId, id, title, statement, status);

    private static Result<UserNeed> Construct(Guid internalId, Guid productInternalId, string? id, string? title,
        string? statement, UserNeedStatus status)
    {
        if (internalId == Guid.Empty || productInternalId == Guid.Empty)
            return Result.Failure<UserNeed>("The User Need identity is invalid.");
        if (string.IsNullOrWhiteSpace(id)) return Result.Failure<UserNeed>("A User Need ID is required.");
        id = id.Trim();
        if (id.EnumerateRunes().Count() > 100)
            return Result.Failure<UserNeed>("A User Need ID must contain at most 100 Unicode characters.");
        if (string.IsNullOrWhiteSpace(title)) return Result.Failure<UserNeed>("A title is required.");
        if (string.IsNullOrWhiteSpace(statement)) return Result.Failure<UserNeed>("A statement is required.");
        return status switch
        {
            UserNeedStatus.Draft => new Draft(internalId, productInternalId, id, title, statement),
            UserNeedStatus.InReview => new InReview(internalId, productInternalId, id, title, statement),
            UserNeedStatus.Approved => new Approved(internalId, productInternalId, id, title, statement),
            _ => Result.Failure<UserNeed>("The User Need status is invalid.")
        };
    }

    public virtual Result<UserNeed> Edit(string? id, string? title, string? statement) =>
        Result.Failure<UserNeed>("Only Draft User Needs can be edited.");
    public virtual Result<UserNeed> SubmitForReview() => Result.Failure<UserNeed>("Only Draft User Needs can be submitted for review.");
    public virtual Result<UserNeed> Approve() => Result.Failure<UserNeed>("Only User Needs in review can be approved.");
    public virtual Result<UserNeed> ReturnToDraft() => Result.Failure<UserNeed>("This User Need is already Draft.");

    private Result<UserNeed> Transition(UserNeedStatus status) => Construct(InternalId, ProductInternalId, Id, Title, Statement, status);

    private sealed record Draft(Guid Identity, Guid ProductIdentity, string ManualId, string NeedTitle, string NeedStatement)
        : UserNeed(Identity, ProductIdentity, ManualId, NeedTitle, NeedStatement)
    {
        public override UserNeedStatus Status => UserNeedStatus.Draft;
        public override Result<UserNeed> Edit(string? id, string? title, string? statement) =>
            Construct(InternalId, ProductInternalId, id, title, statement, UserNeedStatus.Draft);
        public override Result<UserNeed> SubmitForReview() => Transition(UserNeedStatus.InReview);
    }

    private sealed record InReview(Guid Identity, Guid ProductIdentity, string ManualId, string NeedTitle, string NeedStatement)
        : UserNeed(Identity, ProductIdentity, ManualId, NeedTitle, NeedStatement)
    {
        public override UserNeedStatus Status => UserNeedStatus.InReview;
        public override Result<UserNeed> Approve() => Transition(UserNeedStatus.Approved);
        public override Result<UserNeed> ReturnToDraft() => Transition(UserNeedStatus.Draft);
    }

    private sealed record Approved(Guid Identity, Guid ProductIdentity, string ManualId, string NeedTitle, string NeedStatement)
        : UserNeed(Identity, ProductIdentity, ManualId, NeedTitle, NeedStatement)
    {
        public override UserNeedStatus Status => UserNeedStatus.Approved;
        public override Result<UserNeed> ReturnToDraft() => Transition(UserNeedStatus.Draft);
    }
}
