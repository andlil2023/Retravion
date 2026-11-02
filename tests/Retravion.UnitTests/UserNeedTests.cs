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
        var revised = approved.ReturnToDraft().Value.Edit("New", "New title", "New statement").Value;
        Assert.Equal(draft.InternalId, revised.InternalId);
        Assert.Equal(product, revised.ProductInternalId);
        Assert.Equal("Title", review.Title);
        Assert.Equal(UserNeedStatus.Draft, review.ReturnToDraft().Value.Status);
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
