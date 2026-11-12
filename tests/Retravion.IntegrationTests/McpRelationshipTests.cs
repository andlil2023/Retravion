using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpRelationshipContractRejectsInvalidBoundsMissingVersionsAndMutationInjection()
    {
        await using var host = new McpTestHost();
        using (var denied = await host.Send("tools/call", new { name = "explore_relationships", arguments = new { } }))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var tools = (await host.Rpc("tools/list")).GetProperty("tools").EnumerateArray().ToArray();
        var tool = Assert.Single(tools, t => t.GetProperty("name").GetString() == "explore_relationships");
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(new[] { "kind", "internalId", "versionId" }, tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(v => v.GetString()));
        Assert.DoesNotContain(tools, t => t.GetProperty("name").GetString()!.Contains("link", StringComparison.Ordinal));
        var valid = new Dictionary<string, object?> { ["kind"] = "requirement", ["internalId"] = Guid.NewGuid(), ["versionId"] = Guid.NewGuid() };
        async Task Reject(object arguments)
        {
            using var response = await host.Send("tools/call", new { name = "explore_relationships", arguments });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var name in valid.Keys)
        {
            var missing = new Dictionary<string, object?>(valid); missing.Remove(name); await Reject(missing);
            await Reject(new Dictionary<string, object?>(valid) { [name] = null });
        }
        foreach (var kind in new object[] { "revision", "Requirement", "", 1 }) await Reject(new Dictionary<string, object?>(valid) { ["kind"] = kind });
        foreach (var name in new[] { "internalId", "versionId" })
        foreach (var value in new object[] { Guid.Empty, "invalid", 1 }) await Reject(new Dictionary<string, object?>(valid) { [name] = value });
        foreach (var limit in new object?[] { 0, 201, -1, 1.5, "1", null }) await Reject(new Dictionary<string, object?>(valid) { ["limit"] = limit });
        foreach (var expanded in new object[] { "invalid", new[] { Guid.Empty }, Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToArray(), new[] { "invalid" } })
            await Reject(new Dictionary<string, object?>(valid) { ["expandedVersionIds"] = expanded });
        foreach (var field in new[] { "actor", "status", "operation", "target", "links" }) await Reject(new Dictionary<string, object?>(valid) { [field] = "injected" });
        // Duplicate fields must not have parser-dependent meanings.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"explore_relationships\",\"arguments\":{\"kind\":\"requirement\",\"kind\":\"feature\"}}}", System.Text.Encoding.UTF8, "application/json")
        };
        using var duplicate = await host.Mcp.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }
}

public sealed partial class PostgresProductTests
{
    private static Task<JsonElement> ExploreMcp(McpTestHost host, string kind, Guid id, Guid version, Guid[]? expanded = null, int limit = 100) =>
        host.Call("explore_relationships", new { kind, internalId = id, versionId = version, expandedVersionIds = expanded, limit });

    [PostgresFact]
    public async Task McpRelationshipsReadEveryKindBothDirectionsHistoricalEndpointsAndNeverWrite()
    {
        await using var f = await CaseRelationshipFixture.Create(linked: true);
        var store = f.Scope.ServiceProvider.GetRequiredService<IFeatureChangeTransaction>();
        var feature = (await store.Create(f.Data.Product.InternalId, FeatureContent.Create("Capability", ""), FeatureActor, FeatureTime)).Value;
        feature = (await store.ChangeLink(feature, "add", new(f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId), null, FeatureActor, FeatureTime)).Value;
        var before = (await f.Audit.FindAsync(new())).Count;
        var history = (await f.Requirements.History(f.Data.Owner.Requirement.InternalId)).Value.Count;
        var view = await ExploreMcp(f.Host, "requirement", f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId);
        var center = view.GetProperty("center");
        Assert.Equal(f.Data.Owner.Requirement.VersionId, center.GetProperty("versionId").GetGuid());
        Assert.Equal("Approved", center.GetProperty("status").GetString());
        Assert.True(center.GetProperty("hasNewerVersion").GetBoolean());
        Assert.Equal(f.Data.Product.InternalId, center.GetProperty("productInternalId").GetGuid());
        var edges = view.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(5, edges.Length); // Need, parent, child, Test Case and Feature.
        Assert.All(edges, e => Assert.Equal("owner-to-target", e.GetProperty("direction").GetString()));
        Assert.Contains(edges, e => e.GetProperty("target").GetProperty("versionId").GetGuid() == f.Data.Need.Need.VersionId && e.GetProperty("target").GetProperty("hasNewerVersion").GetBoolean());
        Assert.DoesNotContain(edges, e => e.GetProperty("target").GetProperty("versionId").GetGuid() == f.Data.NewNeed.Need.VersionId);
        foreach (var (kind, id, version, incoming) in new[]
        {
            ("user-need", f.Data.Need.Need.InternalId, f.Data.Need.Need.VersionId, true),
            ("test-case", f.Row.TestCase.InternalId, f.Row.TestCase.VersionId, false),
            ("feature", feature.Feature.InternalId, feature.Feature.VersionId, false)
        })
        {
            var next = await ExploreMcp(f.Host, kind, id, version);
            var edge = Assert.Single(next.GetProperty("edges").EnumerateArray());
            Assert.Equal(f.Data.Owner.Requirement.VersionId, edge.GetProperty(incoming ? "owner" : "target").GetProperty("versionId").GetGuid());
        }
        await f.Host.Call("explore_relationships", new { kind = "requirement", internalId = f.Data.Owner.Requirement.InternalId, versionId = Guid.NewGuid() }, true);
        await f.Host.Call("explore_relationships", new { kind = "feature", internalId = f.Data.Owner.Requirement.InternalId, versionId = f.Data.Owner.Requirement.VersionId }, true);
        Assert.True((await f.Products.Update(f.Data.Product, f.Data.Product.Retire().Value)).IsSuccess);
        var retired = await ExploreMcp(f.Host, "requirement", f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId);
        Assert.False(Assert.Single(retired.GetProperty("products").EnumerateArray()).GetProperty("isActive").GetBoolean());
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
        Assert.Equal(history, (await f.Requirements.History(f.Data.Owner.Requirement.InternalId)).Value.Count);
        await f.Host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(f.Actor.FindFirstValue(SessionTicketStore.SessionClaim)!);
        using var denied = await f.Host.Send("tools/call", new { name = "explore_relationships", arguments = new { kind = "requirement", internalId = f.Data.Owner.Requirement.InternalId, versionId = f.Data.Owner.Requirement.VersionId } });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.DoesNotContain("Owner", await denied.Content.ReadAsStringAsync());
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task McpRelationshipExpansionDeduplicatesCyclesDisclosesTruncationAndIgnoresUnreachableIds()
    {
        await using var f = await CaseRelationshipFixture.Create();
        var needs = new PostgresUserNeedStore(f.Database.ConnectionString);
        var second = (await needs.Create(Retravion.Domain.UserNeed.UserNeed.Create(f.Data.Product.InternalId, "N2", "Shared", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var a = await ParentDraft(f.Requirements, f.Data.Product.InternalId, "A");
        var b = await ParentDraft(f.Requirements, f.Data.Product.InternalId, "B");
        foreach (var need in new[] { f.Data.Need, second })
        {
            a = (await f.Requirements.ChangeLink(a, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
            b = (await f.Requirements.ChangeLink(b, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
        }
        var unreachable = f.Data.NewNeed.Need.VersionId;
        var expanded = new[] { a.Requirement.VersionId, b.Requirement.VersionId, second.Need.VersionId, a.Requirement.VersionId, f.Data.Need.Need.VersionId, unreachable };
        var view = await ExploreMcp(f.Host, "user-need", f.Data.Need.Need.InternalId, f.Data.Need.Need.VersionId, expanded);
        Assert.Equal(3, view.GetProperty("expandedVersionIds").GetArrayLength());
        Assert.Equal(unreachable, Assert.Single(view.GetProperty("unexpandedVersionIds").EnumerateArray()).GetGuid());
        Assert.Equal(5, view.GetProperty("edges").GetArrayLength());
        Assert.False(view.GetProperty("truncated").GetBoolean());
        var bounded = await ExploreMcp(f.Host, "user-need", f.Data.Need.Need.InternalId, f.Data.Need.Need.VersionId, expanded, 1);
        Assert.Single(bounded.GetProperty("edges").EnumerateArray());
        Assert.True(bounded.GetProperty("truncated").GetBoolean());
        Assert.Equal(4, bounded.GetProperty("omittedEdges").GetInt32());
        var collapsed = await ExploreMcp(f.Host, "user-need", f.Data.Need.Need.InternalId, f.Data.Need.Need.VersionId, new[] { second.Need.VersionId }, 200);
        Assert.Empty(collapsed.GetProperty("expandedVersionIds").EnumerateArray());
        Assert.Equal(3, collapsed.GetProperty("edges").GetArrayLength());
        Assert.DoesNotContain(collapsed.GetProperty("edges").EnumerateArray(), e => e.GetProperty("target").GetProperty("versionId").GetGuid() == second.Need.VersionId);
    }

    [PostgresFact]
    public async Task McpRelationshipProvenanceKeepsCopyV1AndUnavailableStoredSnapshotsWithoutLiveEndpoints()
    {
        await using var f = await CaseRelationshipFixture.Create();
        var needs = new PostgresUserNeedStore(f.Database.ConnectionString);
        var destination = (await new Retravion.Application.Product.Create.CreateProduct(f.Products).Execute("COPY-PRODUCT", "Copies", Retravion.Domain.Product.ProductKind.System, "")).Value;
        var sourceRequirement = (await f.Requirements.Find(f.Data.Owner.Requirement.InternalId, versionId: f.Data.Owner.Requirement.VersionId)).Value.Value;
        var sourceNeed = (await needs.Find(f.Data.Need.Need.InternalId, versionId: f.Data.Need.Need.VersionId)).Value.Value;
        var copy = (await f.Requirements.Derive(sourceRequirement, Retravion.Domain.Requirement.Requirement.Create(destination.InternalId, "COPY", "Copy", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var copyNeed = (await needs.Derive(sourceNeed, Retravion.Domain.UserNeed.UserNeed.Create(destination.InternalId, "N-COPY", "Copy", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var approved = await ParentApprove(f.Requirements, copy);
        var successor = (await f.Requirements.CreateSuccessor(approved, "Next copy", RequirementActor, RequirementTime)).Value;
        var baseline = (await f.Audit.FindAsync(new())).Count;
        var reverse = await ExploreMcp(f.Host, "requirement", sourceRequirement.Requirement.InternalId, sourceRequirement.Requirement.VersionId);
        var provenance = Assert.Single(reverse.GetProperty("edges").EnumerateArray(), e => e.GetProperty("isProvenance").GetBoolean());
        Assert.Equal(copy.Requirement.VersionId, provenance.GetProperty("owner").GetProperty("versionId").GetGuid());
        Assert.Equal("copy-to-source", provenance.GetProperty("direction").GetString());
        foreach (var (kind, id, version, sourceVersion) in new[]
        {
            ("requirement", successor.Requirement.InternalId, successor.Requirement.VersionId, sourceRequirement.Requirement.VersionId),
            ("user-need", copyNeed.Need.InternalId, copyNeed.Need.VersionId, f.Data.Need.Need.VersionId)
        })
        {
            var forward = await ExploreMcp(f.Host, kind, id, version);
            var edge = Assert.Single(forward.GetProperty("edges").EnumerateArray(), e => e.GetProperty("isProvenance").GetBoolean());
            Assert.Equal(sourceVersion, edge.GetProperty("target").GetProperty("versionId").GetGuid());
            Assert.Empty(forward.GetProperty("expandedVersionIds").EnumerateArray());
        }
        var missing = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(f.Database.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var table in new[] { "retravion_requirement.provenance", "retravion_need.provenance" })
            {
                await using var command = new NpgsqlCommand($"UPDATE {table} SET source_snapshot=jsonb_set(source_snapshot, '{{SourceVersionId}}', to_jsonb($1::text))", connection);
                command.Parameters.AddWithValue(missing.ToString()); await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (kind, id, version) in new[] { ("requirement", copy.Requirement.InternalId, copy.Requirement.VersionId), ("user-need", copyNeed.Need.InternalId, copyNeed.Need.VersionId) })
        {
            var unavailable = await ExploreMcp(f.Host, kind, id, version, new[] { missing });
            Assert.Empty(unavailable.GetProperty("edges").EnumerateArray());
            Assert.Empty(unavailable.GetProperty("expandedVersionIds").EnumerateArray());
            var snapshot = Assert.Single(unavailable.GetProperty("unavailableOrigins").EnumerateArray());
            Assert.False(snapshot.GetProperty("available").GetBoolean()); Assert.True(snapshot.GetProperty("readOnly").GetBoolean());
            Assert.Equal(missing, snapshot.GetProperty("sourceVersionId").GetGuid());
            Assert.Equal("Statement", snapshot.GetProperty("sourceStatement").GetString());
            Assert.Contains("Not an expandable endpoint", snapshot.GetProperty("context").GetString());
        }
        Assert.Equal(baseline, (await f.Audit.FindAsync(new())).Count);
    }
}
