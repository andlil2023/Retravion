using Retravion.Domain.Feature;
using Xunit;
using Feature = Retravion.Domain.Feature.Feature;

namespace Retravion.UnitTests;

public sealed class FeatureTests
{
    [Fact]
    public void DraftPreservesMarkdownAndRequiresTitleForReview()
    {
        var markdown = "Paragraph\n\n| A | B |\n|---|---|\n| x | y |\n\n```mermaid\ngraph TD\n A-->B\n```";
        var draft = Feature.Create(Guid.NewGuid(), 1, FeatureContent.Create(null, markdown)).Value;
        Assert.Equal("F-001", draft.Id);
        Assert.Equal(markdown, draft.Content.Description);
        Assert.True(draft.SubmitForReview().IsFailure);
        var ready = draft.Edit(FeatureContent.Create("Title", markdown)).Value;
        Assert.Equal(draft.InternalId, ready.InternalId);
        Assert.Equal(draft.VersionId, ready.VersionId);
        Assert.Equal(markdown, ready.SubmitForReview().Value.Content.Description);
    }

    [Fact]
    public void LifecycleAndVersionTransitionsPreserveIdentityAndEvidence()
    {
        var draft = Feature.Create(Guid.NewGuid(), 12, FeatureContent.Create("Title", "description")).Value;
        Assert.True(draft.Approve().IsFailure);
        Assert.True(draft.Reopen("reason").IsFailure);
        var review = draft.SubmitForReview().Value;
        Assert.True(review.Edit(draft.Content).IsFailure);
        Assert.Equal("Draft", review.ReturnToDraft().Value.Status);
        var approved = review.Approve().Value;
        Assert.True(approved.Reopen(" ").IsFailure);
        Assert.True(approved.CreateSuccessor(null).IsFailure);
        var reopened = approved.Reopen("correction").Value;
        Assert.Equal(approved.VersionId, reopened.VersionId);
        Assert.Equal("Draft", reopened.Status);
        var successor = approved.CreateSuccessor("reason").Value;
        Assert.Equal(approved.InternalId, successor.InternalId);
        Assert.Equal(approved.ProductInternalId, successor.ProductInternalId);
        Assert.Equal(approved.Id, successor.Id);
        Assert.NotEqual(approved.VersionId, successor.VersionId);
        Assert.Equal(approved.VersionId, successor.SourceVersionId);
        Assert.Equal(2, successor.VersionNumber);
        Assert.Equal("reason", successor.CreationReason);
        Assert.Equal(approved.Content, successor.Content);
    }

    [Fact]
    public void InvalidPersistedStatesAreRejected()
    {
        var id = Guid.NewGuid(); var product = Guid.NewGuid(); var version = Guid.NewGuid();
        var content = FeatureContent.Create("", "");
        Assert.True(Feature.Create(Guid.Empty, 1, content).IsFailure);
        Assert.True(Feature.Create(product, 0, content).IsFailure);
        Assert.True(Feature.Restore(id, product, version, 1, 1, null, null, content, "Approved").IsFailure);
        Assert.True(Feature.Restore(id, product, version, 1, 2, null, null, content, "Draft").IsFailure);
        Assert.True(Feature.Restore(id, product, version, 1, 1, null, null, content, "invalid").IsFailure);
    }
}
