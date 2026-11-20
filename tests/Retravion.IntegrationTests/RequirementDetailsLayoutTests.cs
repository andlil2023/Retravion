using System.Text.RegularExpressions;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task RequirementCompactDetailsKeepActionsAndHistoryInReadingOrder()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("SP-001", "Retravion", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        await using var host = RequirementMcpHost(products, store, store, database.ConnectionString);
        await host.Login(); await host.Replace(); await host.Authenticate();
        await host.Call("create_draft_requirement", new { productId = "SP-001", id = "REQ-UI", title = "Compact title", statement = "Statement content", type = "Software", rationale = "Rationale content" });
        var requirement = Assert.Single((await store.List(product.InternalId)).Value).Requirement;
        var html = await host.Browser.GetStringAsync($"/requirements/details?id={requirement.InternalId}&version={requirement.VersionId}");

        Assert.True(html.IndexOf("aria-label=\"Product context\"", StringComparison.Ordinal) < html.IndexOf("Compact title", StringComparison.Ordinal));
        Assert.Contains("SP-001", html); Assert.Contains("requirement-id", html); Assert.Contains("REQ-UI", html);
        Assert.Contains("Version 1", html); Assert.Contains("requirement-status", html);
        Assert.Contains("Type: Software", html); Assert.Contains("Unclassified", html);
        Assert.Contains("Statement content", html); Assert.Contains("Rationale content", html);
        Assert.Matches("<summary[^>]*>Versions</summary>", html);
        Assert.Matches("<nav[^>]*aria-label=\"Requirement versions\"", html);
        Assert.Matches("<a[^>]*aria-current=\"page\"", html);
        Assert.Contains($"version={requirement.VersionId}", html);
        var actions = Regex.Match(html, "<div\\b(?=[^>]*aria-label=\"Requirement actions\")[^>]*>[\\s\\S]*?</div>");
        Assert.True(actions.Success);
        Assert.Contains("mud-button-filled-primary", actions.Value);
        Assert.True(actions.Value.IndexOf(">Edit<", StringComparison.Ordinal) < actions.Value.IndexOf("Manage links", StringComparison.Ordinal));
        Assert.Contains("Copy to another Product", actions.Value);
        Assert.True(html.LastIndexOf("Submit for review", StringComparison.Ordinal) < html.IndexOf("id=\"requirement-history-heading\"", StringComparison.Ordinal));
        Assert.Contains("No review decisions yet.", html);
    }
}
