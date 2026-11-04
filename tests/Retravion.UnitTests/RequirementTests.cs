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
        Assert.True(approved.ReturnToDraft().IsFailure);
        Assert.True(approved.Reopen(" ").IsFailure);
        var reopened = approved.Reopen("Correct wording").Value;
        Assert.Equal(original.VersionId, reopened.VersionId);
        Assert.Equal(original.VersionNumber, reopened.VersionNumber);
        Assert.True(reopened.HasBeenApproved);
        Assert.True(reopened.Edit("Renamed", "Title", "Statement", original.Type, original.Rationale).IsFailure);
        Assert.Equal(original.Id, reopened.Id);
        Assert.Equal(original.Title, reopened.Title);
        Assert.Equal(original.Statement, reopened.Statement);
        Assert.Equal(original.Type, reopened.Type);
        Assert.Equal(original.Rationale, reopened.Rationale);
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
    public void SuccessorsCopyAllFieldsKeepIdFixedAndOnlyDeliberateCreationIncrementsVersion()
    {
        var draft = RequirementState.Create(Guid.NewGuid(), "ID", "Title", "Statement", RequirementType.Safety, "Rationale").Value;
        Assert.True(draft.CreateSuccessor("Next").IsFailure);
        var approved = draft.SubmitForReview().Value.Approve().Value;
        Assert.True(approved.CreateSuccessor(null).IsFailure);
        Assert.True(approved.CreateSuccessor(" ").IsFailure);
        var second = approved.CreateSuccessor("Changed behavior").Value;
        Assert.Equal(approved.InternalId, second.InternalId); Assert.Equal(approved.ProductInternalId, second.ProductInternalId);
        Assert.Equal(approved.Id, second.Id); Assert.Equal(approved.Title, second.Title); Assert.Equal(approved.Statement, second.Statement);
        Assert.Equal(approved.Type, second.Type); Assert.Equal(approved.Rationale, second.Rationale);
        Assert.NotEqual(approved.VersionId, second.VersionId); Assert.Equal(2, second.VersionNumber);
        Assert.Equal(approved.VersionId, second.SourceVersionId); Assert.Equal("Changed behavior", second.CreationReason);
        Assert.True(second.Edit("Renamed", "Title", "Statement", second.Type, null).IsFailure);
        var edited = second.Edit("ID", "Changed", "Changed", RequirementType.Software, null).Value;
        Assert.Equal(second.VersionId, edited.VersionId); Assert.Equal(second.CreationReason, edited.CreationReason);
        Assert.Null(edited.Rationale); Assert.Equal(RequirementType.Software, edited.Type);
        var rejected = edited.SubmitForReview().Value.ReturnToDraft().Value;
        Assert.Equal(edited, rejected);
        Assert.Equal(3, rejected.SubmitForReview().Value.Approve().Value.CreateSuccessor("Next").Value.VersionNumber);
        Assert.All(typeof(RequirementState).GetProperties(), p => Assert.Null(p.SetMethod));
    }

    [Theory]
    [InlineData(RequirementStatus.Draft)]
    [InlineData(RequirementStatus.InReview)]
    public void RestoringSuccessorsRequiresPriorApprovalReasonAndSource(RequirementStatus status)
    {
        var entity = Guid.NewGuid(); var product = Guid.NewGuid(); var version = Guid.NewGuid(); var source = Guid.NewGuid();
        Assert.True(RequirementState.Restore(entity, product, "ID", "Title", "Statement", RequirementType.System, null,
            status, version, 2, false, "Next", source).IsFailure);
        Assert.True(RequirementState.Restore(entity, product, "ID", "Title", "Statement", RequirementType.System, null,
            status, version, 2, true, " ", source).IsFailure);
        Assert.True(RequirementState.Restore(entity, product, "ID", "Title", "Statement", RequirementType.System, null,
            status, version, 2, true, "Next", version).IsFailure);
        var restored = RequirementState.Restore(entity, product, "ID", "Title", "Statement", RequirementType.System, null,
            status, version, 2, true, "Next", source).Value;
        Assert.True(restored.HasBeenApproved);
        Assert.True(restored.Edit("Changed ID", "Title", "Statement", restored.Type, null).IsFailure);
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
