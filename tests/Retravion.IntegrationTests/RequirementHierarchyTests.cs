using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Web.Features.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed class RequirementHierarchyTests
{
    private static readonly Guid Product = Guid.NewGuid();
    private static RequirementSnapshot Row(string id) => new(RequirementState.Create(Product, id, "Title " + id,
        "Statement", RequirementType.System).Value, 1);

    [Fact]
    [Trait("Req", "REQ-REQ-002")]
    public void BranchesCollapseIndependentlyAndPreserveNestedChoicesInManualIdOrder()
    {
        var a = Row("A"); var b = Row("B"); var c = Row("C"); var d = Row("D"); var e = Row("E");
        var hierarchy = RequirementHierarchy.Create([new(e, b), new(d, c), new(c, a), new(b, null), new(a, null)]).Value;
        var expanded = new HashSet<Guid>();
        Assert.Equal(["A", "B"], Ids(hierarchy.Visible(null, expanded)));
        expanded.Add(a.Requirement.InternalId);
        Assert.Equal(["A", "C", "B"], Ids(hierarchy.Visible(null, expanded)));
        expanded.Add(c.Requirement.InternalId); expanded.Add(b.Requirement.InternalId);
        var all = hierarchy.Visible(null, expanded);
        Assert.Equal(["A", "C", "D", "B", "E"], Ids(all));
        Assert.Equal([0, 1, 2, 0, 1], all.Select(row => row.Depth));
        Assert.False(all.Single(row => row.Item.Snapshot == d).HasChildren);
        expanded.Remove(a.Requirement.InternalId);
        Assert.Equal(["A", "B", "E"], Ids(hierarchy.Visible(null, expanded)));
        expanded.Add(a.Requirement.InternalId);
        Assert.Equal(Ids(all), Ids(hierarchy.Visible(null, expanded)));
    }

    [Fact]
    [Trait("Req", "REQ-REQ-002")]
    public void FiltersRevealOnlyMatchesAndAncestorsWithoutChangingUnfilteredExpansionChoices()
    {
        var a = Row("A"); var b = Row("B"); var c = Row("C"); var unrelated = Row("D");
        var hierarchy = RequirementHierarchy.Create([new(a, null), new(b, a), new(c, b), new(unrelated, a)]).Value;
        var expanded = new HashSet<Guid>();
        var filtered = hierarchy.Visible(new HashSet<Guid> { c.Requirement.InternalId }, expanded);
        Assert.Equal(["A", "B", "C"], Ids(filtered));
        Assert.Equal([true, true, false], filtered.Select(row => row.IsContext));
        Assert.Equal([true, true, false], filtered.Select(row => row.IsExpanded));
        Assert.Empty(expanded);
        Assert.Equal(["A"], Ids(hierarchy.Visible(null, expanded)));
        Assert.Equal(["A"], Ids(hierarchy.Visible(new HashSet<Guid> { a.Requirement.InternalId }, expanded)));
        Assert.Empty(hierarchy.Visible(new HashSet<Guid>(), expanded));
        // Matches already in required must not stop ancestry traversal prematurely.
        Assert.Equal(["A", "B", "C"], Ids(hierarchy.Visible(new HashSet<Guid>
            { b.Requirement.InternalId, c.Requirement.InternalId }, expanded)));
    }

    [Fact]
    [Trait("Req", "REQ-REQ-002")]
    public void EmptyAndDeepForestsAreCompleteAndMalformedHierarchiesFailInsteadOfOmittingRows()
    {
        Assert.Empty(RequirementHierarchy.Create([]).Value.Visible(null, new HashSet<Guid>()));
        var a = Row("A"); var b = Row("B");
        Assert.True(RequirementHierarchy.Create([new(a, b)]).IsFailure);
        Assert.True(RequirementHierarchy.Create([new(a, b), new(b, a)]).IsFailure);
        Assert.True(RequirementHierarchy.Create([new(a, a)]).IsFailure);
        var items = new List<RequirementHierarchyItem>();
        RequirementSnapshot? parent = null;
        for (var i = 0; i < 3000; i++)
        {
            var child = Row($"R-{i:0000}"); items.Add(new(child, parent)); parent = child;
        }
        var hierarchy = RequirementHierarchy.Create(items).Value;
        var all = hierarchy.Visible(null, items.Select(item => item.Snapshot.Requirement.InternalId).ToHashSet());
        Assert.Equal(3000, all.Count); Assert.Equal(2999, all[^1].Depth);
        var filtered = hierarchy.Visible(new HashSet<Guid> { parent!.Requirement.InternalId }, new HashSet<Guid>());
        Assert.Equal(3000, filtered.Count);
    }

    private static IEnumerable<string> Ids(IEnumerable<RequirementHierarchyRow> rows) => rows.Select(row => row.Item.Snapshot.Requirement.Id);
}
