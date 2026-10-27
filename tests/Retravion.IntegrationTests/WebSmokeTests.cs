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
        Assert.Contains("aria-label=\"Login prototype\"", html);
        Assert.Contains("Log in", html);
        Assert.Contains("id=\"username\"", html);
        Assert.Contains("id=\"password\"", html);
        Assert.Contains("novalidate", html);
        Assert.DoesNotContain(" required", html);
        Assert.Contains("No authentication or validation.", html);
        Assert.DoesNotContain("aria-label=\"Main navigation\"", html);
    }

    [Fact]
    public async Task HomeShowsNavigationAndAnEmptyContentAreaWithoutAuthentication()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/home");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>Home · Retravion</title>", html);
        Assert.Contains("<h1>Overview</h1>", html);
        Assert.Contains("aria-label=\"Main navigation\"", html);
        Assert.Contains("Products", html);
        Assert.Contains("aria-label=\"Back to overview\"", html);
        Assert.Contains("aria-label=\"Page actions\"", html);
        Assert.Contains("aria-label=\"Use dark theme\"", html);
        Assert.Matches("<main[^>]*id=\"page-content\"[^>]*>\\s*</main>", html);
        Assert.DoesNotContain("aria-label=\"Login prototype\"", html);
        Assert.DoesNotContain("<table", html);
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
    public async Task FingerprintedPageAssetsAreServedInTheDefaultRunEnvironment()
    {
        await using var factory = CreateFactory()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");
        var assets = Regex.Matches(html, "(?:href|src)=\"([^\"]+\\.(?:css|js))\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(4, assets.Length);
        foreach (var asset in assets)
        {
            using var response = await client.GetAsync(asset);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
    }
}
