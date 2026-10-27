using System.Data.Common;
using Microsoft.AspNetCore.Authentication.Cookies;
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
// This increment guards the replacement flow and restricted-session access to home only.
// Comprehensive protected-entry-point enforcement belongs to its own slice.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var replacementPage = string.Equals(path.Value?.TrimEnd('/'), "/password-replacement", StringComparison.OrdinalIgnoreCase);
    var home = string.Equals(path.Value?.TrimEnd('/'), "/home", StringComparison.OrdinalIgnoreCase);
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
    if (home && restricted)
    {
        context.Response.Redirect("/password-replacement");
        return;
    }
    await next(context);
});
app.UseAntiforgery();
app.MapLogin();
app.MapPasswordReplacement();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Run();
