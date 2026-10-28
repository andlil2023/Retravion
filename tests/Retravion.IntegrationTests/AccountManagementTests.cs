using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class AccountManagementTests
{
    private const string Initial = "InitialPass123!";
    private const string Replacement = "Replacement123!";

    [Theory]
    [InlineData(AccountType.Ordinary)]
    [InlineData(AccountType.Administrator)]
    public async Task AdministratorCreatesListsAndInspectsAnAccountWhichReplacesItsPasswordWithAssignedAccess(AccountType type)
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        var usersPage = await admin.GetStringAsync("/administration/users");
        Assert.Contains("Create user", usersPage);
        Assert.Contains("mud-table-dense", usersPage);
        Assert.Contains("Administration navigation", usersPage);
        Assert.Matches("class=\"sidebar-bottom\"[^>]*>[\\s\\S]*?aria-label=\"Administration\"[\\s\\S]*?class=\"sidebar-footer\"", usersPage);
        Assert.DoesNotContain("margin-top:5rem", usersPage);
        await AssertPasswordChangeStatus(admin, "admin", required: false);
        var formPage = await admin.GetStringAsync("/administration/users/create");
        Assert.Contains("Display name", formPage);
        Assert.Contains("Account type", formPage);
        Assert.Single(Regex.Matches(formPage, "class=\"mud-popover-provider\""));
        using var created = await Create(admin, "New.User", "  李  😀  ", type);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var password = document.RootElement.GetProperty("temporaryPassword").GetString()!;
        Assert.True(PasswordPolicy.Validate(password).IsSuccess);
        var persisted = (await store.LoadForSignIn("new.user")).Value.Value;
        Assert.Equal("New.User", persisted.Username);
        Assert.Equal("李  😀", persisted.DisplayName);
        Assert.Equal(type, persisted.AccountType);
        Assert.False(persisted.IsInitialAdministrator);
        Assert.True(persisted.RequiresPasswordReplacement);
        await AssertPasswordChangeStatus(admin, "New.User", required: true);
        Assert.True(new Pbkdf2PasswordHasher().Verify(password, persisted.PasswordHash));
        foreach (var path in new[] { "/administration/accounts", "/administration/users", "/administration/users/details/New.User" })
        {
            var body = await admin.GetStringAsync(path);
            Assert.DoesNotContain(password, body);
            Assert.DoesNotContain(persisted.PasswordHash, body);
            Assert.DoesNotContain("temporaryPassword", body);
        }
        using var duplicate = await Create(admin, "NEW.USER", "Another", type);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using var exactDuplicate = await Create(admin, "New.User", "Another", type);
        Assert.Equal(HttpStatusCode.BadRequest, exactDuplicate.StatusCode);
        using var sharedName = await Create(admin, "different", "李  😀", type);
        Assert.Equal(HttpStatusCode.OK, sharedName.StatusCode);
        Assert.Equal(3, (await store.List()).Value.Count);

        using var user = Client(factory);
        await Login(user, "nEW.uSER", password);
        using (var restricted = await user.GetAsync("/home")) Assert.Equal("/password-replacement", restricted.Headers.Location?.OriginalString);
        using (var denied = await Create(user, "denied", "Denied", type)) Assert.Equal("/password-replacement", denied.Headers.Location?.OriginalString);
        // Direct submissions cannot bypass confirmation, policy or unchanged-password checks.
        foreach (var (newPassword, confirmation) in new[] { (password, password), ("short", "short"), (Replacement, "replacement123!") })
        {
            using var invalid = await ReplaceRequest(user, password, newPassword, confirmation);
            Assert.StartsWith("/password-replacement?", invalid.Headers.Location?.OriginalString);
            Assert.DoesNotContain(password, await invalid.Content.ReadAsStringAsync());
            Assert.Equal(persisted.PasswordHash, (await store.LoadForSignIn("NEW.USER")).Value.Value.PasswordHash);
        }
        await Replace(user, password, Replacement);
        await AssertPasswordChangeStatus(admin, "New.User", required: false);
        var home = await user.GetStringAsync("/home");
        Assert.Contains("Overview", home);
        Assert.Equal(type == AccountType.Administrator, home.Contains("Administration navigation"));
        using var users = await user.GetAsync("/administration/users");
        Assert.Equal(type == AccountType.Administrator ? HttpStatusCode.OK : HttpStatusCode.Forbidden, users.StatusCode);
        using var details = await user.GetAsync("/administration/users/details/admin");
        Assert.Equal(type == AccountType.Administrator ? HttpStatusCode.OK : HttpStatusCode.Forbidden, details.StatusCode);
        using var direct = await Create(user, "second-user", "Second", AccountType.Ordinary);
        Assert.Equal(type == AccountType.Administrator ? HttpStatusCode.OK : HttpStatusCode.Forbidden, direct.StatusCode);
        using var again = Client(factory);
        await Login(again, "NEW.USER", Replacement, "/home");
        using var old = Client(factory);
        await Login(old, "New.User", password, "/?failed=true");
    }

    [Fact]
    public async Task AnonymousAndOrdinaryDirectAdministrationRequestsHaveNoEffectsAndAntiforgeryIsRequired()
    {
        var store = new StartupAdministratorStore();
        await using var factory = new AdministratorWebFactory(store, "admin", Initial);
        using var client = Client(factory);
        foreach (var path in new[] { "/administration/accounts", "/administration/users", "/administration/users/create", "/administration/users/details/admin" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
        }
        using (var denied = await client.PostAsync("/administration/accounts", new FormUrlEncodedContent(new Dictionary<string, string>())))
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
        Assert.Single((await store.List()).Value);
        await Login(client, "admin", Initial);
        await Replace(client, Initial, Replacement);
        using var noToken = await client.PostAsync("/administration/accounts", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "new-user", ["displayName"] = "Name", ["accountType"] = "Ordinary"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        foreach (var (username, name, type) in new[] { ("ab", "Name", "Ordinary"), ("valid", " ", "Ordinary"), ("valid", "Name", "9"), ("valid", "Name", "unknown") })
        {
            using var invalid = await client.PostAsync("/administration/accounts", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = username, ["displayName"] = name, ["accountType"] = type,
                ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/home"))
            }));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.DoesNotContain("temporaryPassword", await invalid.Content.ReadAsStringAsync());
        }
        Assert.Single((await store.List()).Value);
    }

    [Fact]
    public async Task UsernameCreateHasUnambiguousDetailsAndDoesNotCollideWithTheCreationPage()
    {
        await using var factory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "create", "Create route regression", AccountType.Ordinary);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var list = await admin.GetStringAsync("/administration/users");
        Assert.Contains("/administration/users/details/create", list);
        var details = await admin.GetStringAsync("/administration/users/details/create");
        Assert.Matches("<h1[^>]*>User details</h1>", details);
        Assert.Contains("aria-labelledby=\"account-heading\"", details);
        Assert.Matches("<h2[^>]*id=\"account-heading\"[^>]*>Create route regression</h2>", details);
        Assert.Matches("<dt[^>]*>Username</dt>\\s*<dd[^>]*>create</dd>", details);
        Assert.Matches("<dt[^>]*>Display name</dt>\\s*<dd[^>]*>Create route regression</dd>", details);
        Assert.Matches("<dt[^>]*>Account type</dt>\\s*<dd[^>]*>[\\s\\S]*?Ordinary[\\s\\S]*?</dd>", details);
        Assert.Contains("mud-chip-outlined", details);
        var stylesheet = Regex.Match(details, "href=\"([^\"]*Retravion\\.Web(?:\\.[^\"/]+)?\\.styles\\.css)\"");
        Assert.True(stylesheet.Success, "The page must load its scoped styles, including fingerprinted asset names.");
        var css = await admin.GetStringAsync(WebUtility.HtmlDecode(stylesheet.Groups[1].Value));
        Assert.Contains("max-width: 40rem", css);
        Assert.Contains("grid-template-columns: minmax(0, 9rem) minmax(0, 1fr)", css);
        Assert.Contains(".sidebar-bottom", css);
        Assert.Contains("margin-top: auto", css);
        Assert.Contains("min-height: 56px", css);
        var form = await admin.GetStringAsync("/administration/users/create");
        Assert.Matches("<h1[^>]*>Create user</h1>", form);
        Assert.DoesNotMatch("<dd[^>]*>Create route regression</dd>", form);
    }

    [Theory]
    [InlineData("/administration/users", "Users", false)]
    [InlineData("/administration/users/create", "Create user", true)]
    [InlineData("/administration/users/details/admin", "User details", true)]
    public async Task AccountPagesUseOneToolbarHeadingAndOnlyMeaningfulBackNavigation(string path, string title, bool hasBackLink)
    {
        await using var factory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        var html = await admin.GetStringAsync(path);
        var header = Regex.Match(html, "<header\\b[^>]*class=\"page-toolbar\"[^>]*>([\\s\\S]*?)</header>");
        Assert.True(header.Success);
        Assert.Matches($"<h1[^>]*>{Regex.Escape(title)}</h1>", header.Value);
        Assert.Single(Regex.Matches(html, "<h1\\b"));
        Assert.DoesNotContain("ADMINISTRATION", header.Value);
        Assert.DoesNotContain("Back to overview", html);
        var backLinks = Regex.Matches(header.Value, "href=\"/administration/users\"");
        if (hasBackLink) Assert.Single(backLinks);
        else Assert.Empty(backLinks);
        if (path.EndsWith("/create", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("form=\"create-user-form\"", header.Value);
            Assert.DoesNotMatch("<button[^>]*type=\"submit\"", header.Value);
            var form = Regex.Match(html, "<form(?=[^>]*id=\"create-user-form\")[^>]*>[\\s\\S]*?</form>");
            Assert.True(form.Success);
            var submit = Assert.Single(Regex.Matches(html, "<button(?=[^>]*type=\"submit\")(?=[^>]*form=\"create-user-form\")[^>]*>[\\s\\S]*?</button>").Cast<Match>());
            Assert.True(submit.Index > form.Index + form.Length, "The creation action must follow the form card, not appear in the toolbar.");
            Assert.Contains("Create user", submit.Value);
        }
        else if (!hasBackLink)
        {
            Assert.Contains("href=\"/administration/users/create\"", header.Value);
            Assert.Contains("mud-table-dense", html);
        }
    }

    [Fact]
    public async Task FailureTrackingIsIsolatedAndCapitalizationVariantsSharePersistedAccountRestrictions()
    {
        var store = new StartupAdministratorStore();
        var hasher = new Pbkdf2PasswordHasher();
        await new AdministratorInitializer(store, hasher).Initialize("admin", Initial);
        var creation = new AccountCreation(store, hasher);
        var first = (await creation.Create("first", "Same", AccountType.Ordinary)).Value;
        var second = (await creation.Create("second", "Same", AccountType.Administrator)).Value;
        var clock = new Clock();
        var signIn = new PasswordSignIn(store, hasher, clock);
        foreach (var username in new[] { "First", "FIRST", "first" })
            Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(3, (await store.LoadForSignIn("FIRST")).Value.Value.Failures.Count);
        Assert.True((await new PasswordSignIn(store, hasher, clock).Authenticate("fIrSt", first.TemporaryPassword)).IsFailure);
        Assert.True((await signIn.Authenticate("SECOND", second.TemporaryPassword)).IsSuccess);
        Assert.True((await signIn.Authenticate("ADMIN", Initial)).IsSuccess);
        Assert.Equal(SignInFailures.None, (await store.LoadForSignIn("second")).Value.Value.Failures);
        clock.Now = clock.Now.AddSeconds(5);
        Assert.True((await signIn.Authenticate("FIRST", first.TemporaryPassword)).IsSuccess);
        Assert.Equal(SignInFailures.None, (await store.LoadForSignIn("first")).Value.Value.Failures);
    }

    private static async Task AssertPasswordChangeStatus(HttpClient admin, string username, bool required)
    {
        var html = await admin.GetStringAsync("/administration/users");
        Assert.Matches("<th[^>]*>Password change</th>", html);
        var row = Assert.Single(Regex.Matches(html, "<tr\\b[^>]*>[\\s\\S]*?</tr>").Cast<Match>(),
            match => match.Value.Contains($"href=\"/administration/users/details/{Uri.EscapeDataString(username)}\"", StringComparison.Ordinal));
        var cell = Regex.Match(row.Value, "<td\\b[^>]*data-label=\"Password change\"[^>]*>([\\s\\S]*?)</td>");
        Assert.True(cell.Success, "Each account row must label its password-change status.");
        if (required)
        {
            Assert.Contains(">Required<", cell.Value);
            Assert.Contains("mud-chip-outlined", cell.Value);
            Assert.DoesNotContain("Not required", cell.Value);
        }
        else
        {
            Assert.Contains(">Not required<", cell.Value);
            Assert.DoesNotContain("mud-chip", cell.Value);
        }
        var summaries = await admin.GetFromJsonAsync<AccountSummary[]>("/administration/accounts");
        var summary = Assert.Single(summaries!, account => account.Username == username);
        Assert.Equal(required, summary.RequiresPasswordReplacement);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task Login(HttpClient client, string username, string password, string destination = "/password-replacement")
    {
        using var response = await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password, ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
        Assert.Equal(destination, response.Headers.Location?.OriginalString);
    }
    private static async Task<HttpResponseMessage> Create(HttpClient client, string username, string displayName, AccountType type) =>
        await client.PostAsync("/administration/accounts", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["displayName"] = displayName, ["accountType"] = type.ToString(),
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
    private static async Task<HttpResponseMessage> ReplaceRequest(HttpClient client, string current, string replacement, string confirmation) =>
        await client.PostAsync("/identity/password-replacement", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["currentPassword"] = current, ["newPassword"] = replacement, ["confirmNewPassword"] = confirmation,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/password-replacement"))
        }));
    private static async Task Replace(HttpClient client, string current, string replacement)
    {
        using var response = await ReplaceRequest(client, current, replacement, replacement);
        Assert.Equal("/home", response.Headers.Location?.OriginalString);
    }
}
