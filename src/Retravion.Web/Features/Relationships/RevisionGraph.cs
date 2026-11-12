using Retravion.Application.Revision;
using Retravion.Domain.Revision;

namespace Retravion.Web.Features.Relationships;

internal static class RevisionGraph
{
    internal static IReadOnlySet<Guid> Visible(IReadOnlyList<RevisionBaselineMember> members,
        IReadOnlyList<(Guid Owner, Guid Target, string Label, bool Dashed)> edges,
        IReadOnlySet<Guid> expanded, string filter, string search)
    {
        var indirect = edges.Select(e => e.Target).ToHashSet();
        bool Matches(RevisionBaselineMember m) => filter switch
        {
            "Directly selected" => m.Explicit,
            "Indirectly connected" => indirect.Contains(m.Endpoint.VersionId),
            _ => true
        };
        var visible = members.Where(m => m.Explicit && (m.Endpoint.Kind != RevisionEntityKind.Requirement || Matches(m)))
            .Select(m => m.Endpoint.VersionId).ToHashSet();
        // A filter or search reveals matching records and every explanatory path back to a selected root.
        var required = members.Where(m =>
            (search.Length == 0 || m.Identifier.Contains(search, StringComparison.OrdinalIgnoreCase) || m.Title.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            ((filter.Length > 0 && m.Endpoint.Kind == RevisionEntityKind.Requirement && Matches(m)) ||
             (search.Length > 0 && (m.Endpoint.Kind != RevisionEntityKind.Requirement || Matches(m)))))
            .Select(m => m.Endpoint.VersionId).ToHashSet();
        var pending = new Queue<Guid>(required);
        while (pending.TryDequeue(out var target))
            foreach (var edge in edges.Where(e => e.Target == target))
                if (required.Add(edge.Owner)) pending.Enqueue(edge.Owner);
        visible.UnionWith(required);
        pending = new Queue<Guid>(visible);
        while (pending.TryDequeue(out var owner))
        {
            if (!expanded.Contains(owner)) continue;
            foreach (var edge in edges.Where(e => e.Owner == owner))
            {
                var member = members.Single(m => m.Endpoint.VersionId == edge.Target);
                if (member.Endpoint.Kind == RevisionEntityKind.Requirement && !Matches(member)) continue;
                if (visible.Add(edge.Target)) pending.Enqueue(edge.Target);
            }
        }
        if (search.Length == 0) return visible;
        // Search retains ancestors rather than leaving disconnected matching nodes.
        visible.IntersectWith(required);
        return visible;
    }
}
