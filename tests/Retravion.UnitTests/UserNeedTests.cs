using Retravion.Domain.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.UnitTests;

public sealed class UserNeedTests
{
    [Theory]
    [InlineData(null, "Title", "Statement")]
    [InlineData("  ", "Title", "Statement")]
    [InlineData("ID", " ", "Statement")]
    [InlineData("ID", "Title", " ")]
    public void CreationRejectsMissingFields(string? id, string? title, string? statement) =>
        Assert.True(Need.Create(Guid.NewGuid(), id, title, statement).IsFailure);

    [Fact]
    public void LifecycleEnforcesTransitionsAndDraftOnlyEdits()
    {
        var product = Guid.NewGuid();
        var draft = Need.Create(product, "  Need-1  ", "Title", "**Statement**").Value;
        Assert.Equal("Need-1", draft.Id);
        Assert.Equal(product, draft.ProductInternalId);
        Assert.NotEqual(Guid.Empty, draft.InternalId);
        Assert.Equal(UserNeedStatus.Draft, draft.Status);
        Assert.True(draft.Approve().IsFailure);
        Assert.True(draft.ReturnToDraft().IsFailure);
        var review = draft.SubmitForReview().Value;
        Assert.True(review.Edit("ID", "Changed", "Changed").IsFailure);
        Assert.True(review.SubmitForReview().IsFailure);
        var approved = review.Approve().Value;
        Assert.True(approved.Edit("ID", "Changed", "Changed").IsFailure);
        Assert.True(approved.Approve().IsFailure);
        Assert.True(approved.ReturnToDraft().IsFailure);
        Assert.True(approved.Reopen(" ").IsFailure);
        var reopened = approved.Reopen("Correct the wording").Value;
        Assert.True(reopened.Edit("New", "New title", "New statement").IsFailure);
        var revised = reopened.Edit("Need-1", "New title", "New statement").Value;
        Assert.Equal(draft.VersionId, revised.VersionId);
        Assert.Equal(1, revised.VersionNumber);
        Assert.True(revised.HasBeenApproved);
        Assert.Equal(draft.InternalId, revised.InternalId);
        Assert.Equal(product, revised.ProductInternalId);
        Assert.Equal("Title", review.Title);
        Assert.Equal(UserNeedStatus.Draft, review.ReturnToDraft().Value.Status);
    }

    [Fact]
    public void SuccessorsCopyContentButHaveDistinctSequentialVersionsAndFixedReasons()
    {
        var draft = Need.Create(Guid.NewGuid(), "ID", "Title", "Statement").Value;
        Assert.True(draft.CreateSuccessor("Reason").IsFailure);
        var approved = draft.SubmitForReview().Value.Approve().Value;
        Assert.True(approved.CreateSuccessor(null).IsFailure);
        var second = approved.CreateSuccessor("Changed behavior").Value;
        Assert.Equal(approved.InternalId, second.InternalId);
        Assert.Equal(approved.ProductInternalId, second.ProductInternalId);
        Assert.Equal(approved.Id, second.Id);
        Assert.Equal(approved.Title, second.Title);
        Assert.Equal(approved.Statement, second.Statement);
        Assert.NotEqual(approved.VersionId, second.VersionId);
        Assert.Equal(2, second.VersionNumber);
        Assert.Equal(approved.VersionId, second.SourceVersionId);
        Assert.Equal("Changed behavior", second.CreationReason);
        Assert.True(second.Edit("different", "Title", "Statement").IsFailure);
        var edited = second.Edit("ID", "Updated", "Updated").Value;
        Assert.Equal(second.VersionId, edited.VersionId);
        Assert.Equal(second.CreationReason, edited.CreationReason);
        Assert.Equal(3, edited.SubmitForReview().Value.Approve().Value.CreateSuccessor("Next").Value.VersionNumber);
        Assert.True(Need.Restore(draft.InternalId, draft.ProductInternalId, "ID", "Title", "Statement", UserNeedStatus.Draft,
            Guid.NewGuid(), 2).IsFailure);
    }

    [Theory]
    [InlineData(UserNeedStatus.Draft)]
    [InlineData(UserNeedStatus.InReview)]
    public void RestoringASuccessorRequiresPriorApprovalAndKeepsItsIdFixed(UserNeedStatus status)
    {
        var entity = Guid.NewGuid();
        var product = Guid.NewGuid();
        var version = Guid.NewGuid();
        var source = Guid.NewGuid();
        Assert.True(Need.Restore(entity, product, "ID", "Title", "Statement", status,
            version, 2, false, "Changed behavior", source).IsFailure);
        var restored = Need.Restore(entity, product, "ID", "Title", "Statement", status,
            version, 2, true, "Changed behavior", source).Value;
        Assert.True(restored.HasBeenApproved);
        Assert.True(restored.Edit("Changed ID", "Title", "Statement").IsFailure);
    }

    [Fact]
    public void RestoreRejectsInvalidStateAndIdsRespectProductLimit()
    {
        var product = Guid.NewGuid();
        Assert.True(Need.Restore(Guid.Empty, product, "ID", "Title", "Statement", UserNeedStatus.Draft).IsFailure);
        Assert.True(Need.Restore(Guid.NewGuid(), Guid.Empty, "ID", "Title", "Statement", UserNeedStatus.Draft).IsFailure);
        Assert.True(Need.Restore(Guid.NewGuid(), product, "ID", "Title", "Statement", (UserNeedStatus)99).IsFailure);
        Assert.True(Need.Create(product, string.Concat(Enumerable.Repeat("😀", 100)), "Title", "Statement").IsSuccess);
        Assert.True(Need.Create(product, string.Concat(Enumerable.Repeat("😀", 101)), "Title", "Statement").IsFailure);
        Assert.All(typeof(Need).GetProperties(), property => Assert.Null(property.SetMethod));
    }
}
