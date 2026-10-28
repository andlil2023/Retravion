using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Retravion.Web.Components;
using Retravion.Web.Features.Identity.Inactivity;
using Retravion.Web.Features.Identity.Logout;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class InactivityExpirationTests
{
    [Fact]
    public async Task ActivityExtendsTheDeadlineButReadsAndRenewalDoNot()
    {
        var time = new ManualTime();
        using var store = new SessionTicketStore(time);
        var key = await store.StoreAsync(Ticket(time));
        time.Advance(TimeSpan.FromMinutes(29));
        Assert.True(store.RecordActivity(key));
        var expectedExpiry = time.GetUtcNow().AddMinutes(30);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(store.IsActive(key));
        Assert.Equal(expectedExpiry, (await store.RetrieveAsync(key))!.Properties.ExpiresUtc);
        await store.RenewAsync(key, Ticket(time));
        Assert.Equal(expectedExpiry, (await store.RetrieveAsync(key))!.Properties.ExpiresUtc);
        time.Advance(TimeSpan.FromMinutes(29) - TimeSpan.FromTicks(1));
        Assert.True(store.IsActive(key));
        time.Advance(TimeSpan.FromTicks(1));
        Assert.False(store.IsActive(key));
        Assert.Null(await store.RetrieveAsync(key));
        Assert.False(store.RecordActivity(key));
        await store.RenewAsync(key, Ticket(time));
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task ExpirationRevokesConnectedPrincipalsAndCannotBeReversedByReconnectOrLateActivity()
    {
        var time = new ManualTime();
        using var store = new SessionTicketStore(time);
        var key = await store.StoreAsync(Ticket(time));
        var principal = (await store.RetrieveAsync(key))!.Principal;
        using var provider = new SessionAuthenticationStateProvider(store);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        var notifications = 0;
        provider.AuthenticationStateChanged += _ => notifications++;
        time.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1));
        Assert.True((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, notifications);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        Assert.False(store.RecordActivity(key));
    }

    [Fact]
    public async Task ActivityAndLogoutAreIsolatedPerSession()
    {
        var time = new ManualTime();
        using var store = new SessionTicketStore(time);
        var idle = await store.StoreAsync(Ticket(time));
        var active = await store.StoreAsync(Ticket(time));
        time.Advance(TimeSpan.FromMinutes(29));
        Assert.True(store.RecordActivity(active));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(store.IsActive(idle));
        Assert.True(store.IsActive(active));
        await store.RemoveAsync(active);
        Assert.False(store.RecordActivity(active));
        await store.RenewAsync(active, Ticket(time));
        Assert.Null(await store.RetrieveAsync(active));
    }

    [Fact]
    public async Task SessionTimersRemainOneShotAndClockSupportsPeriodicPopoverTimers()
    {
        var time = new ManualTime();
        using var store = new SessionTicketStore(time);
        await store.StoreAsync(Ticket(time));
        Assert.Single(time.TimerPeriods);
        Assert.All(time.TimerPeriods, period => Assert.Equal(Timeout.InfiniteTimeSpan, period));
        var ticks = 0;
        using var periodic = time.CreateTimer(_ => ticks++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, ticks);
        Assert.True(periodic.Change(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, ticks);
        Assert.True(periodic.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(4, ticks);
        periodic.Dispose();
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(4, ticks);
        Assert.False(periodic.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationRequestsResetInactivityButAssetsAndTransportDoNotAndExpiredCookiesAreDenied(bool pending)
    {
        var time = new ManualTime();
        await using var factory = Factory(time);
        using var client = Client(factory);
        var cookie = await SignIn(client, pending);
        var page = pending ? "/password-replacement" : "/home";
        time.Advance(TimeSpan.FromMinutes(29));
        using (var interaction = await client.GetAsync(page)) Assert.Equal(HttpStatusCode.OK, interaction.StatusCode);
        time.Advance(TimeSpan.FromMinutes(1));
        // Authentication and transport traffic must not count as user presence.
        using (var transport = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null))
            Assert.Equal(HttpStatusCode.OK, transport.StatusCode);
        time.Advance(TimeSpan.FromMinutes(28));
        using (var asset = await client.GetAsync("/_framework/blazor.web.js")) Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        time.Advance(TimeSpan.FromMinutes(1));
        foreach (var path in new[] { page, "/home", "/identity/password-replacement" })
        {
            using var request = new HttpRequestMessage(path.StartsWith("/identity/") ? HttpMethod.Post : HttpMethod.Get, path);
            using var denied = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
            Assert.Empty(await denied.Content.ReadAsStringAsync());
        }
        using var replay = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost")
        });
        replay.DefaultRequestHeaders.Add("Cookie", cookie);
        using var response = await replay.GetAsync(page);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        Assert.Contains("aria-label=\"Login\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task ExpirationRemovesAlreadyRenderedProtectedContent()
    {
        var time = new ManualTime();
        await using var factory = Factory(time);
        using var client = Client(factory);
        var principal = await Principal(factory, await SignIn(client, pending: false));
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        ((IHostEnvironmentNavigationManager)services.GetRequiredService<NavigationManager>())
            .Initialize("https://localhost/", "https://localhost/home");
        var provider = services.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var route = await renderer.RenderComponentAsync<Routes>();
            Assert.Contains("Overview", route.ToHtmlString());
            time.Advance(TimeSpan.FromMinutes(30));
            await route.QuiescenceTask;
            Assert.Contains("Access denied.", route.ToHtmlString());
            Assert.DoesNotContain("Overview", route.ToHtmlString());
            Assert.DoesNotContain("Main navigation", route.ToHtmlString());
        });
    }

    [Fact]
    public async Task InteractiveActivityResetsTheServerDeadlineAndExpirationAutomaticallyRoutesToLogin()
    {
        var time = new ManualTime();
        var navigation = new TestNavigation();
        var js = new TestJS();
        await using var factory = Factory(time);
        using var client = Client(factory);
        var principal = await Principal(factory, await SignIn(client, pending: false));
        await using var services = ActivityServices(factory.Services.GetRequiredService<SessionTicketStore>(), time, navigation, js);
        var provider = services.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await using var renderer = new InteractiveRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.Mount();
            Assert.Equal("./js/sessionActivity.js", js.ImportPath);
            Assert.True(js.Started);
            time.Advance(TimeSpan.FromMinutes(29));
            Assert.True(await component.RecordActivity());
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.Null(navigation.Destination);
            time.Advance(TimeSpan.FromMinutes(29));
            Assert.Equal("/", navigation.Destination);
            Assert.True(navigation.ForceLoad);
            Assert.False(await component.RecordActivity());
        });
        Assert.Empty(renderer.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WarningAppearsAtFiveMinutesWithoutExtendingExpiryAndUnattendedSessionStillExpires(bool pending)
    {
        var time = new ManualTime();
        var navigation = new TestNavigation();
        await using var factory = Factory(time);
        using var client = Client(factory);
        var principal = await Principal(factory, await SignIn(client, pending));
        var store = factory.Services.GetRequiredService<SessionTicketStore>();
        var key = principal.FindFirstValue(SessionTicketStore.SessionClaim)!;
        var expiry = (await store.RetrieveAsync(key))!.Properties.ExpiresUtc;
        await using var services = ActivityServices(store, time, navigation, new TestJS());
        var provider = services.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await using var renderer = new InteractiveRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.Mount();
            Assert.False(renderer.WarningVisible);
            time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
            Assert.False(renderer.WarningVisible);
            time.Advance(TimeSpan.FromTicks(1));
            Assert.True(renderer.WarningVisible);
            Assert.Equal(expiry, (await store.RetrieveAsync(key))!.Properties.ExpiresUtc);
            time.Advance(TimeSpan.FromMinutes(25) - TimeSpan.FromTicks(1));
            Assert.True(renderer.WarningVisible);
            Assert.Null(navigation.Destination);
            Assert.True(store.IsActive(key));
            time.Advance(TimeSpan.FromTicks(1));
            Assert.False(renderer.WarningVisible);
            Assert.Null(await store.RetrieveAsync(key));
            Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
            Assert.Equal("/", navigation.Destination);
            Assert.True(navigation.ForceLoad);
            Assert.False(await component.RecordActivity());
        });
        Assert.Empty(renderer.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresenceButtonAndOtherInteractionsDismissWarningAndRestartBothDeadlines(bool pending)
    {
        var time = new ManualTime();
        var navigation = new TestNavigation();
        await using var factory = Factory(time);
        using var client = Client(factory);
        var principal = await Principal(factory, await SignIn(client, pending));
        var store = factory.Services.GetRequiredService<SessionTicketStore>();
        var key = principal.FindFirstValue(SessionTicketStore.SessionClaim)!;
        await using var services = ActivityServices(store, time, navigation, new TestJS());
        var provider = services.GetRequiredService<AuthenticationStateProvider>();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        await using var renderer = new InteractiveRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.Mount();
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.True(renderer.WarningVisible);
            // Dispatch the actual rendered button, independently of the browser input listener.
            await renderer.ConfirmPresence();
            Assert.False(renderer.WarningVisible);
            Assert.Equal(time.GetUtcNow().AddMinutes(30), (await store.RetrieveAsync(key))!.Properties.ExpiresUtc);
            time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
            Assert.False(renderer.WarningVisible);
            time.Advance(TimeSpan.FromTicks(1));
            Assert.True(renderer.WarningVisible);
            Assert.True(await component.RecordActivity());
            Assert.False(renderer.WarningVisible);
            Assert.Equal(time.GetUtcNow().AddMinutes(30), (await store.RetrieveAsync(key))!.Properties.ExpiresUtc);
            // Activity from another circuit or an application request also defers the warning.
            time.Advance(TimeSpan.FromMinutes(4));
            Assert.True(store.RecordActivity(key));
            time.Advance(TimeSpan.FromMinutes(1));
            Assert.False(renderer.WarningVisible);
            time.Advance(TimeSpan.FromMinutes(4));
            Assert.True(renderer.WarningVisible);
            time.Advance(TimeSpan.FromMinutes(25) - TimeSpan.FromTicks(1));
            Assert.Null(navigation.Destination);
            Assert.True(store.IsActive(key));
            time.Advance(TimeSpan.FromTicks(1));
            Assert.Equal("/", navigation.Destination);
            Assert.False(store.IsActive(key));
        });
        Assert.Empty(renderer.Errors);
    }

    private static ServiceProvider ActivityServices(SessionTicketStore store, TimeProvider time, TestNavigation navigation, TestJS js) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(store)
            .AddSingleton(time)
            .AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>()
            .AddSingleton<NavigationManager>(navigation)
            .AddSingleton<IJSRuntime>(js)
            .AddMudServices()
            .BuildServiceProvider();

    private static AuthenticationTicket Ticket(TimeProvider time) => new(new ClaimsPrincipal(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim(ClaimTypes.Role, "Administrator")
    }, "Cookies")), new AuthenticationProperties { ExpiresUtc = time.GetUtcNow().AddMinutes(30) }, "Cookies");

    private static WebApplicationFactory<Program> Factory(TimeProvider time) =>
        new AdministratorWebFactory(new StartupAdministratorStore(), "admin", "InitialPass123!")
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(time)));

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
    });

    private static async Task<string> SignIn(HttpClient client, bool pending)
    {
        using var login = await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin", ["password"] = "InitialPass123!",
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
        Assert.Equal("/password-replacement", login.Headers.Location?.OriginalString);
        if (pending) return Cookie(login);
        using var replacement = await client.PostAsync("/identity/password-replacement", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["currentPassword"] = "InitialPass123!", ["newPassword"] = "Replacement123!", ["confirmNewPassword"] = "Replacement123!",
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/password-replacement"))
        }));
        Assert.Equal("/home", replacement.Headers.Location?.OriginalString);
        return Cookie(replacement);
    }

    private static string Cookie(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("Set-Cookie"),
        value => value.StartsWith("Retravion.Identity=")).Split(';')[0];

    private static string Token(string html)
    {
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static async Task<ClaimsPrincipal> Principal(WebApplicationFactory<Program> factory, string cookie)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        var reference = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!;
        var key = reference.Principal.FindFirstValue("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!;
        return (await options.SessionStore!.RetrieveAsync(key))!.Principal;
    }

    private sealed class TestNavigation : NavigationManager
    {
        internal string? Destination { get; private set; }
        internal bool ForceLoad { get; private set; }
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/home");
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Destination = uri;
            ForceLoad = forceLoad;
        }
    }

    private sealed class TestJS : IJSRuntime, IJSObjectReference
    {
        internal string? ImportPath { get; private set; }
        internal bool Started { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
            {
                ImportPath = (string)args![0]!;
                return ValueTask.FromResult((TValue)(object)this);
            }
            if (identifier == "start") Started = true;
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

#pragma warning disable BL0006 // Exercise the actual interactive component lifecycle without another test package.
    private sealed class InteractiveRenderer(IServiceProvider services, ILoggerFactory logger) : Renderer(services, logger)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal List<Exception> Errors { get; } = new();
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        private int _root;
        internal bool WarningVisible => Frames(_root).Any(frame => frame.FrameType == RenderTreeFrameType.Attribute &&
            frame.AttributeName == "aria-label" && Equals(frame.AttributeValue, "Inactivity warning"));
        internal Task ConfirmPresence() => DispatchEventAsync(Frames(_root)
            .SkipWhile(frame => frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button")
            .Skip(1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
            .Single(frame => frame.AttributeName == "onclick" && frame.AttributeEventHandlerId != 0)
            .AttributeEventHandlerId, null, new MouseEventArgs());
        private IEnumerable<RenderTreeFrame> Frames(int componentId)
        {
            var frames = GetCurrentRenderTreeFrames(componentId);
            foreach (var frame in frames.Array.Take(frames.Count))
            {
                yield return frame;
                if (frame.FrameType == RenderTreeFrameType.Component)
                    foreach (var child in Frames(frame.ComponentId)) yield return child;
            }
        }
        internal async Task<SessionActivity> Mount()
        {
            var component = (SessionActivity)InstantiateComponent(typeof(SessionActivity));
            _root = AssignRootComponentId(component);
            await RenderRootComponentAsync(_root);
            return component;
        }
    }
#pragma warning restore BL0006

    // Deterministic session and MudBlazor popover timers: no sleeps or external fake-time package.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2035, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = new();
        private readonly object _gate = new();
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        internal TimeSpan[] TimerPeriods { get { lock (_gate) return _timers.Select(timer => timer.Period).ToArray(); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        internal void Advance(TimeSpan duration)
        {
            var target = GetUtcNow() + duration;
            while (true)
            {
                ManualTimer? timer;
                lock (_gate)
                {
                    timer = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                    if (timer is null) { _now = target; return; }
                    _now = timer.Due!.Value;
                    timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : null;
                }
                timer.Fire();
            }
        }
        private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset? Due { get; set; }
            internal TimeSpan Period { get; private set; } = Timeout.InfiniteTimeSpan;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(dueTime, Timeout.InfiniteTimeSpan);
                ArgumentOutOfRangeException.ThrowIfLessThan(period, Timeout.InfiniteTimeSpan);
                lock (clock._gate)
                {
                    if (_disposed) return false;
                    Period = period;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                    return true;
                }
            }
            internal void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) { _disposed = true; Due = null; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
