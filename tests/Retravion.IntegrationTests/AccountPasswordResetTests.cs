using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Retravion.Application.AuditTrail;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Retravion.Web.Features.Identity.Logout;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Theory]
    [InlineData(AccountType.Ordinary)]
    [InlineData(AccountType.Administrator)]
    public async Task ResetRevokesEverySessionAndConnectedCircuitAndRequiresReplacement(AccountType type)
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "target", "Target", type);
        var original = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var first = Client(factory);
        using var second = Client(factory);
        await Login(first, "target", original);
        await Replace(first, original, Replacement);
        // Capture the actual principals carried by two independent HTTP sessions.
        async Task<ClaimsPrincipal> SignInPrincipal(HttpClient client)
        {
            using var response = await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "target", ["password"] = Replacement,
                ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
            }));
            Assert.Equal("/home", response.Headers.Location?.OriginalString);
            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Retravion.Identity=")).Split(';')[0];
            var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
            var reference = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!;
            var key = reference.Principal.FindFirstValue("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!;
            return (await options.SessionStore!.RetrieveAsync(key))!.Principal;
        }
        var firstPrincipal = await SignInPrincipal(first);
        var secondPrincipal = await SignInPrincipal(second);
        var sessions = factory.Services.GetRequiredService<SessionTicketStore>();
        using var firstCircuit = new SessionAuthenticationStateProvider(sessions);
        using var secondCircuit = new SessionAuthenticationStateProvider(sessions);
        firstCircuit.SetAuthenticationState(Task.FromResult(new AuthenticationState(firstPrincipal)));
        secondCircuit.SetAuthenticationState(Task.FromResult(new AuthenticationState(secondPrincipal)));
        Assert.True((await firstCircuit.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        using var reset = await Reset(admin, "TARGET");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal("no-store", reset.Headers.CacheControl?.ToString());
        var issued = (await reset.Content.ReadFromJsonAsync<CreatedAccount>())!;
        Assert.True(PasswordPolicy.Validate(issued.TemporaryPassword).IsSuccess);
        var persisted = (await store.LoadForSignIn("target")).Value.Value;
        Assert.True(new Pbkdf2PasswordHasher().Verify(issued.TemporaryPassword, persisted.PasswordHash));
        Assert.DoesNotContain(issued.TemporaryPassword, persisted.PasswordHash);
        Assert.True(persisted.RequiresPasswordReplacement);
        foreach (var circuit in new[] { firstCircuit, secondCircuit })
            Assert.False((await circuit.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        firstCircuit.SetAuthenticationState(Task.FromResult(new AuthenticationState(firstPrincipal)));
        Assert.False((await firstCircuit.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        foreach (var principal in new[] { firstPrincipal, secondPrincipal })
        {
            var key = principal.FindFirstValue(SessionTicketStore.SessionClaim)!;
            Assert.Null(await sessions.RetrieveAsync(key));
        }
        foreach (var client in new[] { first, second })
        {
            using var denied = await client.GetAsync("/home");
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
        }
        using var old = Client(factory);
        await Login(old, "target", Replacement, "/?failed=true");
        using var recovered = Client(factory);
        await Login(recovered, "target", issued.TemporaryPassword);
        using (var denied = await recovered.GetAsync("/home")) Assert.Equal("/password-replacement", denied.Headers.Location?.OriginalString);
        foreach (var (password, confirmation) in new[]
        {
            (issued.TemporaryPassword, issued.TemporaryPassword), ("short", "short"),
            (Replacement, ""), ("", Replacement), (Replacement, "replacement123!"),
            (Replacement, Replacement + " "), ("ValidPassé123!", "ValidPasse\u0301123!")
        })
        {
            using var invalid = await ReplaceRequest(recovered, issued.TemporaryPassword, password, confirmation);
            Assert.StartsWith("/password-replacement?", invalid.Headers.Location?.OriginalString);
            Assert.Equal(persisted.PasswordHash, (await store.LoadForSignIn("target")).Value.Value.PasswordHash);
        }
        await Replace(recovered, issued.TemporaryPassword, Replacement);
        var home = await recovered.GetStringAsync("/home");
        Assert.Equal(type == AccountType.Administrator, home.Contains("Administration navigation"));
        using var again = Client(factory);
        await Login(again, "target", Replacement, "/home");
        foreach (var path in new[] { "/administration/accounts", "/administration/users/details/target" })
            Assert.DoesNotContain(issued.TemporaryPassword, await admin.GetStringAsync(path));
    }

    [Theory]
    [InlineData(AccountType.Ordinary, false, false)]
    [InlineData(AccountType.Administrator, false, true)]
    [InlineData(AccountType.Ordinary, true, true)]
    [InlineData(AccountType.Administrator, true, false)]
    public async Task ResetClearsLockoutButPreservesAvailability(AccountType type, bool active, bool locked)
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "target", "Target", type);
        if (locked)
        {
            var clock = new Clock();
            var signIn = new PasswordSignIn(store, new Pbkdf2PasswordHasher(), clock);
            for (var i = 0; i < 10; i++)
            {
                Assert.True((await signIn.Authenticate("target", "wrong")).IsFailure);
                clock.Now = clock.Now.AddSeconds(5);
            }
            Assert.True((await store.LoadForSignIn("target")).Value.Value.Failures.IsLocked);
        }
        if (!active) using (var changed = await Availability(admin, "target", false)) Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var reset = await Reset(admin, "target");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var password = (await reset.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        var account = (await store.LoadForSignIn("target")).Value.Value;
        Assert.Equal(active, account.IsActive);
        Assert.Equal(SignInFailures.None, account.Failures);
        Assert.True(account.RequiresPasswordReplacement);
        using var target = Client(factory);
        if (!active)
        {
            await Login(target, "target", password, "/?failed=true");
            using var changed = await Availability(admin, "target", true);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }
        await Login(target, "target", password);
    }

    [Fact]
    public async Task ResetRejectsAnonymousOrdinarySelfMissingAndUnprotectedRequestsWithoutEffects()
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var anonymous = Client(factory);
        using (var denied = await Reset(anonymous, "admin")) Assert.Equal("/", denied.Headers.Location?.OriginalString);
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "ordinary", "Ordinary", AccountType.Ordinary);
        var password = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var ordinary = Client(factory);
        await Login(ordinary, "ordinary", password);
        using (var denied = await Reset(ordinary, "admin")) Assert.Equal("/password-replacement", denied.Headers.Location?.OriginalString);
        await Replace(ordinary, password, Replacement);
        var snapshot = store.Account.Value;
        var writes = store.Writes;
        using (var denied = await Reset(ordinary, "admin")) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await Reset(admin, "ADMIN")) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using (var denied = await Reset(admin, "missing")) Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        using (var denied = await admin.PostAsync("/administration/accounts/ordinary/password-reset", new FormUrlEncodedContent(new Dictionary<string, string>())))
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Same(snapshot, store.Account.Value);
        Assert.Equal(writes, store.Writes);
        Assert.Contains("You cannot reset your own password.", await admin.GetStringAsync("/administration/users/details/admin"));
        Assert.Contains("Reset password", await admin.GetStringAsync("/administration/users/details/ordinary"));
        Assert.Contains("Overview", await admin.GetStringAsync("/home"));
    }

    [Fact]
    public async Task ResetWhileLoginAwaitsAuditPreventsStaleSessionIssuance()
    {
        var store = new StartupAdministratorStore();
        var audit = new ResetAudit();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IAuditSubmission>(audit)));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "target", "Target", AccountType.Ordinary);
        var password = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var target = Client(factory);
        audit.Block = true;
        var login = target.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "target", ["password"] = password,
            ["__RequestVerificationToken"] = Token(await target.GetStringAsync("/"))
        }));
        await audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var reset = await Reset(admin, "target");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        audit.Release.TrySetResult();
        using var response = await login;
        Assert.Equal("/?failed=true", response.Headers.Location?.OriginalString);
        using var denied = await target.GetAsync("/home");
        Assert.Equal("/", denied.Headers.Location?.OriginalString);
    }

    private sealed class ResetAudit : IAuditSubmission
    {
        internal bool Block;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default)
        {
            if (Block)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return AuditSubmissionResult.Success;
        }
    }

    private static async Task<HttpResponseMessage> Reset(HttpClient client, string username) =>
        await client.PostAsync($"/administration/accounts/{username}/password-reset", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
}
