using MudBlazor.Services;
using Retravion.Web.Components;

var builder = WebApplication.CreateBuilder(args);
// Also support source-tree `dotnet run` without a Development launch profile.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.Run();
