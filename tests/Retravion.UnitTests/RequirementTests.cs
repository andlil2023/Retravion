using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.UnitTests;

public sealed class RequirementTests
{
    [Fact]
    public void RequiredFieldsTypesAndUnicodeIdLimitAreValidated()
    {
        var product = Guid.NewGuid();
        foreach (var blank in new string?[] { null, "", " \t" })
        {
            Assert.True(RequirementState.Create(product, blank, "Title", "Statement", RequirementType.System).IsFailure);
            Assert.True(RequirementState.Create(product, "ID", blank, "Statement", RequirementType.System).IsFailure);
            Assert.True(RequirementState.Create(product, "ID", "Title", blank, RequirementType.System).IsFailure);
        }
        Assert.True(RequirementState.Create(Guid.Empty, "ID", "Title", "Statement", RequirementType.System).IsFailure);
        Assert.True(RequirementState.Create(product, "ID", "Title", "Statement", null).IsFailure);
        Assert.True(RequirementState.Create(product, "ID", "Title", "Statement", (RequirementType)99).IsFailure);
        var unicode = string.Concat(Enumerable.Repeat("😀", 100));
        Assert.True(RequirementState.Create(product, unicode, "Title", "Statement", RequirementType.System).IsSuccess);
        Assert.True(RequirementState.Create(product, unicode + "x", "Title", "Statement", RequirementType.System).IsFailure);
        foreach (var type in Enum.GetValues<RequirementType>())
        {
            var result = RequirementState.Create(product, " ID ", "Title", "Statement", type);
            Assert.True(result.IsSuccess);
            Assert.Equal("ID", result.Value.Id);
            Assert.Equal(type, result.Value.Type);
            Assert.Null(result.Value.Rationale);
            Assert.Equal(RequirementStatus.Draft, result.Value.Status);
        }
    }

    [Fact]
    public void AllStateTransitionsAndDraftOnlyEditsPreserveIdentityAndContent()
    {
        var original = RequirementState.Create(Guid.NewGuid(), "ID", "Title", "Statement", RequirementType.Software, "Reason").Value;
        var review = original.SubmitForReview().Value;
        var approved = review.Approve().Value;
        Assert.True(original.Approve().IsFailure);
        Assert.True(original.ReturnToDraft().IsFailure);
        Assert.True(review.SubmitForReview().IsFailure);
        Assert.True(approved.SubmitForReview().IsFailure);
        Assert.True(approved.Approve().IsFailure);
        Assert.Equal(original, review.ReturnToDraft().Value);
        Assert.Equal(original, approved.ReturnToDraft().Value);
        foreach (var value in new[] { original, review, approved })
        {
            Assert.Equal(original.InternalId, value.InternalId);
            Assert.Equal(original.ProductInternalId, value.ProductInternalId);
            Assert.Equal(original.Id, value.Id);
            Assert.Equal(original.Title, value.Title);
            Assert.Equal(original.Statement, value.Statement);
            Assert.Equal(original.Type, value.Type);
            Assert.Equal(original.Rationale, value.Rationale);
        }
        foreach (var value in new[] { review, approved })
            Assert.True(value.Edit("Changed", "Changed", "Changed", RequirementType.Hardware, null).IsFailure);
        var edited = original.Edit("New", "New title", "New statement", RequirementType.Hardware, null).Value;
        Assert.Equal(original.InternalId, edited.InternalId);
        Assert.Equal(original.ProductInternalId, edited.ProductInternalId);
        Assert.Null(edited.Rationale);
        Assert.Equal(RequirementType.Hardware, edited.Type);
        Assert.True(original.Edit("", "Title", "Statement", RequirementType.System, null).IsFailure);
        Assert.True(original.Edit("ID", "Title", "Statement", null, null).IsFailure);
        Assert.True(RequirementState.Restore(Guid.Empty, original.ProductInternalId, "ID", "Title", "Statement", RequirementType.System, null, RequirementStatus.Draft).IsFailure);
        Assert.True(RequirementState.Restore(original.InternalId, original.ProductInternalId, "ID", "Title", "Statement", RequirementType.System, null, (RequirementStatus)99).IsFailure);
        Assert.True(RequirementState.Restore(original.InternalId, original.ProductInternalId, "ID", "Title", "Statement", (RequirementType)99, null, RequirementStatus.Draft).IsFailure);
    }

    [Fact]
    public void FiltersCombineAndSearchIsCaseInsensitiveSubstringOfIdOrTitle()
    {
        var product = Guid.NewGuid();
        var first = RequirementState.Create(product, "REQ-ABC", "Alpha behavior", "Statement", RequirementType.Software).Value;
        var second = RequirementState.Create(product, "REQ-XYZ", "Beta ALPHA", "Statement", RequirementType.Hardware).Value.SubmitForReview().Value;
        var third = RequirementState.Create(product, "REQ-123", "Other", "Statement", RequirementType.Software).Value.SubmitForReview().Value.Approve().Value;
        RequirementSnapshot[] rows = [new(first, 1), new(second, 2), new(third, 3)];
        Assert.Equal(3, RequirementList.Filter(rows, null, null, null).Count);
        Assert.Equal(2, RequirementList.Filter(rows, null, null, " aLpHa ").Count);
        Assert.Equal(rows[0], Assert.Single(RequirementList.Filter(rows, null, null, "abc")));
        Assert.Equal(rows[0], Assert.Single(RequirementList.Filter(rows, RequirementType.Software, RequirementStatus.Draft, "alpha")));
        Assert.Empty(RequirementList.Filter(rows, RequirementType.Hardware, RequirementStatus.Approved, "alpha"));
        Assert.Empty(RequirementList.Filter(rows, null, null, "missing"));
        Assert.Equal(3, RequirementList.Filter(rows, null, null, " ").Count);
        Assert.Equal(rows[2], Assert.Single(RequirementList.Filter(rows, null, RequirementStatus.Approved, null)));
        Assert.Equal(rows[1], Assert.Single(RequirementList.Filter(rows, RequirementType.Hardware, null, null)));
    }
}
