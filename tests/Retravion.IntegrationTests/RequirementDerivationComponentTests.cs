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
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task DerivationComponentCreatesEditableCopyAndShowsStoredProvenance()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("From", "From", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("To", "To", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var source = (await store.Create(RequirementState.Create(from.InternalId, "SRC", "Source title", "Source statement", RequirementType.Software, "Original rationale").Value, RequirementActor, RequirementTime)).Value;
        await using var host = RequirementMcpHost(products, store, store, database.ConnectionString);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RequirementOperations>();
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var beforeValidation = (await audit.FindAsync(new())).Count;
        Assert.True((await operations.Derive(principal, source, to.InternalId, "", "Title", "Statement", RequirementType.System, null)).IsFailure);
        Assert.Equal(beforeValidation, (await audit.FindAsync(new())).Count);
        var navigation = new RequirementNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RequirementOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductAreaOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RequirementDerive), new() { [nameof(RequirementDerive.Id)] = source.Requirement.InternalId, [nameof(RequirementDerive.VersionId)] = source.Requirement.VersionId });
            Assert.Equal("Source title", renderer.Field("Title").GetState(x => x.Value));
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(to.InternalId);
            await renderer.Field("Requirement ID").ValueChanged.InvokeAsync("COPY");
            await renderer.Field("Title").ValueChanged.InvokeAsync("Destination title");
            await renderer.Event("oninput", new ChangeEventArgs { Value = "Destination statement" });
            await renderer.Event("onsubmit", EventArgs.Empty);
            var copy = Assert.Single((await store.List(to.InternalId)).Value);
            Assert.Equal("Destination title", copy.Requirement.Title);
            Assert.Equal("Destination statement", copy.Requirement.Statement);
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = copy.Requirement.InternalId });
            Assert.Contains("Derived from", renderer.Text());
            Assert.Contains("Source title", renderer.Text());
            Assert.Contains("Source statement", renderer.Text());
            Assert.Contains("Open source version", renderer.Text());
            await using (var connection = new NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync();
                foreach (var table in new[] { "requirement_version", "requirement" })
                {
                    await using var delete = new NpgsqlCommand($"DELETE FROM retravion_requirement.{table} WHERE internal_id=$1", connection);
                    delete.Parameters.AddWithValue(source.Requirement.InternalId); await delete.ExecuteNonQueryAsync();
                }
            }
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = copy.Requirement.InternalId });
            Assert.Contains("Source title", renderer.Text());
            Assert.Contains("Source statement", renderer.Text());
            Assert.DoesNotContain("Open source version", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
    }
}
#pragma warning restore BL0006
