using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Web.Components.Layout;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task DevelopmentCatalogReusesThemeComponentsAndEstablishedFormLayout()
    {
        await using var original = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(factory);
        await Login(client, "admin", Initial);
        await Replace(client, Initial, Replacement);
        Assert.Contains("aria-label=\"Development navigation\"", await client.GetStringAsync("/home"));
        var html = await client.GetStringAsync("/development/ui-catalog");
        Assert.Single(Regex.Matches(html, "<h1\\b"));
        var header = Regex.Match(html, "<header\\b[^>]*class=\"page-toolbar\"[^>]*>[\\s\\S]*?</header>").Value;
        Assert.Contains("UI catalog</h1>", header);
        Assert.Contains("href=\"/home\"", header);
        Assert.DoesNotContain("catalog-example-form", header);
        Assert.Contains("aria-current=\"page\"", html);
        foreach (var section in new[] { "foundations", "components", "page-pattern" })
        {
            Assert.Contains($"id=\"{section}\"", html);
            var link = Regex.Match(html, $"href=\"([^\"]*#{section})\"");
            Assert.True(link.Success);
            // App.razor sets <base href="/">, so fragment-only links resolve to the login page.
            var destination = new Uri(new Uri("https://localhost/"), link.Groups[1].Value);
            Assert.Equal("/development/ui-catalog", destination.AbsolutePath);
            Assert.Equal("#" + section, destination.Fragment);
        }
        Assert.Contains(PrototypeTheme.Value.PaletteLight.Primary.ToString(), html);
        Assert.Contains(PrototypeTheme.Value.PaletteDark.Primary.ToString(), html);
        Assert.Contains(PrototypeTheme.Value.Typography.Default.FontFamily![0], html);
        Assert.Contains(PrototypeTheme.Value.Typography.Body1.FontSize!, html);
        Assert.Contains(PrototypeTheme.Value.LayoutProperties.DefaultBorderRadius, html);
        Assert.Contains("--mud-palette-primary", html);
        Assert.Contains("mud-grid-item-xs-12", html);
        Assert.Contains("mud-grid-item-sm-6", html);
        Assert.Contains("mud-grid-item-md-4", html);
        Assert.Contains("aria-label=\"Back to home\"", html);
        Assert.Contains("aria-busy=\"true\"", html);
        Assert.Matches("<button[^>]*disabled[^>]*>[\\s\\S]*?Disabled action", html);
        Assert.Contains("A value is required.", html);
        Assert.Contains("mud-table-dense", html);
        Assert.Contains("Example category", html);
        Assert.Contains("CreateUser.razor", html);
        Assert.Contains("docs/patterns.md", html);
        foreach (var section in new[] { "list-template", "create-template", "edit-template", "markdown-template", "button-guide" })
            Assert.Contains($"id=\"{section}\"", html);
        Assert.Contains("List demonstration state", html);
        Assert.Contains("Edit example name", html);
        Assert.Contains("Existing example item", html);
        Assert.Contains("Save Markdown example", html);
        Assert.Contains("DescriptionEditor.razor.css", html);
        Assert.Contains("min-width: 64px", html);
        Assert.Contains("44 × 44px", WebUtility.HtmlDecode(html));
        Assert.Contains("catalog-state-button", html);
        Assert.Contains("&lt;DescriptionEditor", html);
        foreach (var id in new[] { "catalog-create-form", "catalog-edit-form", "catalog-markdown-form" })
        {
            var templateForm = Regex.Match(html, $"<form(?=[^>]*id=\"{id}\")[^>]*>[\\s\\S]*?</form>");
            Assert.True(templateForm.Success);
            var action = Regex.Match(html, $"<button(?=[^>]*form=\"{id}\")[^>]*>[\\s\\S]*?</button>");
            Assert.True(action.Success);
            Assert.True(action.Index > templateForm.Index + templateForm.Length);
            AssertFilledPrimaryButton(action.Value);
            Assert.DoesNotContain(id, header);
        }
        Assert.Contains("&lt;MudButton", html); // Snippets are encoded text, not executable markup.
        Assert.Contains("&lt;MudTable", html);
        var form = Regex.Match(html, "<form(?=[^>]*id=\"catalog-example-form\")[^>]*>[\\s\\S]*?</form>");
        Assert.True(form.Success);
        var submit = Assert.Single(Regex.Matches(html, "<button(?=[^>]*form=\"catalog-example-form\")[^>]*>[\\s\\S]*?</button>").Cast<Match>());
        Assert.True(submit.Index > form.Index + form.Length);
        AssertFilledPrimaryButton(submit.Value);
        Assert.Contains("Validate example", submit.Value);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopmentCatalogIsUnavailableEvenWhenAuthenticatedAndCircuitPolicyDeniesIt(string environment)
    {
        await using var original = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = Client(factory);
        using (var denied = await client.GetAsync("/development/ui-catalog"))
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        await Login(client, "admin", Initial);
        await Replace(client, Initial, Replacement);
        var actor = await ProductPrincipal(client, factory.Services, "admin", Replacement, "/home");
        foreach (var path in new[] { "/development/ui-catalog", "/DEVELOPMENT/UI-CATALOG/", "/development/ui-catalog?test=1" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.DoesNotContain("Living reference", await denied.Content.ReadAsStringAsync());
        }
        Assert.DoesNotContain("Development navigation", await client.GetStringAsync("/home"));
        using var scope = factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.False((await authorization.AuthorizeAsync(actor, null, "UiCatalogDevelopment")).Succeeded);
        var attribute = Assert.Single(typeof(Retravion.Web.Features.UiCatalog.UiCatalog)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal("UiCatalogDevelopment", attribute.Policy); // AuthorizeRouteView enforces this on circuit navigation.
    }

    [Fact]
    public async Task DevelopmentCatalogRetainsAnonymousAndRestrictedSessionGuards()
    {
        await using var original = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = original.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(factory);
        using (var denied = await client.GetAsync("/development/ui-catalog"))
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
        var restricted = await ProductPrincipal(client, factory.Services, "admin", Initial, "/password-replacement");
        using (var denied = await client.GetAsync("/development/ui-catalog"))
            Assert.Equal("/password-replacement", denied.Headers.Location?.OriginalString);
        using var scope = factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.False((await authorization.AuthorizeAsync(restricted, null, "UiCatalogDevelopment")).Succeeded);
        var ordinary = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "User") }, "test"));
        Assert.True((await authorization.AuthorizeAsync(ordinary, null, "UiCatalogDevelopment")).Succeeded);
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, "UiCatalogDevelopment")).Succeeded);
    }
}
