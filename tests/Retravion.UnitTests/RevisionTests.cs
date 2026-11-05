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
    public void ReleasedStateRequiresFrozenRootsClosureAndAttributionAndExposesNoDraftMutations()
    {
        var root = new RevisionSelection(RevisionEntityKind.TestCase, Guid.NewGuid(), Guid.NewGuid(), 1);
        var indirect = new RevisionSelection(RevisionEntityKind.Requirement, Guid.NewGuid(), Guid.NewGuid(), 2);
        var draft = DraftRevision.Create(Guid.NewGuid(), Guid.NewGuid(), "Release", "Frozen").Value.ChangeSelection("add", root).Value;
        var time = DateTimeOffset.Parse("2026-01-02T03:04:05+02:00");
        var members = new[] { new ReleasedRevisionMember(root, "TC-001", "Procedure", true), new ReleasedRevisionMember(indirect, "R", "Requirement", false) };
        var released = ReleasedRevision.Restore(draft, "approver", time, members).Value;
        Assert.Equal("Released", released.State); Assert.Equal(draft.InternalId, released.InternalId);
        Assert.Equal(draft.ProductInternalId, released.ProductInternalId); Assert.Equal(draft.Selections, released.Selections);
        Assert.Equal("Frozen", released.Description); Assert.Equal("approver", released.ReleasedBy); Assert.Equal(time.UtcTicks, released.ReleasedAt.UtcTicks);
        Assert.Equal(members, released.Members.ToArray()); Assert.False(draft.SameValues(released));
        Assert.DoesNotContain(typeof(ReleasedRevision).GetMethods(), m => m.Name is "Edit" or "ChangeSelection" or "Reopen");
        Assert.DoesNotContain(typeof(ReleasedRevision).GetProperties(), p => p.SetMethod?.IsPublic == true);
        Assert.True(ReleasedRevision.Restore(draft, "", time, members).IsFailure);
        Assert.True(ReleasedRevision.Restore(draft, "actor", default, members).IsFailure);
        Assert.True(ReleasedRevision.Restore(draft, "actor", time, null).IsFailure);
        Assert.True(ReleasedRevision.Restore(draft, "actor", time, [members[1]]).IsFailure);
        Assert.True(ReleasedRevision.Restore(draft, "actor", time, [members[0], members[0]]).IsFailure);
        Assert.True(ReleasedRevision.Restore(draft, "actor", time, [members[0] with { Explicit = false }]).IsFailure);
        Assert.True(ReleasedRevision.Restore(draft, "actor", time, [null!]).IsFailure);
        Assert.True(ReleasedRevision.Restore(DraftRevision.Create(Guid.NewGuid(), Guid.NewGuid(), "Empty", "").Value, "actor", time, members).IsFailure);
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
