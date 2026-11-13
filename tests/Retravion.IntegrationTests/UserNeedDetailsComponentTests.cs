using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Domain.UserNeed;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Same real-callback renderer as the existing Requirement/User Need tests.
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task NeedCompactDetailsKeepContextVersionsActionsAndHistoryInReadingOrder()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("SP-001", "Retravion", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var first = (await store.Create(Need.Create(product.InternalId, "UN-001", "Compact title", "Statement content").Value, VersionActor, VersionTime)).Value;
        await using var host = NeedMcpHost(products, store, store);
        await host.Login(); await host.Replace();
        var path = $"/user-needs/details?id={first.Need.InternalId}";
        var html = await host.Browser.GetStringAsync(path);
        Assert.True(html.IndexOf("aria-label=\"Product context\"", StringComparison.Ordinal) < html.IndexOf("Compact title", StringComparison.Ordinal));
        Assert.Contains("SP-001", html); Assert.Contains("Retravion", html);
        Assert.Contains("need-id", html); Assert.Contains("UN-001", html);
        Assert.Contains("Version 1", html); Assert.Contains("need-status", html);
        Assert.Contains("need-badges", html);
        Assert.DoesNotContain("Move this Draft into review when it is ready", html);
        Assert.DoesNotContain("Status:", html);
        Assert.Matches("<summary[^>]*>Versions</summary>", html);
        Assert.Matches("<nav[^>]*aria-label=\"User Need versions\"", html);
        Assert.Contains($"version={first.Need.VersionId}", html);
        Assert.Matches("<a[^>]*aria-current=\"page\"", html);
        var actions = Regex.Match(html, "<div\\b(?=[^>]*aria-label=\"User Need actions\")[^>]*>[\\s\\S]*?</div>");
        Assert.True(actions.Success);
        Assert.True(actions.Index > html.IndexOf("Statement content", StringComparison.Ordinal));
        Assert.Contains(">Edit<", actions.Value);
        Assert.Contains("Manage links", actions.Value);
        Assert.True(actions.Value.IndexOf(">Edit<", StringComparison.Ordinal) < actions.Value.IndexOf("Manage links", StringComparison.Ordinal));
        Assert.Contains("mud-button-filled-primary", actions.Value);
        Assert.Contains($"/user-needs/edit?id={first.Need.InternalId}", actions.Value);
        Assert.Contains($"id={first.Need.InternalId}&amp;version={first.Need.VersionId}", actions.Value);
        Assert.Contains("Copy to another Product", actions.Value);
        Assert.Contains("id=\"need-history-heading\"", html);
        Assert.Contains("Submit for review", html);
        Assert.True(html.LastIndexOf("Submit for review", StringComparison.Ordinal) < html.IndexOf("id=\"need-history-heading\"", StringComparison.Ordinal));
        Assert.Contains("No review decisions yet.", html);

        var review = (await store.Update(first, first.Need.SubmitForReview().Value, VersionActor, VersionTime, "InReview", "Ready to review")).Value;
        html = await host.Browser.GetStringAsync(path);
        Assert.Contains("In Review", html);
        Assert.DoesNotContain($"/user-needs/edit?id={first.Need.InternalId}", html);
        Assert.Contains("Return to Draft", html);
        Assert.True(html.LastIndexOf("Return to Draft", StringComparison.Ordinal) < html.IndexOf("id=\"need-history-heading\"", StringComparison.Ordinal));
        Assert.Contains("Ready to review", html);
        var approved = (await store.Update(review, review.Need.Approve().Value, VersionActor, VersionTime, "Approved")).Value;
        html = await host.Browser.GetStringAsync(path);
        Assert.Contains("Create successor Draft", html); Assert.Contains("Reopen to Draft", html);
        Assert.True(html.LastIndexOf("Reopen to Draft", StringComparison.Ordinal) < html.IndexOf("id=\"need-history-heading\"", StringComparison.Ordinal));

        var second = (await store.CreateSuccessor(approved, "New wording", VersionActor, VersionTime)).Value;
        html = await host.Browser.GetStringAsync(path + $"&version={first.Need.VersionId}");
        Assert.Contains("earlier version is read-only", html);
        Assert.Contains("Version 1", html);
        Assert.Contains($"version={first.Need.VersionId}", html);
        Assert.Contains($"version={second.Need.VersionId}", html);
        Assert.DoesNotContain("id=\"need-review-heading\"", html);
        Assert.DoesNotContain($"/user-needs/edit?id={first.Need.InternalId}", html);
        Assert.Contains("Manage links", html); Assert.Contains("Review history", html);
        var currentVersionLink = Regex.Match(html, "<a\\b(?=[^>]*aria-current=\"page\")[^>]*>");
        Assert.Contains($"version={first.Need.VersionId}", currentVersionLink.Value);

        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        html = await host.Browser.GetStringAsync(path);
        Assert.Contains("Product retired", html); Assert.Contains("Version 2", html);
        Assert.Contains("Successor creation reason: New wording", html);
        Assert.DoesNotContain("id=\"need-review-heading\"", html);
        Assert.DoesNotContain($"/user-needs/edit?id={first.Need.InternalId}", html);
        Assert.Contains("Manage links", html); Assert.Contains("Review history", html);
    }

    [PostgresFact]
    public async Task NeedCompactDetailsPreserveReviewCallbacksCommentsAndPrimaryEdit()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var first = (await store.Create(Need.Create(product.InternalId, "UN-UI", "Title", "Statement").Value, VersionActor, VersionTime)).Value;
        await using var host = NeedMcpHost(products, store, store);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(new RequirementNavigation())
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<UserNeedOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(UserNeedDetails), new() { [nameof(UserNeedDetails.Id)] = first.Need.InternalId });
            var edit = renderer.Components<MudButton>().Single(b => b.Href == $"/user-needs/edit?id={first.Need.InternalId}");
            Assert.Equal(Variant.Filled, edit.Variant); Assert.Equal(Color.Primary, edit.Color);
            Assert.Equal(2, renderer.Field("Review comment (optional)").Lines);
            await renderer.Field("Review comment (optional)").ValueChanged.InvokeAsync("Ready for review");
            await renderer.Click("Submit for review");
            Assert.Equal(UserNeedStatus.InReview, (await store.Find(first.Need.InternalId)).Value.Value.Need.Status);
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.Href == edit.Href);
            await renderer.Field("Review comment (optional)").ValueChanged.InvokeAsync("Clarify wording");
            await renderer.Click("Return to Draft");
            Assert.Equal(UserNeedStatus.Draft, (await store.Find(first.Need.InternalId)).Value.Value.Need.Status);
            await renderer.Click("Submit for review"); await renderer.Click("Approve");
            Assert.Equal(UserNeedStatus.Approved, (await store.Find(first.Need.InternalId)).Value.Value.Need.Status);
            var history = (await store.History(first.Need.InternalId)).Value;
            Assert.Equal(4, history.Count);
            Assert.Contains(history, h => h.Comment == "Ready for review");
            Assert.Contains(history, h => h.Comment == "Clarify wording");
            Assert.Contains("Ready for review", renderer.Text());
            Assert.Contains("Clarify wording", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
    }
}
#pragma warning restore BL0006
