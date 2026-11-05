using Retravion.Application.AuditTrail;
using Retravion.Domain.TestCase;
using Xunit;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.UnitTests;

public sealed class TestCaseTests
{
    [Fact]
    public void IncompleteDraftsPreserveTextAndIdentityAndCopyStepsImmutably()
    {
        var steps = new[] { new TestCaseStep("", ""), new TestCaseStep(" \n# Action 😀 ", "| Expected |\n| --- |\n| Yes |") };
        var content = TestCaseContent.Create(" \t", "", " \n", steps).Value;
        var product = Guid.NewGuid();
        var value = Case.Create(product, 1, content).Value;
        steps[0] = new("external change", "not retained");
        Assert.Equal("", value.Content.Steps[0].Action);
        Assert.Equal(" \t", value.Content.Title);
        Assert.Equal(" \n", value.Content.Preconditions);
        Assert.Equal(" \n# Action 😀 ", value.Content.Steps[1].Action);
        Assert.Equal("TC-001", value.Id);
        Assert.Equal("Draft", value.Status);
        Assert.Equal(1, value.VersionNumber);
        Assert.NotEqual(Guid.Empty, value.InternalId);
        Assert.NotEqual(Guid.Empty, value.VersionId);
        Assert.NotEqual(value.InternalId, value.VersionId);
        var empty = TestCaseContent.Create(null, null, null, null).Value;
        var edited = value.Edit(empty).Value;
        Assert.Equal(value.InternalId, edited.InternalId);
        Assert.Equal(value.VersionId, edited.VersionId);
        Assert.Equal(product, edited.ProductInternalId);
        Assert.Equal(value.Number, edited.Number);
        Assert.Empty(edited.Content.Steps);
        Assert.Equal("", edited.Content.Title);
        Assert.Equal(2, value.Content.Steps.Length);
        Assert.True(content.SameContent(TestCaseContent.Create(content.Title, content.Description, content.Preconditions, content.Steps.ToArray()).Value));
        Assert.False(content.SameContent(empty));
        Assert.False(content.SameContent(TestCaseContent.Create(content.Title, content.Description, content.Preconditions, content.Steps.Reverse()).Value));
    }

    [Theory]
    [InlineData(1, "TC-001")]
    [InlineData(999, "TC-999")]
    [InlineData(1000, "TC-1000")]
    [InlineData(long.MaxValue, "TC-9223372036854775807")]
    public void NumbersUseMinimumPaddingAndDoNotTruncate(long number, string expected) =>
        Assert.Equal(expected, Case.Create(Guid.NewGuid(), number, TestCaseContent.Create("", "", "", []).Value).Value.Id);

    [Fact]
    public void ConstructionAndRestorationRejectInvalidIdentitiesAndStepShapes()
    {
        var content = TestCaseContent.Create("", "", "", []).Value;
        Assert.True(Case.Create(Guid.Empty, 1, content).IsFailure);
        foreach (var number in new long[] { 0, -1 }) Assert.True(Case.Create(Guid.NewGuid(), number, content).IsFailure);
        Assert.True(Case.Restore(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), 1, content).IsFailure);
        Assert.True(Case.Restore(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, 1, content).IsFailure);
        Assert.True(Case.Restore(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, null!).IsFailure);
        Assert.True(TestCaseContent.Create("", "", "", [null!]).IsFailure);
        Assert.True(TestCaseContent.Create("", "", "", [new(null!, "")]).IsFailure);
        Assert.True(TestCaseContent.Create("", "", "", [new("", null!)]).IsFailure);
    }

    [Fact]
    public void LifecycleRequiresCompleteContentLinksAndApprovedTargetsAndPreservesIdentity()
    {
        var content = TestCaseContent.Create("Title", "", "", [new("Action", "Result")]).Value;
        var draft = Case.Create(Guid.NewGuid(), 1, content).Value;
        Assert.True(draft.SubmitForReview().IsFailure);
        var endpoint = new TestCaseRequirementLink(Guid.NewGuid(), Guid.NewGuid());
        foreach (var incomplete in new[] {
            TestCaseContent.Create(" \n", "", "", content.Steps).Value,
            TestCaseContent.Create("Title", "", "", []).Value,
            TestCaseContent.Create("Title", "", "", [new(" \t", "Result")]).Value,
            TestCaseContent.Create("Title", "", "", [new("Action", " \n")]).Value })
            Assert.True(draft.Edit(incomplete).Value.ChangeLinks([endpoint]).Value.SubmitForReview().IsFailure);
        Assert.True(draft.ChangeLinks([endpoint, endpoint]).IsFailure);
        Assert.True(draft.ChangeLinks([endpoint, endpoint with { RequirementVersionId = Guid.NewGuid() }]).IsFailure);
        Assert.True(draft.ChangeLinks([new(Guid.Empty, Guid.NewGuid())]).IsFailure);
        var review = draft.ChangeLinks([endpoint]).Value.SubmitForReview().Value;
        Assert.Equal(draft.VersionId, review.VersionId);
        Assert.Equal("InReview", review.Status);
        Assert.True(review.Edit(content).IsFailure); Assert.True(review.ChangeLinks([]).IsFailure);
        Assert.True(review.Approve(false).IsFailure);
        var rejected = review.ReturnToDraft().Value;
        Assert.Equal(review.VersionId, rejected.VersionId); Assert.Equal(1, rejected.VersionNumber);
        var approved = rejected.SubmitForReview().Value.Approve(true).Value;
        Assert.True(approved.Edit(content).IsFailure); Assert.True(approved.ChangeLinks([]).IsFailure);
        Assert.True(approved.SubmitForReview().IsFailure); Assert.True(approved.ReturnToDraft().IsFailure);
        Assert.True(approved.CreateSuccessor(" \n").IsFailure);
        var successor = approved.CreateSuccessor(" Fixed reason ").Value;
        Assert.NotEqual(approved.VersionId, successor.VersionId);
        Assert.Equal(approved.VersionId, successor.SourceVersionId);
        Assert.Equal(approved.InternalId, successor.InternalId); Assert.Equal(approved.ProductInternalId, successor.ProductInternalId);
        Assert.Equal(approved.Id, successor.Id); Assert.Equal(2, successor.VersionNumber); Assert.Equal("Draft", successor.Status);
        Assert.Equal(" Fixed reason ", successor.CreationReason);
        Assert.True(approved.Content.SameContent(successor.Content)); Assert.Equal(approved.Links.ToArray(), successor.Links.ToArray());
        Assert.Equal(successor.CreationReason, successor.Edit(content).Value.CreationReason);
        Assert.True(Case.Restore(draft.InternalId, draft.ProductInternalId, draft.VersionId, 1, content, "Bogus").IsFailure);
        Assert.True(Case.Restore(draft.InternalId, draft.ProductInternalId, draft.VersionId, 1, content, "Approved").IsFailure);
        Assert.True(Case.Restore(draft.InternalId, draft.ProductInternalId, draft.VersionId, 1, content, versionNumber: 2).IsFailure);
        var limit = Case.Restore(draft.InternalId, draft.ProductInternalId, draft.VersionId, 1, content, "Approved", int.MaxValue,
            "Reason", Guid.NewGuid(), [endpoint]).Value;
        Assert.True(limit.CreateSuccessor("Next").IsFailure);
    }

    [Fact]
    public void SharedAuditContractRetainsTestCaseValuesWithoutChangingLegacyEvents()
    {
        var value = Case.Create(Guid.NewGuid(), 1, TestCaseContent.Create("Title", "# Description", "", [new("Action", "Result")]).Value).Value;
        var values = new TestCaseAuditValues(value.ProductInternalId, value.Id, value.Content.Title, value.Content.Description,
            value.Content.Preconditions, value.Status, value.Content.Steps);
        var details = new TestCaseAuditDetails(value.InternalId, value.VersionId, 1, null, values);
        var information = new AuditInformation(new AuditActor.Identified("author"), DateTimeOffset.UnixEpoch,
            "test-case.create", new AuditTarget.Identified(value.InternalId.ToString()), AuditActionOutcome.Success, testCaseChange: details);
        Assert.Same(details, information.TestCaseChange);
        Assert.Null(information.TestCaseChange!.Before);
        Assert.Equal(value.Content.Steps, information.TestCaseChange.After.Steps);
        Assert.Null(new AuditInformation(information.Actor, information.OccurredAt, "legacy", information.Target, information.Outcome).TestCaseChange);
    }

    [Fact]
    public void TestCaseUiKeepsPersistenceBehindApplicationContracts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Retravion.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(directory.FullName, "src", "Retravion.Web", "Features", "TestCase"), "*", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Retravion.Infrastructure", source);
            Assert.DoesNotContain("Retravion.Identity.Domain", source);
            Assert.DoesNotContain("Npgsql", source);
        }
    }
}
