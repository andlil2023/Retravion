using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Retravion.Web.Components;
using Retravion.Web.Features.Identity.Logout;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class LogoutTests
{
    private const string Initial = "InitialPass123!";
    private const string Replacement = "Replacement123!";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogoutRedirectsToLoginAndRevokesOnlyTheCurrentSessionIncludingCookieReplay(bool pending)
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var cookie = await SignIn(client, pending);
        using var other = CreateClient(factory);
        await SignIn(other, pending, completedAccount: !pending);
        var page = pending ? "/password-replacement" : "/home";
        var html = await client.GetStringAsync(page);
        if (!pending)
        {
            Assert.Contains("action=\"/identity/logout\"", html);
            Assert.Contains("method=\"post\"", html);
            Assert.Contains("data-enhance=\"false\"", html);
            Assert.Contains("Log out", html);
            Assert.DoesNotContain("Return to login", html);
        }

        using var response = await client.PostAsync("/identity/logout", Form(Token(html)));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("Retravion.Identity=;") && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("aria-label=\"Login\"", await client.GetStringAsync("/"));
        await AssertDenied(client, "/home", HttpMethod.Get);

        using var replay = CreateClient(factory, handleCookies: false);
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        await AssertDenied(replay, "/home", HttpMethod.Get);
        await AssertDenied(replay, "/home", HttpMethod.Post);
        await AssertDenied(replay, "/identity/password-replacement", HttpMethod.Post);
        using var otherSession = await other.GetAsync(page);
        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);

        using var again = await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = pending ? Initial : Replacement,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
        Assert.Equal(page, again.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-token")]
    public async Task MissingOrInvalidAntiforgeryCannotTerminateTheSession(string? token)
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);
        await SignIn(client, pending: false);
        await client.GetStringAsync("/home");
        using var response = await client.PostAsync("/identity/logout", Form(token));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
        using var home = await client.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task GetCannotLogOutAndAnonymousPostIsSafe()
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);
        using var anonymous = await client.PostAsync("/identity/logout", Form(Token(await client.GetStringAsync("/"))));
        Assert.Equal("/", anonymous.Headers.Location?.OriginalString);
        await SignIn(client, pending: false);
        using var get = await client.GetAsync("/identity/logout");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var home = await client.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task LogoutRemovesProtectedContentFromAnAlreadyConnectedCircuitAndReconnectCannotRestoreIt()
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);
        var cookie = await SignIn(client, pending: false);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var reference = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!;
        var key = reference.Principal.FindFirstValue("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!;
        var ticket = (await options.SessionStore!.RetrieveAsync(key))!;
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        ((IHostEnvironmentNavigationManager)services.GetRequiredService<NavigationManager>())
            .Initialize("https://localhost/", "https://localhost/home");
        var provider = services.GetRequiredService<AuthenticationStateProvider>();
        var host = (IHostEnvironmentAuthenticationStateProvider)provider;
        host.SetAuthenticationState(Task.FromResult(new AuthenticationState(ticket.Principal)));
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var route = await renderer.RenderComponentAsync<Routes>();
            Assert.Contains("Main navigation", route.ToHtmlString());
            Assert.Contains("Overview", route.ToHtmlString());
            using var logout = await client.PostAsync("/identity/logout", Form(Token(await client.GetStringAsync("/home"))));
            Assert.Equal("/", logout.Headers.Location?.OriginalString);
            await route.QuiescenceTask;
            Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
            Assert.Contains("Access denied.", route.ToHtmlString());
            Assert.DoesNotContain("Main navigation", route.ToHtmlString());
            Assert.DoesNotContain("Overview", route.ToHtmlString());

            // A reconnect carrying the old principal must not restore circuit access.
            host.SetAuthenticationState(Task.FromResult(new AuthenticationState(ticket.Principal)));
            await route.QuiescenceTask;
            Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
            Assert.DoesNotContain("Main navigation", route.ToHtmlString());
        });
    }

    [Fact]
    public async Task RevocationDuringPendingCircuitInitializationAndLateRenewalCannotRestoreAccess()
    {
        using var store = new SessionTicketStore();
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Administrator")
        }, "Cookies")), new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) }, "Cookies");
        var key = await store.StoreAsync(ticket);
        var principal = (await store.RetrieveAsync(key))!.Principal;
        using var provider = new SessionAuthenticationStateProvider(store);
        var pending = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.SetAuthenticationState(pending.Task);
        await store.RemoveAsync(key);
        await store.RenewAsync(key, ticket);
        Assert.Null(await store.RetrieveAsync(key));
        pending.SetResult(new AuthenticationState(principal));
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
    }

    private static AdministratorWebFactory CreateFactory() => new(new StartupAdministratorStore(), "admin", Initial);

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, bool handleCookies = true) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = handleCookies, BaseAddress = new Uri("https://localhost")
        });

    private static async Task<string> SignIn(HttpClient client, bool pending, bool completedAccount = false)
    {
        using var login = await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = completedAccount ? Replacement : Initial,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
        if (completedAccount)
        {
            Assert.Equal("/home", login.Headers.Location?.OriginalString);
            return Cookie(login);
        }
        Assert.Equal("/password-replacement", login.Headers.Location?.OriginalString);
        if (pending) return Cookie(login);
        using var replacement = await client.PostAsync("/identity/password-replacement", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["currentPassword"] = Initial, ["newPassword"] = Replacement, ["confirmNewPassword"] = Replacement,
                ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/password-replacement"))
            }));
        Assert.Equal("/home", replacement.Headers.Location?.OriginalString);
        return Cookie(replacement);
    }

    private static string Cookie(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("Set-Cookie"),
        value => value.StartsWith("Retravion.Identity=")).Split(';')[0];

    private static FormUrlEncodedContent Form(string? token) => new(token is null
        ? new Dictionary<string, string>() : new Dictionary<string, string> { ["__RequestVerificationToken"] = token });

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task AssertDenied(HttpClient client, string path, HttpMethod method)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(method, path));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

}
