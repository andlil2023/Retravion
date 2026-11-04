using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static RequirementParentLink ParentEndpoint(RequirementSnapshot row) => new(row.Requirement.InternalId, row.Requirement.VersionId);
    private static async Task<RequirementSnapshot> ParentDraft(PostgresRequirementStore store, Guid product, string id) =>
        (await store.Create(RequirementState.Create(product, id, "Title " + id, "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
    private static async Task<RequirementSnapshot> ParentApprove(PostgresRequirementStore store, RequirementSnapshot row)
    {
        var review = (await store.Update(row, row.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        return (await store.Update(review, review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
    }

    [PostgresFact]
    public async Task ParentLinksEnforceCardinalityExactVersionsApprovalAndPreserveOtherRelationships()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Other", "Other", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var child = await ParentDraft(store, product.InternalId, "Child");
        var parent = await ParentDraft(store, product.InternalId, "Parent");
        var replacement = await ParentDraft(store, product.InternalId, "Replacement");
        var foreign = await ParentDraft(store, other.InternalId, "Foreign");
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var count = (await audit.FindAsync(new())).Count;
        Assert.True((await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value.HasNoValue);
        Assert.True((await store.Parent(Guid.NewGuid(), child.Requirement.VersionId)).IsFailure);
        Assert.True((await store.AddParent(child, ParentEndpoint(foreign), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.AddParent(child, new(Guid.NewGuid(), Guid.NewGuid()), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.AddParent(child, new(Guid.NewGuid(), parent.Requirement.VersionId), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.AddParent(child, ParentEndpoint(parent), new AuditActor.Unknown(), RequirementTime)).IsFailure);
        Assert.True((await store.ReplaceParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(child, (await store.RemoveParent(child, RequirementActor, RequirementTime)).Value);
        Assert.Equal(count, (await audit.FindAsync(new())).Count);
        var unlinked = child;
        child = (await store.AddParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime)).Value;
        Assert.Equal(unlinked.Revision + 1, child.Revision);
        count = (await audit.FindAsync(new())).Count;
        Assert.True((await store.AddParent(child, ParentEndpoint(replacement), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ReplaceParent(child, ParentEndpoint(foreign), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ReplaceParent(unlinked, ParentEndpoint(replacement), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.RemoveParent(unlinked, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Update(unlinked, unlinked.Requirement, RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(child, (await store.ReplaceParent(child, ParentEndpoint(parent), RequirementActor, RequirementTime)).Value);
        Assert.Equal(count, (await audit.FindAsync(new())).Count);
        Assert.Equal(ParentEndpoint(parent), (await store.Parent(child.Requirement.InternalId, child.Requirement.VersionId)).Value.Value);
        var review = (await store.Update(child, child.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        count = (await audit.FindAsync(new())).Count;
        Assert.True((await store.Update(review, review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).IsFailure);
        foreach (var operation in new[] { store.AddParent(review, ParentEndpoint(replacement), RequirementActor, RequirementTime),
            store.RemoveParent(review, RequirementActor, RequirementTime), store.ReplaceParent(review, ParentEndpoint(replacement), RequirementActor, RequirementTime) })
            Assert.True((await operation).IsFailure);
        Assert.Equal(count, (await audit.FindAsync(new())).Count);
        parent = (await store.Update(parent, parent.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        Assert.True((await store.Update(review, review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).IsFailure);
        parent = (await store.Update(parent, parent.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var approved = (await store.Update(review, review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        Assert.True((await store.RemoveParent(approved, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.AddParent(approved, ParentEndpoint(replacement), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ReplaceParent(approved, ParentEndpoint(replacement), RequirementActor, RequirementTime)).IsFailure);
        var parent2 = (await store.CreateSuccessor(parent, "Next parent", RequirementActor, RequirementTime)).Value;
        var next = (await store.CreateSuccessor(approved, "Next child", RequirementActor, RequirementTime)).Value;
        Assert.Equal(ParentEndpoint(parent), (await store.Parent(next.Requirement.InternalId, next.Requirement.VersionId)).Value.Value);
        Assert.NotEqual(parent2.Requirement.VersionId, parent.Requirement.VersionId);
        next = (await store.ReplaceParent(next, ParentEndpoint(replacement), RequirementActor, RequirementTime)).Value;
        var descendant = await ParentDraft(store, product.InternalId, "Descendant");
        descendant = (await store.AddParent(descendant, ParentEndpoint(next), RequirementActor, RequirementTime)).Value;
        Assert.True((await store.ReplaceParent(next, ParentEndpoint(descendant), RequirementActor, RequirementTime)).IsFailure);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var need = (await needs.Create(Need.Create(product.InternalId, "N", "Need", "Statement").Value, RequirementActor, RequirementTime)).Value;
        next = (await store.ChangeLink(next, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
        next = (await store.RemoveParent(next, RequirementActor, RequirementTime)).Value;
        Assert.True((await store.Parent(next.Requirement.InternalId, next.Requirement.VersionId)).Value.HasNoValue);
        Assert.Equal(ParentEndpoint(next), (await store.Parent(descendant.Requirement.InternalId, descendant.Requirement.VersionId)).Value.Value);
        Assert.Equal(Endpoint(need), Assert.Single((await store.Links(next.Requirement.InternalId, next.Requirement.VersionId)).Value));
        Assert.Equal(ParentEndpoint(parent), (await store.Parent(approved.Requirement.InternalId, approved.Requirement.VersionId)).Value.Value);
        Assert.Equal(approved.Requirement, (await store.Find(approved.Requirement.InternalId, versionId: approved.Requirement.VersionId)).Value.Value.Requirement);
        var history = (await store.History(next.Requirement.InternalId)).Value;
        var inherited = Assert.Single(history, h => h.Decision == "Successor");
        Assert.Null(inherited.BeforeParent); Assert.Equal(ParentEndpoint(parent), inherited.AfterParent);
        var replaced = Assert.Single(history, h => h.Decision == "ParentReplaced");
        Assert.Equal(ParentEndpoint(parent), replaced.BeforeParent); Assert.Equal(ParentEndpoint(replacement), replaced.AfterParent);
        Assert.Equal(RequirementActor, replaced.Actor); Assert.Equal(RequirementTime, replaced.OccurredAt);
        Assert.Equal(next.Requirement.VersionId, replaced.VersionId); Assert.Equal("Child–parent Requirement", replaced.ParentRelationshipType);
        var removed = Assert.Single(history, h => h.Decision == "ParentRemoved");
        Assert.Equal(ParentEndpoint(replacement), removed.BeforeParent); Assert.Null(removed.AfterParent);
        var events = await audit.FindAsync(new());
        var added = Assert.Single(events, e => e.Action == "requirement.parent.add" && e.RequirementChange!.InternalId == child.Requirement.InternalId);
        Assert.Equal(RequirementActor, added.Actor); Assert.Equal(RequirementTime, added.OccurredAt);
        Assert.Equal(new AuditTarget.Identified(child.Requirement.InternalId.ToString()), added.Target); Assert.Equal(AuditActionOutcome.Success, added.Outcome);
        Assert.Null(added.RequirementChange!.BeforeParent); Assert.Equal(ParentEndpoint(parent), added.RequirementChange.AfterParent);
        Assert.Equal(child.Requirement.VersionId, added.RequirementChange.VersionId); Assert.Equal("Child–parent Requirement", added.RequirementChange.ParentRelationshipType);
        var changed = Assert.Single(events, e => e.Action == "requirement.parent.replace");
        Assert.Equal(ParentEndpoint(parent), changed.RequirementChange!.BeforeParent); Assert.Equal(ParentEndpoint(replacement), changed.RequirementChange.AfterParent);
        Assert.Equal(ParentEndpoint(replacement), Assert.Single(events, e => e.Action == "requirement.parent.remove").RequirementChange!.BeforeParent);
        var creation = Assert.Single(events, e => e.Action == "requirement.version.create" && e.RequirementChange!.InternalId == child.Requirement.InternalId);
        Assert.Null(creation.RequirementChange!.BeforeParent); Assert.Equal(ParentEndpoint(parent), creation.RequirementChange.AfterParent);
        Assert.DoesNotContain(events, e => e.Action == "requirement.parent.add" && e.RequirementChange!.VersionId == next.Requirement.VersionId);
        Assert.Equal(ParentEndpoint(parent), Assert.Single(events, e => e.Action == "requirement.review.Approved" && e.RequirementChange!.InternalId == child.Requirement.InternalId).RequirementChange!.AfterParent);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        count = (await audit.FindAsync(new())).Count;
        Assert.True((await store.AddParent(next, ParentEndpoint(replacement), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ReplaceParent(descendant, ParentEndpoint(parent), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.RemoveParent(descendant, RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(ParentEndpoint(next), (await new PostgresRequirementStore(database.ConnectionString).Parent(descendant.Requirement.InternalId, descendant.Requirement.VersionId)).Value.Value);
        Assert.Equal(count, (await audit.FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task ParentCyclesAreRejectedAcrossChainsAndEveryRetainedVersion()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var a = await ParentDraft(store, product.InternalId, "A"); var b = await ParentDraft(store, product.InternalId, "B"); var c = await ParentDraft(store, product.InternalId, "C");
        a = (await store.AddParent(a, ParentEndpoint(b), RequirementActor, RequirementTime)).Value;
        b = (await store.AddParent(b, ParentEndpoint(c), RequirementActor, RequirementTime)).Value;
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var count = (await audit.FindAsync(new())).Count;
        Assert.True((await store.AddParent(c, ParentEndpoint(a), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.AddParent(c, ParentEndpoint(c), RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(count, (await audit.FindAsync(new())).Count); Assert.Empty((await store.History(c.Requirement.InternalId)).Value);
        c = await ParentApprove(store, c); b = await ParentApprove(store, b); a = await ParentApprove(store, a);
        var a2 = (await store.CreateSuccessor(a, "Next A", RequirementActor, RequirementTime)).Value;
        a2 = (await store.RemoveParent(a2, RequirementActor, RequirementTime)).Value;
        var b2 = (await store.CreateSuccessor(b, "Next B", RequirementActor, RequirementTime)).Value;
        count = (await audit.FindAsync(new())).Count;
        Assert.True((await store.ReplaceParent(b2, ParentEndpoint(a2), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.AddParent(a2, ParentEndpoint(a), RequirementActor, RequirementTime)).IsFailure);
        Assert.Equal(ParentEndpoint(c), (await store.Parent(b2.Requirement.InternalId, b2.Requirement.VersionId)).Value.Value);
        Assert.Equal(count, (await audit.FindAsync(new())).Count);
        Assert.Equal(ParentEndpoint(b), (await store.Parent(a.Requirement.InternalId, a.Requirement.VersionId)).Value.Value);
        // Distinct exact-version chains can both be roots at their targets, but historical identities still prohibit reversal.
        var x = await ParentApprove(store, await ParentDraft(store, product.InternalId, "X"));
        var y = await ParentApprove(store, await ParentDraft(store, product.InternalId, "Y"));
        var x2 = (await store.CreateSuccessor(x, "Next X", RequirementActor, RequirementTime)).Value;
        x2 = (await store.AddParent(x2, ParentEndpoint(y), RequirementActor, RequirementTime)).Value;
        var y2 = (await store.CreateSuccessor(y, "Next Y", RequirementActor, RequirementTime)).Value;
        Assert.True((await store.AddParent(y2, ParentEndpoint(x), RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Parent(x.Requirement.InternalId, x.Requirement.VersionId)).Value.HasNoValue);
        Assert.True((await store.Parent(y.Requirement.InternalId, y.Requirement.VersionId)).Value.HasNoValue);
        Assert.Equal(ParentEndpoint(y), (await store.Parent(x2.Requirement.InternalId, x2.Requirement.VersionId)).Value.Value);
    }

    [PostgresFact]
    public async Task ConcurrentParentChangesCannotCreateIdentityCyclesOrLoseStaleProtection()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var a = await ParentDraft(store, product.InternalId, "A"); var b = await ParentDraft(store, product.InternalId, "B");
        var races = await Task.WhenAll(store.AddParent(a, ParentEndpoint(b), RequirementActor, RequirementTime), store.AddParent(b, ParentEndpoint(a), RequirementActor, RequirementTime));
        Assert.Single(races, r => r.IsSuccess); Assert.Single(races, r => r.IsFailure);
        var winner = Assert.Single(races, r => r.IsSuccess).Value;
        var before = winner.Requirement.InternalId == a.Requirement.InternalId ? a : b;
        Assert.True((await store.RemoveParent(before, RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.ReplaceParent(before, ParentEndpoint(winner), RequirementActor, RequirementTime)).IsFailure);
        var root = await ParentDraft(store, product.InternalId, "Root");
        var replacements = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.ReplaceParent(winner, ParentEndpoint(root), RequirementActor, RequirementTime)));
        Assert.Single(replacements, r => r.IsSuccess); Assert.Equal(3, replacements.Count(r => r.IsFailure));
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        Assert.Single(events, e => e.Action == "requirement.parent.add"); Assert.Single(events, e => e.Action == "requirement.parent.replace");
        Assert.Equal(ParentEndpoint(root), (await store.Parent(winner.Requirement.InternalId, winner.Requirement.VersionId)).Value.Value);
    }

    [PostgresFact]
    public async Task AuthorizedParentCallbacksUseSamePersistenceAndAccessBoundary()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var child = await ParentDraft(store, product.InternalId, "Child");
        var parent = await ParentDraft(store, product.InternalId, "Parent");
        var replacement = await ParentDraft(store, product.InternalId, "Replacement");
        await using var host = RequirementMcpHost(products, store, store);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RequirementOperations>();
        child = (await operations.AddParent(principal, child, ParentEndpoint(parent))).Value;
        child = (await operations.ReplaceParent(principal, child, ParentEndpoint(replacement))).Value;
        Assert.Equal(ParentEndpoint(replacement), (await operations.Parent(principal, child.Requirement.InternalId, child.Requirement.VersionId)).Value.Value);
        child = (await operations.RemoveParent(principal, child)).Value;
        Assert.True((await operations.Parent(principal, child.Requirement.InternalId, child.Requirement.VersionId)).Value.HasNoValue);
    }
}
