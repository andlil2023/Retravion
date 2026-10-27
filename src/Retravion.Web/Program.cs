using System.Data.Common;
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

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Run();
