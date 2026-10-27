using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Retravion.Web.Components;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class ProtectedAccessTests
{
    private const string Initial = "InitialPass123!";
    private const string Replacement = "Replacement123!";

    [Theory]
    [InlineData("/home", "GET")]
    [InlineData("/HOME/", "GET")]
    [InlineData("/home?direct=true", "GET")]
    [InlineData("/home", "POST")]
    [InlineData("/protected-probe", "GET")]
    [InlineData("/protected-probe", "POST")]
    [InlineData("/_content/private-data", "GET")]
    [InlineData("/_framework/private-data", "GET")]
    [InlineData("/css/private-data", "GET")]
    public async Task DirectRequestsDenyAnonymousAndPendingSessionsWithoutExecutingProtectedOperations(string path, string method)
    {
        var probe = new ProtectedProbe();
        await using var factory = CreateFactory(probe);
        using var client = CreateClient(factory);
        using (var denied = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            await AssertDenied(denied, "/");

        await Login(client);
        using (var denied = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
            await AssertDenied(denied, "/password-replacement");
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task ReplacementUnlocksTheShellAndDirectProtectedOperations()
    {
        var probe = new ProtectedProbe();
        await using var factory = CreateFactory(probe);
        using var client = CreateClient(factory);
        await Login(client);
        using var replaced = await client.PostAsync("/identity/password-replacement", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["currentPassword"] = Initial, ["newPassword"] = Replacement,
                ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/password-replacement"))
            }));
        Assert.Equal("/home", replaced.Headers.Location?.OriginalString);

        var html = await client.GetStringAsync("/home");
        Assert.Contains("<title>Home · Retravion</title>", html);
        Assert.Contains("<h1>Overview</h1>", html);
        Assert.Contains("aria-label=\"Main navigation\"", html);
        Assert.Contains("Products", html);
        Assert.Contains("aria-label=\"Back to overview\"", html);
        Assert.Contains("aria-label=\"Page actions\"", html);
        Assert.Contains("aria-label=\"Use dark theme\"", html);
        Assert.Matches("<main[^>]*id=\"page-content\"[^>]*>\\s*</main>", html);
        Assert.DoesNotContain("aria-label=\"Login\"", html);
        Assert.DoesNotContain("<table", html);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, "/protected-probe"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("protected-data", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task InvalidAndExpiredSessionCookiesCannotReadProtectedData()
    {
        var probe = new ProtectedProbe();
        await using var factory = CreateFactory(probe);
        using var client = CreateClient(factory);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Administrator")
        }, CookieAuthenticationDefaults.AuthenticationScheme));
        var expired = await SessionCookie(options, new AuthenticationTicket(principal,
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow.AddHours(-2), ExpiresUtc = DateTimeOffset.UtcNow.AddHours(-1)
            }, CookieAuthenticationDefaults.AuthenticationScheme));
        foreach (var cookie in new[] { "tampered-cookie", expired })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/protected-probe");
            request.Headers.Add("Cookie", $"Retravion.Identity={cookie}");
            using var response = await client.SendAsync(request);
            await AssertDenied(response, "/");
        }
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task AuthenticatedIdentityWithoutCompletedReplacementAccessFailsClosed()
    {
        var probe = new ProtectedProbe();
        await using var factory = CreateFactory(probe);
        using var client = CreateClient(factory);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "admin") },
            CookieAuthenticationDefaults.AuthenticationScheme));
        var cookie = await SessionCookie(options, new AuthenticationTicket(principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) },
            CookieAuthenticationDefaults.AuthenticationScheme));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected-probe");
        request.Headers.Add("Cookie", $"Retravion.Identity={cookie}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task ConnectedCompletedSessionIsClosedAtTicketExpiryAndCannotRegainProtectedAccess()
    {
        var probe = new ProtectedProbe();
        await using var factory = CreateFactory(probe);
        using var client = CreateClient(factory);
        var dispatcher = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/_blazor/negotiate")
            .Metadata.GetMetadata<HttpConnectionDispatcherOptions>();
        Assert.NotNull(dispatcher);
        Assert.True(dispatcher.CloseOnAuthenticationExpiration);

        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Administrator")
        }, CookieAuthenticationDefaults.AuthenticationScheme));
        var ticket = await SessionCookie(options, new AuthenticationTicket(principal,
            new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = expires },
            CookieAuthenticationDefaults.AuthenticationScheme));
        client.DefaultRequestHeaders.Add("Cookie", $"Retravion.Identity={ticket}");
        using var beforeExpiry = await client.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, beforeExpiry.StatusCode);
        using var negotiation = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        negotiation.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await negotiation.Content.ReadAsStringAsync());
        var token = json.RootElement.GetProperty("connectionToken").GetString();
        var sockets = factory.Server.CreateWebSocketClient();
        sockets.ConfigureRequest = request => request.Headers.Cookie = $"Retravion.Identity={ticket}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await sockets.ConnectAsync(new Uri($"wss://localhost/_blazor?id={Uri.EscapeDataString(token!)}"), timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"blazorpack\",\"version\":1}\u001e"),
            WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[4096];
        var handshake = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        Assert.Equal("{}\u001e", Encoding.UTF8.GetString(buffer, 0, handshake.Count));
        Assert.Equal(WebSocketState.Open, socket.State);

        WebSocketReceiveResult message;
        do
        {
            message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        } while (message.MessageType != WebSocketMessageType.Close);
        Assert.True(DateTimeOffset.UtcNow >= expires);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);

        // Reconnects reauthenticate through the same pipeline; an expired cookie is anonymous.
        using var reconnect = await client.GetAsync("/home");
        await AssertDenied(reconnect, "/");
        using var operation = await client.PostAsync("/protected-probe", null);
        await AssertDenied(operation, "/");
        Assert.Equal(0, probe.Calls);
    }

    private static WebApplicationFactory<Program> CreateFactory(ProtectedProbe probe) =>
        new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(probe)));

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    private static async Task Login(HttpClient client)
    {
        using var response = await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = Initial,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
        Assert.Equal("/password-replacement", response.Headers.Location?.OriginalString);
    }

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<string> SessionCookie(CookieAuthenticationOptions options, AuthenticationTicket ticket)
    {
        var key = await options.SessionStore!.StoreAsync(ticket);
        var reference = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("Microsoft.AspNetCore.Authentication.Cookies-SessionId", key)
        }, CookieAuthenticationDefaults.AuthenticationScheme));
        return options.TicketDataFormat.Protect(new AuthenticationTicket(reference, ticket.Properties,
            CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static async Task AssertDenied(HttpResponseMessage response, string location)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CircuitNavigationCannotRenderProtectedPagesOrTheirLayout(bool pending)
    {
        await using var factory = CreateFactory(new ProtectedProbe()).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddScoped<NavigationManager, CircuitNavigation>()));
        using var client = CreateClient(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var claims = pending ? new[] { new Claim("Retravion.PasswordReplacementRequired", "true") } : Array.Empty<Claim>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, pending ? "test" : null));
        if (pending)
        {
            var store = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme).SessionStore!;
            var key = await store.StoreAsync(new AuthenticationTicket(principal,
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) },
                CookieAuthenticationDefaults.AuthenticationScheme));
            principal = (await store.RetrieveAsync(key))!.Principal;
        }
        var authentication = (IHostEnvironmentAuthenticationStateProvider)services.GetRequiredService<AuthenticationStateProvider>();
        authentication.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var route = await renderer.RenderComponentAsync<Routes>();
            Assert.Contains("aria-label=\"Login\"", route.ToHtmlString());
            var navigation = services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo("/home");
            await route.QuiescenceTask;
            var denied = route.ToHtmlString();
            Assert.Contains("Access denied.", denied);
            Assert.DoesNotContain("Main navigation", denied);
            Assert.DoesNotContain("Products", denied);
            Assert.DoesNotContain("Overview", denied);
            navigation.NavigateTo("/password-replacement");
            await route.QuiescenceTask;
            var replacement = route.ToHtmlString();
            if (pending) Assert.Contains("aria-label=\"Replace password\"", replacement);
            else Assert.Contains("Access denied.", replacement);
            Assert.DoesNotContain("Main navigation", replacement);
        });
    }

    private sealed class CircuitNavigation : NavigationManager
    {
        public CircuitNavigation() => Initialize("https://localhost/", "https://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(false);
        }
    }

    private sealed class ProtectedProbe : IStartupFilter
    {
        public int Calls { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints => endpoints.MapMethods("/protected-probe", new[] { "GET", "POST" }, () =>
            {
                Calls++;
                return Results.Text("protected-data");
            }));
        };
    }
}
