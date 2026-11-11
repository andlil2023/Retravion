using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Npgsql;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Retravion.Application.Feature;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Domain.Feature;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.Product;
using Retravion.Web.Features.Feature;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Identity.Logout;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task FeatureReviewAndReopenDenySessionsExpiringWhileWaitingForDatabase()
    {
        foreach (var reopening in new[] { false, true })
        {
            await using var database = await Database.Create(); var clock = new FeatureClock();
            var products = new PostgresProductStore(database.ConnectionString);
            var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
            var store = new PostgresFeatureStore(database.ConnectionString);
            var draft = (await store.Create(product.InternalId, FeatureContent.Create("Title", ""), FeatureActor, FeatureTime)).Value;
            var expected = draft;
            if (reopening) { var review = (await store.Review(draft, "InReview", null, FeatureActor, FeatureTime)).Value; expected = (await store.Review(review, "Approved", null, FeatureActor, FeatureTime)).Value; }
            await using var host = new McpTestHost(services => { services.AddSingleton<TimeProvider>(clock); services.AddSingleton<IFeatureStore>(store); services.AddSingleton<IFeatureChangeTransaction>(store); });
            await host.Login(); await host.Replace(); var token = await host.Authenticate();
            var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
            var sessions = host.Factory.Services.GetRequiredService<SessionTicketStore>();
            var session = principal.FindFirstValue(SessionTicketStore.SessionClaim)!;
            using var scope = host.Factory.Services.CreateScope(); var operations = scope.ServiceProvider.GetRequiredService<FeatureOperations>();
            await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
            await using (var locked = new NpgsqlCommand("SELECT pg_advisory_xact_lock(821749620118)", connection, tx)) await locked.ExecuteNonQueryAsync();
            var pending = reopening ? operations.Reopen(principal, expected, "reason") : operations.Review(principal, expected, "InReview", null);
            await using var observer = new NpgsqlConnection(database.ConnectionString); await observer.OpenAsync();
            var waiting = false;
            for (var i = 0; i < 100 && !waiting; i++)
            {
                await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%821749620118%')", observer);
                waiting = (bool)(await check.ExecuteScalarAsync())!;
                if (!waiting) await Task.Delay(20);
            }
            Assert.True(waiting, "Feature transaction did not reach the database lock.");
            clock.Now += TimeSpan.FromMinutes(31); Assert.False(sessions.IsActive(session));
            await tx.CommitAsync(); var result = await pending;
            Assert.True(result.IsFailure); Assert.Equal("Access denied.", result.Error);
            Assert.Equal(expected.Feature.Status, (await store.Find(expected.Feature.InternalId)).Value.Value.Feature.Status);
            Assert.Equal(reopening ? 3 : 1, (await store.History(expected.Feature.InternalId)).Value.Count);
        }
    }
    private sealed class FeatureClock : TimeProvider { internal DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }

    [PostgresFact]
    public async Task FeaturePagesCreateEditFindAndRenderMarkdownSafely()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IFeatureStore>(store); services.AddSingleton<IFeatureChangeTransaction>(store);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope(); var navigation = new CaseNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices().AddSingleton<IJSRuntime, CaseJS>()
            .AddSingleton<NavigationManager>(navigation).AddSingleton<AuthenticationStateProvider>(new CaseAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<FeatureOperations>()).AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(FeatureEditor), new() { [nameof(FeatureEditor.ProductId)] = "P" });
            Assert.Empty(renderer.Errors); await renderer.Event("onsubmit", EventArgs.Empty);
            var created = Assert.Single((await store.List(product.InternalId)).Value);
            Assert.Equal("", created.Feature.Content.Title);
            navigation.Go($"features/edit?id={created.Feature.InternalId}&version={created.Feature.VersionId}");
            await renderer.Mount(typeof(FeatureEditor), new() { [nameof(FeatureEditor.Id)] = created.Feature.InternalId, [nameof(FeatureEditor.VersionId)] = created.Feature.VersionId });
            var markdown = "Paragraph\n\n| A | B |\n|---|---|\n| x | y |\n\n```mermaid\ngraph TD\n A-->B\n```\n\n<script>bad()</script>";
            await renderer.Field("Title").ValueChanged.InvokeAsync(" <b>UI title</b> ");
            await renderer.Input("feature-description", markdown);
            Assert.Contains("Description preview", renderer.Text());
            Assert.Contains("<table>", renderer.Text());
            Assert.Contains("language-mermaid", renderer.Text());
            Assert.DoesNotContain("<script>bad", renderer.Text());
            await renderer.Event("onsubmit", EventArgs.Empty);
            var changed = (await store.Find(created.Feature.InternalId)).Value.Value;
            Assert.Equal(markdown, changed.Feature.Content.Description);
            using var response = await host.Browser.GetAsync($"/features/details?id={created.Feature.InternalId}&version={created.Feature.VersionId}");
            var detail = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode);
            Assert.Contains("<table>", detail); Assert.Contains("language-mermaid", detail); Assert.Contains("&lt;b&gt;UI title&lt;/b&gt;", detail); Assert.DoesNotContain("<script>bad", detail);
            await store.Create(other.InternalId, FeatureContent.Create("Other product secret", ""), FeatureActor, FeatureTime);
            await renderer.Mount(typeof(FeatureList), new() { [nameof(FeatureList.ProductId)] = "P" });
            Assert.Contains("UI title", renderer.Text()); Assert.DoesNotContain("Other product secret", renderer.Text());
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("ui title"); Assert.Contains("UI title", renderer.Text());
        });
    }
}
#pragma warning restore BL0006
