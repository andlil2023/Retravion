using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Application.AiWorkflow;
using Retravion.Application.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.UserNeed;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpRequiresBrowserLoginAndAntiforgeryAndNeverAcceptsCookiesAsMcpCredentials()
    {
        await using var host = new McpTestHost();
        using (var anonymous = await host.Issue()) Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        using (var anonymous = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var pageDenied = await host.Browser.GetAsync("/mcp-access")) Assert.Equal("/", pageDenied.Headers.Location?.OriginalString);
        await host.Login();
        using (var restricted = await host.Issue()) Assert.Equal("/password-replacement", restricted.Headers.Location?.OriginalString);
        using (var pageDenied = await host.Browser.GetAsync("/mcp-access")) Assert.Equal("/password-replacement", pageDenied.Headers.Location?.OriginalString);
        Assert.DoesNotContain("href=\"/settings/ai-workflow\"", await host.Browser.GetStringAsync("/password-replacement"));
        await host.Replace();
        using (var csrf = await host.Browser.PostAsync("/mcp-connect/decide", new FormUrlEncodedContent([])))
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        // The copied-credential route no longer exists.
        using (var removed = await host.Browser.PostAsync("/identity/mcp-token", await host.Form("/")))
            Assert.Equal(HttpStatusCode.MethodNotAllowed, removed.StatusCode);
        using (var cookieOnly = await McpTestHost.Send(host.Browser, "tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        Assert.Contains("href=\"/settings/ai-workflow\"", await host.Browser.GetStringAsync("/home"));
        using var pageResponse = await host.Browser.GetAsync("/mcp-access?revoked=true");
        Assert.True(pageResponse.Headers.CacheControl?.NoStore);
        var page = await pageResponse.Content.ReadAsStringAsync();
        Assert.Matches("<h1[^>]*>AI Workflow</h1>", page);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<h1\\b"));
        Assert.Contains("aria-label=\"AI Workflow content\"", page);
        Assert.Matches("href=\"/settings/ai-workflow\"[^>]*aria-current=\"page\"", page);
        Assert.Contains("href=\"/home\"", page);
        Assert.Contains("navigation-toggle", page);
        Assert.DoesNotContain("Create MCP credential", page);
        Assert.DoesNotContain("action=\"/identity/mcp-token\"", page);
        Assert.Contains("Code shown in your terminal", page);
        Assert.Contains("Confirm ending", page);
        Assert.Contains("/identity/mcp-token/revoke", page);
        var token = await host.Authenticate();
        Assert.Equal(64, token.Length);
        var tools = await host.Rpc("tools/list");
        Assert.Equal(new[] { "create_draft_requirement", "create_draft_test_case", "create_draft_user_need", "edit_draft_requirement", "edit_draft_test_case", "edit_draft_user_need", "list_products", "list_requirements", "list_test_cases", "list_user_needs", "read_product", "read_requirement", "read_test_case", "read_user_need" },
            tools.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order().ToArray());
        using (var origin = await host.Send("tools/list", origin: "https://evil.invalid")) Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        host.Mcp.DefaultRequestHeaders.Host = "evil.invalid";
        using (var rebinding = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Forbidden, rebinding.StatusCode);
        host.Mcp.DefaultRequestHeaders.Host = null;
        var replacement = await host.Authenticate();
        host.SetToken(token);
        using (var rotated = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
        host.SetToken(replacement);
        using (var noCsrf = await host.Browser.PostAsync("/identity/mcp-token/revoke", new FormUrlEncodedContent([])))
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using (var revoke = await host.Revoke()) Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        using (var revoked = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task McpDeniedCreatesAndEditsAreAuditedWithoutValuesAndAuditFailuresStayDenied()
    {
        var recorder = new NeedAuditRecorder();
        var logger = new NeedAuditLogger();
        var store = new NeedStoreDouble();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAuditSubmission>(recorder);
            services.AddSingleton<ILogger<UserNeedOperations>>(logger);
            services.AddSingleton<IUserNeedStore>(store);
            services.AddSingleton<IUserNeedChangeTransaction>(store);
        });
        await host.Login();
        // Only the browser flow can issue credentials. A forced restricted credential is rejected too.
        var restricted = await ProductPrincipal(host.Browser, host.Factory.Services, "admin", Initial, "/password-replacement");
        var credentials = host.Factory.Services.GetRequiredService<McpCredentials>();
        Assert.False(await credentials.Issue(restricted, host.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>(),
            McpCredentials.Digest(new string('A', 64))));
        await host.Replace();
        await host.Authenticate();
        var bearer = host.Mcp.DefaultRequestHeaders.Authorization!.Parameter!;
        var principal = credentials.Resolve(bearer);
        using (var originWrite = await host.Send("tools/call", new { name = "create_draft_user_need", arguments = new { id = "secret" } }, "https://evil.invalid"))
            Assert.Equal(HttpStatusCode.Forbidden, originWrite.StatusCode);
        Assert.Single(recorder.Events);
        Assert.Equal(new AuditActor.Identified("admin"), recorder.Events[0].Actor);
        recorder.Events.Clear();
        await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(principal.FindFirstValue(SessionTicketStore.SessionClaim)!);
        foreach (var token in new string?[] { null, "invalid", bearer })
        {
            host.SetToken(token);
            foreach (var tool in new[] { "create_draft_user_need", "edit_draft_user_need" })
            {
                using var denied = await host.Send("tools/call", new { name = tool, arguments = new { id = "secret-id", title = "secret-title", statement = "secret-statement", actor = "spoofed" } });
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            using var read = await host.Send("tools/call", new { name = "read_user_need", arguments = new { internalId = Guid.NewGuid() } });
            Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        }
        Assert.Equal(6, recorder.Events.Count);
        Assert.Equal(2, recorder.Events.Count(e => e.Actor == new AuditActor.Identified("admin")));
        Assert.All(recorder.Events, e =>
        {
            Assert.IsType<AuditTarget.Unknown>(e.Target);
            Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
            Assert.Null(e.UserNeedChange);
            Assert.Contains(e.Action, new[] { "need.create", "need.edit" });
            Assert.True(e.OccurredAt > DateTimeOffset.UnixEpoch);
        });
        foreach (var throwing in new[] { false, true })
        {
            recorder.Fail = true; recorder.Throw = throwing;
            foreach (var tool in new[] { "create_draft_user_need", "edit_draft_user_need" })
            {
                using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret-title" } });
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
        }
        Assert.Equal(4, logger.Messages.Count);
        Assert.All(logger.Messages, m => Assert.Equal("Unauthorized User Need change audit submission failed.", m));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task McpCredentialsExpireAndLogoutAndAccountRevocationBlockAccessAtTheWriteBoundary()
    {
        var clock = new McpClock();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IUserNeedStore>(new NeedStoreDouble());
            services.AddSingleton<IUserNeedChangeTransaction>(new NeedStoreDouble());
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var credentials = host.Factory.Services.GetRequiredService<McpCredentials>();
        var principal = credentials.Resolve(host.Mcp.DefaultRequestHeaders.Authorization!.Parameter);
        clock.Now += McpCredentials.Lifetime;
        using (var expired = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<UserNeedOperations>();
        Assert.True((await operations.Create(principal, Guid.NewGuid(), "ID", "Title", "Statement")).IsFailure);
        // A live browser session does not make an expired/rotated/revoked MCP principal active.
        Assert.False(credentials.IsActive(principal));
        clock.Now -= McpCredentials.Lifetime;
        await host.Authenticate();
        principal = credentials.Resolve(host.Mcp.DefaultRequestHeaders.Authorization!.Parameter);
        await host.Authenticate();
        Assert.True((await operations.EditDraft(principal, Guid.NewGuid(), 1, "ID", "Title", "Statement")).IsFailure);
        principal = credentials.Resolve(host.Mcp.DefaultRequestHeaders.Authorization!.Parameter);
        host.Factory.Services.GetRequiredService<SessionTicketStore>().RevokeAccount("admin");
        Assert.True((await operations.Create(principal, Guid.NewGuid(), "ID", "Title", "Statement")).IsFailure);
        using (var revoked = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await host.Login(Replacement, "/home"); await host.Authenticate();
        using (var logout = await host.Browser.PostAsync("/identity/logout", await host.Form("/home"))) Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using (var ended = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
    }

    [Fact]
    public async Task McpRejectsMalformedAndOversizedRequestsWithoutDisclosingData()
    {
        await using var host = new McpTestHost();
        await host.Login(); await host.Replace(); await host.Authenticate();
        using var malformed = await host.Mcp.PostAsync("/mcp", new StringContent("{private"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var oversized = await host.Mcp.PostAsync("/mcp", new StringContent(new string('s', 1024 * 1024 + 1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.DoesNotContain("private", await malformed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OrdinaryUsersCanReachMcpAccessThroughTheSharedNavigationAndCreateCredentials()
    {
        await using var factory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        using var admin = Client(factory);
        await Login(admin, "admin", Initial); await Replace(admin, Initial, Replacement);
        var settings = factory.Services.GetRequiredService<IAiWorkflowSettingsStore>();
        Assert.True((await settings.Save((await settings.Load()).Value, true, new AuditActor.Identified("admin"), DateTimeOffset.UtcNow)).IsSuccess);
        using var created = await Create(admin, "mcp-user", "MCP user", Retravion.Identity.Domain.AccountType.Ordinary);
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var temporary = document.RootElement.GetProperty("temporaryPassword").GetString()!;
        using var user = Client(factory);
        await Login(user, "mcp-user", temporary); await Replace(user, temporary, Replacement);
        var home = await user.GetStringAsync("/home");
        Assert.Contains("href=\"/settings/ai-workflow\"", home);
        Assert.DoesNotContain("Administration navigation", home);
        var page = await user.GetStringAsync("/mcp-access");
        Assert.Matches("<h1[^>]*>AI Workflow</h1>", page);
        using var started = await user.PostAsync("/mcp-connect/start", null);
        var start = await started.Content.ReadFromJsonAsync<JsonElement>();
        using var approved = await user.PostAsync("/mcp-connect/decide", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Token(page), ["code"] = start.GetProperty("userCode").GetString()!, ["decision"] = "approve" }));
        Assert.Equal("/settings/ai-workflow?connection=approved", approved.Headers.Location?.OriginalString);
        var principal = factory.Services.GetRequiredService<McpCredentials>().Resolve(start.GetProperty("deviceCode").GetString());
        Assert.Equal("mcp-user", principal.FindFirstValue(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public async Task TerminalHandoffIsApprovedInTheBrowserOnceAndNeverReturnsACredential()
    {
        var clock = new McpClock();
        await using var host = new McpTestHost(services => services.AddSingleton<TimeProvider>(clock));
        await host.Login(); await host.Replace();
        using (var origin = await host.Start(origin: "https://evil.invalid")) Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        host.Mcp.DefaultRequestHeaders.Host = "evil.invalid";
        using (var rebinding = await host.Start()) Assert.Equal(HttpStatusCode.Forbidden, rebinding.StatusCode);
        host.Mcp.DefaultRequestHeaders.Host = null;

        using (var started = await host.Start())
        {
            Assert.Equal("no-store", started.Headers.CacheControl?.ToString());
            using var document = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
            var start = document.RootElement;
            Assert.Equal(new[] { "deviceCode", "expiresIn", "interval", "userCode", "verificationUri" }, start.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Matches("^[0-9A-F]{64}$", host.DeviceCode);
            Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", host.UserCode);
            Assert.Equal($"https://localhost/settings/ai-workflow?connect={host.UserCode}", start.GetProperty("verificationUri").GetString());
            Assert.Equal(600, start.GetProperty("expiresIn").GetInt32());
        }
        // Until approval the bridge's secret is not a credential.
        Assert.Equal("pending", await host.Status(host.DeviceCode));
        host.SetToken(host.DeviceCode);
        using (var early = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, early.StatusCode);
        var page = await host.Browser.GetStringAsync($"/settings/ai-workflow?connect={host.UserCode!.ToLowerInvariant()}");
        Assert.Contains(host.UserCode, page);
        Assert.Contains("Approve connection", page);
        Assert.Contains("Deny connection", page);
        Assert.DoesNotContain(host.DeviceCode!, page);
        Assert.Contains("unknown, already used or expired", await host.Browser.GetStringAsync("/settings/ai-workflow?connect=ZZZZ-ZZZZ"));
        Assert.DoesNotContain("<script>", await host.Browser.GetStringAsync("/settings/ai-workflow?connect=%3Cscript%3E"));
        using (var invalid = await host.Decide("maybe")) Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using (var approved = await host.Decide("approve"))
        {
            Assert.Equal("/settings/ai-workflow?connection=approved", approved.Headers.Location?.OriginalString);
            Assert.DoesNotContain(host.DeviceCode!, await approved.Content.ReadAsStringAsync());
        }
        using (var listed = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        // Single use: a second decision is refused and the outcome is reported to the bridge once.
        using (var reused = await host.Decide("deny")) Assert.Equal("/settings/ai-workflow?connection=expired", reused.Headers.Location?.OriginalString);
        Assert.Equal("approved", await host.Status(host.DeviceCode));
        Assert.Equal("expired", await host.Status(host.DeviceCode));
        using (var still = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        Assert.Contains("Connection approved", await host.Browser.GetStringAsync("/settings/ai-workflow?connection=approved"));

        // Denied and expired requests never become credentials.
        using (await host.Start()) { }
        using (var denied = await host.Decide("deny")) Assert.Equal("/settings/ai-workflow?connection=denied", denied.Headers.Location?.OriginalString);
        Assert.Equal("denied", await host.Status(host.DeviceCode));
        host.SetToken(host.DeviceCode);
        using (var refused = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        using (await host.Start()) { }
        clock.Now += McpConnections.Lifetime;
        Assert.Equal("expired", await host.Status(host.DeviceCode));
        using (var late = await host.Decide("approve")) Assert.Equal("/settings/ai-workflow?connection=expired", late.Headers.Location?.OriginalString);
        host.SetToken(host.DeviceCode);
        using (var expired = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal("expired", await host.Status("unknown"));

        // Pending requests are bounded; expired ones free their place.
        for (var i = 0; i < McpConnections.Capacity; i++) using (var filled = await host.Start()) Assert.Equal(HttpStatusCode.OK, filled.StatusCode);
        using (var full = await host.Start()) Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        clock.Now += McpConnections.Lifetime;
        using (var freed = await host.Start()) Assert.Equal(HttpStatusCode.OK, freed.StatusCode);
    }

    [Fact]
    public async Task TerminalApprovalLinkSurvivesSignInWithoutAcceptingAReturnAddress()
    {
        await using var host = new McpTestHost();
        await host.Login(); await host.Replace();
        using (var logout = await host.Browser.PostAsync("/identity/logout", await host.Form("/home"))) Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using (await host.Start()) { }
        var code = host.UserCode!;
        // Signed out: the approval link leads to sign-in carrying only the normalized code.
        using (var link = await host.Browser.GetAsync($"/settings/ai-workflow?connect={code.ToLowerInvariant()}"))
            Assert.Equal($"/?connect={code}", link.Headers.Location?.OriginalString);
        foreach (var junk in new[] { "/settings/ai-workflow?connect=https://evil.invalid", "/settings/ai-workflow?connect=%2Fadministration", "/settings/ai-workflow", "/home?connect=" + code })
            using (var plain = await host.Browser.GetAsync(junk)) Assert.Equal("/", plain.Headers.Location?.OriginalString);
        Assert.Contains($"name=\"connect\" value=\"{code}\"", await host.Browser.GetStringAsync($"/?connect={code}"));
        Assert.DoesNotContain("name=\"connect\"", await host.Browser.GetStringAsync("/?connect=https://evil.invalid"));
        Assert.DoesNotContain("name=\"connect\"", await host.Browser.GetStringAsync("/"));

        async Task<string?> SignIn(string password, string connect)
        {
            using var response = await host.Browser.PostAsync("/identity/login", await host.Form("/",
                new() { ["username"] = "admin", ["password"] = password, ["connect"] = connect }));
            return response.Headers.Location?.OriginalString;
        }
        Assert.Equal($"/?failed=true&connect={code}", await SignIn("wrong", code));
        Assert.Equal("/?failed=true", await SignIn("wrong", "https://evil.invalid"));
        Assert.Equal("/home", await SignIn("Replacement123!", "//evil.invalid/settings/ai-workflow"));
        using (var logout = await host.Browser.PostAsync("/identity/logout", await host.Form("/home"))) Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal($"/settings/ai-workflow?connect={code}", await SignIn("Replacement123!", code));
        Assert.Contains("Approve connection", await host.Browser.GetStringAsync($"/settings/ai-workflow?connect={code}"));
        using (var approved = await host.Decide("approve")) Assert.Equal("/settings/ai-workflow?connection=approved", approved.Headers.Location?.OriginalString);
        host.SetToken(host.DeviceCode);
        using (var listed = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    [Fact]
    public async Task TerminalAccessRenewsWhileInUseExpiresWhenIdleAndEndsOnDisconnectOrDisabling()
    {
        var clock = new McpClock();
        var settings = new InMemoryAiWorkflowSettingsStore(true);
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IAiWorkflowSettingsStore>(settings);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        // Well past the original 30 minutes, activity keeps the same credential alive.
        for (var i = 0; i < 4; i++)
        {
            clock.Now += McpCredentials.Lifetime - TimeSpan.FromMinutes(1);
            using var active = await host.Send("tools/list");
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        }
        // Denied requests do not renew.
        clock.Now += McpCredentials.Lifetime - TimeSpan.FromMinutes(1);
        using (var origin = await host.Send("tools/list", origin: "https://evil.invalid")) Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        clock.Now += TimeSpan.FromMinutes(1);
        using (var idle = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, idle.StatusCode);

        // The idle half hour also ended the browser session it was bound to.
        await host.Login("Replacement123!", "/home");
        var token = await host.Authenticate();
        using (var foreign = await host.Disconnect(new string('A', 64))) Assert.Equal(HttpStatusCode.NoContent, foreign.StatusCode);
        using (var kept = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        using (var disconnected = await host.Disconnect(token)) Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        using (var ended = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);

        // Disabling between request and approval refuses the approval and tells the bridge.
        using (await host.Start()) { }
        using (var disabled = await ConfigureAi(host, settings, false)) Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        using (var refused = await host.Decide("approve")) Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("denied", await host.Status(host.DeviceCode));
        using (var blocked = await host.Start()) Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    public void CertificateFingerprintIsShownOnlyForAReadableConfiguredCertificate()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), $"retravion-fingerprint-{Guid.NewGuid():N}.pfx");
        try
        {
            File.WriteAllBytes(path, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "throwaway"));
            Microsoft.Extensions.Configuration.IConfiguration Configuration(string? file, string? password) =>
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Kestrel:Certificates:Default:Path"] = file, ["Kestrel:Certificates:Default:Password"] = password }).Build();
            var expected = string.Join(':', Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.RawData)).Chunk(2).Select(c => new string(c)));
            Assert.Equal(expected, TerminalCertificate.Fingerprint(Configuration(path, "throwaway")));
            Assert.Null(TerminalCertificate.Fingerprint(Configuration(path, "wrong")));
            Assert.Null(TerminalCertificate.Fingerprint(Configuration(path + ".missing", "throwaway")));
            Assert.Null(TerminalCertificate.Fingerprint(Configuration(null, null)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ShippedBridgeToolListMatchesTheServerToolContracts()
    {
        await using var host = new McpTestHost();
        await host.Login(); await host.Replace(); await host.Authenticate();
        var server = await host.Rpc("tools/list");
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(TerminalPackages.Get("pi")!.Bytes));
        using var reader = new StreamReader(zip.GetEntry("bridge/tools.json")!.Open());
        using var shipped = JsonDocument.Parse(reader.ReadToEnd());
        static string Contract(JsonElement tool) => JsonSerializer.Serialize(new
        {
            name = tool.GetProperty("name").GetString(),
            description = tool.GetProperty("description").GetString(),
            inputSchema = JsonSerializer.Serialize(tool.GetProperty("inputSchema"))
        });
        // The bridge cannot ask the server before sign-in, so its list is shipped; this keeps it from drifting.
        Assert.Equal(server.GetProperty("tools").EnumerateArray().Select(Contract).Order(StringComparer.Ordinal).ToArray(),
            shipped.RootElement.EnumerateArray().Select(Contract).Order(StringComparer.Ordinal).ToArray());
    }

    private sealed class McpClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

internal sealed class McpTestHost : IAsyncDisposable
{
    private readonly AdministratorWebFactory original;
    internal WebApplicationFactory<Program> Factory { get; }
    internal HttpClient Browser { get; }
    internal HttpClient Mcp { get; }
    internal McpTestHost(Action<IServiceCollection>? configure = null)
    {
        original = new(new StartupAdministratorStore(), "admin", "InitialPass123!");
        Factory = original.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAiWorkflowSettingsStore>(new InMemoryAiWorkflowSettingsStore(true));
            configure?.Invoke(services);
        }));
        Browser = Factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Mcp = Factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("https://localhost") });
    }
    internal async Task<FormUrlEncodedContent> Form(string page, Dictionary<string, string>? fields = null)
    {
        var html = await Browser.GetStringAsync(page);
        var token = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        fields ??= [];
        fields["__RequestVerificationToken"] = token;
        return new(fields);
    }
    internal async Task Login(string password = "InitialPass123!", string destination = "/password-replacement")
    {
        using var response = await Browser.PostAsync("/identity/login", await Form("/", new() { ["username"] = "admin", ["password"] = password }));
        Assert.Equal(destination, response.Headers.Location?.OriginalString);
    }
    internal async Task Replace()
    {
        using var response = await Browser.PostAsync("/identity/password-replacement", await Form("/password-replacement", new()
        { ["currentPassword"] = "InitialPass123!", ["newPassword"] = "Replacement123!", ["confirmNewPassword"] = "Replacement123!" }));
        Assert.Equal("/home", response.Headers.Location?.OriginalString);
    }
    internal string? DeviceCode { get; private set; }
    internal string? UserCode { get; private set; }
    // The bridge's side of the handoff: no cookies, no Origin.
    internal async Task<HttpResponseMessage> Start(string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp-connect/start");
        if (origin is not null) request.Headers.Add("Origin", origin);
        var response = await Mcp.SendAsync(request);
        if (!response.IsSuccessStatusCode) return response;
        using var started = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        DeviceCode = started.RootElement.GetProperty("deviceCode").GetString();
        UserCode = started.RootElement.GetProperty("userCode").GetString();
        return response;
    }
    internal async Task<HttpResponseMessage> Decide(string decision) => await Browser.PostAsync("/mcp-connect/decide",
        await Form("/", new() { ["code"] = UserCode!, ["decision"] = decision }));
    internal async Task<string> Status(string? deviceCode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp-connect/status");
        request.Headers.Authorization = new("Bearer", deviceCode);
        using var response = await Mcp.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()!;
    }
    internal async Task<HttpResponseMessage> Disconnect(string deviceCode)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp-connect/disconnect");
        request.Headers.Authorization = new("Bearer", deviceCode);
        return await Mcp.SendAsync(request);
    }
    // Starts a terminal handoff and has the browser user approve it; a refused start is returned as is.
    internal async Task<HttpResponseMessage> Issue()
    {
        var started = await Start();
        if (!started.IsSuccessStatusCode) return started;
        started.Dispose();
        return await Decide("approve");
    }
    internal async Task<HttpResponseMessage> Revoke() => await Browser.PostAsync("/identity/mcp-token/revoke", await Form("/home"));
    internal async Task<string> Authenticate()
    {
        using var response = await Issue();
        Assert.Equal("/settings/ai-workflow?connection=approved", response.Headers.Location?.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("approved", await Status(DeviceCode));
        SetToken(DeviceCode);
        return DeviceCode!;
    }
    internal void SetToken(string? token) => Mcp.DefaultRequestHeaders.Authorization = token is null ? null : new("Bearer", token);
    internal Task<HttpResponseMessage> Send(string method, object? parameters = null, string? origin = null) => Send(Mcp, method, parameters, origin);
    internal static async Task<HttpResponseMessage> Send(HttpClient client, string method, object? parameters = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters }) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }
    internal async Task<JsonElement> Rpc(string method, object? parameters = null)
    {
        using var response = await Send(method, parameters);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {response.StatusCode}: {text}");
        if (text.StartsWith("event:", StringComparison.Ordinal) || text.StartsWith("data:", StringComparison.Ordinal))
            text = string.Join("", text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()));
        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.TryGetProperty("error", out _), text);
        return doc.RootElement.GetProperty("result").Clone();
    }
    internal async Task<JsonElement> Call(string tool, object arguments, bool error = false)
    {
        var result = await Rpc("tools/call", new { name = tool, arguments });
        Assert.Equal(error, result.TryGetProperty("isError", out var failed) && failed.GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        if (error) return JsonSerializer.SerializeToElement(text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        Browser.Dispose(); Mcp.Dispose();
        await Factory.DisposeAsync(); await original.DisposeAsync();
    }
}
