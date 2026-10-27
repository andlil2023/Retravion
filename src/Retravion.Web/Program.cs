using System.Data.Common;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Retravion.Web.Features.Identity.Logout;
using Microsoft.AspNetCore.Http.Connections;
using Retravion.Web.Features.Identity.Login;
using Retravion.Web.Features.Identity.PasswordReplacement;
using MudBlazor.Services;
using Retravion.Identity.Application;
using Retravion.Identity.Infrastructure;
using Retravion.Web.Components;

var builder = WebApplication.CreateBuilder(args);
// Also support source-tree `dotnet run` without a Development launch profile.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<SessionTicketStore>();
builder.Services.AddScoped<AuthenticationStateProvider, SessionAuthenticationStateProvider>();
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<SessionTicketStore>((options, store) => options.SessionStore = store);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CompletedPasswordReplacement", policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("Administrator")
        .RequireAssertion(context => !context.User.HasClaim(PasswordReplacementEndpoint.RestrictedClaim, "true")));
    options.AddPolicy("PasswordReplacementRequired", policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(PasswordReplacementEndpoint.RestrictedClaim, "true"));
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/";
        options.Cookie.Name = "Retravion.Identity";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = false;
    });
builder.Services.AddSingleton<PasswordSignIn>();
builder.Services.AddSingleton<Retravion.Identity.Application.PasswordReplacement>();

builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IAdministratorStore>(_ => new PostgresAdministratorStore(
    builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.")));
builder.Services.AddSingleton<AdministratorInitializer>();

var app = builder.Build();
try
{
    var initialization = await app.Services.GetRequiredService<AdministratorInitializer>().Initialize(
        app.Configuration["InitialAdmin:Username"], app.Configuration["InitialAdmin:Password"]);
    if (initialization.IsFailure)
    {
        throw new InvalidOperationException(initialization.Error);
    }
}
catch (Exception exception) when (exception is DbException or ArgumentException)
{
    // Provider/configuration exceptions may contain connection details. Fail closed without echoing them.
    throw new InvalidOperationException("Administrator initialization failed. Check database configuration and availability.");
}

app.UseAuthentication();
// Default-deny application requests. Only login, replacement, mapped public assets,
// and the Blazor transport (whose routed content is guarded separately) are exceptions.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var replacementPage = string.Equals(path.Value?.TrimEnd('/'), "/password-replacement", StringComparison.OrdinalIgnoreCase);
    var replacementOperation = string.Equals(path.Value?.TrimEnd('/'), "/identity/password-replacement", StringComparison.OrdinalIgnoreCase);
    var logout = string.Equals(path.Value?.TrimEnd('/'), "/identity/logout", StringComparison.OrdinalIgnoreCase);
    var login = path == "/" || string.Equals(path.Value?.TrimEnd('/'), "/identity/login", StringComparison.OrdinalIgnoreCase);
    var publicResource = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null
        || path.StartsWithSegments("/_blazor");
    var authenticated = context.User.Identity?.IsAuthenticated == true;
    var restricted = context.User.HasClaim(PasswordReplacementEndpoint.RestrictedClaim, "true");
    if (replacementPage && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect("/");
        return;
    }
    if (replacementPage && !restricted)
    {
        context.Response.Redirect("/home");
        return;
    }
    if (!login && !logout && !replacementPage && !replacementOperation && !publicResource)
    {
        if (!authenticated)
        {
            context.Response.Redirect("/");
            return;
        }
        if (restricted)
        {
            context.Response.Redirect("/password-replacement");
            return;
        }
        if (!context.User.IsInRole("Administrator"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next(context);
});
app.UseAuthorization();
app.UseAntiforgery();
app.MapLogin();
app.MapLogout();
app.MapPasswordReplacement();
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .Add(endpoint =>
    {
        // The negotiate metadata holds the same dispatcher options used by the hub.
        // Expire connected sessions as well as subsequent HTTP requests.
        foreach (var options in endpoint.Metadata.OfType<HttpConnectionDispatcherOptions>())
            options.CloseOnAuthenticationExpiration = true;
    });
app.Run();
