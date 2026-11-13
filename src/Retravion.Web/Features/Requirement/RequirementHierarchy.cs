using CSharpFunctionalExtensions;
using Retravion.Application.Requirement;

namespace Retravion.Web.Features.Requirement;

internal sealed record RequirementHierarchyItem(RequirementSnapshot Snapshot, RequirementSnapshot? Parent);
internal sealed record RequirementHierarchyRow(RequirementHierarchyItem Item, int Depth, bool HasChildren,
    bool IsContext, bool IsExpanded);

/// <summary>A current-list projection, not an exact-version baseline or a relationship mutation.</summary>
internal sealed class RequirementHierarchy
{
    private readonly IReadOnlyList<(RequirementHierarchyItem Item, int Depth)> ordered;
    private readonly Dictionary<Guid, RequirementHierarchyItem[]> children;

    private RequirementHierarchy(IReadOnlyList<(RequirementHierarchyItem, int)> ordered,
        Dictionary<Guid, RequirementHierarchyItem[]> children) => (this.ordered, this.children) = (ordered, children);

    internal IEnumerable<RequirementSnapshot> Snapshots => ordered.Select(row => row.Item.Snapshot);

    internal static Result<RequirementHierarchy> Create(IReadOnlyList<RequirementHierarchyItem> items)
    {
        var byId = items.ToDictionary(item => item.Snapshot.Requirement.InternalId);
        if (items.Any(item => item.Parent is { } parent &&
            (!byId.ContainsKey(parent.Requirement.InternalId) ||
             parent.Requirement.ProductInternalId != item.Snapshot.Requirement.ProductInternalId)))
            return Result.Failure<RequirementHierarchy>("The Requirement hierarchy is incomplete. Reload the list.");

        var children = items.Where(item => item.Parent is not null)
            .GroupBy(item => item.Parent!.Requirement.InternalId)
            .ToDictionary(group => group.Key, group => Sort(group).ToArray());
        var pending = new Stack<(RequirementHierarchyItem Item, int Depth)>(
            Sort(items.Where(item => item.Parent is null)).Reverse().Select(item => (item, 0)));
        var ordered = new List<(RequirementHierarchyItem Item, int Depth)>();
        // Iterative traversal supports deeply nested branches without consuming the call stack.
        while (pending.TryPop(out var next))
        {
            ordered.Add(next);
            if (children.TryGetValue(next.Item.Snapshot.Requirement.InternalId, out var descendants))
                foreach (var child in descendants.Reverse()) pending.Push((child, next.Depth + 1));
        }
        // A single-parent forest with missing parents already rejected can leave rows unvisited only for a cycle.
        if (ordered.Count != items.Count)
            return Result.Failure<RequirementHierarchy>("The Requirement hierarchy could not be read completely. Reload the list.");
        return Result.Success(new RequirementHierarchy(ordered, children));
    }

    internal IReadOnlyList<RequirementHierarchyRow> Visible(IReadOnlySet<Guid>? matches, IReadOnlySet<Guid> expanded)
    {
        HashSet<Guid>? required = null;
        if (matches is not null)
        {
            required = matches.ToHashSet();
            var byId = ordered.ToDictionary(row => row.Item.Snapshot.Requirement.InternalId, row => row.Item);
            foreach (var id in matches)
                for (var parent = byId[id].Parent; parent is not null && required.Add(parent.Requirement.InternalId);
                     parent = byId[parent.Requirement.InternalId].Parent) { }
        }

        var visible = new List<RequirementHierarchyRow>();
        var open = new HashSet<Guid>();
        foreach (var (item, depth) in ordered)
        {
            var id = item.Snapshot.Requirement.InternalId;
            if (required is not null ? !required.Contains(id) : item.Parent is { } parent && !open.Contains(parent.Requirement.InternalId))
                continue;
            var hasChildren = children.TryGetValue(id, out var descendants);
            var isExpanded = hasChildren && (required is null ? expanded.Contains(id) :
                descendants!.Any(child => required.Contains(child.Snapshot.Requirement.InternalId)));
            visible.Add(new(item, depth, hasChildren, matches is not null && !matches.Contains(id), isExpanded));
            if (isExpanded) open.Add(id);
        }
        return visible;
    }

    private static IOrderedEnumerable<RequirementHierarchyItem> Sort(IEnumerable<RequirementHierarchyItem> items) =>
        items.OrderBy(item => item.Snapshot.Requirement.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Snapshot.Requirement.InternalId);
}
