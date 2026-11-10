using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
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
        using (var csrf = await host.Browser.PostAsync("/identity/mcp-token", new FormUrlEncodedContent([])))
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
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
        Assert.Contains("Create MCP credential", page);
        Assert.Contains("Confirm revoking", page);
        Assert.Contains("/identity/mcp-token", page);
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
        Assert.Null(await credentials.Issue(restricted, host.Factory.Services.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>()));
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
        using var issued = await user.PostAsync("/identity/mcp-token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Token(page) }));
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var credential = (await issued.Content.ReadFromJsonAsync<IssuedMcpCredential>())!;
        var principal = factory.Services.GetRequiredService<McpCredentials>().Resolve(credential.Token);
        Assert.Equal("mcp-user", principal.FindFirstValue(ClaimTypes.NameIdentifier));
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
    internal async Task<HttpResponseMessage> Issue() => await Browser.PostAsync("/identity/mcp-token", await Form("/"));
    internal async Task<HttpResponseMessage> Revoke() => await Browser.PostAsync("/identity/mcp-token/revoke", await Form("/home"));
    internal async Task<string> Authenticate()
    {
        using var response = await Issue();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var issued = (await response.Content.ReadFromJsonAsync<IssuedMcpCredential>())!;
        SetToken(issued.Token);
        return issued.Token;
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
