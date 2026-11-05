using System.Security.Claims;
using CSharpFunctionalExtensions;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Requirement;
using Retravion.Web.Features.UserNeed;

namespace Retravion.Web.Features.Relationships;

internal sealed record RelationshipNode(string Kind, Guid InternalId, Guid VersionId, Guid ProductInternalId,
    string Id, string Title, int VersionNumber, string Status, bool HasNewerVersion)
{
    public string LinksPath => $"/products/links?kind={Kind}&id={InternalId}&version={VersionId}";
    public string DetailsPath => $"/{(Kind == "requirement" ? "requirements" : "user-needs")}/details?id={InternalId}&version={VersionId}";
    public string Label => $"{(Kind == "requirement" ? "Requirement" : "User Need")} {Id} · v{VersionNumber}";
}

internal sealed record RelationshipEdge(RelationshipNode Owner, RelationshipNode Target, string Relationship)
{
    public string Ownership(bool productActive) => !productActive
        ? $"Owner: {Owner.Label}. Read-only: Product retired."
        : Owner.HasNewerVersion ? $"Owner: {Owner.Label}. Read-only: a newer owner version exists."
        : Owner.Status != "Draft" ? $"Owner: {Owner.Label}. Read-only: owner is {Owner.Status}."
        : $"Owner: {Owner.Label}. Draft owner eligible for editing; target status does not lock this reference.";
}

internal sealed record RelationshipView(RelationshipNode Center, Retravion.Domain.Product.Product Product,
    IReadOnlyList<RelationshipEdge> Edges);

internal sealed record RelationshipExploration(RelationshipView Root, IReadOnlyList<RelationshipView> Neighborhoods)
{
    public IReadOnlyList<RelationshipEdge> Edges => Neighborhoods.SelectMany(v => v.Edges)
        .DistinctBy(e => (e.Owner.VersionId, e.Target.VersionId, e.Relationship)).ToArray();
    public IReadOnlyList<Guid> Expanded => Neighborhoods.Skip(1).Select(v => v.Center.VersionId).ToArray();
}

// This Web-only read model composes the existing authorized feature contracts; no new mutation or MCP surface.
internal sealed class RelationshipOperations(RequirementOperations requirements, UserNeedOperations needs, ProductOperations products)
{
    internal async Task<Result<RelationshipView>> Load(ClaimsPrincipal actor, string? kind, Guid id, Guid? version,
        CancellationToken token = default)
    {
        if (kind is not ("requirement" or "user-need") || id == Guid.Empty || version is null || version == Guid.Empty)
            return Result.Failure<RelationshipView>("Select an exact Requirement or User Need version.");
        var center = await Node(actor, kind, id, version.Value, token);
        if (center.IsFailure) return Result.Failure<RelationshipView>(center.Error);
        var edges = new List<RelationshipEdge>();
        if (kind == "requirement")
        {
            var links = await requirements.Links(actor, id, version.Value, token);
            if (links.IsFailure) return Result.Failure<RelationshipView>(links.Error);
            foreach (var link in links.Value)
            {
                var target = await Node(actor, "user-need", link.UserNeedInternalId, link.UserNeedVersionId, token);
                if (target.IsFailure) return Result.Failure<RelationshipView>(target.Error);
                edges.Add(new(center.Value, target.Value, "Addresses User Need"));
            }
            var parent = await requirements.Parent(actor, id, version.Value, token);
            if (parent.IsFailure) return Result.Failure<RelationshipView>(parent.Error);
            if (parent.Value.HasValue)
            {
                var target = await Node(actor, "requirement", parent.Value.Value.RequirementInternalId, parent.Value.Value.VersionId, token);
                if (target.IsFailure) return Result.Failure<RelationshipView>(target.Error);
                edges.Add(new(center.Value, target.Value, "Child → parent Requirement"));
            }
        }
        var incoming = kind == "requirement"
            ? await requirements.Children(actor, id, version.Value, token)
            : await requirements.IncomingUserNeed(actor, id, version.Value, token);
        if (incoming.IsFailure) return Result.Failure<RelationshipView>(incoming.Error);
        foreach (var reference in incoming.Value)
        {
            var owner = await Node(actor, "requirement", reference.InternalId, reference.VersionId, token);
            if (owner.IsFailure) return Result.Failure<RelationshipView>(owner.Error);
            edges.Add(new(owner.Value, center.Value, kind == "requirement" ? "Child → parent Requirement" : "Addresses User Need"));
        }
        // Recheck live access before returning a complete view; never publish a partially loaded neighborhood.
        var availableProducts = await products.List(actor, token);
        if (availableProducts.IsFailure) return Result.Failure<RelationshipView>(availableProducts.Error);
        var product = availableProducts.Value.SingleOrDefault(p => p.InternalId == center.Value.ProductInternalId);
        if (product is null || edges.Any(e => e.Owner.ProductInternalId != product.InternalId || e.Target.ProductInternalId != product.InternalId))
            return Result.Failure<RelationshipView>("Relationship Product not available.");
        return new RelationshipView(center.Value, product, edges);
    }

    internal async Task<Result<RelationshipExploration>> Explore(ClaimsPrincipal actor, string? kind, Guid id,
        Guid? version, IReadOnlyCollection<Guid> expanded, CancellationToken token = default)
    {
        var root = await Load(actor, kind, id, version, token);
        if (root.IsFailure) return Result.Failure<RelationshipExploration>(root.Error);
        var neighborhoods = new List<RelationshipView> { root.Value };
        var visited = new HashSet<Guid> { root.Value.Center.VersionId };
        // Only traverse explicitly expanded, reachable exact versions. Each neighborhood is loaded once,
        // even in a non-hierarchical cycle; removing an expansion prunes unreachable descendants.
        for (var index = 0; index < neighborhoods.Count; index++)
        {
            foreach (var node in neighborhoods[index].Edges.SelectMany(e => new[] { e.Owner, e.Target }))
            {
                if (!expanded.Contains(node.VersionId) || !visited.Add(node.VersionId)) continue;
                var next = await Load(actor, node.Kind, node.InternalId, node.VersionId, token);
                if (next.IsFailure) return Result.Failure<RelationshipExploration>(next.Error);
                neighborhoods.Add(next.Value);
            }
        }
        // Do not publish previously loaded data if the session was revoked during expansion.
        var access = await products.List(actor, token);
        if (access.IsFailure) return Result.Failure<RelationshipExploration>(access.Error);
        return new RelationshipExploration(root.Value, neighborhoods);
    }

    private async Task<Result<RelationshipNode>> Node(ClaimsPrincipal actor, string kind, Guid id, Guid version, CancellationToken token)
    {
        if (kind == "requirement")
        {
            var found = await requirements.Find(actor, id, token, version);
            if (found.IsFailure) return Result.Failure<RelationshipNode>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<RelationshipNode>("Requirement version not found.");
            var versions = await requirements.Versions(actor, id, token);
            if (versions.IsFailure) return Result.Failure<RelationshipNode>(versions.Error);
            var r = found.Value.Value.Requirement;
            return new RelationshipNode(kind, r.InternalId, r.VersionId, r.ProductInternalId, r.Id, r.Title, r.VersionNumber,
                r.Status == Retravion.Domain.Requirement.RequirementStatus.InReview ? "In Review" : r.Status.ToString(),
                versions.Value.Any(v => v.Requirement.VersionNumber > r.VersionNumber));
        }
        var need = await needs.Find(actor, id, token, version);
        if (need.IsFailure) return Result.Failure<RelationshipNode>(need.Error);
        if (need.Value.HasNoValue) return Result.Failure<RelationshipNode>("User Need version not found.");
        var needVersions = await needs.Versions(actor, id, token);
        if (needVersions.IsFailure) return Result.Failure<RelationshipNode>(needVersions.Error);
        var n = need.Value.Value.Need;
        return new RelationshipNode(kind, n.InternalId, n.VersionId, n.ProductInternalId, n.Id, n.Title, n.VersionNumber,
            n.Status == Retravion.Domain.UserNeed.UserNeedStatus.InReview ? "In Review" : n.Status.ToString(),
            needVersions.Value.Any(v => v.Need.VersionNumber > n.VersionNumber));
    }
}
