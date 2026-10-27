using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class PasswordReplacementTests
{
    private const string Initial = "InitialPass123!";
    private const string Replacement = "Replacement123!";

    [Fact]
    public async Task ReplacementCompletesRestrictedSessionAndSubsequentLoginGoesHome()
    {
        var store = new StartupAdministratorStore();
        var observer = new AccessObserver();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(observer)));
        using var client = CreateClient(factory);
        using var login = await Login(client, Initial);
        Assert.Equal("/password-replacement", login.Headers.Location?.OriginalString);
        var html = await client.GetStringAsync("/password-replacement");
        Assert.Contains("aria-label=\"Replace password\"", html);
        Assert.Contains("autocomplete=\"current-password\"", html);
        Assert.Contains("autocomplete=\"new-password\"", html);
        Assert.DoesNotContain("aria-label=\"Main navigation\"", html);
        Assert.False(observer.FullAccess);

        using var response = await Replace(client, Token(html), Initial, Replacement);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/home", response.Headers.Location?.OriginalString);
        Assert.False(store.Account.Value.RequiresPasswordReplacement);
        Assert.Equal(2, store.Writes);
        var hasher = new Pbkdf2PasswordHasher();
        Assert.True(hasher.Verify(Replacement, store.Account.Value.PasswordHash));
        Assert.False(hasher.Verify(Initial, store.Account.Value.PasswordHash));
        using var home = await client.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.True(observer.FullAccess);
        using var repeat = await client.GetAsync("/password-replacement");
        Assert.Equal("/home", repeat.Headers.Location?.OriginalString);

        using var nextClient = CreateClient(factory);
        using var oldLogin = await Login(nextClient, Initial);
        Assert.Equal("/?failed=true", oldLogin.Headers.Location?.OriginalString);
        using var newLogin = await Login(nextClient, Replacement);
        Assert.Equal("/home", newLogin.Headers.Location?.OriginalString);
        using var nextHome = await nextClient.GetAsync("/home");
        Assert.True(observer.FullAccess);
    }

    [Theory]
    [InlineData(Initial, "short")]
    [InlineData(Initial, Initial)]
    [InlineData("wrong-current", Replacement)]
    [InlineData(Initial, "")]
    public async Task FailureShowsMessageAndRetainsRestriction(string current, string replacement)
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var client = CreateClient(factory);
        using var login = await Login(client, Initial);
        var original = store.Account.Value;
        using var response = await Replace(client, Token(await client.GetStringAsync("/password-replacement")), current, replacement);
        Assert.Equal("/password-replacement?failed=true", response.Headers.Location?.OriginalString);
        var html = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains("role=\"alert\"", html);
        Assert.Contains("Password replacement failed.", html);
        Assert.DoesNotContain(current, html);
        if (replacement.Length > 0) Assert.DoesNotContain(replacement, html);
        Assert.Same(original, store.Account.Value);
        Assert.Equal(1, store.Writes);
        using var home = await client.GetAsync("/home");
        Assert.Equal("/password-replacement", home.Headers.Location?.OriginalString);
        Assert.DoesNotContain("Main navigation", await home.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-token")]
    public async Task InvalidAntiforgeryCannotReplaceOrElevate(string? token)
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var client = CreateClient(factory);
        using var login = await Login(client, Initial);
        await client.GetStringAsync("/password-replacement");
        using var response = await Replace(client, token, Initial, Replacement);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
        Assert.True(store.Account.Value.RequiresPasswordReplacement);
        Assert.Equal(1, store.Writes);
        using var home = await client.GetAsync("/home");
        Assert.Equal("/password-replacement", home.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task AnonymousRequestsCannotViewOrSubmitReplacement()
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var client = CreateClient(factory);
        using var page = await client.GetAsync("/password-replacement");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/", page.Headers.Location?.OriginalString);
        using var response = await Replace(client, Token(await client.GetStringAsync("/")), Initial, Replacement);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.True(store.Account.Value.RequiresPasswordReplacement);
        Assert.Equal(1, store.Writes);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string password) =>
        await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = password,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));

    private static Task<HttpResponseMessage> Replace(HttpClient client, string? token, string current, string replacement)
    {
        var form = new Dictionary<string, string> { ["currentPassword"] = current, ["newPassword"] = replacement };
        if (token is not null) form["__RequestVerificationToken"] = token;
        return client.PostAsync("/identity/password-replacement", new FormUrlEncodedContent(form));
    }

    private sealed class AccessObserver : IStartupFilter
    {
        public bool FullAccess { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                await continuation();
                FullAccess = context.User.IsInRole("Administrator");
            });
            next(app);
        };
    }
}
