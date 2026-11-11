using CSharpFunctionalExtensions;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AiWorkflow;
using Retravion.Application.AuditTrail;
using Retravion.Application.UserNeed;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Web.Features.Requirement;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.UserNeed;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task AiWorkflowStartsDisabledAndAdministratorMustConfirmAndPassAntiforgeryToChangeIt()
    {
        var settings = new InMemoryAiWorkflowSettingsStore();
        await using var host = new McpTestHost(services => services.AddSingleton<IAiWorkflowSettingsStore>(settings));
        await host.Login(); await host.Replace();
        var page = await host.Browser.GetStringAsync("/settings/mcp");
        Assert.Matches("<h1[^>]*>MCP settings</h1>", page);
        Assert.Contains("Settings navigation", page);
        Assert.Contains("Enable MCP", page);
        var action = System.Text.RegularExpressions.Regex.Match(page, "<button(?=[^>]*form=\"ai-workflow-settings-form\")[^>]*>[\\s\\S]*?</button>");
        Assert.True(action.Success);
        AssertFilledPrimaryButton(action.Value);
        var header = System.Text.RegularExpressions.Regex.Match(page, "<header[^>]*class=\"page-toolbar\"[^>]*>[\\s\\S]*?</header>");
        Assert.DoesNotContain("ai-workflow-settings-form", header.Value);
        Assert.True(action.Index > page.IndexOf("</form>", page.IndexOf("id=\"ai-workflow-settings-form\"", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.Contains("<strong>Disabled</strong>", page);
        Assert.Contains("requests starting after confirmed disabling are denied", page);
        Assert.Contains("name=\"confirm\"", page);
        using (var disabled = await host.Issue()) Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        var fields = new Dictionary<string, string> { ["before"] = "false", ["revision"] = "1", ["enabled"] = "true", ["confirm"] = "true" };
        using (var csrf = await host.Browser.PostAsync("/settings/ai-workflow/save", new FormUrlEncodedContent(fields)))
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        fields.Remove("confirm");
        using (var confirmation = await host.Browser.PostAsync("/settings/ai-workflow/save", await host.Form("/settings/mcp", fields)))
            Assert.Equal(HttpStatusCode.BadRequest, confirmation.StatusCode);
        Assert.Equal(0, settings.Saves);
        using (var enabled = await ConfigureAi(host, settings, true))
        {
            Assert.Equal(HttpStatusCode.Redirect, enabled.StatusCode);
            Assert.Equal("/settings/mcp?saved=true", enabled.Headers.Location?.OriginalString);
        }
        Assert.Equal(new(true, 2), settings.Current);
        await host.Authenticate();
        using (var stale = await host.Browser.PostAsync("/settings/ai-workflow/save", await host.Form("/settings/mcp", new()
        { ["before"] = "false", ["revision"] = "1", ["enabled"] = "false", ["confirm"] = "true" })))
            Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal(new(true, 2), settings.Current);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AiWorkflowKeepsCredentialsAndCompactIntegrationsTogetherWithoutAdministration(bool enabled, bool unavailable)
    {
        var settings = new InMemoryAiWorkflowSettingsStore(enabled) { Unavailable = unavailable };
        await using var host = new McpTestHost(services => services.AddSingleton<IAiWorkflowSettingsStore>(settings));
        await host.Login(); await host.Replace();
        foreach (var route in new[] { "/settings/ai-workflow", "/mcp-access" })
        {
            var page = await host.Browser.GetStringAsync(route);
            Assert.Matches("<h1[^>]*>AI Workflow</h1>", page);
            Assert.Contains("Terminal connection", page);
            Assert.Contains("Terminal integrations", page);
            Assert.Contains("Download Pi skills", page);
            Assert.Contains("Download Claude Code skills", page);
            Assert.DoesNotContain("Application-wide MCP access", page);
            Assert.DoesNotContain("ai-workflow-settings-form", page);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "href=\"/settings/mcp\"").Count);
            // No credential is created or shown here any more; the page only takes the code from the terminal.
            Assert.DoesNotContain("mcp-issue-form", page);
            Assert.DoesNotContain("Create MCP credential", page);
            Assert.Matches("<input[^>]*name=\"connect\"", page);
            Assert.DoesNotContain("Approve connection", page);
            Assert.Contains("30 idle minutes", page);
            Assert.Contains("no credential is shown or copied", page);
            Assert.Contains("Remote access is not supported", page);
            Assert.Contains("contain no credentials and grant no permissions", page);
            Assert.Contains("preserves unrelated client configuration", page);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, "<summary[^>]*>Setup and checksum</summary>").Count);
            Assert.DoesNotContain("<details open", page);
            foreach (var client in new[] { "pi", "claude" })
            {
                var bundle = TerminalPackages.Get(client)!;
                Assert.Contains(bundle.DownloadPath, page);
                Assert.Contains(bundle.Sha256, page);
            }
            if (!enabled && !unavailable) Assert.Contains("MCP is disabled for all users", page);
            if (unavailable) Assert.Contains("Terminal connections are unavailable", page);
        }
        Assert.Equal(0, settings.Saves);
    }

    [Fact]
    public async Task McpSettingsRequiresCompletedAdministratorAuthentication()
    {
        await using var host = new McpTestHost();
        using (var anonymous = await host.Browser.GetAsync("/settings/mcp"))
            Assert.Equal("/", anonymous.Headers.Location?.OriginalString);
        await host.Login();
        using (var restricted = await host.Browser.GetAsync("/settings/mcp"))
            Assert.Equal("/password-replacement", restricted.Headers.Location?.OriginalString);
        Assert.DoesNotContain("href=\"/settings/mcp\"", await host.Browser.GetStringAsync("/password-replacement"));
        await host.Replace();
        var page = await host.Browser.GetStringAsync("/settings/mcp?from=workflow");
        Assert.Contains("aria-label=\"MCP settings content\"", page);
        Assert.Matches("href=\"/settings/mcp\"[^>]*aria-current=\"page\"", page);
        Assert.Contains("href=\"/settings/ai-workflow\"", page);
        Assert.Contains("ai-workflow-settings-form", page);
        Assert.DoesNotContain("mcp-issue-form", page);
    }

    [Fact]
    public async Task DisableAndReenableNeverRestoreCredentialsAndUnavailableSettingsFailClosed()
    {
        var settings = new InMemoryAiWorkflowSettingsStore(true);
        var audit = new NeedAuditRecorder();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAiWorkflowSettingsStore>(settings);
            services.AddSingleton<IAuditSubmission>(audit);
            services.AddSingleton<IUserNeedStore>(new NeedStoreDouble());
            services.AddSingleton<IUserNeedChangeTransaction>(new NeedStoreDouble());
        });
        await host.Login(); await host.Replace();
        var oldToken = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(oldToken);
        using (var disabled = await ConfigureAi(host, settings, false)) Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        using (var issueDenied = await host.Issue()) Assert.Equal(HttpStatusCode.Forbidden, issueDenied.StatusCode);
        using (var denied = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        foreach (var tool in new[] { "create_draft_user_need", "edit_draft_user_need" })
        {
            using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret" } });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, e => { Assert.Equal(new AuditActor.Identified("admin"), e.Actor); Assert.Null(e.UserNeedChange); Assert.IsType<AuditTarget.Unknown>(e.Target); });
        using (var enabled = await ConfigureAi(host, settings, true)) Assert.Equal(HttpStatusCode.Redirect, enabled.StatusCode);
        using (var oldDenied = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, oldDenied.StatusCode);
        using var scope = host.Factory.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserNeedOperations>().EditDraft(principal, Guid.NewGuid(), 1, "ID", "secret", "secret")).IsFailure);
        await host.Authenticate();
        Assert.Equal(14, (await host.Rpc("tools/list")).GetProperty("tools").GetArrayLength());
        settings.Unavailable = true;
        using (var unavailable = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, unavailable.StatusCode);
        using (var unavailableIssue = await host.Issue()) Assert.Equal(HttpStatusCode.Forbidden, unavailableIssue.StatusCode);
        Assert.Contains("Settings unavailable", await host.Browser.GetStringAsync("/settings/ai-workflow"));
        using (var download = await host.Browser.GetAsync($"/settings/ai-workflow/packages/{TerminalPackages.Version}/pi"))
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task UnauthorizedSettingsAttemptsAreAuditedWithoutValuesAndSubmissionFailuresRemainDenied()
    {
        var settings = new InMemoryAiWorkflowSettingsStore();
        var audit = new SettingsAuditRecorder();
        var logger = new SettingsLogger();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAiWorkflowSettingsStore>(settings);
            services.AddSingleton<IAuditSubmission>(audit);
            services.AddSingleton<ILogger<AiWorkflowOperations>>(logger);
        });
        using (var anonymous = await host.Browser.PostAsync("/settings/ai-workflow/save", new StringContent("secret")))
            Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        await host.Login();
        using (var restricted = await host.Browser.PostAsync("/settings/ai-workflow/save", new StringContent("secret")))
            Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);
        await host.Replace();
        using (var created = await Create(host.Browser, "ordinary", "Ordinary", Retravion.Identity.Domain.AccountType.Ordinary))
        {
            using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var temporary = document.RootElement.GetProperty("temporaryPassword").GetString()!;
            using var user = Client(host.Factory);
            await Login(user, "ordinary", temporary); await Replace(user, temporary, Replacement);
            var page = await user.GetStringAsync("/settings/ai-workflow");
            Assert.Contains("Download Pi skills", page);
            Assert.DoesNotContain("id=\"ai-workflow-settings-form\"", page);
            Assert.DoesNotContain("href=\"/settings/mcp\"", page);
            Assert.DoesNotContain("href=\"/settings/mcp\"", await user.GetStringAsync("/home"));
            using var settingsPage = await user.GetAsync("/settings/mcp");
            Assert.Equal(HttpStatusCode.Forbidden, settingsPage.StatusCode);
            Assert.DoesNotContain("ai-workflow-settings-form", await settingsPage.Content.ReadAsStringAsync());
            using var denied = await user.PostAsync("/settings/ai-workflow/save", new StringContent("secret"));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await host.AuthenticateIfEnabledForTest(settings);
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(host.Mcp.DefaultRequestHeaders.Authorization!.Parameter);
        host.Factory.Services.GetRequiredService<Retravion.Web.Features.Identity.Logout.SessionTicketStore>().RevokeAccount("admin");
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<AiWorkflowOperations>();
        Assert.True((await operations.Save(principal, new(true, 999), true)).IsFailure);
        Assert.Equal(4, audit.Events.Count);
        Assert.All(audit.Events, e =>
        {
            Assert.Equal("ai-workflow.configure", e.Action); Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
            Assert.IsType<AuditTarget.Unknown>(e.Target); Assert.Null(e.AiWorkflowChange);
            Assert.True(e.OccurredAt > DateTimeOffset.UnixEpoch);
        });
        Assert.IsType<AuditActor.Unknown>(audit.Events[0].Actor);
        Assert.Equal(new AuditActor.Identified("ordinary"), audit.Events[2].Actor);
        foreach (var throwing in new[] { false, true })
        {
            audit.Fail = true; audit.Throw = throwing;
            Assert.True((await operations.Save(principal, new(false, 1), true)).IsFailure);
        }
        Assert.Equal(0, settings.Saves);
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, m => Assert.Equal("Unauthorized AI Workflow settings audit submission failed.", m));
    }

    [Fact]
    public async Task ShippedSkillDownloadsAreAuthenticatedDeterministicAndContainOnlyFixedCredentialFreeAssets()
    {
        await using var host = new McpTestHost();
        foreach (var client in new[] { "pi", "claude" })
        {
            var bundle = TerminalPackages.Get(client)!;
            Assert.Equal(bundle.Bytes, TerminalPackages.Get(client)!.Bytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bundle.Bytes)), bundle.Sha256);
            using (var anonymous = await host.Browser.GetAsync(bundle.DownloadPath)) Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        }
        await host.Login();
        using (var restricted = await host.Browser.GetAsync(TerminalPackages.Get("pi")!.DownloadPath))
            Assert.Equal("/password-replacement", restricted.Headers.Location?.OriginalString);
        await host.Replace();
        foreach (var client in new[] { "pi", "claude" })
        {
            var bundle = TerminalPackages.Get(client)!;
            using var response = await host.Browser.GetAsync(bundle.DownloadPath);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(bundle.Bytes, bytes);
            using var zip = new ZipArchive(new MemoryStream(bytes));
            Assert.Equal(new[] { "README.md", "bridge/server.mjs", "bridge/tools.json", "mcp_setup.ps1", "skills/retravion/SKILL.md" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
            string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
            var skill = Read("skills/retravion/SKILL.md");
            Assert.StartsWith("---\nname: retravion\ndescription:", skill.Replace("\r\n", "\n"));
            foreach (var tool in new[] { "list_products", "read_product", "list_user_needs", "read_user_need", "create_draft_user_need", "edit_draft_user_need" }) Assert.Contains(tool, skill);
            Assert.Contains("explicit human approval", skill);
            Assert.Contains("Never silently overwrite", skill);
            Assert.Contains("Do not automatically retry", skill);
            foreach (var tool in new[] { "rtv_connect", "rtv_complete_connection", "rtv_disconnect" }) { Assert.Contains(tool, skill); Assert.Contains(tool, Read("bridge/server.mjs")); }
            Assert.Contains("Ask the user whether they accept", skill);
            // Each download's setup defaults to its own client; nothing carries a copied credential,
            // changes the Windows certificate store or weakens TLS verification.
            var setup = Read("mcp_setup.ps1");
            Assert.Contains($"$Client = '{client}'", setup);
            Assert.DoesNotContain("%CLIENT_ID%", setup);
            Assert.DoesNotContain("Cert:", setup);
            var everything = string.Concat(zip.Entries.Select(e => Read(e.FullName)));
            Assert.DoesNotContain("RETRAVION_MCP_TOKEN", everything);
            Assert.DoesNotContain("NODE_EXTRA_CA_CERTS", everything);
            Assert.DoesNotContain("NODE_USE_SYSTEM_CA", everything);
            Assert.DoesNotContain("rejectUnauthorized", everything);
            var readme = Read("README.md");
            Assert.Contains(client == "pi" ? ".pi/skills/retravion" : ".claude/skills/retravion", readme);
            Assert.Contains(client == "pi" ? "/skill:retravion" : "/retravion", readme);
            Assert.Contains("Preserve unrelated server entries", readme);
            Assert.Contains("Remove only", readme);
            Assert.DoesNotContain("%CLIENT%", readme);
            Assert.Contains("mcp_setup.ps1", readme);
            Assert.DoesNotContain("InitialPass123!", everything);
        }
        using (var unknown = await host.Browser.GetAsync("/settings/ai-workflow/packages/0.0.0/pi")) Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var unknown = await host.Browser.GetAsync($"/settings/ai-workflow/packages/{TerminalPackages.Version}/other")) Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task CredentialIssuedAcrossDisableReenableRaceIsNeverAccepted()
    {
        var settings = new InMemoryAiWorkflowSettingsStore(true);
        var hook = new SettingsReadHook(settings);
        await using var host = new McpTestHost(services => services.AddSingleton<IAiWorkflowSettingsStore>(hook));
        await host.Login(); await host.Replace();
        using (await host.Start()) { }
        hook.AfterLoad = () => settings.Current = new(true, settings.Current.Revision + 2); // Disable, then re-enable after the issuance snapshot.
        using (var approved = await host.Decide("approve")) Assert.Equal("/settings/ai-workflow?connection=approved", approved.Headers.Location?.OriginalString);
        host.SetToken(host.DeviceCode);
        using (var denied = await host.Send("tools/list")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await host.Authenticate();
        Assert.Equal(14, (await host.Rpc("tools/list")).GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task GlobalSettingsGateAlsoProtectsMergedRequirementToolsAndTheirWriteBoundary()
    {
        var settings = new InMemoryAiWorkflowSettingsStore(true);
        var store = new RequirementStoreDouble();
        var audit = new RequirementAuditRecorder();
        var logger = new RequirementAuditLogger();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAiWorkflowSettingsStore>(settings);
            services.AddSingleton<IRequirementStore>(store);
            services.AddSingleton<IRequirementChangeTransaction>(store);
            services.AddSingleton<IRequirementLinkStore>(store);
            services.AddSingleton<IAuditSubmission>(audit);
            services.AddSingleton<ILogger<RequirementOperations>>(logger);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(host.Mcp.DefaultRequestHeaders.Authorization!.Parameter);
        using (var disabled = await ConfigureAi(host, settings, false)) Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        foreach (var tool in new[] { "create_draft_requirement", "edit_draft_requirement" })
        {
            using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret", rationale = "secret" } });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RequirementOperations>();
        Assert.True((await operations.Create(principal, Guid.NewGuid(), "secret", "secret", "secret", RequirementType.Software, "secret")).IsFailure);
        Assert.True((await operations.EditDraft(principal, Guid.NewGuid(), 1, "secret", "secret", "secret", RequirementType.Software, "secret")).IsFailure);
        using (var enabled = await ConfigureAi(host, settings, true)) Assert.Equal(HttpStatusCode.Redirect, enabled.StatusCode);
        Assert.True((await operations.EditDraft(principal, Guid.NewGuid(), 1, "secret", "secret", "secret", RequirementType.Software, "secret")).IsFailure);
        using (var denied = await host.Send("tools/call", new { name = "create_draft_requirement", arguments = new { title = "secret" } }))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(6, audit.Events.Count);
        Assert.All(audit.Events, e =>
        {
            Assert.Equal(new AuditActor.Identified("admin"), e.Actor);
            Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
            Assert.IsType<AuditTarget.Unknown>(e.Target);
            Assert.Null(e.RequirementChange); Assert.Null(e.AiWorkflowChange);
            Assert.Contains(e.Action, new[] { "requirement.create", "requirement.edit" });
        });
        foreach (var throwing in new[] { false, true })
        {
            audit.Fail = true; audit.Throw = throwing;
            Assert.True((await operations.Create(principal, Guid.NewGuid(), "secret", "secret", "secret", RequirementType.Software, "secret")).IsFailure);
        }
        Assert.Equal(0, store.Calls);
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal("Unauthorized Requirement change audit submission failed.", message));
    }

    private sealed class SettingsReadHook(IAiWorkflowSettingsStore store) : IAiWorkflowSettingsStore
    {
        internal Action? AfterLoad;
        public async Task<Result<AiWorkflowSettings>> Load(CancellationToken token = default)
        {
            var result = await store.Load(token);
            Interlocked.Exchange(ref AfterLoad, null)?.Invoke();
            return result;
        }
        public Task<Result<AiWorkflowSettings>> Save(AiWorkflowSettings expected, bool enabled, AuditActor actor, DateTimeOffset time, CancellationToken token = default) =>
            store.Save(expected, enabled, actor, time, token);
    }

    private static Task<HttpResponseMessage> ConfigureAi(McpTestHost host, InMemoryAiWorkflowSettingsStore settings, bool enabled) =>
        ConfigureAi(host, settings.Current, enabled);
    private static async Task<HttpResponseMessage> ConfigureAi(McpTestHost host, AiWorkflowSettings expected, bool enabled) =>
        await host.Browser.PostAsync("/settings/ai-workflow/save", await host.Form("/settings/mcp", new()
        { ["before"] = expected.Enabled.ToString(), ["revision"] = expected.Revision.ToString(), ["enabled"] = enabled.ToString(), ["confirm"] = "true" }));

    private sealed class SettingsAuditRecorder : IAuditSubmission
    {
        internal List<AuditInformation> Events = [];
        internal bool Fail, Throw;
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken token = default)
        {
            if (information.Action != "ai-workflow.configure") return Task.FromResult(AuditSubmissionResult.Success);
            if (Throw) throw new IOException("secret-provider-detail");
            Events.Add(information);
            return Task.FromResult(Fail ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
        }
    }
    private sealed class SettingsLogger : ILogger<AiWorkflowOperations>
    {
        internal List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
}

internal static class McpSettingsTestExtensions
{
    internal static async Task AuthenticateIfEnabledForTest(this McpTestHost host, InMemoryAiWorkflowSettingsStore settings)
    {
        settings.Current = new(true, settings.Current.Revision + 1);
        await host.Authenticate();
    }
}
