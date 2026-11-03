using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.UserNeed;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task McpDraftWritesShareWebValidationConcurrencyAndAttributableAuditAndReadAllStatuses()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        await using var host = NeedMcpHost(products, store, store);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var first = await host.Call("create_draft_user_need", new { productId = "P", id = " N-1 ", title = "Original", statement = "# Statement\n\n**Markdown**" });
        var identity = first.GetProperty("internalId").GetGuid();
        Assert.Equal(product.InternalId, first.GetProperty("productInternalId").GetGuid());
        Assert.Equal("N-1", first.GetProperty("id").GetString());
        Assert.Equal("Draft", first.GetProperty("status").GetString());
        Assert.Equal(1, first.GetProperty("revision").GetInt64());
        var edited = await host.Call("edit_draft_user_need", new { internalId = identity, expectedRevision = 1, id = "N-2", title = "Changed", statement = "Updated" });
        Assert.Equal(identity, edited.GetProperty("internalId").GetGuid());
        Assert.Equal(2, edited.GetProperty("revision").GetInt64());
        // No-op, reads, validation, duplicate IDs and stale saves never produce change events.
        await host.Call("edit_draft_user_need", new { internalId = identity, expectedRevision = 2, id = "N-2", title = "Changed", statement = "Updated" });
        await host.Call("edit_draft_user_need", new { internalId = identity, expectedRevision = 1, id = "N-2", title = "Stale", statement = "Updated" }, true);
        await host.Call("edit_draft_user_need", new { internalId = identity, expectedRevision = 2, id = "N-2", title = "", statement = "Updated" }, true);
        await host.Call("create_draft_user_need", new { productId = "P", id = "n-2", title = "Duplicate", statement = "Updated" }, true);
        await host.Call("create_draft_user_need", new { productId = "P", id = "Invalid", title = "", statement = "Updated" }, true);
        foreach (var field in new[] { "status", "actor", "productInternalId", "decision", "version" })
        {
            var arguments = new Dictionary<string, object> { ["internalId"] = identity, ["expectedRevision"] = 2, ["id"] = "N-2", ["title"] = "Injected", ["statement"] = "Injected", [field] = "Approved" };
            using var injected = await host.Send("tools/call", new { name = "edit_draft_user_need", arguments });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, injected.StatusCode);
        }
        var query = new PostgresAuditQuery(database.ConnectionString);
        var events = (await query.FindAsync(new())).Where(e => e.UserNeedChange is not null).ToArray();
        Assert.Equal(2, events.Length);
        Assert.All(events, e =>
        {
            Assert.Equal(new AuditActor.Identified("admin"), e.Actor);
            Assert.Equal(new AuditTarget.Identified(identity.ToString()), e.Target);
            Assert.Equal(AuditActionOutcome.Success, e.Outcome);
            Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        });
        var createdEvent = events.Single(e => e.Action == "need.create").UserNeedChange!;
        Assert.Equal(identity, createdEvent.InternalId);
        Assert.Null(createdEvent.Before);
        Assert.Equal(new UserNeedAuditValues(product.InternalId, "N-1", "Original", "# Statement\n\n**Markdown**", "Draft"), createdEvent.After);
        var editEvent = events.Single(e => e.Action == "need.edit").UserNeedChange!;
        Assert.Equal(createdEvent.After, editEvent.Before);
        Assert.Equal(new UserNeedAuditValues(product.InternalId, "N-2", "Changed", "Updated", "Draft"), editEvent.After);
        Assert.Null(editEvent.Decision); Assert.Null(editEvent.Comment);
        var draft = (await store.Find(identity)).Value.Value;
        var actor = new AuditActor.Identified("web-reviewer");
        var reviewed = (await store.Update(draft, draft.Need.SubmitForReview().Value, actor, DateTimeOffset.UtcNow, "InReview", "Ready")).Value;
        await host.Call("edit_draft_user_need", new { internalId = identity, expectedRevision = reviewed.Revision, id = "N-2", title = "Denied", statement = "Denied" }, true);
        var approved = (await store.Update(reviewed, reviewed.Need.Approve().Value, actor, DateTimeOffset.UtcNow, "Approved")).Value;
        await host.Call("edit_draft_user_need", new { internalId = identity, expectedRevision = approved.Revision, id = "N-2", title = "Denied", statement = "Denied" }, true);
        foreach (var state in new[] { "Draft", "InReview" })
        {
            var need = Need.Create(product.InternalId, state, state, "Statement").Value;
            var snapshot = (await store.Create(need, actor, DateTimeOffset.UtcNow)).Value;
            if (state == "InReview") await store.Update(snapshot, need.SubmitForReview().Value, actor, DateTimeOffset.UtcNow, "InReview");
        }
        var listed = await host.Call("list_user_needs", new { productId = "P" });
        Assert.Equal(new[] { "Approved", "Draft", "InReview" }, listed.EnumerateArray().Select(n => n.GetProperty("status").GetString()).Order().ToArray());
        foreach (var row in listed.EnumerateArray())
        {
            var read = await host.Call("read_user_need", new { internalId = row.GetProperty("internalId").GetGuid() });
            Assert.Equal(row.GetProperty("statement").GetString(), read.GetProperty("need").GetProperty("statement").GetString());
        }
        var detail = await host.Call("read_user_need", new { internalId = identity });
        var history = detail.GetProperty("history");
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("web-reviewer", history[0].GetProperty("actor").GetString());
        Assert.Equal("Ready", history[0].GetProperty("comment").GetString());
        Assert.Equal("Changed", history[0].GetProperty("title").GetString());
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Equal(3, (await host.Call("list_user_needs", new { productId = "P" })).GetArrayLength());
        await host.Call("read_user_need", new { internalId = identity });
        await host.Call("create_draft_user_need", new { productId = "P", id = "retired", title = "Denied", statement = "Denied" }, true);
        var retiredDraft = listed.EnumerateArray().Single(n => n.GetProperty("status").GetString() == "Draft");
        await host.Call("edit_draft_user_need", new { internalId = retiredDraft.GetProperty("internalId").GetGuid(), expectedRevision = retiredDraft.GetProperty("revision").GetInt64(), id = "Draft", title = "Denied", statement = "Denied" }, true);
        Assert.Equal(2, (await query.FindAsync(new())).Count(e => e.Actor == new AuditActor.Identified("admin")));
    }

    [PostgresFact]
    public async Task McpRejectsConcurrentReviewRetirementAndContentChangesAtTransactionBoundary()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var reads = new McpReadHook(store);
        var writes = new McpWriteHook(store);
        await using var host = NeedMcpHost(products, reads, writes);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var actor = new AuditActor.Identified("web");
        foreach (var race in new[] { "edit", "review", "retirement" })
        {
            var snapshot = (await store.Create(Need.Create(product.InternalId, race, "Before", "Before").Value, actor, DateTimeOffset.UtcNow)).Value;
            reads.AfterFind = async () =>
            {
                if (race == "retirement") Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
                else
                {
                    var replacement = race == "edit" ? snapshot.Need.Edit(race, "Winner", "Winner").Value : snapshot.Need.SubmitForReview().Value;
                    Assert.True((await store.Update(snapshot, replacement, actor, DateTimeOffset.UtcNow, race == "review" ? "InReview" : null)).IsSuccess);
                }
            };
            await host.Call("edit_draft_user_need", new { internalId = snapshot.Need.InternalId, expectedRevision = snapshot.Revision, id = race, title = "Loser", statement = "Loser" }, true);
            Assert.NotEqual("Loser", (await store.Find(snapshot.Need.InternalId)).Value.Value.Need.Title);
        }
        product = (await products.Find("P")).Value.Value;
        Assert.True((await products.Update(product, product.Reactivate().Value)).IsSuccess);
        product = (await products.Find("P")).Value.Value;
        writes.BeforeCreate = async () => Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        await host.Call("create_draft_user_need", new { productId = "P", id = "race-create", title = "Denied", statement = "Denied" }, true);
        Assert.DoesNotContain((await store.List(product.InternalId)).Value, n => n.Need.Id == "race-create");
        Assert.DoesNotContain(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()), e => e.Actor == new AuditActor.Identified("admin"));
    }

    [PostgresFact]
    public async Task McpCreateAndEditAuditFailuresAreAtomicAndBothUncertainOutcomesRequireRetrievalWithoutRetry()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var writes = new McpWriteHook(store);
        await using var host = NeedMcpHost(products, store, writes);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var original = (await store.Create(Need.Create(product.InternalId, "existing", "Before", "Before").Value, new AuditActor.Identified("seed"), DateTimeOffset.UtcNow)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("CREATE FUNCTION retravion_audit.reject_mcp() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-submitted-provider-detail' USING ERRCODE = '42501'; END $$; CREATE TRIGGER reject_mcp BEFORE INSERT ON retravion_audit.event FOR EACH ROW EXECUTE FUNCTION retravion_audit.reject_mcp();", connection)) await inject.ExecuteNonQueryAsync();
        await host.Call("create_draft_user_need", new { productId = "P", id = "blocked", title = "Blocked", statement = "Blocked" }, true);
        await host.Call("edit_draft_user_need", new { internalId = original.Need.InternalId, expectedRevision = original.Revision, id = "existing", title = "Blocked", statement = "Blocked" }, true);
        Assert.Equal(original, (await store.Find(original.Need.InternalId)).Value.Value);
        Assert.Single((await store.List(product.InternalId)).Value);
        await using (var remove = new NpgsqlCommand("DROP TRIGGER reject_mcp ON retravion_audit.event", connection)) await remove.ExecuteNonQueryAsync();
        foreach (var commits in new[] { false, true })
        {
            var attempts = 0;
            writes.Store = new PostgresUserNeedStore(database.ConnectionString, async (transaction, token) =>
            {
                attempts++;
                if (commits) await transaction.CommitAsync(token);
                throw new IOException("private-provider-detail");
            });
            var id = "uncertain-" + commits;
            var create = await host.Call("create_draft_user_need", new { productId = "P", id, title = "Uncertain", statement = "Uncertain" }, true);
            Assert.Contains("could not be confirmed", create.GetString());
            Assert.Contains("Retrieve", create.GetString());
            Assert.DoesNotContain("private", create.GetString());
            Assert.Equal(1, attempts);
            var listed = await host.Call("list_user_needs", new { productId = "P" });
            Assert.Equal(commits, listed.EnumerateArray().Any(n => n.GetProperty("id").GetString() == id));
            var before = (await store.Find(original.Need.InternalId)).Value.Value;
            var edit = await host.Call("edit_draft_user_need", new { internalId = before.Need.InternalId, expectedRevision = before.Revision, id = "existing", title = id, statement = id }, true);
            Assert.Contains("could not be confirmed", edit.GetString());
            Assert.Contains("Retrieve", edit.GetString());
            Assert.Equal(2, attempts);
            Assert.Equal(commits ? id : before.Need.Title, (await store.Find(original.Need.InternalId)).Value.Value.Need.Title);
        }
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.Actor == new AuditActor.Identified("admin")).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Single(events, e => e.Action == "need.create"); Assert.Single(events, e => e.Action == "need.edit");
    }

    private static McpTestHost NeedMcpHost(IProductStore products, IUserNeedStore reads, IUserNeedChangeTransaction writes) => new(services =>
    {
        services.AddSingleton(products);
        services.AddSingleton(reads);
        services.AddSingleton(writes);
    });

    private sealed class McpReadHook(IUserNeedStore store) : IUserNeedStore
    {
        internal Func<Task>? AfterFind;
        public Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(Guid product, CancellationToken token = default) => store.List(product, token);
        public Task<Result<IReadOnlyList<UserNeedReview>>> History(Guid id, CancellationToken token = default) => store.History(id, token);
        public async Task<Result<Maybe<UserNeedSnapshot>>> Find(Guid id, CancellationToken token = default)
        {
            var result = await store.Find(id, token);
            var hook = Interlocked.Exchange(ref AfterFind, null);
            if (hook is not null) await hook();
            return result;
        }
    }
    private sealed class McpWriteHook(PostgresUserNeedStore store) : IUserNeedChangeTransaction
    {
        internal PostgresUserNeedStore Store = store;
        internal Func<Task>? BeforeCreate;
        public async Task<Result<UserNeedSnapshot>> Create(Need need, AuditActor actor, DateTimeOffset time, CancellationToken token = default)
        {
            var hook = Interlocked.Exchange(ref BeforeCreate, null);
            if (hook is not null) await hook();
            return await Store.Create(need, actor, time, token);
        }
        public Task<Result<UserNeedSnapshot>> Update(UserNeedSnapshot expected, Need replacement, AuditActor actor, DateTimeOffset time,
            string? decision = null, string? comment = null, CancellationToken token = default) => Store.Update(expected, replacement, actor, time, decision, comment, token);
    }
}
