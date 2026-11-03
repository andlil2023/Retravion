using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AiWorkflow;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AiWorkflow;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task AiWorkflowDefaultPersistenceConcurrencyAndAuditAreAtomic()
    {
        await using var database = await Database.Create();
        var store = new PostgresAiWorkflowSettingsStore(database.ConnectionString);
        var initial = (await store.Load()).Value;
        Assert.Equal(new(false, 1), initial);
        Assert.Empty(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()));
        var actor = new AuditActor.Identified("administrator");
        var time = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.Save(initial, true, actor, time)));
        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(3, results.Count(r => r.IsFailure));
        var enabled = results.Single(r => r.IsSuccess).Value;
        Assert.Equal(new(true, 2), enabled);
        Assert.Equal(enabled, (await new PostgresAiWorkflowSettingsStore(database.ConnectionString).Load()).Value);
        Assert.Equal(enabled, (await store.Save(enabled, true, actor, time)).Value);
        Assert.True((await store.Save(initial, false, actor, time)).IsFailure);
        Assert.True((await store.Save(enabled, false, new AuditActor.Unknown(), time)).IsFailure);
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        var change = Assert.Single(events);
        Assert.Equal(actor, change.Actor); Assert.Equal(time.UtcTicks, change.OccurredAt.UtcTicks);
        Assert.Equal("ai-workflow.configure", change.Action); Assert.Equal(new AuditTarget.Identified("ai-workflow"), change.Target);
        Assert.Equal(AuditActionOutcome.Success, change.Outcome); Assert.Equal(new(false, true), change.AiWorkflowChange);
        Assert.Null(change.ProductChange); Assert.Null(change.UserNeedChange);
        var disabled = (await store.Save(enabled, false, actor, time)).Value;
        Assert.Equal(new(false, 3), disabled);
        var reenabling = (await store.Save(disabled, true, actor, time)).Value;
        Assert.Equal(new(true, 4), reenabling);
        Assert.Equal(3, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task SettingsAuditFailureRollsBackAndBothUncertainCommitOutcomesAreReportedWithoutRetry()
    {
        await using var database = await Database.Create();
        var store = new PostgresAiWorkflowSettingsStore(database.ConnectionString);
        var initial = (await store.Load()).Value;
        var actor = new AuditActor.Identified("admin");
        var enabled = (await store.Save(initial, true, actor, DateTimeOffset.UtcNow)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("CREATE FUNCTION retravion_audit.reject_settings() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-settings-detail' USING ERRCODE='42501'; END $$; CREATE TRIGGER reject_settings BEFORE INSERT ON retravion_audit.event FOR EACH ROW EXECUTE FUNCTION retravion_audit.reject_settings();", connection)) await command.ExecuteNonQueryAsync();
        var failed = await store.Save(enabled, false, actor, DateTimeOffset.UtcNow);
        Assert.True(failed.IsFailure); Assert.DoesNotContain("private", failed.Error);
        Assert.Equal(enabled, (await store.Load()).Value);
        Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()));
        await using (var command = new NpgsqlCommand("DROP TRIGGER reject_settings ON retravion_audit.event", connection)) await command.ExecuteNonQueryAsync();
        foreach (var commits in new[] { false, true })
        {
            var attempts = 0;
            var uncertain = new PostgresAiWorkflowSettingsStore(database.ConnectionString, async (transaction, token) =>
            {
                attempts++;
                if (commits) await transaction.CommitAsync(token);
                throw new IOException("private-provider-detail");
            });
            var result = await uncertain.Save(enabled, false, actor, DateTimeOffset.UtcNow);
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error);
            Assert.Contains("Reload", result.Error); Assert.Contains("manually", result.Error); Assert.DoesNotContain("private", result.Error);
            Assert.Equal(1, attempts);
            var persisted = (await store.Load()).Value;
            Assert.Equal(commits ? new(false, 3) : enabled, persisted);
            Assert.Equal(commits ? 2 : 1, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        }
    }

    [PostgresFact]
    public async Task PersistedDisableReenableRejectsOldTokensAndProductReadsHaveNoChangeEvents()
    {
        await using var database = await Database.Create();
        var settings = new PostgresAiWorkflowSettingsStore(database.ConnectionString);
        var products = new PostgresProductStore(database.ConnectionString);
        var active = (await new CreateProduct(products).Execute("ACTIVE", "Active Product", ProductKind.System, "# Full\n\nMarkdown")).Value;
        var retired = (await new CreateProduct(products).Execute("RETIRED", "Retired Product", ProductKind.Firmware, "Retained description")).Value;
        Assert.True((await products.Update(retired, retired.Retire().Value)).IsSuccess);
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAiWorkflowSettingsStore>(settings);
            services.AddSingleton<IProductStore>(products);
            services.AddSingleton<IAuditQuery>(new PostgresAuditQuery(database.ConnectionString));
        });
        await host.Login(); await host.Replace();
        using (var disabled = await host.Issue()) Assert.Equal(System.Net.HttpStatusCode.Forbidden, disabled.StatusCode);
        var initial = (await settings.Load()).Value;
        var actor = new AuditActor.Identified("admin");
        var enabled = (await settings.Save(initial, true, actor, DateTimeOffset.UtcNow)).Value;
        await host.Authenticate();
        var listed = await host.Call("list_products", new { });
        Assert.Equal(2, listed.GetArrayLength());
        var read = await host.Call("read_product", new { productId = "active" });
        Assert.Equal(active.Id, read.GetProperty("id").GetString());
        Assert.Equal(active.Name, read.GetProperty("name").GetString());
        Assert.Equal(active.Kind.ToString(), read.GetProperty("kind").GetString());
        Assert.Equal(active.Description, read.GetProperty("description").GetString());
        Assert.Equal("Active", read.GetProperty("status").GetString());
        Assert.False(read.TryGetProperty("internalId", out _));
        Assert.Equal("Retired", (await host.Call("read_product", new { productId = "RETIRED" })).GetProperty("status").GetString());
        await host.Call("read_product", new { productId = "missing" }, true);
        using (var injected = await host.Send("tools/call", new { name = "read_product", arguments = new { productId = "ACTIVE", status = "Retired" } }))
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, injected.StatusCode);
        Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()));
        var disabledState = (await settings.Save(enabled, false, actor, DateTimeOffset.UtcNow)).Value;
        using (var denied = await host.Send("tools/list")) Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.True((await settings.Save(disabledState, true, actor, DateTimeOffset.UtcNow)).IsSuccess);
        using (var denied = await host.Send("tools/list")) Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);
        await host.Authenticate();
        await host.Call("list_products", new { });
        var html = await host.Browser.GetStringAsync("/administration/audit");
        Assert.Contains("AI Workflow settings", html); Assert.Contains("MCP enabled", html);
        Assert.Equal(active, (await products.Find(active.Id)).Value.Value);
    }
}
