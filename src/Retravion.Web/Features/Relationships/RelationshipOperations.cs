using System.Security.Claims;
using CSharpFunctionalExtensions;
using Retravion.Application.Requirement;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Web.Features.Feature;
using Retravion.Application.TestCase;
using Retravion.Domain.TestCase;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Requirement;
using Retravion.Web.Features.TestCase;
using Retravion.Web.Features.UserNeed;

namespace Retravion.Web.Features.Relationships;

internal sealed record RelationshipNode(string Kind, Guid InternalId, Guid VersionId, Guid ProductInternalId,
    string Id, string Title, int VersionNumber, string Status, bool HasNewerVersion)
{
    // Retain the displayed owner's concurrency token; never refresh it silently when saving.
    internal RequirementSnapshot? Snapshot { get; init; }
    internal TestCaseSnapshot? TestCaseSnapshot { get; init; }
    internal FeatureSnapshot? FeatureSnapshot { get; init; }
    public bool Editable => Kind is "requirement" or "test-case" or "feature" && Status == "Draft" && !HasNewerVersion;
    public string LinksPath => $"/products/links?kind={Kind}&id={InternalId}&version={VersionId}";
    public string DetailsPath => $"/{(Kind == "requirement" ? "requirements" : Kind == "test-case" ? "test-cases" : Kind == "feature" ? "features" : "user-needs")}/details?id={InternalId}&version={VersionId}";
    public string Label => $"{(Kind == "requirement" ? "Requirement" : Kind == "test-case" ? "Test Case" : Kind == "feature" ? "Feature" : "User Need")} {Id} · v{VersionNumber}";
}

internal sealed record RelationshipEdge(RelationshipNode Owner, RelationshipNode Target, string Relationship)
{
    public bool IsProvenance => Relationship == RelationshipOperations.ProvenanceRelationship;
    public string Ownership(bool productActive) => IsProvenance ? "Read-only provenance; not an ordinary link." : !productActive
        ? $"Owner: {Owner.Label}. Read-only: Product retired."
        : Owner.HasNewerVersion ? $"Owner: {Owner.Label}. Read-only: a newer owner version exists."
        : Owner.Status != "Draft" ? $"Owner: {Owner.Label}. Read-only: owner is {Owner.Status}."
        : $"Owner: {Owner.Label}. Draft owner eligible for editing; target status does not lock this reference.";
}

internal sealed record UnavailableOrigin(RelationshipNode Copy, Guid SourceProductId, Guid SourceId, Guid SourceVersionId,
    int SourceVersionNumber, string SourceStatus, string SourceManualId, string SourceTitle, string SourceStatement,
    string? SourceType = null, string? SourceRationale = null);

internal sealed record RelationshipView(RelationshipNode Center, Retravion.Domain.Product.Product Product,
    IReadOnlyList<RelationshipEdge> Edges, IReadOnlyList<UnavailableOrigin> UnavailableOrigins);

internal sealed record RelationshipExploration(RelationshipView Root, IReadOnlyList<RelationshipView> Neighborhoods,
    IReadOnlyList<Retravion.Domain.Product.Product> Products)
{
    public IReadOnlyList<RelationshipEdge> Edges => Neighborhoods.SelectMany(v => v.Edges)
        .DistinctBy(e => (e.Owner.VersionId, e.Target.VersionId, e.Relationship)).ToArray();
    public IReadOnlyList<Guid> Expanded => Neighborhoods.Skip(1).Select(v => v.Center.VersionId).ToArray();
    public IReadOnlyList<UnavailableOrigin> UnavailableOrigins => Neighborhoods.SelectMany(v => v.UnavailableOrigins)
        .DistinctBy(v => (v.Copy.VersionId, v.SourceVersionId)).ToArray();
}

// Web-only composition of existing authorized feature contracts; no new public or MCP surface.
internal sealed class RelationshipOperations(RequirementOperations requirements, UserNeedOperations needs, ProductOperations products,
    TestCaseOperations testCases, FeatureOperations features)
{
    internal const string NeedRelationship = "Addresses User Need";
    internal const string ParentRelationship = "Child → parent Requirement";
    internal const string TestCaseRelationship = "Verifies Requirement";
    internal const string FeatureRelationship = "Addresses Requirement";
    internal const string ProvenanceRelationship = "Derived from / Source for";

    internal async Task<Result> Change(ClaimsPrincipal actor, RelationshipNode owner, RelationshipNode target,
        string relationship, string operation, RelationshipNode? replacement = null, CancellationToken token = default)
    {
        if (relationship is not (NeedRelationship or ParentRelationship or TestCaseRelationship or FeatureRelationship))
            return Result.Failure("Unsupported relationship change.");
        if (owner.Kind == "feature" && owner.FeatureSnapshot is { } feature && relationship == FeatureRelationship)
        {
            var result = await features.ChangeLink(actor, feature, operation, new FeatureRequirementLink(target.InternalId, target.VersionId),
                replacement is null ? null : new FeatureRequirementLink(replacement.InternalId, replacement.VersionId), token);
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        if (owner.Kind == "test-case" && owner.TestCaseSnapshot is { } testCase && relationship == TestCaseRelationship)
        {
            var result = await testCases.ChangeLink(actor, testCase, operation,
                new TestCaseRequirementLink(target.InternalId, target.VersionId),
                replacement is null ? null : new TestCaseRequirementLink(replacement.InternalId, replacement.VersionId),
                confirmed: operation is "remove" or "retarget", token: token);
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        if (owner.Snapshot is { } snapshot && operation == "retarget" && replacement is not null)
        {
            var result = relationship == NeedRelationship
                ? await requirements.RetargetLink(actor, snapshot, new(target.InternalId, target.VersionId),
                    new(replacement.InternalId, replacement.VersionId), token)
                : await requirements.RetargetParent(actor, snapshot, new(target.InternalId, target.VersionId),
                    new(replacement.InternalId, replacement.VersionId), token);
            return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
        }
        if (owner.Snapshot is null || operation is not ("add" or "remove"))
            return Result.Failure("Unsupported relationship change.");
        var changed = await Change(actor, owner.Snapshot, target, relationship, operation == "add", token);
        return changed.IsSuccess ? Result.Success() : Result.Failure(changed.Error);
    }

    internal Task<Result<RequirementSnapshot>> Change(ClaimsPrincipal actor, RequirementSnapshot owner,
        RelationshipNode target, string relationship, bool add, CancellationToken token = default) =>
        relationship == NeedRelationship
            ? add ? requirements.AddLink(actor, owner, new(target.InternalId, target.VersionId), token)
                  : requirements.RemoveLink(actor, owner, new(target.InternalId, target.VersionId), token)
            : relationship == ParentRelationship
                ? add ? requirements.AddParent(actor, owner, new(target.InternalId, target.VersionId), token)
                      : requirements.RemoveParent(actor, owner, token)
                : Task.FromResult(Result.Failure<RequirementSnapshot>("Unsupported relationship change."));

    internal async Task<Result<IReadOnlyList<RelationshipNode>>> Candidates(ClaimsPrincipal actor, Guid product,
        string kind, bool ownersOnly, CancellationToken token = default)
    {
        var nodes = new List<RelationshipNode>();
        if (kind == "feature")
        {
            var records = await features.List(actor, product, token);
            if (records.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(records.Error);
            foreach (var record in records.Value)
            {
                var versions = await features.Versions(actor, record.Feature.InternalId, token);
                if (versions.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(versions.Error);
                foreach (var version in versions.Value)
                {
                    var node = await Node(actor, kind, version.Feature.InternalId, version.Feature.VersionId, token);
                    if (node.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(node.Error);
                    if (!ownersOnly || node.Value.Editable) nodes.Add(node.Value);
                }
            }
        }
        else if (kind == "test-case")
        {
            var records = await testCases.List(actor, product, token);
            if (records.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(records.Error);
            foreach (var record in records.Value)
            {
                var versions = await testCases.Versions(actor, record.TestCase.InternalId, token);
                if (versions.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(versions.Error);
                foreach (var version in versions.Value)
                {
                    var node = await Node(actor, kind, version.TestCase.InternalId, version.TestCase.VersionId, token);
                    if (node.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(node.Error);
                    if (!ownersOnly || node.Value.Editable) nodes.Add(node.Value);
                }
            }
        }
        else if (kind == "requirement")
        {
            var records = await requirements.List(actor, product, token);
            if (records.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(records.Error);
            foreach (var record in records.Value)
            {
                var versions = await requirements.Versions(actor, record.Requirement.InternalId, token);
                if (versions.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(versions.Error);
                foreach (var version in versions.Value)
                {
                    var node = await Node(actor, kind, version.Requirement.InternalId, version.Requirement.VersionId, token);
                    if (node.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(node.Error);
                    if (!ownersOnly || node.Value.Editable) nodes.Add(node.Value);
                }
            }
        }
        else
        {
            var records = await needs.List(actor, product, token);
            if (records.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(records.Error);
            foreach (var record in records.Value)
            {
                var versions = await needs.Versions(actor, record.Need.InternalId, token);
                if (versions.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(versions.Error);
                foreach (var version in versions.Value)
                {
                    var node = await Node(actor, kind, version.Need.InternalId, version.Need.VersionId, token);
                    if (node.IsFailure) return Result.Failure<IReadOnlyList<RelationshipNode>>(node.Error);
                    nodes.Add(node.Value);
                }
            }
        }
        return nodes.OrderBy(n => n.Id).ThenBy(n => n.VersionNumber).ToArray();
    }

    internal async Task<Result<RelationshipView>> Load(ClaimsPrincipal actor, string? kind, Guid id, Guid? version,
        CancellationToken token = default)
    {
        if (kind is not ("requirement" or "user-need" or "test-case" or "feature") || id == Guid.Empty || version is null || version == Guid.Empty)
            return Result.Failure<RelationshipView>("Select an exact Requirement, User Need, or Test Case version.");
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
        if (kind == "feature")
        {
            foreach (var link in center.Value.FeatureSnapshot!.Feature.Links)
            {
                var target = await Node(actor, "requirement", link.RequirementInternalId, link.RequirementVersionId, token);
                if (target.IsFailure) return Result.Failure<RelationshipView>(target.Error);
                edges.Add(new(center.Value, target.Value, FeatureRelationship));
            }
        }
        if (kind == "test-case")
        {
            foreach (var link in center.Value.TestCaseSnapshot!.TestCase.Links)
            {
                var target = await Node(actor, "requirement", link.RequirementInternalId, link.RequirementVersionId, token);
                if (target.IsFailure) return Result.Failure<RelationshipView>(target.Error);
                edges.Add(new(center.Value, target.Value, TestCaseRelationship));
            }
        }
        if (kind == "requirement")
        {
            var featureOwners = await Candidates(actor, center.Value.ProductInternalId, "feature", false, token);
            if (featureOwners.IsFailure) return Result.Failure<RelationshipView>(featureOwners.Error);
            foreach (var owner in featureOwners.Value.Where(n => n.FeatureSnapshot!.Feature.Links.Any(l =>
                l.RequirementInternalId == id && l.RequirementVersionId == version.Value)))
                edges.Add(new(owner, center.Value, FeatureRelationship));
            var owners = await Candidates(actor, center.Value.ProductInternalId, "test-case", false, token);
            if (owners.IsFailure) return Result.Failure<RelationshipView>(owners.Error);
            foreach (var owner in owners.Value.Where(n => n.TestCaseSnapshot!.TestCase.Links.Any(l =>
                l.RequirementInternalId == id && l.RequirementVersionId == version.Value)))
                edges.Add(new(owner, center.Value, TestCaseRelationship));
        }
        if (kind is "requirement" or "user-need")
        {
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
        }
        // Ordinary links remain within the center's Product. Provenance is the explicit exception.
        var availableProducts = await products.List(actor, token);
        if (availableProducts.IsFailure) return Result.Failure<RelationshipView>(availableProducts.Error);
        var product = availableProducts.Value.SingleOrDefault(p => p.InternalId == center.Value.ProductInternalId);
        if (product is null || edges.Any(e => e.Owner.ProductInternalId != product.InternalId || e.Target.ProductInternalId != product.InternalId))
            return Result.Failure<RelationshipView>("Relationship Product not available.");
        var unavailable = new List<UnavailableOrigin>();
        if (kind is "requirement" or "user-need")
        {
            var origins = await Origins(actor, center.Value, availableProducts.Value, token);
            if (origins.IsFailure) return Result.Failure<RelationshipView>(origins.Error);
            edges.AddRange(origins.Value.Edges);
            unavailable.AddRange(origins.Value.Unavailable);
        }
        // Recheck live access after all reads; never publish a partially loaded neighborhood.
        var access = await products.List(actor, token);
        if (access.IsFailure) return Result.Failure<RelationshipView>(access.Error);
        return new RelationshipView(center.Value, product, edges, unavailable);
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
        if (neighborhoods.SelectMany(n => n.Edges).SelectMany(e => new[] { e.Owner, e.Target })
            .Any(n => access.Value.All(p => p.InternalId != n.ProductInternalId)))
            return Result.Failure<RelationshipExploration>("Relationship Product not available.");
        return new RelationshipExploration(root.Value, neighborhoods, access.Value);
    }

    private async Task<Result<(IReadOnlyList<RelationshipEdge> Edges, IReadOnlyList<UnavailableOrigin> Unavailable)>> Origins(
        ClaimsPrincipal actor, RelationshipNode center, IReadOnlyList<Retravion.Domain.Product.Product> products,
        CancellationToken token)
    {
        var edges = new List<RelationshipEdge>();
        var unavailable = new List<UnavailableOrigin>();
        UnavailableOrigin? origin = null;
        if (center.Kind == "requirement")
        {
            var result = await requirements.Provenance(actor, center.InternalId, token);
            if (result.IsFailure) return Result.Failure<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>(result.Error);
            if (result.Value.HasValue)
            {
                var p = result.Value.Value;
                origin = new(center, p.SourceProductId, p.SourceRequirementId, p.SourceVersionId, p.SourceVersionNumber,
                    p.SourceStatus, p.SourceManualId, p.SourceTitle, p.SourceStatement, p.SourceType.ToString(), p.SourceRationale);
            }
            var copies = await requirements.CopiesOf(actor, center.VersionId, token);
            if (copies.IsFailure) return Result.Failure<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>(copies.Error);
            foreach (var copy in copies.Value.Where(c => products.Any(p => p.InternalId == c.Requirement.ProductInternalId)))
            {
                var node = await Node(actor, center.Kind, copy.Requirement.InternalId, copy.Requirement.VersionId, token);
                if (node.IsFailure) return Result.Failure<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>(node.Error);
                edges.Add(new(node.Value, center, ProvenanceRelationship));
            }
        }
        else
        {
            var result = await needs.Provenance(actor, center.InternalId, token);
            if (result.IsFailure) return Result.Failure<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>(result.Error);
            if (result.Value.HasValue)
            {
                var p = result.Value.Value;
                origin = new(center, p.SourceProductId, p.SourceNeedId, p.SourceVersionId, p.SourceVersionNumber,
                    p.SourceStatus, p.SourceManualId, p.SourceTitle, p.SourceStatement);
            }
            var copies = await needs.CopiesOf(actor, center.VersionId, token);
            if (copies.IsFailure) return Result.Failure<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>(copies.Error);
            foreach (var copy in copies.Value.Where(c => products.Any(p => p.InternalId == c.Need.ProductInternalId)))
            {
                var node = await Node(actor, center.Kind, copy.Need.InternalId, copy.Need.VersionId, token);
                if (node.IsFailure) return Result.Failure<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>(node.Error);
                edges.Add(new(node.Value, center, ProvenanceRelationship));
            }
        }
        if (origin is not null)
        {
            if (!products.Any(p => p.InternalId == origin.SourceProductId)) unavailable.Add(origin);
            else
            {
                var source = await Node(actor, center.Kind, origin.SourceId, origin.SourceVersionId, token);
                if (source.IsSuccess) edges.Add(new(center, source.Value, ProvenanceRelationship));
                else if (source.Error is "Requirement version not found." or "User Need version not found.") unavailable.Add(origin);
                else return Result.Failure<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>(source.Error);
            }
        }
        return Result.Success<(IReadOnlyList<RelationshipEdge>, IReadOnlyList<UnavailableOrigin>)>((edges, unavailable));
    }

    private async Task<Result<RelationshipNode>> Node(ClaimsPrincipal actor, string kind, Guid id, Guid version, CancellationToken token)
    {
        if (kind == "feature")
        {
            var found = await features.Find(actor, id, version, token);
            if (found.IsFailure) return Result.Failure<RelationshipNode>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<RelationshipNode>("Feature version not found.");
            var versions = await features.Versions(actor, id, token);
            if (versions.IsFailure) return Result.Failure<RelationshipNode>(versions.Error);
            var f = found.Value.Value.Feature;
            return new RelationshipNode(kind, f.InternalId, f.VersionId, f.ProductInternalId, f.Id, f.Content.Title,
                f.VersionNumber, f.Status == "InReview" ? "In Review" : f.Status,
                versions.Value.Any(v => v.Feature.VersionNumber > f.VersionNumber)) { FeatureSnapshot = found.Value.Value };
        }
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
                versions.Value.Any(v => v.Requirement.VersionNumber > r.VersionNumber)) { Snapshot = found.Value.Value };
        }
        if (kind == "test-case")
        {
            var found = await testCases.Find(actor, id, version, token);
            if (found.IsFailure) return Result.Failure<RelationshipNode>(found.Error);
            if (found.Value.HasNoValue) return Result.Failure<RelationshipNode>("Test Case version not found.");
            var versions = await testCases.Versions(actor, id, token);
            if (versions.IsFailure) return Result.Failure<RelationshipNode>(versions.Error);
            var c = found.Value.Value.TestCase;
            return new RelationshipNode(kind, c.InternalId, c.VersionId, c.ProductInternalId, c.Id, c.Content.Title,
                c.VersionNumber, c.Status == "InReview" ? "In Review" : c.Status,
                versions.Value.Any(v => v.TestCase.VersionNumber > c.VersionNumber)) { TestCaseSnapshot = found.Value.Value };
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
