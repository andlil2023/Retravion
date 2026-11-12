using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.UserNeed;
using Retravion.Application.Requirement;
using Retravion.Application.TestCase;
using Retravion.Application.Feature;
using Retravion.Application.Revision;
using Retravion.Domain.Product;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.UserNeed;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.Revision;
using Retravion.Web.Features.Mcp;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class McpDiscoveryTests
{
    private sealed record Row(string Id, string Title, string Status, Guid Identity);
    private static readonly Row[] Rows = Enumerable.Range(0, 205)
        .Select(i => new Row($"ID-{i:D3}", i % 2 == 0 ? "Login" : "Other", i % 2 == 0 ? "Draft" : "Approved", new Guid(i, 0, 0, new byte[8])))
        .Reverse().ToArray();

    private static JsonElement Page(string? search = null, string? status = null, int? offset = null, int? limit = null)
    {
        var result = CollectionDiscovery.Result(Rows, r => r.Id, r => r.Title, r => r.Status, r => r.Identity,
            r => r, ["Draft", "Approved"], search, status, offset, limit);
        Assert.NotEqual(true, result.IsError);
        return JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text).RootElement.Clone();
    }

    [Fact]
    public void DiscoveryPreservesLegacyArrayAndPagesDeterministicallyWithExplicitBoundaries()
    {
        var legacy = Page();
        Assert.Equal(205, legacy.GetArrayLength());
        Assert.Equal("ID-000", legacy[0].GetProperty("id").GetString());
        var first = Page(search: "");
        Assert.Equal(50, first.GetProperty("items").GetArrayLength());
        Assert.Equal(205, first.GetProperty("total").GetInt32());
        Assert.Equal(155, first.GetProperty("remaining").GetInt32());
        Assert.Equal(50, first.GetProperty("nextOffset").GetInt32());
        Assert.False(first.GetProperty("complete").GetBoolean());
        var second = Page(offset: 50, limit: 200);
        Assert.Equal("ID-050", second.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(155, second.GetProperty("items").GetArrayLength());
        Assert.Equal(0, second.GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
        Assert.False(second.GetProperty("complete").GetBoolean());
        var beyond = Page(offset: int.MaxValue, limit: 200);
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.Equal(0, beyond.GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.Null, beyond.GetProperty("nextOffset").ValueKind);
        Assert.False(beyond.GetProperty("complete").GetBoolean());
        var filtered = Page(search: "LOGIN", status: "draft", limit: 200);
        Assert.Equal(103, filtered.GetProperty("total").GetInt32());
        Assert.True(filtered.GetProperty("complete").GetBoolean());
        Assert.Single(Page(search: "id-004", limit: 1).GetProperty("items").EnumerateArray());
        Assert.Equal(0, Page(search: "Other", status: "Draft").GetProperty("total").GetInt32());
        Assert.True(Page(search: "absent").GetProperty("complete").GetBoolean());
    }

    [Theory]
    [InlineData(-1, 50, null, null)]
    [InlineData(0, 0, null, null)]
    [InlineData(0, 201, null, null)]
    [InlineData(0, 50, "Unknown", null)]
    [InlineData(0, 50, "1", null)]
    [InlineData(0, 50, null, "oversized")]
    public void DiscoveryRejectsInvalidBoundsAndStatuses(int offset, int limit, string? status, string? search)
    {
        var result = CollectionDiscovery.Result(Rows, r => r.Id, r => r.Title, r => r.Status, r => r.Identity,
            r => r, ["Draft", "Approved"], search is null ? null : new string('x', 201), status, offset, limit);
        Assert.True(result.IsError);
        Assert.DoesNotContain("ID-", ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public async Task DiscoveryRejectsMalformedAndDuplicateArgumentsAndUnauthorizedRequests()
    {
        await using var host = new McpTestHost();
        var arguments = new { productId = "Hidden", search = "secret", status = "Draft", offset = 0, limit = 1 };
        using (var denied = await host.Send("tools/call", new { name = "list_requirements", arguments }))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await host.Login(); await host.Replace(); await host.Authenticate();
        foreach (var value in new object[] { new { limit = "1" }, new { offset = 1.5 }, new { search = 1 }, new { status = true }, new { limit = 2147483648L } })
        {
            using var malformed = await host.Send("tools/call", new { name = "list_products", arguments = value });
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }
        using var duplicate = JsonDocument.Parse("{\"limit\":1,\"limit\":2}");
        using var response = await host.Send("tools/call", new { name = "list_products", arguments = duplicate.RootElement });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task McpDiscoverySupportsAllCollectionContractsWithoutCrossProductCountsOrRevokedAccess()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var areas = new PostgresProductAreaStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var cases = new PostgresTestCaseStore(database.ConnectionString);
        var features = new PostgresFeatureStore(database.ConnectionString);
        var revisions = new PostgresRevisionStore(database.ConnectionString);
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IProductStore>(products); services.AddSingleton<IProductAreaStore>(areas);
            services.AddSingleton<IUserNeedStore>(needs); services.AddSingleton<IUserNeedChangeTransaction>(needs);
            services.AddSingleton<IRequirementStore>(requirements); services.AddSingleton<IRequirementChangeTransaction>(requirements);
            services.AddSingleton<IRequirementLinkStore>(requirements);
            services.AddSingleton<ITestCaseStore>(cases); services.AddSingleton<ITestCaseChangeTransaction>(cases);
            services.AddSingleton<IFeatureStore>(features); services.AddSingleton<IFeatureChangeTransaction>(features);
            services.AddSingleton<IRevisionStore>(revisions); services.AddSingleton<IRevisionChangeTransaction>(revisions);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        foreach (var id in new[] { "P", "Other" })
        {
            var product = (await new CreateProduct(products).Execute(id, "Login Product", ProductKind.System, "")).Value;
            await AreaCreate(areas, product.InternalId, "LOGIN", "Login Area");
            await host.Call("create_draft_user_need", new { productId = id, id = "UN-1", title = "Login", statement = "Statement" });
            await host.Call("create_draft_requirement", new { productId = id, id = "REQ-1", title = "Login", statement = "Statement", type = "Software" });
            await host.Call("create_draft_test_case", new { productId = id, title = "Login" });
            await host.Call("create_draft_feature", new { productId = id, title = "Login" });
            await host.Call("create_draft_revision", new { productId = id, identifier = "R-1", description = "Login", confirmed = true });
        }
        var productPage = await host.Call("list_products", new { search = "login", status = "active", limit = 1 });
        Assert.Equal(2, productPage.GetProperty("total").GetInt32());
        Assert.Equal(1, productPage.GetProperty("remaining").GetInt32());
        foreach (var tool in new[] { "list_user_needs", "list_requirements", "list_test_cases", "list_features", "list_revisions", "list_product_areas" })
        {
            var status = tool == "list_product_areas" ? "Active" : "Draft";
            var legacy = await host.Call(tool, new { productId = "P" });
            Assert.Single(legacy.EnumerateArray());
            var page = await host.Call(tool, new { productId = "P", search = "LOGIN", status, limit = 1 });
            Assert.Equal(1, page.GetProperty("total").GetInt32());
            Assert.True(page.GetProperty("complete").GetBoolean());
            Assert.Equal(legacy[0].GetProperty("internalId").GetGuid(), page.GetProperty("items")[0].GetProperty("internalId").GetGuid());
            var empty = await host.Call(tool, new { productId = "P", status, offset = 1 });
            Assert.Equal(1, empty.GetProperty("total").GetInt32());
            Assert.Empty(empty.GetProperty("items").EnumerateArray());
            await host.Call(tool, new { productId = "Missing", search = "Login", limit = 1 }, true);
            await host.Call(tool, new { productId = "P", status = "invalid", limit = 1 }, true);
        }
        using (var revoke = await host.Revoke()) Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        foreach (var tool in new[] { "list_products", "list_user_needs", "list_requirements", "list_test_cases", "list_features", "list_revisions", "list_product_areas" })
        {
            using var denied = await host.Send("tools/call", new { name = tool, arguments = new { productId = "P", search = "Login", limit = 1 } });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.DoesNotContain("total", await denied.Content.ReadAsStringAsync());
        }
    }
}
