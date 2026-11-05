using Retravion.Domain.Revision;
using Xunit;

namespace Retravion.UnitTests;

public sealed class RevisionTests
{
    [Fact]
    public void RevisionConstructionAndEditsEnforceIdentityMetadataAndExactSelections()
    {
        var id = Guid.NewGuid(); var product = Guid.NewGuid();
        var draft = DraftRevision.Create(id, product, "  Baseline α  ", null).Value;
        Assert.Equal(id, draft.InternalId); Assert.Equal(product, draft.ProductInternalId);
        Assert.Equal("Baseline α", draft.Identifier); Assert.Equal("", draft.Description); Assert.Equal("Draft", draft.State); Assert.Empty(draft.Selections);
        var edited = draft.Edit("Next", " <script>text</script> ").Value;
        Assert.Equal(id, edited.InternalId); Assert.Equal(product, edited.ProductInternalId);
        Assert.Equal(" <script>text</script> ", edited.Description); Assert.Equal("Baseline α", draft.Identifier);
        foreach (var identifier in new string?[] { null, "", " \t\n", new('x', 101), string.Concat(Enumerable.Repeat("😀", 101)) })
            Assert.True(draft.Edit(identifier, "").IsFailure);
        Assert.True(draft.Edit(string.Concat(Enumerable.Repeat("😀", 100)), "").IsSuccess);
        Assert.True(DraftRevision.Create(Guid.Empty, product, "A", "").IsFailure);
        Assert.True(DraftRevision.Create(id, Guid.Empty, "A", "").IsFailure);
        Assert.True(DraftRevision.Restore(id, product, "A", null, []).IsFailure);
        Assert.True(DraftRevision.Restore(id, product, "A", "", null).IsFailure);
        Assert.True(DraftRevision.Restore(id, product, "A", "", [null!]).IsFailure);
        Assert.True(DraftRevision.Restore(id, product, "A", "", [new((RevisionEntityKind)99, id, id, 1)]).IsFailure);
    }

    [Fact]
    public void MultipleVersionsCoexistReplacementIsAtomicAndInvalidDuplicateEndpointsFail()
    {
        var draft = DraftRevision.Create(Guid.NewGuid(), Guid.NewGuid(), "A", "").Value;
        var record = Guid.NewGuid();
        var first = new RevisionSelection(RevisionEntityKind.Requirement, record, Guid.NewGuid(), 1);
        var second = first with { VersionId = Guid.NewGuid(), VersionNumber = 2 };
        var need = new RevisionSelection(RevisionEntityKind.UserNeed, Guid.NewGuid(), Guid.NewGuid(), 1);
        var added = draft.ChangeSelection("add", first).Value.ChangeSelection("add", second).Value;
        Assert.Equal(2, added.Selections.Length); Assert.Empty(draft.Selections);
        Assert.True(added.SameValues(added.ChangeSelection("add", first).Value));
        var replaced = added.ChangeSelection("replace", first, need).Value;
        Assert.Contains(need, replaced.Selections); Assert.Contains(second, replaced.Selections); Assert.DoesNotContain(first, replaced.Selections);
        Assert.True(added.ChangeSelection("replace", first, second).IsFailure);
        Assert.True(added.ChangeSelection("replace", need, first).IsFailure);
        Assert.True(added.ChangeSelection("invented", first).IsFailure);
        Assert.True(added.ChangeSelection("add", first with { VersionNumber = 0 }).IsFailure);
        Assert.True(added.ChangeSelection("add", first with { VersionId = Guid.Empty }).IsFailure);
        Assert.True(added.ChangeSelection("add", first with { InternalId = Guid.Empty }).IsFailure);
        Assert.True(DraftRevision.Restore(draft.InternalId, draft.ProductInternalId, "A", "", [first, first with { VersionNumber = 7 }]).IsFailure);
        Assert.Single(added.ChangeSelection("remove", first).Value.Selections);
        Assert.True(added.SameValues(added.ChangeSelection("remove", need).Value));
    }
}
