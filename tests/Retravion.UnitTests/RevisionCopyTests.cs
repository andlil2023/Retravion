using Retravion.Application.AuditTrail;
using Retravion.Domain.Revision;
using System.Text.Json;
using Xunit;

namespace Retravion.UnitTests;

public sealed class RevisionCopyTests
{
    [Fact]
    public void ReleasedCopyHasIndependentIdentitySameProductAndExactSelectionsWithoutReleaseEvidence()
    {
        var entity = Guid.NewGuid();
        var endpoints = new[] {
            new RevisionSelection(RevisionEntityKind.UserNeed, entity, Guid.NewGuid(), 1),
            new RevisionSelection(RevisionEntityKind.UserNeed, entity, Guid.NewGuid(), 2),
            new RevisionSelection(RevisionEntityKind.Requirement, Guid.NewGuid(), Guid.NewGuid(), 1),
            new RevisionSelection(RevisionEntityKind.TestCase, Guid.NewGuid(), Guid.NewGuid(), 1) };
        var draft = DraftRevision.Restore(Guid.NewGuid(), Guid.NewGuid(), "Source", "Source description", endpoints).Value;
        var released = ReleasedRevision.Restore(draft, "actor", DateTimeOffset.UtcNow,
            endpoints.Select(e => new ReleasedRevisionMember(e, "Identifier", "Title", true))).Value;
        var before = JsonSerializer.Serialize(released); var id = Guid.NewGuid();
        var result = released.CopyToDraft(id, "  Destination  ", null);
        Assert.True(result.IsSuccess); var copy = result.Value;
        Assert.Equal(id, copy.InternalId); Assert.Equal(released.ProductInternalId, copy.ProductInternalId);
        Assert.Equal("Destination", copy.Identifier); Assert.Equal("", copy.Description); Assert.Equal("Draft", copy.State);
        Assert.Equal(released.Selections.ToArray(), copy.Selections.ToArray());
        Assert.Equal(before, JsonSerializer.Serialize(released));
        Assert.True(released.CopyToDraft(released.InternalId, "New", "").IsFailure);
        Assert.True(released.CopyToDraft(Guid.Empty, "New", "").IsFailure);
        Assert.True(released.CopyToDraft(Guid.NewGuid(), " ", "").IsFailure);
        Assert.True(released.CopyToDraft(Guid.NewGuid(), new string('x', 101), "").IsFailure);
    }

    [Fact]
    public void ExistingRevisionEvidenceWithoutSourceIdentityRemainsReadable()
    {
        var values = new RevisionAuditValues("Existing", "Description", "Draft", []);
        var existing = new RevisionAuditDetails(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, values);
        var json = JsonSerializer.Serialize(existing).Replace(",\"SourceRevisionInternalId\":null", "");
        var restored = JsonSerializer.Deserialize<RevisionAuditDetails>(json)!;
        Assert.Null(restored.SourceRevisionInternalId); Assert.Equal(existing.InternalId, restored.InternalId);
        Assert.Equal(existing.After!.Identifier, restored.After!.Identifier);
    }
}
