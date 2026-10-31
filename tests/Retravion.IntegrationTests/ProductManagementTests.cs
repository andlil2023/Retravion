using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Retravion.Application.Product;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Domain.Product;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Product;
using Xunit;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Theory]
    [InlineData(AccountType.Ordinary)]
    [InlineData(AccountType.Administrator)]
    public async Task BothAccountTypesCreateRetrieveAndChangeProductsUsingProtectedOperationsAndConventionalPages(AccountType type)
    {
        var products = new ProductStoreDouble();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IProductStore>(products)));
        using var admin = Client(factory);
        await Login(admin, "admin", Initial);
        await Replace(admin, Initial, Replacement);
        using var created = await Create(admin, "product-user", "Product user", type);
        var temporary = (await created.Content.ReadFromJsonAsync<CreatedAccount>())!.TemporaryPassword;
        using var user = Client(factory);
        await Login(user, "product-user", temporary);
        await Replace(user, temporary, Replacement);
        var actor = await ProductPrincipal(user, factory.Services, "product-user", Replacement, "/home");
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<ProductOperations>();
        var home = await user.GetStringAsync("/home");
        Assert.Contains("href=\"/products\"", home);
        Assert.Contains("aria-controls=\"product-navigation-list\"", home);
        Assert.True(home.IndexOf("aria-controls=\"product-navigation-list\"", StringComparison.Ordinal) <
                    home.IndexOf("href=\"/products\"", StringComparison.Ordinal));
        Assert.Contains("No products found.", home);
        var landing = await user.GetStringAsync("/products");
        Assert.Contains("Search Product ID or name", landing);
        Assert.Contains("Status", landing);
        Assert.Contains("Product ID", landing);
        Assert.Contains("href=\"/products/create\"", landing);
        Assert.Single(Regex.Matches(landing, "<h1\\b"));
        var form = await user.GetStringAsync("/products/create");
        Assert.Contains("Product ID", form);
        Assert.Contains("Kind", form);
        Assert.Contains("Description (optional)", form);
        Assert.Contains("Description preview", form);
        Assert.Contains("aria-label=\"Side by side\"", form);
        Assert.Contains("aria-label=\"Stacked\"", form);
        Assert.DoesNotContain("<select", form);
        Assert.DoesNotContain("max-width:40rem", form);
        Assert.Contains("Products</span>", form);
        Assert.Contains("aria-current=\"page\"", form);
        Assert.Contains("Maximum 100 Unicode characters.", form);
        var header = Regex.Match(form, "<header\\b[^>]*class=\"page-toolbar\"[^>]*>[\\s\\S]*?</header>");
        Assert.DoesNotContain("form=\"create-product-form\"", header.Value);
        var submit = Regex.Match(form, "<button(?=[^>]*form=\"create-product-form\")[^>]*>[\\s\\S]*?</button>");
        Assert.True(submit.Success);
        Assert.True(submit.Index > form.IndexOf("</form>", StringComparison.Ordinal));
        AssertFilledPrimaryButton(submit.Value);
        var id = "Custom / ID ? # & + 😀";
        var result = await operations.Create(actor, id, "<script>Product</script>", ProductKind.System, "<b>Plain description</b>");
        Assert.True(result.IsSuccess);
        Assert.Equal(id, result.Value.Id);
        Assert.True(result.Value.IsActive);
        Assert.True((await operations.Create(actor, id, "Duplicate", ProductKind.System, "")).IsFailure);
        Assert.True((await operations.Create(actor, "invalid", " \t", ProductKind.System, "")).IsFailure);
        var list = await operations.List(actor);
        Assert.True(list.IsSuccess);
        Assert.Contains(list.Value, p => p.Id == id);
        var callsBeforeOversizedId = products.Calls;
        var oversized = await operations.Create(actor, new string('a', 101), "Too long", ProductKind.System, "");
        Assert.True(oversized.IsFailure);
        Assert.Equal("A Product ID must contain at most 100 Unicode characters.", oversized.Error);
        Assert.Equal(callsBeforeOversizedId, products.Calls);
        var detailPath = "/products/details?id=" + Uri.EscapeDataString(id);
        var details = await user.GetStringAsync(detailPath);
        Assert.Contains("class=\"navigation-node product-navigation-item selected\"", details);
        Assert.Contains("href=\"/products/details?id=" + Uri.EscapeDataString(id) + "\"", details);
        Assert.Contains("&lt;script&gt;Product&lt;/script&gt;", details);
        Assert.Contains("&lt;b&gt;Plain description&lt;/b&gt;", details);
        Assert.DoesNotContain("<script>Product</script>", details);
        Assert.Contains("<dl class=\"product-kind\"><dt>Kind</dt><dd>System</dd></dl>", details);
        Assert.Contains("/products/edit?id=", details);
        Assert.DoesNotContain(result.Value.InternalId.ToString(), details);
        Assert.DoesNotContain(">Retire</button>", details);
        var editPage = await user.GetStringAsync("/products/edit?id=" + Uri.EscapeDataString(id));
        Assert.Contains("Description (Markdown)", editPage);
        Assert.Contains("Retire", editPage);
        AssertFilledPrimaryButton(Assert.Single(Regex.Matches(editPage, "<button\\b[^>]*>[\\s\\S]*?</button>").Cast<Match>(),
            match => Regex.Replace(match.Value, "<[^>]+>", "").Trim() == "Retire").Value);
        Assert.Contains("Save", editPage);
        Assert.True((await operations.ChangeStatus(actor, id, result.Value.InternalId, false)).IsSuccess);
        Assert.False((await operations.Retrieve(actor, id)).Value.IsActive);
        details = await user.GetStringAsync(detailPath);
        Assert.Contains("class=\"product-status\">Retired</span>", details);
        Assert.DoesNotContain("<dt>Name</dt>", details);
        Assert.DoesNotContain("max-width:40rem", details);
        Assert.Contains("/products/edit?id=", details);
        Assert.DoesNotContain(">Reactivate</button>", details);
        editPage = await user.GetStringAsync("/products/edit?id=" + Uri.EscapeDataString(id));
        Assert.Contains("Reactivate", editPage);
        AssertFilledPrimaryButton(Assert.Single(Regex.Matches(editPage, "<button\\b[^>]*>[\\s\\S]*?</button>").Cast<Match>(),
            match => Regex.Replace(match.Value, "<[^>]+>", "").Trim() == "Reactivate").Value);
        Assert.Matches("<textarea[^>]*disabled", editPage);
        var retired = (await operations.Retrieve(actor, id)).Value;
        Assert.True((await operations.Edit(actor, retired, id, "Changed", ProductKind.Firmware, "new", false)).IsFailure);
        var reactivated = await operations.Edit(actor, retired, id, "Changed", ProductKind.Firmware, "new", true);
        Assert.True(reactivated.IsSuccess);
        Assert.True(reactivated.Value.IsActive);
        Assert.Equal("Changed", reactivated.Value.Name);
        Assert.Equal("new", reactivated.Value.Description);
        Assert.True((await operations.Retire(actor, retired)).IsFailure);
        Assert.True((await operations.Edit(actor, reactivated.Value, id, "<script>Product</script>", ProductKind.System, "<b>Plain description</b>", false)).IsSuccess);
        Assert.True((await operations.Retrieve(actor, id)).Value.IsActive);
        var editor = await user.GetStringAsync("/products/description?id=" + Uri.EscapeDataString(id));
        Assert.Contains("Description (Markdown)", editor);
        Assert.Contains("Side by side", editor);
        Assert.Contains("Stacked", editor);
        Assert.Contains("Save description", editor);
        Assert.Contains("&lt;b&gt;Plain description&lt;/b&gt;", editor);
        Assert.DoesNotContain("_product.Description", editor);
        Assert.DoesNotContain("<select", editor);
        Assert.True((await operations.UpdateDescription(actor, id, result.Value.InternalId, "<b>Plain description</b>", "# Heading\n\n**Formatted**")).IsSuccess);
        details = await user.GetStringAsync(detailPath);
        Assert.Contains("<h1>Heading</h1>", details);
        Assert.Contains("<strong>Formatted</strong>", details);
        editor = await user.GetStringAsync("/products/description?id=" + Uri.EscapeDataString(id));
        Assert.Contains("<h1>Heading</h1>", editor);
        Assert.Contains("<strong>Formatted</strong>", editor);
        Assert.True((await operations.UpdateDescription(actor, id, result.Value.InternalId, "# Heading\n\n**Formatted**", "## Updated\n\n*Again*\n\n- One\n- Two\n\n1. First\n2. Second")).IsSuccess);
        details = await user.GetStringAsync(detailPath);
        Assert.Contains("<h2>Updated</h2>", details);
        Assert.Contains("<em>Again</em>", details);
        Assert.Contains("<ul>", details);
        Assert.Contains("<li>One</li>", details);
        Assert.Contains("<ol>", details);
        Assert.Contains("<li>First</li>", details);
        Assert.DoesNotContain("<h1>Heading</h1>", details);
        Assert.Contains("Product not found.", await user.GetStringAsync("/products/details?id=missing"));
        Assert.Contains("A Product ID is required.", await user.GetStringAsync("/products/details"));
        // A retained circuit principal must not bypass revocation.
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(actor.FindFirstValue(SessionTicketStore.SessionClaim)!);
        var calls = products.Calls;
        Assert.True((await operations.Create(actor, "revoked", "No", ProductKind.System, "")).IsFailure);
        Assert.True((await operations.Retrieve(actor, id)).IsFailure);
        Assert.True((await operations.List(actor)).IsFailure);
        Assert.True((await operations.Edit(actor, result.Value, id, "Denied", ProductKind.System, "", false)).IsFailure);
        Assert.True((await operations.Retire(actor, result.Value)).IsFailure);
        Assert.True((await operations.ChangeStatus(actor, id, result.Value.InternalId, false)).IsFailure);
        Assert.True((await operations.UpdateDescription(actor, id, result.Value.InternalId, "", "denied")).IsFailure);
        Assert.Equal(calls, products.Calls);
    }

    [Fact]
    public async Task AnonymousAndPasswordRestrictedProductPagesAndCallbacksHaveNoEffects()
    {
        var products = new ProductStoreDouble();
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IProductStore>(products)));
        using var user = Client(factory);
        foreach (var path in new[] { "/products", "/products/create", "/products/details?id=anything", "/products/description?id=anything", "/products/edit?id=anything" })
        {
            using var denied = await user.GetAsync(path);
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
        }
        var restricted = await ProductPrincipal(user, factory.Services, "admin", Initial, "/password-replacement");
        foreach (var path in new[] { "/products", "/products/create", "/products/details?id=anything", "/products/description?id=anything", "/products/edit?id=anything" })
        {
            using var denied = await user.GetAsync(path);
            Assert.Equal("/password-replacement", denied.Headers.Location?.OriginalString);
        }
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<ProductOperations>();
        foreach (var actor in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted })
        {
            Assert.True((await operations.Create(actor, "denied", "Denied", ProductKind.System, "")).IsFailure);
            Assert.True((await operations.Retrieve(actor, "anything")).IsFailure);
            Assert.True((await operations.List(actor)).IsFailure);
            var expected = ProductState.Create("anything", "Name", ProductKind.System, "").Value;
            Assert.True((await operations.Edit(actor, expected, "anything", "Denied", ProductKind.System, "", false)).IsFailure);
            Assert.True((await operations.Retire(actor, expected)).IsFailure);
            Assert.True((await operations.ChangeStatus(actor, "anything", Guid.NewGuid(), false)).IsFailure);
            Assert.True((await operations.UpdateDescription(actor, "anything", Guid.NewGuid(), "", "denied")).IsFailure);
        }
        Assert.Equal(0, products.Calls);
    }

    private static void AssertProductAction(string html, string action)
    {
        var header = Regex.Match(html, "<header\\b[^>]*class=\"page-toolbar\"[^>]*>[\\s\\S]*?</header>");
        Assert.DoesNotContain(action, header.Value);
        var button = Assert.Single(Regex.Matches(html, "<button\\b[^>]*>[\\s\\S]*?</button>").Cast<Match>(),
            match => Regex.Replace(match.Value, "<[^>]+>", "").Trim() == action);
        AssertFilledPrimaryButton(button.Value);
        var article = Regex.Match(html, "<article\\b[^>]*aria-label=\"Product description\"[^>]*>[\\s\\S]*?</article>");
        Assert.True(article.Success);
        var cardEnd = html.IndexOf("</div>", article.Index + article.Length, StringComparison.Ordinal);
        Assert.True(cardEnd >= 0);
        Assert.True(button.Index > cardEnd);
        Assert.DoesNotContain("<input", article.Value);
    }

    private static async Task<ClaimsPrincipal> ProductPrincipal(HttpClient client, IServiceProvider services,
        string username, string password, string destination)
    {
        using var response = await client.PostAsync("/identity/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password,
            ["__RequestVerificationToken"] = Token(await client.GetStringAsync("/"))
        }));
        Assert.Equal(destination, response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Retravion.Identity=")).Split(';')[0];
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        var reference = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!;
        var key = reference.Principal.FindFirstValue("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!;
        return (await options.SessionStore!.RetrieveAsync(key))!.Principal;
    }

    [Fact]
    public async Task ProductNavigationRefreshesAfterProductCreationAndRenameInTheSameCircuit()
    {
        var products = new ProductStoreDouble();
        var useCircuitNavigation = false;
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IProductStore>(products);
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(NavigationManager));
            services.AddScoped<NavigationManager>(provider => useCircuitNavigation
                ? new ProductCircuitNavigation()
                : (NavigationManager)(original.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)));
        }));
        using var user = Client(factory);
        await Login(user, "admin", Initial);
        await Replace(user, Initial, Replacement);
        var actor = await ProductPrincipal(user, factory.Services, "admin", Replacement, "/home");
        useCircuitNavigation = true;
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        ((IHostEnvironmentAuthenticationStateProvider)services.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(actor)));
        var operations = services.GetRequiredService<ProductOperations>();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ProductNavigation>();
            Assert.Contains("No products found.", component.ToHtmlString());
            var created = await operations.Create(actor, "nav-id", "First name", ProductKind.System, "");
            Assert.True(created.IsSuccess);
            services.GetRequiredService<NavigationManager>().NavigateTo("/products/details?id=nav-id");
            Assert.Contains("First name", component.ToHtmlString());
            var renamed = await operations.Edit(actor, created.Value, "nav-id", "Renamed", ProductKind.System, "", false);
            Assert.True(renamed.IsSuccess);
            services.GetRequiredService<NavigationManager>().NavigateTo("/products/edit?id=nav-id");
            services.GetRequiredService<NavigationManager>().NavigateTo("/products/details?id=nav-id");
            var html = component.ToHtmlString();
            Assert.Contains("Renamed", html);
            Assert.DoesNotContain("First name", html);
            products.FailList = true;
            services.GetRequiredService<NavigationManager>().NavigateTo("/home");
            Assert.Contains("Could not load products.", component.ToHtmlString());
        });
    }

    private sealed class ProductCircuitNavigation : NavigationManager
    {
        public ProductCircuitNavigation() => Initialize("https://localhost/", "https://localhost/home");
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(false);
        }
    }

    [Fact]
    public async Task ProductNavigationShowsFailureWithoutDisclosingStoreErrorAndRecoversOnNextRequest()
    {
        var products = new ProductStoreDouble { ThrowList = true };
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IProductStore>(products)));
        using var user = Client(factory);
        await Login(user, "admin", Initial);
        await Replace(user, Initial, Replacement);

        var failed = await user.GetStringAsync("/home");
        Assert.Contains("Could not load products.", failed);
        Assert.Contains(">Retry</button>", failed);
        Assert.DoesNotContain("sensitive provider details", failed);
        products.ThrowList = false;
        products.FailList = true;
        var expectedFailure = await user.GetStringAsync("/home");
        Assert.Contains("Could not load products.", expectedFailure);
        Assert.DoesNotContain("Product list unavailable.", expectedFailure);
        products.FailList = false;
        var recovered = await user.GetStringAsync("/home");
        Assert.Contains("No products found.", recovered);
        Assert.DoesNotContain("Could not load products.", recovered);
    }

    private sealed class ProductStoreDouble : IProductStore
    {
        private readonly Dictionary<string, ProductState> products = new(StringComparer.OrdinalIgnoreCase);
        public bool FailList { get; set; }
        public bool ThrowList { get; set; }
        public int Calls;
        public Task<Result> Create(ProductState product, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(products.TryAdd(product.Id, product) ? Result.Success() : Result.Failure("Duplicate Product ID."));
        }
        public Task<Result<IReadOnlyList<ProductState>>> List(CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ThrowList) throw new InvalidOperationException("sensitive provider details");
            return Task.FromResult(FailList
                ? Result.Failure<IReadOnlyList<ProductState>>("Product list unavailable.")
                : Result.Success<IReadOnlyList<ProductState>>(products.Values.ToArray()));
        }
        public Task<Result<Maybe<ProductState>>> Find(string id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result.Success(Maybe<ProductState>.From(products.GetValueOrDefault(id))));
        }
        public Task<Result> Update(ProductState expected, ProductState replacement, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (products.GetValueOrDefault(expected.Id) != expected)
                return Task.FromResult(Result.Failure("Stale Product."));
            products.Remove(expected.Id);
            products[replacement.Id] = replacement;
            return Task.FromResult(Result.Success());
        }
    }
}
