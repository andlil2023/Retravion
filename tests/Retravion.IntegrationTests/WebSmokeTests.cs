using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class WebSmokeTests
{
    [Fact]
    public async Task HomePageRendersSuccessfully()
    {
        await using var factory = new WebApplicationFactory<Program>();
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
    }
}
