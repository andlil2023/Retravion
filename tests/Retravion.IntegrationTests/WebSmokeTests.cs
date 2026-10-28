using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class WebSmokeTests
{
    private static AdministratorWebFactory CreateFactory() => new(new StartupAdministratorStore
    {
        Account = Retravion.Identity.Domain.Administrator.Create("test-admin", "test-hash").Value
    });

    [Fact]
    public async Task HomePageRendersSuccessfully()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Retravion</title>", html);
        Assert.Contains("<h1>Retravion</h1>", html);
        Assert.Contains("aria-label=\"Login\"", html);
        Assert.Contains("Log in", html);
        Assert.Contains("id=\"username\"", html);
        Assert.Contains("id=\"password\"", html);
        Assert.Contains("action=\"/identity/login\"", html);
        Assert.Contains("name=\"__RequestVerificationToken\"", html);
        Assert.DoesNotContain("No authentication or validation.", html);
        Assert.DoesNotContain("aria-label=\"Main navigation\"", html);
    }

    [Fact]
    public async Task HomeRequiresAuthenticationWithoutDisclosingTheShell()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/home");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.Empty(html);
    }

    [Theory]
    [InlineData("/css/app.css", "text/css")]
    [InlineData("/images/login-background.jpg", "image/jpeg")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css", "text/css")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.js", "text/javascript")]
    [InlineData("/_framework/blazor.web.js", "text/javascript")]
    public async Task PrototypeAssetsAreServed(string path, string contentType)
    {
        await using var factory = CreateFactory()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SidebarLogoutStylesMaintainContrastAndVisibleInteractionStates()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var css = await client.GetStringAsync("/css/app.css");

        Assert.Contains("--sidebar: #142e43;", css);
        Assert.Contains("--sidebar: #0d2131;", css);
        Assert.Contains(".sidebar-footer .mud-button-root { color: #e5edf3; }", css);
        Assert.Contains(".sidebar-footer .mud-button-root:hover { background: #1b4056; }", css);
        Assert.Contains(".sidebar-footer .mud-button-root:focus-visible { outline: 3px solid #6bd4d4; outline-offset: 4px; }", css);

        foreach (var background in new[] { "142e43", "0d2131", "1b4056" })
            Assert.True(Contrast("e5edf3", background) >= 4.5, $"Insufficient contrast against #{background}");
    }

    private static double Contrast(string foreground, string background)
    {
        static double Luminance(string color)
        {
            static double Channel(string color, int offset)
            {
                var value = Convert.ToInt32(color.Substring(offset, 2), 16) / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(color, 0) + 0.7152 * Channel(color, 2) + 0.0722 * Channel(color, 4);
        }

        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    [Fact]
    public async Task FingerprintedPageAssetsAreServedInTheDefaultRunEnvironment()
    {
        await using var factory = CreateFactory()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");
        var assets = Regex.Matches(html, "(?:href|src)=\"([^\"]+\\.(?:css|js))\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(5, assets.Length);
        Assert.Contains(assets, asset => Regex.IsMatch(asset, "^Retravion\\.Web(?:\\.[^/]+)?\\.styles\\.css$"));
        foreach (var asset in assets)
        {
            using var response = await client.GetAsync(asset);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
    }
}
