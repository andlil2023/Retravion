using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Feature;
using Retravion.Application.Product.Create;
using Retravion.Domain.Feature;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Feature;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Web.Features.Feature;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpFeatureSchemasRejectMalformedAndInjectedArgumentsBeforeStorage()
    {
        var store = new FeatureGateStore();
        await using var host = new McpTestHost(services =>
        { services.AddSingleton<IFeatureStore>(store); services.AddSingleton<IFeatureChangeTransaction>(store); });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var tools = (await host.Rpc("tools/list")).GetProperty("tools").EnumerateArray()
            .Where(t => t.GetProperty("name").GetString()!.Contains("feature", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, tools.Length);
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();
            var required = tool.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order().ToArray();
            Assert.Equal(name switch
            {
                "list_features" or "create_draft_feature" => new[] { "productId" },
                "read_feature" => new[] { "internalId" },
                _ => new[] { "description", "expectedRevision", "internalId", "title", "versionId" }
            }, required);
        }
        var valid = new Dictionary<string, object?> { ["internalId"] = Guid.NewGuid(), ["versionId"] = Guid.NewGuid(), ["expectedRevision"] = 1, ["title"] = "", ["description"] = "" };
        async Task Reject(string name, object arguments)
        {
            using var response = await host.Send("tools/call", new { name, arguments });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var key in valid.Keys)
        {
            var missing = new Dictionary<string, object?>(valid); missing.Remove(key); await Reject("edit_draft_feature", missing);
            await Reject("edit_draft_feature", new Dictionary<string, object?>(valid) { [key] = null });
        }
        foreach (var revision in new object[] { 0, -1, 1.5, "1", true })
            await Reject("edit_draft_feature", new Dictionary<string, object?>(valid) { ["expectedRevision"] = revision });
        foreach (var field in new[] { "actor", "status", "id", "links", "operationId", "reason", "sourceVersionId", "versionNumber", "productInternalId" })
        {
            await Reject("edit_draft_feature", new Dictionary<string, object?>(valid) { [field] = "injected" });
            await Reject("create_draft_feature", new Dictionary<string, object?> { ["productId"] = "P", [field] = "injected" });
        }
        await Reject("read_feature", new { internalId = "invalid" });
        await Reject("read_feature", new { internalId = Guid.NewGuid(), versionId = "invalid" });
        await Reject("create_draft_feature", new { productId = "P", title = 1 });
        await Reject("list_features", new { productId = "P", filter = "Draft" });
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task McpFeatureDenialsAreSanitizedBeforeValidationAndAuditFailureStaysDenied()
    {
        var audit = new CaseAuditRecorder(); var logger = new FeatureAuditLogger(); var store = new FeatureGateStore();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAuditSubmission>(audit); services.AddSingleton<ILogger<FeatureOperations>>(logger);
            services.AddSingleton<IFeatureStore>(store); services.AddSingleton<IFeatureChangeTransaction>(store);
        });
        foreach (var state in new[] { "anonymous", "restricted", "active", "revoked" })
        {
            if (state == "restricted") await host.Login();
            if (state == "active") { await host.Replace(); await host.Authenticate(); }
            var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(host.Mcp.DefaultRequestHeaders.Authorization?.Parameter);
            if (state == "revoked") await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(principal.FindFirstValue(SessionTicketStore.SessionClaim)!);
            foreach (var tool in new[] { "create_draft_feature", "edit_draft_feature" })
            {
                var count = audit.Events.Count;
                using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret", actor = "spoof" } }, state == "active" ? "https://evil.invalid" : null);
                Assert.Equal(state == "active" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, denied.StatusCode);
                Assert.Equal(count + 1, audit.Events.Count);
                var entry = audit.Events.Last();
                Assert.Equal(tool.StartsWith("create", StringComparison.Ordinal) ? "feature.create" : "feature.edit", entry.Action);
                Assert.IsType<AuditTarget.Unknown>(entry.Target); Assert.Null(entry.FeatureChange);
                Assert.Equal(AuditActionOutcome.Failure, entry.Outcome); Assert.True(entry.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
                if (state is "active" or "revoked") Assert.Equal(new AuditActor.Identified("admin"), entry.Actor);
                else Assert.IsType<AuditActor.Unknown>(entry.Actor);
            }
        }
        foreach (var throwing in new[] { false, true })
        {
            audit.Fail = true; audit.Throw = throwing;
            foreach (var tool in new[] { "create_draft_feature", "edit_draft_feature" })
            { using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret" } }); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        }
        Assert.Equal(4, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal("Unauthorized Feature change audit submission failed.", message));
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("rotate")]
    [InlineData("expire")]
    public async Task McpFeatureQueuedWritesRecheckLiveCredentials(string invalidation)
    {
        var store = new FeatureGateStore(); var audit = new CaseAuditRecorder(); var clock = new McpClock();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<TimeProvider>(clock); services.AddSingleton<IFeatureStore>(store);
            services.AddSingleton<IFeatureChangeTransaction>(store); services.AddSingleton<IAuditSubmission>(audit);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var credentials = host.Factory.Services.GetRequiredService<McpCredentials>();
        var principal = credentials.Resolve(token);
        var sessions = host.Factory.Services.GetRequiredService<SessionTicketStore>();
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<FeatureOperations>();
        await sessions.MutationGate.WaitAsync();
        Task<CSharpFunctionalExtensions.Result<FeatureSnapshot>> create, edit;
        try
        {
            create = operations.CreateMcp(principal, host.Factory.Services.GetRequiredService<Retravion.Application.Product.IProductStore>(), "secret", null, null, default);
            edit = operations.EditMcp(principal, Guid.NewGuid(), Guid.NewGuid(), 1, "secret", "", default);
            Assert.False(create.IsCompleted); Assert.False(edit.IsCompleted);
            if (invalidation == "revoke") credentials.Revoke(principal);
            else if (invalidation == "rotate") await host.Authenticate();
            else clock.Now += TimeSpan.FromMinutes(31);
        }
        finally { sessions.MutationGate.Release(); }
        Assert.Equal("Access denied.", (await create).Error); Assert.Equal("Access denied.", (await edit).Error);
        Assert.Equal(0, store.Calls);
        var denied = audit.Events.Where(e => e.Action.StartsWith("feature.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, denied.Length);
        Assert.All(denied, e => { Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.IsType<AuditTarget.Unknown>(e.Target); Assert.Null(e.FeatureChange); });
    }
}

public sealed partial class PostgresProductTests
{
    private static McpTestHost FeatureMcpHost(PostgresProductStore products, PostgresFeatureStore reads, PostgresFeatureStore? writes = null) => new(services =>
    { services.AddSingleton<Retravion.Application.Product.IProductStore>(products); services.AddSingleton<IFeatureStore>(reads); services.AddSingleton<IFeatureChangeTransaction>(writes ?? reads); });
    private static object FeatureEdit(FeatureSnapshot row, string title = "Changed", long? revision = null, Guid? versionId = null) => new
    { internalId = row.Feature.InternalId, versionId = versionId ?? row.Feature.VersionId, expectedRevision = revision ?? row.Revision, title, description = "# Raw Markdown" };

    [PostgresFact]
    public async Task McpFeaturesPreserveGeneratedIdentitiesLinksExactVersionsHistoryAndAudit()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        await using var host = FeatureMcpHost(products, store);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var created = await host.Call("create_draft_feature", new { productId = "P" });
        var id = created.GetProperty("internalId").GetGuid();
        Assert.Equal("F-001", created.GetProperty("id").GetString());
        Assert.Equal("Draft", created.GetProperty("status").GetString()); Assert.Equal(1, created.GetProperty("versionNumber").GetInt32());
        Assert.Equal("", created.GetProperty("title").GetString()); Assert.Equal("", created.GetProperty("description").GetString());
        Assert.Empty(created.GetProperty("links").EnumerateArray());
        var row = (await store.Find(id)).Value.Value;
        var edited = await host.Call("edit_draft_feature", FeatureEdit(row));
        Assert.Equal(2, edited.GetProperty("revision").GetInt64());
        row = (await store.Find(id)).Value.Value;
        await host.Call("edit_draft_feature", FeatureEdit(row)); // current no-op
        await host.Call("edit_draft_feature", FeatureEdit(row, revision: 1), true);
        await host.Call("edit_draft_feature", FeatureEdit(row, versionId: Guid.NewGuid()), true);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.FeatureChange is not null).ToArray();
        Assert.Equal(2, events.Length);
        Assert.All(events, e =>
        {
            Assert.Equal(new AuditActor.Identified("admin"), e.Actor); Assert.Equal(new AuditTarget.Identified(id.ToString()), e.Target);
            Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
            Assert.Equal(id, e.FeatureChange!.InternalId); Assert.Equal(row.Feature.VersionId, e.FeatureChange.VersionId);
            Assert.Equal(1, e.FeatureChange.VersionNumber); Assert.Equal(product.InternalId, e.FeatureChange.After.ProductInternalId);
        });
        var initial = events.Single(e => e.Action == "feature.create").FeatureChange!;
        Assert.Null(initial.Before); Assert.Equal("", initial.After.Title); Assert.Equal("", initial.After.Description);
        var change = events.Single(e => e.Action == "feature.edit").FeatureChange!;
        Assert.Equal(initial.After.Title, change.Before!.Title); Assert.Equal("Changed", change.After.Title); Assert.Equal("# Raw Markdown", change.After.Description);
        Assert.Equal(initial.After.Id, change.After.Id); Assert.Null(change.Decision); Assert.Null(change.Comment);
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var target = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "R"));
        row = (await store.ChangeLink(row, "add", new(target.Requirement.InternalId, target.Requirement.VersionId), null, FeatureActor, FeatureTime)).Value;
        await host.Call("edit_draft_feature", FeatureEdit(row, "With link"));
        row = (await store.Find(id)).Value.Value;
        Assert.Single(row.Feature.Links);
        var reviewing = (await store.Review(row, "InReview", "Ready", FeatureActor, FeatureTime)).Value;
        await host.Call("edit_draft_feature", FeatureEdit(reviewing), true);
        var approved = (await store.Review(reviewing, "Approved", "Accepted", FeatureActor, FeatureTime)).Value;
        await host.Call("edit_draft_feature", FeatureEdit(approved), true);
        var successor = (await store.CreateSuccessor(approved, "Next", FeatureActor, FeatureTime)).Value;
        await host.Call("edit_draft_feature", FeatureEdit(approved, revision: successor.Revision), true);
        var current = await host.Call("read_feature", new { internalId = id });
        Assert.Equal(successor.Feature.VersionId, current.GetProperty("feature").GetProperty("versionId").GetGuid());
        var historical = await host.Call("read_feature", new { internalId = id, versionId = approved.Feature.VersionId });
        Assert.Equal("Approved", historical.GetProperty("feature").GetProperty("status").GetString());
        Assert.Equal(2, historical.GetProperty("versions").GetArrayLength());
        Assert.Equal((await store.History(id)).Value.Count, historical.GetProperty("history").GetArrayLength());
        Assert.Contains(historical.GetProperty("history").EnumerateArray(), h => h.GetProperty("change").GetProperty("comment").GetString() == "Accepted");
        await host.Call("read_feature", new { internalId = id, versionId = Guid.NewGuid() }, true);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var protect = new NpgsqlCommand("UPDATE retravion_feature.feature_version SET release_protected=true WHERE version_id=$1", connection))
        { protect.Parameters.AddWithValue(successor.Feature.VersionId); await protect.ExecuteNonQueryAsync(); }
        var count = (await store.History(id)).Value.Count;
        await host.Call("edit_draft_feature", FeatureEdit(successor), true);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        await host.Call("create_draft_feature", new { productId = "P" }, true);
        await host.Call("edit_draft_feature", FeatureEdit(successor), true);
        Assert.Single((await host.Call("list_features", new { productId = "P" })).EnumerateArray());
        await host.Call("read_feature", new { internalId = id });
        Assert.Equal(count, (await store.History(id)).Value.Count);
    }

    [PostgresFact]
    public async Task McpFeaturePrecommitFailuresRollBackBusinessHistoryAndAudit()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        var row = (await store.Create(product.InternalId, FeatureContent.Create("Original", ""), FeatureActor, FeatureTime)).Value;
        await using var host = FeatureMcpHost(products, store);
        await host.Login(); await host.Replace(); await host.Authenticate();
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var table in new[] { "retravion_audit.event", "retravion_feature.history", "retravion_feature.feature_version", "retravion_feature.feature" })
        {
            await using (var inject = new NpgsqlCommand($"CREATE OR REPLACE FUNCTION public.reject_mcp_feature() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private provider' USING ERRCODE='42501'; END $$; CREATE TRIGGER reject_mcp_feature BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_mcp_feature();", connection)) await inject.ExecuteNonQueryAsync();
            await host.Call("create_draft_feature", new { productId = "P", title = "Blocked" }, true);
            await host.Call("edit_draft_feature", FeatureEdit(row), true);
            Assert.True(row.Feature.SameState((await store.Find(row.Feature.InternalId)).Value.Value.Feature));
            Assert.Single((await store.List(product.InternalId)).Value); Assert.Single((await store.History(row.Feature.InternalId)).Value);
            Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()), e => e.FeatureChange is not null);
            await using var remove = new NpgsqlCommand($"DROP TRIGGER reject_mcp_feature ON {table}", connection); await remove.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public Task McpFeatureCommittedUncertainCreationAndEditCanBeReconciled() => FeatureUncertainOutcome(true);

    [PostgresFact]
    public Task McpFeatureUncommittedUncertainCreationAndEditCanBeReconciled() => FeatureUncertainOutcome(false);

    private async Task FeatureUncertainOutcome(bool commits)
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresFeatureStore(database.ConnectionString);
        var row = (await store.Create(product.InternalId, FeatureContent.Create("Original", ""), FeatureActor, FeatureTime)).Value;
        var attempts = 0;
        var uncertain = new PostgresFeatureStore(database.ConnectionString, async (transaction, token) =>
        { attempts++; if (commits) await transaction.CommitAsync(token); throw new IOException("private provider"); });
        await using var host = FeatureMcpHost(products, store, uncertain);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var create = await host.Call("create_draft_feature", new { productId = "P", title = "Candidate" }, true);
        Assert.Contains("could not be confirmed", create.GetString()); Assert.Contains("candidate records", create.GetString()); Assert.DoesNotContain("private", create.GetString());
        Assert.Equal(1, attempts);
        var list = await host.Call("list_features", new { productId = "P" });
        Assert.Equal(commits, list.EnumerateArray().Any(f => f.GetProperty("title").GetString() == "Candidate"));
        var edit = await host.Call("edit_draft_feature", FeatureEdit(row), true);
        Assert.Contains("could not be confirmed", edit.GetString()); Assert.Contains("Retrieve authorized", edit.GetString()); Assert.Equal(2, attempts);
        var current = await host.Call("read_feature", new { internalId = row.Feature.InternalId });
        Assert.Equal(commits ? "Changed" : "Original", current.GetProperty("feature").GetProperty("title").GetString());
        Assert.Equal(commits ? 2 : 1, current.GetProperty("history").GetArrayLength());
        Assert.Equal(commits ? 3 : 1, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count(e => e.FeatureChange is not null));
    }
}
