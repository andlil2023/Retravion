using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task McpReadRequirementBeforeAnyProductAccessReturnsNotFoundWithoutInitializingProductStorage()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var store = new PostgresRequirementStore(database.ConnectionString);
        await using var host = RequirementMcpHost(products, store, store);
        await host.Login(); await host.Replace(); await host.Authenticate();
        // Do not navigate to a Product page or render the authenticated shell before this call.
        // Identity in this host is initialized by the existing isolated identity fixture.
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        async Task ProductStorageAbsent()
        {
            await using var check = new NpgsqlCommand("SELECT to_regnamespace('retravion_product') IS NULL", connection);
            Assert.Equal(true, await check.ExecuteScalarAsync());
        }
        await ProductStorageAbsent();
        var result = await host.Call("read_requirement", new { internalId = Guid.NewGuid() }, true);
        Assert.Equal("Requirement not found.", result.GetString());
        await ProductStorageAbsent();
        Assert.Empty(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()));
    }

    [PostgresFact]
    public async Task McpRequirementsShareValidationConcurrencyAllStatusReadsAndExactAudit()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        await using var host = RequirementMcpHost(products, store, store);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var first = await host.Call("create_draft_requirement", new { productId = "P", id = " R-1 ", title = "Original", statement = "# Statement", type = "Software", rationale = "Reason" });
        var identity = first.GetProperty("internalId").GetGuid();
        Assert.Equal("R-1", first.GetProperty("id").GetString()); Assert.Equal("Software", first.GetProperty("type").GetString());
        Assert.Equal("Draft", first.GetProperty("status").GetString()); Assert.Equal(product.InternalId, first.GetProperty("productInternalId").GetGuid());
        var edited = await host.Call("edit_draft_requirement", new { internalId = identity, expectedRevision = 1, id = "R-2", title = "Changed", statement = "Updated", type = "Hardware" });
        Assert.Equal(identity, edited.GetProperty("internalId").GetGuid()); Assert.Equal(2, edited.GetProperty("revision").GetInt64()); Assert.Equal(System.Text.Json.JsonValueKind.Null, edited.GetProperty("rationale").ValueKind);
        await host.Call("edit_draft_requirement", new { internalId = identity, expectedRevision = 2, id = "R-2", title = "Changed", statement = "Updated", type = "Hardware" });
        await host.Call("edit_draft_requirement", new { internalId = identity, expectedRevision = 1, id = "R-2", title = "Stale", statement = "Updated", type = "Hardware" }, true);
        foreach (var invalid in new[] { "", "Unknown", "99", "Software, Safety" })
            await host.Call("create_draft_requirement", new { productId = "P", id = "invalid", title = "Title", statement = "Statement", type = invalid }, true);
        await host.Call("create_draft_requirement", new { productId = "P", id = "r-2", title = "Duplicate", statement = "Statement", type = "System" }, true);
        await host.Call("create_draft_requirement", new { productId = "P", id = "invalid", title = "", statement = "Statement", type = "System" }, true);
        await host.Call("edit_draft_requirement", new { internalId = identity, expectedRevision = 2, id = "R-2", title = "", statement = "Statement", type = "System" }, true);
        foreach (var field in new[] { "status", "actor", "productInternalId", "decision", "version" })
        {
            var arguments = new Dictionary<string, object> { ["internalId"] = identity, ["expectedRevision"] = 2, ["id"] = "R-2", ["title"] = "Injected", ["statement"] = "Injected", ["type"] = "Safety", [field] = "Approved" };
            using var injected = await host.Send("tools/call", new { name = "edit_draft_requirement", arguments });
            Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        }
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.RequirementChange is not null).ToArray();
        Assert.Equal(2, events.Length);
        Assert.All(events, e => { Assert.Equal(new AuditActor.Identified("admin"), e.Actor); Assert.Equal(new AuditTarget.Identified(identity.ToString()), e.Target); Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5)); });
        var initial = events.Single(e => e.Action == "requirement.create").RequirementChange!;
        Assert.Null(initial.Before); Assert.Equal(new RequirementAuditValues(product.InternalId, "R-1", "Original", "# Statement", "Software", "Reason", "Draft", Retravion.Domain.Product.AreaEvidence.Unclassified), initial.After);
        var editEvent = events.Single(e => e.Action == "requirement.edit").RequirementChange!;
        Assert.Equal(initial.After, editEvent.Before); Assert.Equal(new RequirementAuditValues(product.InternalId, "R-2", "Changed", "Updated", "Hardware", null, "Draft", Retravion.Domain.Product.AreaEvidence.Unclassified), editEvent.After);
        Assert.Null(editEvent.Decision); Assert.Null(editEvent.Comment);
        var snapshot = (await store.Find(identity)).Value.Value;
        var reviewed = (await store.Update(snapshot, snapshot.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview", "Ready")).Value;
        await host.Call("edit_draft_requirement", new { internalId = identity, expectedRevision = reviewed.Revision, id = "R-2", title = "Denied", statement = "Denied", type = "Safety" }, true);
        var approved = (await store.Update(reviewed, reviewed.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        await host.Call("edit_draft_requirement", new { internalId = identity, expectedRevision = approved.Revision, id = "R-2", title = "Denied", statement = "Denied", type = "Safety" }, true);
        foreach (var status in new[] { RequirementStatus.Draft, RequirementStatus.InReview })
        {
            var r = RequirementState.Create(product.InternalId, status.ToString(), "Title", "Statement", RequirementType.System).Value;
            var s = (await store.Create(r, RequirementActor, RequirementTime)).Value;
            if (status == RequirementStatus.InReview) await store.Update(s, r.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview");
        }
        var listed = await host.Call("list_requirements", new { productId = "P" });
        Assert.Equal(new[] { "Approved", "Draft", "InReview" }, listed.EnumerateArray().Select(r => r.GetProperty("status").GetString()).Order().ToArray());
        foreach (var row in listed.EnumerateArray())
        {
            var read = await host.Call("read_requirement", new { internalId = row.GetProperty("internalId").GetGuid() });
            Assert.Equal(row.GetProperty("statement").GetString(), read.GetProperty("requirement").GetProperty("statement").GetString());
        }
        var detail = await host.Call("read_requirement", new { internalId = identity });
        var history = detail.GetProperty("history"); Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("R-2", history[0].GetProperty("id").GetString()); Assert.Equal("Hardware", history[0].GetProperty("type").GetString());
        Assert.Equal("Ready", history[0].GetProperty("comment").GetString()); Assert.Equal("author", history[0].GetProperty("actor").GetString());
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Equal(3, (await host.Call("list_requirements", new { productId = "P" })).GetArrayLength());
        await host.Call("read_requirement", new { internalId = identity });
        await host.Call("create_draft_requirement", new { productId = "P", id = "retired", title = "Denied", statement = "Denied", type = "System" }, true);
        var draft = listed.EnumerateArray().Single(r => r.GetProperty("status").GetString() == "Draft");
        await host.Call("edit_draft_requirement", new { internalId = draft.GetProperty("internalId").GetGuid(), expectedRevision = draft.GetProperty("revision").GetInt64(), id = "Draft", title = "Denied", statement = "Denied", type = "System" }, true);
        Assert.Equal(2, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count(e => e.Actor == new AuditActor.Identified("admin")));
    }

    [PostgresFact]
    public async Task McpRequirementAuditFailuresAreAtomicAndUncertainWritesAreNeverRetried()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var writes = new RequirementWriteHook(store);
        await using var host = RequirementMcpHost(products, store, writes);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var original = (await store.Create(RequirementState.Create(product.InternalId, "existing", "Before", "Before", RequirementType.Software).Value, RequirementActor, RequirementTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("CREATE FUNCTION retravion_requirement.reject_mcp() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE = '42501'; END $$; CREATE TRIGGER reject_mcp BEFORE INSERT ON retravion_audit.event FOR EACH ROW EXECUTE FUNCTION retravion_requirement.reject_mcp();", connection)) await inject.ExecuteNonQueryAsync();
        await host.Call("create_draft_requirement", new { productId = "P", id = "blocked", title = "Blocked", statement = "Blocked", type = "Software" }, true);
        await host.Call("edit_draft_requirement", new { internalId = original.Requirement.InternalId, expectedRevision = 1, id = "existing", title = "Blocked", statement = "Blocked", type = "Software" }, true);
        Assert.Equal(original, (await store.Find(original.Requirement.InternalId)).Value.Value); Assert.Single((await store.List(product.InternalId)).Value);
        await using (var remove = new NpgsqlCommand("DROP TRIGGER reject_mcp ON retravion_audit.event", connection)) await remove.ExecuteNonQueryAsync();
        foreach (var commits in new[] { false, true })
        {
            var attempts = 0;
            writes.Store = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) => { attempts++; if (commits) await transaction.CommitAsync(token); throw new IOException("private"); });
            var id = "uncertain-" + commits;
            var result = await host.Call("create_draft_requirement", new { productId = "P", id, title = "Uncertain", statement = "Uncertain", type = "Safety" }, true);
            Assert.Contains("could not be confirmed", result.GetString()); Assert.Contains("Retrieve", result.GetString()); Assert.DoesNotContain("private", result.GetString()); Assert.Equal(1, attempts);
            var listed = await host.Call("list_requirements", new { productId = "P" }); Assert.Equal(commits, listed.EnumerateArray().Any(r => r.GetProperty("id").GetString() == id));
            var before = (await store.Find(original.Requirement.InternalId)).Value.Value;
            var edit = await host.Call("edit_draft_requirement", new { internalId = before.Requirement.InternalId, expectedRevision = before.Revision, id = "existing", title = id, statement = id, type = "Hardware" }, true);
            Assert.Contains("could not be confirmed", edit.GetString()); Assert.Contains("Retrieve", edit.GetString()); Assert.Equal(2, attempts);
            Assert.Equal(commits ? id : before.Requirement.Title, (await store.Find(before.Requirement.InternalId)).Value.Value.Requirement.Title);
        }
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.Actor == new AuditActor.Identified("admin")).ToArray();
        Assert.Equal(2, events.Length); Assert.Single(events, e => e.Action == "requirement.create"); Assert.Single(events, e => e.Action == "requirement.edit");
    }

    private static McpTestHost RequirementMcpHost(Retravion.Application.Product.IProductStore products, IRequirementStore reads, IRequirementChangeTransaction writes, string? connectionString = null) => new(services =>
    { if (connectionString is not null) services.AddSingleton<Retravion.Application.Product.IProductAreaStore>(new PostgresProductAreaStore(connectionString));
        services.AddSingleton(products); services.AddSingleton(reads); services.AddSingleton(writes);
        services.AddSingleton((IRequirementLinkStore)reads); });

    private sealed class RequirementWriteHook(PostgresRequirementStore store) : IRequirementChangeTransaction
    {
        internal PostgresRequirementStore Store = store;
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> Derive(RequirementSnapshot source, RequirementState destination, AuditActor actor, DateTimeOffset time, CancellationToken token = default) => Store.Derive(source, destination, actor, time, token);
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> CreateSuccessor(RequirementSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => Store.CreateSuccessor(expected, reason, actor, time, token, operationId, authorized);
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> Create(RequirementState r, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => Store.Create(r, actor, time, token, operationId, authorized);
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> Update(RequirementSnapshot expected, RequirementState replacement, AuditActor actor, DateTimeOffset time, string? decision = null, string? comment = null, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => Store.Update(expected, replacement, actor, time, decision, comment, token, operationId, authorized);
    }
}

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task DeniedMcpRequirementWritesAreAuditedBeforeBusinessArgumentValidation()
    {
        var audit = new RequirementMcpRecorder();
        var store = new RequirementStoreDouble();
        var logger = new RequirementAuditLogger();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAuditSubmission>(audit);
            services.AddSingleton<IRequirementStore>(store);
            services.AddSingleton<IRequirementChangeTransaction>(store);
            services.AddSingleton<IRequirementLinkStore>(store);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<Retravion.Web.Features.Requirement.RequirementOperations>>(logger);
        });
        foreach (var state in new[] { "anonymous", "restricted", "revoked" })
        {
            if (state == "restricted") await host.Login();
            if (state == "revoked")
            {
                await host.Replace(); var token = await host.Authenticate();
                var principal = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>().Resolve(token);
                var session = principal.FindFirst(Retravion.Web.Features.Identity.Logout.SessionTicketStore.SessionClaim)!.Value;
                await host.Factory.Services.GetRequiredService<Retravion.Web.Features.Identity.Logout.SessionTicketStore>().RemoveAsync(session);
            }
            foreach (var tool in new[] { "create_draft_requirement", "edit_draft_requirement" })
            {
                using var denied = await host.Send("tools/call", new { name = tool, arguments = new { id = "secret", title = "secret", statement = "secret", type = "secret", rationale = "secret", actor = "spoof" } });
                Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            using var read = await host.Send("tools/call", new { name = "read_requirement", arguments = new { internalId = Guid.NewGuid() } });
            Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        }
        Assert.Equal(6, audit.Events.Count);
        Assert.All(audit.Events, e =>
        {
            Assert.Contains(e.Action, new[] { "requirement.create", "requirement.edit" }); Assert.IsType<AuditTarget.Unknown>(e.Target);
            Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.Null(e.RequirementChange); Assert.Null(e.UserNeedChange);
        });
        Assert.All(audit.Events.Take(4), e => Assert.IsType<AuditActor.Unknown>(e.Actor));
        Assert.All(audit.Events.Skip(4), e => Assert.Equal(new AuditActor.Identified("admin"), e.Actor));
        foreach (var failure in new[] { "failure", "throw" })
        {
            audit.Mode = failure;
            foreach (var tool in new[] { "create_draft_requirement", "edit_draft_requirement" })
            { using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret" } }); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        }
        Assert.Equal(0, store.Calls);
        Assert.Equal(4, logger.Messages.Count);
        Assert.All(logger.Messages, m => Assert.Equal("Unauthorized Requirement change audit submission failed.", m));
    }
    private sealed class RequirementMcpRecorder : IAuditSubmission
    {
        internal List<AuditInformation> Events = [];
        internal string Mode = "success";
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken token = default)
        {
            if (!information.Action.StartsWith("requirement.", StringComparison.Ordinal)) return Task.FromResult(AuditSubmissionResult.Success);
            if (Mode == "throw") throw new IOException("private");
            Events.Add(information); return Task.FromResult(Mode == "failure" ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
        }
    }
}
