using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        Assert.Contains("href=\"/products\"", await user.GetStringAsync("/home"));
        var landing = await user.GetStringAsync("/products");
        Assert.Contains("Retrieve Product", landing);
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
        var callsBeforeOversizedId = products.Calls;
        var oversized = await operations.Create(actor, new string('a', 101), "Too long", ProductKind.System, "");
        Assert.True(oversized.IsFailure);
        Assert.Equal("A Product ID must contain at most 100 Unicode characters.", oversized.Error);
        Assert.Equal(callsBeforeOversizedId, products.Calls);
        var detailPath = "/products/details?id=" + Uri.EscapeDataString(id);
        var details = await user.GetStringAsync(detailPath);
        Assert.Contains("&lt;script&gt;Product&lt;/script&gt;", details);
        Assert.Contains("&lt;b&gt;Plain description&lt;/b&gt;", details);
        Assert.DoesNotContain("<script>Product</script>", details);
        AssertProductAction(details, "Retire");
        Assert.True((await operations.ChangeStatus(actor, id, result.Value.InternalId, false)).IsSuccess);
        Assert.False((await operations.Retrieve(actor, id)).Value.IsActive);
        details = await user.GetStringAsync(detailPath);
        Assert.Contains("class=\"product-status\">Retired</span>", details);
        Assert.DoesNotContain("<dt>Name</dt>", details);
        Assert.DoesNotContain("max-width:40rem", details);
        AssertProductAction(details, "Reactivate");
        Assert.True((await operations.ChangeStatus(actor, id, result.Value.InternalId, true)).IsSuccess);
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
        Assert.True((await operations.UpdateDescription(actor, id, result.Value.InternalId, "# Heading\n\n**Formatted**", "## Updated\n\n*Again*")).IsSuccess);
        details = await user.GetStringAsync(detailPath);
        Assert.Contains("<h2>Updated</h2>", details);
        Assert.Contains("<em>Again</em>", details);
        Assert.DoesNotContain("<h1>Heading</h1>", details);
        Assert.Contains("Product not found.", await user.GetStringAsync("/products/details?id=missing"));
        Assert.Contains("A Product ID is required.", await user.GetStringAsync("/products/details"));
        // A retained circuit principal must not bypass revocation.
        await factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(actor.FindFirstValue(SessionTicketStore.SessionClaim)!);
        var calls = products.Calls;
        Assert.True((await operations.Create(actor, "revoked", "No", ProductKind.System, "")).IsFailure);
        Assert.True((await operations.Retrieve(actor, id)).IsFailure);
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
        foreach (var path in new[] { "/products", "/products/create", "/products/details?id=anything", "/products/description?id=anything" })
        {
            using var denied = await user.GetAsync(path);
            Assert.Equal("/", denied.Headers.Location?.OriginalString);
        }
        var restricted = await ProductPrincipal(user, factory.Services, "admin", Initial, "/password-replacement");
        foreach (var path in new[] { "/products", "/products/create", "/products/details?id=anything", "/products/description?id=anything" })
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

    private sealed class ProductStoreDouble : IProductStore
    {
        private readonly Dictionary<string, ProductState> products = new(StringComparer.OrdinalIgnoreCase);
        public int Calls;
        public Task<Result> Create(ProductState product, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(products.TryAdd(product.Id, product) ? Result.Success() : Result.Failure("Duplicate Product ID."));
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
