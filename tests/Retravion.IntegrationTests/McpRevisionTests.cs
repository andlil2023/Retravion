using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.Product;
using Retravion.Application.Requirement;
using Retravion.Application.Revision;
using Retravion.Domain.Requirement;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Revision;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpRevisionDirectReadsRejectRotatedAndRevokedCredentialsBeforeStorage()
    {
        var store = new RevisionGateStore();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IRevisionStore>(store);
            services.AddSingleton<IRevisionChangeTransaction>(store);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var credentials = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>();
        var principal = credentials.Resolve(host.Mcp.DefaultRequestHeaders.Authorization!.Parameter);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<Retravion.Web.Features.Revision.RevisionOperations>();
        Assert.True(await operations.CanRead(principal));
        await host.Authenticate();
        Assert.False(await operations.CanRead(principal));
        Assert.True((await operations.List(principal, Guid.NewGuid())).IsFailure);
        principal = credentials.Resolve(host.Mcp.DefaultRequestHeaders.Authorization!.Parameter);
        using (var revoke = await host.Revoke()) Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        Assert.True((await operations.Baseline(principal, Guid.NewGuid(), Guid.NewGuid())).IsFailure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task McpRevisionReadContractsRemainReadOnlyAndRejectMalformedOrInjectedArguments()
    {
        await using var host = new McpTestHost();
        foreach (var name in new[] { "list_revisions", "read_revision" })
        {
            using var denied = await host.Send("tools/call", new { name, arguments = new { productId = "P", internalId = Guid.NewGuid() } });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        await host.Login(); await host.Replace(); await host.Authenticate();
        var tools = (await host.Rpc("tools/list")).GetProperty("tools").EnumerateArray()
            .Where(t => t.GetProperty("name").GetString() is "list_revisions" or "read_revision").ToArray();
        Assert.Equal(new[] { "list_revisions", "read_revision" }, tools.Select(t => t.GetProperty("name").GetString()).Order());
        Assert.All(tools, t => Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        foreach (var name in new[] { "list_revisions", "read_revision" })
        {
            foreach (var arguments in new object[] { new { }, new { productId = 12 },
                new { productId = "P", internalId = "invalid" }, new { productId = "P", actor = "spoofed" },
                new { productId = "P", selections = Array.Empty<object>() }, new { productId = "P", status = "Released" } })
            {
                using var invalid = await host.Send("tools/call", new { name, arguments });
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
        }
        foreach (var arguments in new object[] { new { productId = "P" }, new { productId = "P", internalId = Guid.Empty } })
        {
            using var invalid = await host.Send("tools/call", new { name = "read_revision", arguments });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using (var duplicate = await host.Mcp.PostAsync("/mcp", new StringContent(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"list_revisions\",\"arguments\":{\"productId\":\"P\",\"productId\":\"Q\"}}}",
            System.Text.Encoding.UTF8, "application/json"))) Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using (var origin = await host.Send("tools/call", new { name = "list_revisions", arguments = new { productId = "P" } }, "https://evil.invalid"))
            Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        using (var revoke = await host.Revoke()) Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        using var revoked = await host.Send("tools/call", new { name = "read_revision", arguments = new { productId = "P", internalId = Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }
}

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task McpRevisionDraftReadsIncludeDraftMembersAndFailRatherThanReturnPartialClosure()
    {
        await using var db = await Database.Create();
        var product = await AreaProduct(db.ConnectionString);
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var draft = await ParentDraft(requirements, product.InternalId, "Draft member");
        var store = new PostgresRevisionStore(db.ConnectionString);
        var row = await RevCreate(store, product.InternalId);
        row = (await store.Select(Guid.NewGuid(), row, "add", new RevisionSelection(RevisionEntityKind.Requirement,
            draft.Requirement.InternalId, draft.Requirement.VersionId, 1), null, RevActor, RevTime, RevAllowed)).Value;
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IProductStore>(new PostgresProductStore(db.ConnectionString));
            services.AddSingleton<IRevisionStore>(store);
            services.AddSingleton<IRevisionChangeTransaction>(store);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var args = new { productId = product.Id, internalId = row.Value.InternalId };
        var read = await host.Call("read_revision", args);
        Assert.Equal("Draft", Assert.Single(read.GetProperty("members").EnumerateArray()).GetProperty("status").GetString());
        await using var connection = new NpgsqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var drop = new NpgsqlCommand("DROP TABLE retravion_requirement.parent_link", connection); await drop.ExecuteNonQueryAsync();
        var failed = await host.Call("read_revision", args, true);
        Assert.Contains("complete outgoing reference storage", failed.GetString());
    }

    [PostgresFact]
    public async Task McpRevisionReadsPreserveCompleteExactClosureAndFrozenAreaEvidenceWithoutWrites()
    {
        await using var db = await Database.Create();
        var graph = await PrepareRelease(db.ConnectionString);
        var products = new PostgresProductStore(db.ConnectionString);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var areas = new PostgresProductAreaStore(db.ConnectionString);
        var area = await AreaCreate(areas, graph.Product.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var classified = await ParentApprove(requirements, (await requirements.Create(Requirement.Create(graph.Product.InternalId,
            "CLASSIFIED", "Recorded title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime)).Value);
        var row = (await store.Select(Guid.NewGuid(), graph.Row, "add", new RevisionSelection(RevisionEntityKind.Requirement,
            classified.Requirement.InternalId, classified.Requirement.VersionId, 1), null, RevActor, RevTime, RevAllowed)).Value;
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IProductStore>(products);
            services.AddSingleton<IRevisionStore>(store);
            services.AddSingleton<IRevisionChangeTransaction>(store);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var args = new { productId = graph.Product.Id, internalId = row.Value.InternalId };
        var audit = new PostgresAuditQuery(db.ConnectionString);
        var before = (await audit.FindAsync(new())).Count;
        var list = await host.Call("list_revisions", new { productId = graph.Product.Id });
        Assert.Single(list.EnumerateArray());
        Assert.Equal("Draft", list[0].GetProperty("state").GetString());
        var draft = await host.Call("read_revision", args);
        Assert.Equal("calculated-draft", draft.GetProperty("evidence").GetString());
        var members = draft.GetProperty("members").EnumerateArray().ToArray();
        Assert.Equal(9, members.Length); Assert.Equal(5, members.Count(m => m.GetProperty("explicit").GetBoolean()));
        foreach (var id in new[] { graph.Need1.Need.InternalId, graph.Parent1.Requirement.InternalId })
            Assert.Equal(new[] { 1, 2 }, members.Where(m => m.GetProperty("endpoint").GetProperty("internalId").GetGuid() == id)
                .Select(m => m.GetProperty("endpoint").GetProperty("versionNumber").GetInt32()).Order());
        Assert.Equal(5, draft.GetProperty("revision").GetProperty("selections").GetArrayLength());
        Assert.Equal(before, (await audit.FindAsync(new())).Count);
        await host.Call("read_revision", new { productId = graph.Product.Id, internalId = Guid.NewGuid() }, true);
        await host.Call("read_revision", new { productId = "missing", internalId = row.Value.InternalId }, true);
        var other = (await new Retravion.Application.Product.Create.CreateProduct(products).Execute("OTHER", "Other", Retravion.Domain.Product.ProductKind.System, "")).Value;
        await host.Call("read_revision", new { productId = other.Id, internalId = row.Value.InternalId }, true);
        var released = (await ReleaseNow(store, await ReleasePreview(store, row))).Value;
        area = (await areas.Save(Guid.NewGuid(), area, area.Area.Rename("Current replacement").Value, RevActor, RevTime, RevAllowed)).Value;
        await requirements.CreateSuccessor(classified, "Not baseline evidence", RevActor, RevTime);
        Assert.True((await products.Update(graph.Product, graph.Product.Retire().Value)).IsSuccess);
        before = (await audit.FindAsync(new())).Count;
        var frozen = await host.Call("read_revision", args);
        Assert.Equal("frozen-released", frozen.GetProperty("evidence").GetString());
        Assert.Equal("Released", frozen.GetProperty("revision").GetProperty("state").GetString());
        Assert.Equal(((ReleasedRevision)released.Value).ReleasedBy, frozen.GetProperty("revision").GetProperty("releasedBy").GetString());
        Assert.Equal(RevTime, frozen.GetProperty("revision").GetProperty("releasedAt").GetDateTimeOffset());
        Assert.Equal(draft.GetProperty("members").GetRawText(), frozen.GetProperty("members").GetRawText());
        var recorded = frozen.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("identifier").GetString() == "CLASSIFIED");
        Assert.Equal("Software", recorded.GetProperty("area").GetProperty("area").GetProperty("label").GetString());
        Assert.Equal("Released", (await host.Call("list_revisions", new { productId = graph.Product.Id }))[0].GetProperty("state").GetString());
        Assert.Equal(before, (await audit.FindAsync(new())).Count);
        // Frozen reads do not consult current reference storage at all.
        await using var connection = new NpgsqlConnection(db.ConnectionString); await connection.OpenAsync();
        await using var drop = new NpgsqlCommand("DROP TABLE retravion_requirement.parent_link", connection); await drop.ExecuteNonQueryAsync();
        Assert.Equal(frozen.GetRawText(), (await host.Call("read_revision", args)).GetRawText());
        var empty = await RevCreate(store, other.InternalId);
        var emptyRead = await host.Call("read_revision", new { productId = other.Id, internalId = empty.Value.InternalId });
        Assert.Empty(emptyRead.GetProperty("members").EnumerateArray());
    }
}
