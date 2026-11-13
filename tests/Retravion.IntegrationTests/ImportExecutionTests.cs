using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Xunit;
using Xunit.Abstractions;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private readonly ITestOutputHelper importOutput;
    public PostgresProductTests(ITestOutputHelper output) => importOutput = output;

    [PostgresFact]
    public async Task ApprovedImportsExecuteAllKindsAndExactLinksWithAtomicAuditAndBenchmarkEquivalentWork()
    {
        var result = await RunImportWorker("benchmark");
        Assert.Equal(69, result.GetProperty("bulk").GetProperty("mcpRequests").GetInt32());
        Assert.Equal(49, result.GetProperty("perRecordScriptedBaseline").GetProperty("mcpRequests").GetInt32());
    }

    [PostgresFact]
    public Task ApprovedImportCommittedLostConfirmationStopsAndReconcilesWithoutReplay() => RunImportWorker("committed");

    [PostgresFact]
    public Task ApprovedImportUncommittedLostConfirmationStopsAndReconcilesWithoutReplay() => RunImportWorker("uncommitted");

    private async Task<JsonElement> RunImportWorker(string mode)
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        foreach (var id in new[] { "P-import", "P-baseline" })
            Assert.True((await new CreateProduct(products).Execute(id, id, ProductKind.System, "")).IsSuccess);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var attempts = 0;
        var features = mode == "benchmark" ? new PostgresFeatureStore(database.ConnectionString) :
            new PostgresFeatureStore(database.ConnectionString, async (transaction, token) =>
            {
                attempts++;
                if (mode == "committed") await transaction.CommitAsync(token);
                throw new IOException("private provider detail");
            });
        await using var host = new McpTestHost(services =>
        {
            RegisterRelationshipCases(services, database.ConnectionString);
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<Retravion.Application.UserNeed.IUserNeedStore>(needs);
            services.AddSingleton<Retravion.Application.UserNeed.IUserNeedChangeTransaction>(needs);
            services.AddSingleton<Retravion.Application.Requirement.IRequirementStore>(requirements);
            services.AddSingleton<Retravion.Application.Requirement.IRequirementChangeTransaction>(requirements);
            services.AddSingleton<Retravion.Application.Requirement.IRequirementLinkStore>(requirements);
            // Inject lost-confirmation only on writes; reconciliation uses the normal read store.
            services.AddSingleton<Retravion.Application.Feature.IFeatureStore>(new PostgresFeatureStore(database.ConnectionString));
            services.AddSingleton<Retravion.Application.Feature.IFeatureChangeTransaction>(features);
            services.AddSingleton<IAuditSubmission>(new PostgresAuditSubmission(database.ConnectionString));
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        // Test-only stdio adapter to existing authenticated MCP tools. No new server endpoint or credential transfer.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Retravion.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root.FullName };
        start.ArgumentList.Add("tests/ImportExecution.Worker.mjs"); start.ArgumentList.Add(mode);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        JsonElement result = default;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.GetProperty("type").GetString() == "result") { result = message.Clone(); continue; }
                Assert.Equal("call", message.GetProperty("type").GetString());
                var response = await host.Rpc("tools/call", new { name = message.GetProperty("name").GetString(), arguments = message.GetProperty("args").Clone() });
                await process.StandardInput.WriteLineAsync(response.GetRawText()); await process.StandardInput.FlushAsync();
            }
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await errors);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        importOutput.WriteLine(result.GetRawText());
        // Browser login is separately audited with an unknown target; inspect the import's owning-feature actions.
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()))
            .Where(e => new[] { "need.", "requirement.", "test-case.", "feature." }.Any(prefix => e.Action.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
        Assert.All(events, e => Assert.Equal(new AuditActor.Identified("admin"), e.Actor));
        Assert.All(events, e => { Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.IsType<AuditTarget.Identified>(e.Target); Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5)); });
        if (mode == "benchmark")
        {
            Assert.Equal(42, events.Length); // 12 creations + 9 actual links per equivalent Product.
            foreach (var action in new[] { "need.create", "requirement.create", "test-case.create", "feature.create" })
                Assert.Equal(6, events.Count(e => e.Action == action));
            foreach (var e in events)
            {
                Assert.True(e.UserNeedChange is not null || e.RequirementChange is not null || e.TestCaseChange is not null || e.FeatureChange is not null);
                Assert.DoesNotContain("private provider", JsonSerializer.Serialize(e));
            }
        }
        else
        {
            Assert.Equal(1, attempts); // Creation is never retried, including after reconciliation.
            Assert.Equal(mode == "committed" ? 3 : 2, events.Length);
            var observation = Assert.Single(result.GetProperty("observations").EnumerateArray(), o => o.GetProperty("sourceKey").GetString() == "f1");
            Assert.Equal(mode == "committed" ? 1 : 0, observation.GetProperty("candidates").GetArrayLength());
            Assert.DoesNotContain("private provider", result.GetRawText());
        }
        return result;
    }
}
