using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using MudBlazor.Extensions;
using Npgsql;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task NeedDerivationComponentCreatesEditableCopyAndShowsSnapshot()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var source = (await store.Create(Need.Create(from.InternalId, "SRC", "Source title", "Source statement").Value, VersionActor, VersionTime)).Value;
        await using var host = NeedMcpHost(products, store, store);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var navigation = new RequirementNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<UserNeedOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(UserNeedDerive), new() { [nameof(UserNeedDerive.Id)] = source.Need.InternalId, [nameof(UserNeedDerive.VersionId)] = source.Need.VersionId });
            Assert.Equal("Source title", renderer.Field("Title").GetState(x => x.Value));
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(to.InternalId);
            await renderer.Field("User Need ID").ValueChanged.InvokeAsync("COPY");
            await renderer.Field("Title").ValueChanged.InvokeAsync("Destination title");
            await renderer.Event("oninput", new ChangeEventArgs { Value = "Destination statement" });
            await renderer.Event("onsubmit", EventArgs.Empty);
            var copy = Assert.Single((await store.List(to.InternalId)).Value);
            Assert.Equal("Destination title", copy.Need.Title);
            Assert.Equal("Destination statement", copy.Need.Statement);
            await renderer.Mount(typeof(UserNeedDetails), new() { [nameof(UserNeedDetails.Id)] = copy.Need.InternalId });
            Assert.Contains("Derived from", renderer.Text());
            Assert.Contains("Source title", renderer.Text());
            Assert.Contains("Source statement", renderer.Text());
            Assert.Contains("Open source version", renderer.Text());
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                foreach (var table in new[] { "need_version", "need" })
                {
                    await using var delete = new NpgsqlCommand($"DELETE FROM retravion_need.{table} WHERE internal_id=$1", connection);
                    delete.Parameters.AddWithValue(source.Need.InternalId); await delete.ExecuteNonQueryAsync();
                }
            }
            await renderer.Mount(typeof(UserNeedDetails), new() { [nameof(UserNeedDetails.Id)] = copy.Need.InternalId });
            Assert.Contains("Source title", renderer.Text());
            Assert.Contains("Source statement", renderer.Text());
            Assert.DoesNotContain("Open source version", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
    }
}
#pragma warning restore BL0006
