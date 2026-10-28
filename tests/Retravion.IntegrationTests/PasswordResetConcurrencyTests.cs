using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Retravion.Web.Features.Identity.Logout;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Theory]
    [InlineData(AccountType.Ordinary, false)]
    [InlineData(AccountType.Administrator, false)]
    [InlineData(AccountType.Ordinary, true)]
    [InlineData(AccountType.Administrator, true)]
    public async Task ConcurrentReplacementAndResetCannotRestoreStaleCredentialsOrSessions(AccountType type, bool resetFirst)
    {
        var inner = new StartupAdministratorStore();
        var store = new PausedCredentialStore(inner, resetFirst);
        var requestProbe = new ValidatedRequestProbe(resetFirst
            ? "/identity/password-replacement" : "/administration/accounts/target/password-reset");
        await using var factory = new AdministratorWebFactory(inner, "admin", Initial)
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAdministratorStore>(store);
                // Retain real antiforgery validation; observe only its successful completion.
                var registration = services.Single(service => service.ServiceType == typeof(IAntiforgery));
                services.Remove(registration);
                services.AddSingleton<IAntiforgery>(provider => new ObservedAntiforgery(
                    (IAntiforgery)(registration.ImplementationInstance ?? registration.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!)), requestProbe));
            }));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "target", "Target", type);
        var originalPassword = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var target = Client(factory);
        using var login = await target.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "target", ["password"] = originalPassword,
            ["__RequestVerificationToken"] = Token(await target.GetStringAsync("/"))
        }));
        Assert.Equal("/password-replacement", login.Headers.Location?.OriginalString);
        var originalCookie = SessionCookie(login);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        string SessionKey(string cookie) => options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!
            .Principal.FindFirstValue("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!;
        var sessions = factory.Services.GetRequiredService<SessionTicketStore>();
        var originalKey = SessionKey(originalCookie);
        var principal = (await sessions.RetrieveAsync(originalKey))!.Principal;
        using var circuit = new SessionAuthenticationStateProvider(sessions);
        circuit.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        Assert.True((await circuit.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        var replacementToken = Token(await target.GetStringAsync("/password-replacement"));
        var resetToken = Token(await admin.GetStringAsync("/home"));
        var before = (await inner.LoadForSignIn("target")).Value.Value;
        var writes = inner.Writes;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<HttpResponseMessage> SendReplacement() => target.PostAsync("/identity/password-replacement",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["currentPassword"] = originalPassword, ["newPassword"] = Replacement,
                ["confirmNewPassword"] = Replacement, ["__RequestVerificationToken"] = replacementToken
            }), deadline.Token);
        Task<HttpResponseMessage> SendReset() => admin.PostAsync("/administration/accounts/target/password-reset",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = resetToken }), deadline.Token);

        store.Armed = true;
        var first = resetFirst ? SendReset() : SendReplacement();
        Task<HttpResponseMessage> second;
        try
        {
            await store.WriteEntered.Task.WaitAsync(deadline.Token);
            requestProbe.Armed = true;
            second = resetFirst ? SendReplacement() : SendReset();
            var competingActor = await requestProbe.Validated.Task.WaitAsync(deadline.Token);
            Assert.True(competingActor.Identity!.IsAuthenticated);
            Assert.Equal(resetFirst ? "target" : "admin", competingActor.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.Equal(before.PasswordHash, (await inner.LoadForSignIn("target")).Value.Value.PasswordHash);
            Assert.Equal(writes, inner.Writes);
        }
        finally
        {
            store.ReleaseWrite.TrySetResult();
        }
        await Task.WhenAll(first, second).WaitAsync(deadline.Token);
        using var replacementResponse = await (resetFirst ? second : first);
        using var resetResponse = await (resetFirst ? first : second);
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, replacementResponse.StatusCode);
        Assert.Equal(resetFirst ? "/" : "/home", replacementResponse.Headers.Location?.OriginalString);
        Assert.Equal(resetFirst ? 0 : 1, store.TargetReplacementWrites);
        Assert.Equal(1, store.TargetResetWrites);
        Assert.Equal(writes + (resetFirst ? 1 : 2), inner.Writes);
        var issued = (await resetResponse.Content.ReadFromJsonAsync<CreatedAccount>())!;
        var current = (await inner.LoadForSignIn("target")).Value.Value;
        var hasher = new Pbkdf2PasswordHasher();
        Assert.True(hasher.Verify(issued.TemporaryPassword, current.PasswordHash));
        Assert.False(hasher.Verify(originalPassword, current.PasswordHash));
        Assert.False(hasher.Verify(Replacement, current.PasswordHash));
        Assert.True(current.RequiresPasswordReplacement);
        Assert.True(current.IsActive);
        Assert.Equal(type, current.AccountType);
        Assert.Equal(SignInFailures.None, current.Failures);
        Assert.Null(await sessions.RetrieveAsync(originalKey));
        Assert.False((await circuit.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        circuit.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        Assert.False((await circuit.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        if (!resetFirst)
        {
            // The replacement response can arrive late with a cookie for a now-revoked session.
            var replacementCookie = SessionCookie(replacementResponse);
            Assert.Null(await sessions.RetrieveAsync(SessionKey(replacementCookie)));
            using var replay = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost") });
            replay.DefaultRequestHeaders.Add("Cookie", replacementCookie);
            using var denied = await replay.GetAsync("/home");
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
        }
        using (var denied = await target.GetAsync("/home")) Assert.Equal("/", denied.Headers.Location?.OriginalString);
        using var oldLogin = Client(factory);
        await Login(oldLogin, "target", originalPassword, "/?failed=true");
        await Login(oldLogin, "target", Replacement, "/?failed=true");
        using var recovered = Client(factory);
        await Login(recovered, "target", issued.TemporaryPassword);
        using (var restricted = await recovered.GetAsync("/home")) Assert.Equal("/password-replacement", restricted.Headers.Location?.OriginalString);
        using var stale = await ReplaceRequest(recovered, originalPassword, "AnotherPassword123!", "AnotherPassword123!");
        Assert.Equal("/password-replacement?failed=true", stale.Headers.Location?.OriginalString);
        Assert.Equal(current.PasswordHash, (await inner.LoadForSignIn("target")).Value.Value.PasswordHash);
    }

    private static string SessionCookie(HttpResponseMessage response) => Assert.Single(response.Headers.GetValues("Set-Cookie"),
        value => value.StartsWith("Retravion.Identity=", StringComparison.Ordinal)).Split(';')[0];

    private sealed class ValidatedRequestProbe(string path)
    {
        internal bool Armed;
        internal TaskCompletionSource<ClaimsPrincipal> Validated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Observe(HttpContext context)
        {
            if (Armed && context.Request.Path == path) Validated.TrySetResult(context.User.Clone());
        }
    }

    private sealed class ObservedAntiforgery(IAntiforgery inner, ValidatedRequestProbe probe) : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext context) => inner.GetAndStoreTokens(context);
        public AntiforgeryTokenSet GetTokens(HttpContext context) => inner.GetTokens(context);
        public Task<bool> IsRequestValidAsync(HttpContext context) => inner.IsRequestValidAsync(context);
        public void SetCookieTokenAndHeader(HttpContext context) => inner.SetCookieTokenAndHeader(context);
        public async Task ValidateRequestAsync(HttpContext context)
        {
            await inner.ValidateRequestAsync(context);
            probe.Observe(context);
        }
    }

    private sealed class PausedCredentialStore(StartupAdministratorStore inner, bool resetFirst) : IAdministratorStore
    {
        internal bool Armed;
        internal int TargetReplacementWrites;
        internal int TargetResetWrites;
        internal TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Pause(bool reset, Administrator expected, CancellationToken cancellationToken)
        {
            if (!Armed || expected.Username != "target" || reset != resetFirst) return;
            WriteEntered.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
        }
        public async Task<Result> ResetPassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default)
        {
            await Pause(true, expected, cancellationToken);
            var result = await inner.ResetPassword(expected, replacement, cancellationToken);
            if (expected.Username == "target" && result.IsSuccess) Interlocked.Increment(ref TargetResetWrites);
            return result;
        }
        public async Task<Result> ReplacePassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default)
        {
            await Pause(false, expected, cancellationToken);
            var result = await inner.ReplacePassword(expected, replacement, cancellationToken);
            if (expected.Username == "target" && result.IsSuccess) Interlocked.Increment(ref TargetReplacementWrites);
            return result;
        }
        public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default) => inner.Load(cancellationToken);
        public Task<Result<Maybe<Administrator>>> LoadForSignIn(string? username, CancellationToken cancellationToken = default) => inner.LoadForSignIn(username, cancellationToken);
        public Task<Result<IReadOnlyList<Administrator>>> List(CancellationToken cancellationToken = default) => inner.List(cancellationToken);
        public Task<Result> CreateAccount(Administrator account, CancellationToken cancellationToken = default) => inner.CreateAccount(account, cancellationToken);
        public Task<Result> ChangeAvailability(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) => inner.ChangeAvailability(expected, replacement, cancellationToken);
        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) => inner.CreateIfAbsent(administrator, cancellationToken);
        public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now, CancellationToken cancellationToken = default) => inner.TryRecordSignIn(expected, username, succeeded, now, cancellationToken);
        public Task<Result> RecoverPassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) => inner.RecoverPassword(expected, replacement, cancellationToken);
    }
}
