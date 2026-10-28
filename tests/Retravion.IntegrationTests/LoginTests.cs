using System.Net;
using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Identity.Application;
using Retravion.Application.AuditTrail;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class LoginTests
{
    [Fact]
    public async Task DecisionsAreAuditedWithoutCredentialsOrInventedIdentities()
    {
        var audit = new AuditDouble();
        await using var factory = WithAudit(audit);
        using var client = CreateClient(factory);
        var token = await GetToken(client);
        using var failure = await Submit(client, token, "unknown-user", "secret-password");
        using var success = await Submit(client, token, "specific-admin", "ValidPass123!");
        Assert.Equal("/?failed=true", failure.Headers.Location?.OriginalString);
        Assert.Equal("/password-replacement", success.Headers.Location?.OriginalString);
        Assert.Equal(2, audit.Events.Count);
        var failed = audit.Events[0];
        Assert.Equal("identity.login", failed.Action);
        Assert.Equal(AuditActionOutcome.Failure, failed.Outcome);
        Assert.IsType<AuditActor.Unknown>(failed.Actor);
        Assert.IsType<AuditTarget.Unknown>(failed.Target);
        var passed = audit.Events[1];
        Assert.Equal("identity.login", passed.Action);
        Assert.Equal(AuditActionOutcome.Success, passed.Outcome);
        Assert.Equal("specific-admin", Assert.IsType<AuditActor.Identified>(passed.Actor).Identifier);
        Assert.IsType<AuditTarget.Unknown>(passed.Target);
        Assert.DoesNotContain("secret-password", string.Join(" ", audit.Events));
        Assert.DoesNotContain("ValidPass123!", string.Join(" ", audit.Events));
        Assert.DoesNotContain("unknown-user", string.Join(" ", audit.Events));
    }

    [Fact]
    public async Task SessionIsGrantedOnlyAfterAuditConfirmation()
    {
        var completion = new TaskCompletionSource<AuditSubmissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audit = new AuditDouble { Submit = (_, _) => completion.Task };
        await using var factory = WithAudit(audit);
        using var client = CreateClient(factory);
        var request = Submit(client, await GetToken(client), "specific-admin", "ValidPass123!");
        await audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(request.IsCompleted);
        completion.SetResult(AuditSubmissionResult.Success);
        using var response = await request;
        Assert.Equal("/password-replacement", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Retravion.Identity="));
    }

    [Fact]
    public async Task PasswordReplacementDuringAuditConfirmationCannotGrantAStaleSession()
    {
        var completion = new TaskCompletionSource<AuditSubmissionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audit = new AuditDouble { Submit = (_, _) => completion.Task };
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "specific-admin", "ValidPass123!")
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IAuditSubmission>(audit)));
        using var client = CreateClient(factory);
        var request = Submit(client, await GetToken(client), "specific-admin", "ValidPass123!");
        await audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var account = store.Account.Value;
        store.Account = account.ReplacePassword("Replacement123!", new Pbkdf2PasswordHasher().Hash("Replacement123!")).Value;
        completion.SetResult(AuditSubmissionResult.Success);
        using var response = await request;
        Assert.Equal("/?failed=true", response.Headers.Location?.OriginalString);
        using var home = await client.GetAsync("/home");
        Assert.NotEqual(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal(AuditActionOutcome.Success, Assert.Single(audit.Events).Outcome);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancellation")]
    [InlineData("timeout")]
    public async Task UnconfirmedAuditNeverGrantsSession(string mode)
    {
        var audit = new AuditDouble { Submit = (_, _) => mode switch
        {
            "failure" => Task.FromResult(AuditSubmissionResult.Failure),
            "exception" => Task.FromException<AuditSubmissionResult>(new Exception("secret-provider-details")),
            "cancellation" => Task.FromCanceled<AuditSubmissionResult>(new CancellationToken(true)),
            _ => new TaskCompletionSource<AuditSubmissionResult>().Task
        }};
        await using var factory = WithAudit(audit);
        using var client = CreateClient(factory);
        using var response = await Submit(client, await GetToken(client), "specific-admin", "ValidPass123!");
        Assert.Equal("/?failed=true", response.Headers.Location?.OriginalString);
        using var home = await client.GetAsync("/home");
        Assert.NotEqual(HttpStatusCode.OK, home.StatusCode);
        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task AntiforgeryAndRetryRestrictionsAreAuditedAtTheCorrectBoundary()
    {
        var audit = new AuditDouble();
        await using var factory = WithAudit(audit);
        using var client = CreateClient(factory);
        var token = await GetToken(client);
        using var invalid = await Submit(client, "invalid-token", "specific-admin", "ValidPass123!");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(audit.Events);
        for (var i = 0; i < 3; i++)
        {
            using var failure = await Submit(client, token, "specific-admin", "wrong");
            Assert.Equal("/?failed=true", failure.Headers.Location?.OriginalString);
        }
        using var blocked = await Submit(client, token, "specific-admin", "ValidPass123!");
        Assert.Equal("/?failed=true", blocked.Headers.Location?.OriginalString);
        Assert.Equal(4, audit.Events.Count);
        Assert.All(audit.Events, item => Assert.Equal(AuditActionOutcome.Failure, item.Outcome));
    }

    [Fact]
    public async Task FailedAuthenticationRemainsDeniedWhenAuditFails()
    {
        var audit = new AuditDouble { Submit = (_, _) => Task.FromResult(AuditSubmissionResult.Failure) };
        await using var factory = WithAudit(audit);
        using var client = CreateClient(factory);
        using var response = await Submit(client, await GetToken(client), "specific-admin", "wrong");
        Assert.Equal("/?failed=true", response.Headers.Location?.OriginalString);
        Assert.Equal(AuditActionOutcome.Failure, Assert.Single(audit.Events).Outcome);
    }

    [Fact]
    public async Task MatchingCredentialsEstablishTheSpecificIdentityOnTheNextRequest()
    {
        var observer = new IdentityObserver();
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(observer)));
        using var client = CreateClient(factory);
        var token = await GetToken(client);
        using var response = await Submit(client, token, "specific-admin", "ValidPass123!");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/password-replacement", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Retravion.Identity="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
        using var home = await client.GetAsync("/home");
        Assert.True(observer.User?.Identity?.IsAuthenticated);
        Assert.Equal("specific-admin", observer.User?.Identity?.Name);
        Assert.Equal("specific-admin", observer.User?.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.False(observer.User?.IsInRole("Administrator"));
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/password-replacement", home.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("wrong-user", "ValidPass123!")]
    [InlineData("specific-admin", "wrong-password")]
    [InlineData("", "")]
    public async Task FailureReturnsGenericMessageAndNoIdentity(string username, string password)
    {
        var observer = new IdentityObserver();
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(observer)));
        using var client = CreateClient(factory);
        var token = await GetToken(client);
        using var response = await Submit(client, token, username, password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?failed=true", response.Headers.Location?.OriginalString);
        var html = await client.GetStringAsync(response.Headers.Location);
        Assert.Contains(PasswordSignIn.FailureMessage, html);
        Assert.DoesNotContain(username.Length == 0 ? "wrong-user" : username, html);
        Assert.DoesNotContain(password.Length == 0 ? "wrong-password" : password, html);
        Assert.False(observer.User?.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task FailedSignInClearsAnExistingAuthenticatedSession()
    {
        var observer = new IdentityObserver();
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(observer)));
        using var client = CreateClient(factory);
        using var success = await Submit(client, await GetToken(client), "specific-admin", "ValidPass123!");
        using var home = await client.GetAsync("/home");
        Assert.True(observer.User?.Identity?.IsAuthenticated);

        using var failure = await Submit(client, await GetToken(client), "specific-admin", "wrong");
        Assert.Equal("/?failed=true", failure.Headers.Location?.OriginalString);
        using var next = await client.GetAsync("/home");
        Assert.False(observer.User?.Identity?.IsAuthenticated);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-token")]
    public async Task MissingOrInvalidAntiforgeryTokenCannotSignIn(string? token)
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);
        await GetToken(client);
        using var response = await Submit(client, token, "specific-admin", "ValidPass123!");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task ReplacementBetweenAuthenticationAndSessionCreationCannotGrantAccessWithOldPassword()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var initial = Administrator.Create("specific-admin", hasher.Hash("ValidPass123!")).Value;
        var replaced = initial.ReplacePassword("Replacement123!", hasher.Hash("Replacement123!")).Value;
        var store = new ConcurrentReplacementStore(initial, replaced);
        var audit = new AuditDouble();
        var observer = new IdentityObserver();
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IAdministratorStore>(store);
                services.AddSingleton<IAuditSubmission>(audit);
                services.AddSingleton<IStartupFilter>(observer);
            }));
        using var client = CreateClient(factory);
        using var response = await Submit(client, await GetToken(client), "specific-admin", "ValidPass123!");

        Assert.Equal(AuditActionOutcome.Failure, Assert.Single(audit.Events).Outcome);
        Assert.Equal(3, store.Loads); // Startup, credential verification, then access decision.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?failed=true", response.Headers.Location?.OriginalString);
        using var next = await client.GetAsync("/");
        Assert.False(observer.User?.Identity?.IsAuthenticated);
        Assert.False(observer.User?.IsInRole("Administrator"));
    }

    [Fact]
    public async Task InactivationDuringAuditConfirmationCannotIssueASession()
    {
        var store = new StartupAdministratorStore();
        var audit = new AuditDouble
        {
            Submit = (_, _) =>
            {
                store.Account = store.Account.Value.Inactivate("another-admin").Value;
                return Task.FromResult(AuditSubmissionResult.Success);
            }
        };
        await using var factory = new AdministratorWebFactory(store, "specific-admin", "ValidPass123!")
            .WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IAuditSubmission>(audit)));
        using var client = CreateClient(factory);
        using var response = await Submit(client, await GetToken(client), "specific-admin", "ValidPass123!");
        Assert.Equal("/?failed=true", response.Headers.Location?.OriginalString);
        Assert.False(store.Account.Value.IsActive);
        using var protectedResponse = await client.GetAsync("/home");
        Assert.Equal("/", protectedResponse.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("specific-admin")]
    [InlineData("unknown")]
    public async Task RetryRestrictionsAreSharedAcrossBrowsersAndUseTheSameOutwardFailure(string username)
    {
        var clock = new Clock();
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var firstBrowser = CreateClient(factory);
        using var secondBrowser = CreateClient(factory);
        var firstToken = await GetToken(firstBrowser);
        var secondToken = await GetToken(secondBrowser);

        for (var i = 0; i < 3; i++)
        {
            using var failure = await Submit(firstBrowser, firstToken, username, "wrong");
            Assert.Equal(HttpStatusCode.Redirect, failure.StatusCode);
            Assert.Equal("/?failed=true", failure.Headers.Location?.OriginalString);
        }

        using var blocked = await Submit(secondBrowser, secondToken, username, "ValidPass123!");
        Assert.Equal("/?failed=true", blocked.Headers.Location?.OriginalString);
        Assert.Contains(PasswordSignIn.FailureMessage, await secondBrowser.GetStringAsync("/?failed=true"));
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        using var early = await Submit(secondBrowser, secondToken, username, "ValidPass123!");
        Assert.Equal("/?failed=true", early.Headers.Location?.OriginalString);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var fourth = await Submit(secondBrowser, secondToken, username, "wrong");
        Assert.Equal("/?failed=true", fourth.Headers.Location?.OriginalString);
        using var blockedAgain = await Submit(firstBrowser, firstToken, username, "ValidPass123!");
        Assert.Equal("/?failed=true", blockedAgain.Headers.Location?.OriginalString);

        clock.Advance(TimeSpan.FromSeconds(5));
        using var allowed = await Submit(secondBrowser, secondToken, username, "ValidPass123!");
        Assert.Equal(username == "specific-admin" ? "/password-replacement" : "/?failed=true",
            allowed.Headers.Location?.OriginalString);

        if (username == "specific-admin")
        {
            for (var i = 0; i < 2; i++)
            {
                using var failure = await Submit(firstBrowser, firstToken, username, "wrong");
                Assert.Equal("/?failed=true", failure.Headers.Location?.OriginalString);
            }
            using var reset = await Submit(firstBrowser, firstToken, username, "ValidPass123!");
            Assert.Equal("/password-replacement", reset.Headers.Location?.OriginalString);
        }
        else
        {
            using var unaffected = await Submit(firstBrowser, firstToken, "specific-admin", "ValidPass123!");
            Assert.Equal("/password-replacement", unaffected.Headers.Location?.OriginalString);
        }
    }

    [Theory]
    [InlineData("specific-admin")]
    [InlineData("unknown")]
    public async Task TenthFailureBlocksOtherBrowsersWithoutRevealingAccountExistence(string username)
    {
        var clock = new Clock();
        var observer = new IdentityObserver();
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IStartupFilter>(observer);
            }));
        using var first = CreateClient(factory);
        using var second = CreateClient(factory);
        var token = await GetToken(first);
        for (var i = 0; i < 10; i++)
        {
            using var failure = await Submit(first, token, username, "wrong");
            Assert.Equal(HttpStatusCode.Redirect, failure.StatusCode);
            Assert.Equal("/?failed=true", failure.Headers.Location?.OriginalString);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var secondToken = await GetToken(second);
        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromDays(365) })
        {
            clock.Advance(wait);
            using var blocked = await Submit(second, secondToken, username, "ValidPass123!");
            Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
            Assert.Equal("/?failed=true", blocked.Headers.Location?.OriginalString);
            var html = await second.GetStringAsync("/?failed=true");
            Assert.Contains(PasswordSignIn.FailureMessage, html);
            Assert.DoesNotContain(username, html);
            Assert.False(observer.User?.Identity?.IsAuthenticated);
        }
    }

    [Fact]
    public async Task LockoutLeavesExistingSessionsActiveUntilNormalInactivityExpiration()
    {
        var clock = new Clock();
        var store = new StartupAdministratorStore
        {
            Account = Administrator.Create("specific-admin", new Pbkdf2PasswordHasher().Hash("ValidPass123!"),
                requiresPasswordReplacement: false).Value
        };
        await using var factory = new AdministratorWebFactory(store).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var existing = CreateClient(factory);
        using var attacker = CreateClient(factory);
        using var login = await Submit(existing, await GetToken(existing), "specific-admin", "ValidPass123!");
        Assert.Equal("/home", login.Headers.Location?.OriginalString);
        var token = await GetToken(attacker);
        for (var i = 0; i < 10; i++)
        {
            using var failure = await Submit(attacker, token, "specific-admin", "wrong");
            Assert.Equal("/?failed=true", failure.Headers.Location?.OriginalString);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        Assert.True(store.Account.Value.Failures.IsLocked);
        using var home = await existing.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(30).Subtract(TimeSpan.FromTicks(1)));
        using var stillActive = await existing.GetAsync("/home");
        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(30));
        using var expired = await existing.GetAsync("/home");
        Assert.Equal(HttpStatusCode.Redirect, expired.StatusCode);
        Assert.Equal("/", expired.Headers.Location?.OriginalString);
    }

    private static WebApplicationFactory<Program> WithAudit(AuditDouble audit) =>
        CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IAuditSubmission>(audit)));

    private sealed class AuditDouble : IAuditSubmission
    {
        public List<AuditInformation> Events { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<AuditInformation, CancellationToken, Task<AuditSubmissionResult>> Submit { get; init; } =
            (_, _) => Task.FromResult(AuditSubmissionResult.Success);
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default)
        {
            Events.Add(information);
            Entered.TrySetResult();
            return Submit(information, cancellationToken);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class ConcurrentReplacementStore(Administrator initial, Administrator replaced) : IAdministratorStore
    {
        public Task<Result<Maybe<Administrator>>> LoadForSignIn(string? username, CancellationToken cancellationToken = default) => Load(cancellationToken);
        public Task<Result<IReadOnlyList<Administrator>>> List(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> CreateAccount(Administrator account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ChangeAvailability(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public int Loads { get; private set; }
        public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
        {
            Loads++;
            return Task.FromResult(Result.Success<Maybe<Administrator>>(Loads <= 2 ? initial : replaced));
        }
        public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(!string.Equals(username, expected.Username, StringComparison.Ordinal) ||
                ReferenceEquals(initial, expected) && succeeded && expected.Failures.CanAttempt(now));
        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Result> ResetPassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RecoverPassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static AdministratorWebFactory CreateFactory() =>
        new(new StartupAdministratorStore(), "specific-admin", "ValidPass123!");

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    private static async Task<string> GetToken(HttpClient client)
    {
        var html = await client.GetStringAsync("/");
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Login form must render an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static Task<HttpResponseMessage> Submit(HttpClient client, string? token, string username, string password)
    {
        var form = new Dictionary<string, string> { ["username"] = username, ["password"] = password };
        if (token is not null) form["__RequestVerificationToken"] = token;
        return client.PostAsync("/identity/login", new FormUrlEncodedContent(form));
    }

    private sealed class IdentityObserver : IStartupFilter
    {
        public ClaimsPrincipal? User { get; private set; }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                await continuation();
                User = context.User;
            });
            next(app);
        };
    }
}
