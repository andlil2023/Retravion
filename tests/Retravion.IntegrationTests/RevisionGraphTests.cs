using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Web.Features.Relationships;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class RevisionGraphTests
{
    private static RevisionBaselineMember Member(RevisionEntityKind kind, bool selected, string title) =>
        new(new(kind, Guid.NewGuid(), Guid.NewGuid(), 1), title, title, "Approved", selected);

    [Fact]
    public void SharedDirectAndIndirectRequirementKeepsBothPathsAndCollapseRespectsFilter()
    {
        var f1 = Member(RevisionEntityKind.Feature, true, "F1");
        var f2 = Member(RevisionEntityKind.Feature, true, "F2");
        var r = Member(RevisionEntityKind.Requirement, true, "R");
        var n = Member(RevisionEntityKind.UserNeed, false, "N");
        RevisionBaselineMember[] members = [f1, f2, r, n];
        (Guid Owner, Guid Target, string Label, bool Dashed)[] edges =
            [(f1.Endpoint.VersionId, r.Endpoint.VersionId, "Addresses", false),
             (f2.Endpoint.VersionId, r.Endpoint.VersionId, "Addresses", false),
             (r.Endpoint.VersionId, n.Endpoint.VersionId, "Addresses", false)];
        foreach (var filter in new[] { "", "Directly selected", "Indirectly connected", "All connected" })
        {
            var visible = RevisionGraph.Visible(members, edges, new HashSet<Guid>(), filter, "");
            Assert.Equal(3, visible.Count);
            Assert.Contains(r.Endpoint.VersionId, visible);
            Assert.DoesNotContain(n.Endpoint.VersionId, visible);
            visible = RevisionGraph.Visible(members, edges, new HashSet<Guid> { r.Endpoint.VersionId }, filter, "");
            Assert.Equal(4, visible.Count);
        }
    }

    [Fact]
    public void SearchAndFilterRevealPathsBeyondTablePageSizeWithoutUnrelatedRoots()
    {
        var features = Enumerable.Range(0, 25).Select(i => Member(RevisionEntityKind.Feature, true, $"F{i}")).ToArray();
        var r = Member(RevisionEntityKind.Requirement, false, "Find me");
        var members = features.Append(r).ToArray();
        (Guid Owner, Guid Target, string Label, bool Dashed)[] edges = [(features[24].Endpoint.VersionId, r.Endpoint.VersionId, "Addresses", false)];
        var visible = RevisionGraph.Visible(members, edges, new HashSet<Guid>(), "All connected", "Find me");
        Assert.Equal(2, visible.Count);
        Assert.Contains(features[24].Endpoint.VersionId, visible);
        Assert.Contains(r.Endpoint.VersionId, visible);
        Assert.Empty(RevisionGraph.Visible(members, edges, new HashSet<Guid>(), "All connected", "missing"));
        Assert.Empty(RevisionGraph.Visible(members, edges, new HashSet<Guid>(), "Directly selected", "Find me"));
    }
}
