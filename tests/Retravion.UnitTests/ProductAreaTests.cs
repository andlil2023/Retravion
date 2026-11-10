using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Domain.TestCase;
using Xunit;
using Requirement = Retravion.Domain.Requirement.Requirement;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.UnitTests;

public sealed class ProductAreaTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("a_b")]
    [InlineData("ä")] [InlineData("ſ")] [InlineData("a.b")] [InlineData("a b")]
    public void AreaCodesRejectNonAsciiAndMissingValues(string? code) =>
        Assert.True(ProductArea.Create(Guid.NewGuid(), code, "Label").IsFailure);

    [Fact]
    public void AreaIdentityCodeLengthAndLabelRulesAreEnforced()
    {
        var area = ProductArea.Create(Guid.NewGuid(), " broad-9 ", " Label ").Value;
        Assert.Equal("BROAD-9", area.Code); Assert.Equal("Label", area.Label); Assert.True(area.IsActive);
        var renamed = area.Rename(new string('x', 20000)).Value;
        Assert.Equal(area.InternalId, renamed.InternalId); Assert.Equal(area.Code, renamed.Code);
        var retired = renamed.Retire(); Assert.False(retired.IsActive); Assert.Equal(renamed.Label, retired.Label);
        Assert.False(retired.Rename("Other").Value.IsActive);
        Assert.True(area.Rename(" ").IsFailure);
        Assert.True(ProductArea.Create(Guid.NewGuid(), new string('a', 100), "Label").IsSuccess);
        Assert.True(ProductArea.Create(Guid.NewGuid(), new string('a', 101), "Label").IsFailure);
        Assert.True(ProductArea.Create(Guid.Empty, "A", "Label").IsFailure);
        Assert.True(ProductArea.Restore(Guid.Empty, area.ProductInternalId, "A", "Label", true).IsFailure);
    }

    [Fact]
    public void RequirementClassificationIsDraftOnlyAndInheritedByExactSuccessors()
    {
        var area = Guid.NewGuid();
        var draft = Requirement.Create(Guid.NewGuid(), "R", "Title", "Statement", RequirementType.Software, areaId: area).Value;
        var review = draft.SubmitForReview().Value;
        Assert.True(review.Classify(null).IsFailure);
        var approved = review.Approve().Value; Assert.True(approved.Classify(null).IsFailure);
        var successor = approved.CreateSuccessor("New work").Value;
        Assert.Equal(area, successor.AreaId); Assert.Equal(area, approved.AreaId);
        var edited = successor.Edit("R", "New title", "Statement", RequirementType.Software, null).Value;
        Assert.Equal(area, edited.AreaId); Assert.Null(edited.Classify(null).Value.AreaId);
        Assert.True(edited.Classify(Guid.Empty).IsFailure);
    }

    [Fact]
    public void TestCaseClassificationIsIndependentAndSurvivesLinksAndLifecycle()
    {
        var area = Guid.NewGuid();
        var content = TestCaseContent.Create("Title", "", "", [new("Do", "Expect")]).Value;
        var draft = Case.Create(Guid.NewGuid(), 1, content, area).Value;
        var linked = draft.ChangeLinks([new(Guid.NewGuid(), Guid.NewGuid())]).Value;
        Assert.Equal(area, linked.AreaId);
        var review = linked.SubmitForReview().Value; Assert.True(review.Classify(null).IsFailure);
        var approved = review.Approve(true).Value; Assert.True(approved.Classify(null).IsFailure);
        Assert.Equal(area, approved.Reopen("Correction").Value.AreaId);
        var successor = approved.CreateSuccessor("New work").Value;
        Assert.Equal(area, successor.AreaId); Assert.Equal(area, successor.Edit(content).Value.AreaId);
        Assert.Null(successor.Classify(null).Value.AreaId); Assert.True(successor.Classify(Guid.Empty).IsFailure);
        Assert.False(successor.SameState(successor.Classify(null).Value));
    }
}
