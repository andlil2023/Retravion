using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Revision;
using Retravion.Web.Features.Revision;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpRevisionMutationDenialsPrecedeValidationAndSanitizeAuditAndFailureLogs()
    {
        var audit = new CaseAuditRecorder(); var logger = new RevisionLogger(); var store = new RevisionGateStore();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IRevisionStore>(store); services.AddSingleton<IRevisionChangeTransaction>(store);
            services.AddSingleton<IAuditSubmission>(audit); services.AddSingleton<ILogger<RevisionOperations>>(logger);
        });
        var names = new[] { "create_draft_revision", "edit_draft_revision", "change_draft_revision_selection" };
        async Task Denied(string? origin = null)
        {
            foreach (var name in names)
            {
                using var response = await host.Send("tools/call", new { name, arguments = new { actor = "private", productId = "private" } }, origin);
                Assert.Equal(origin is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        await Denied();
        Assert.All(audit.Events, e => Assert.IsType<AuditActor.Unknown>(e.Actor));
        await host.Login(); await host.Replace(); await host.Authenticate();
        await Denied("https://evil.invalid");
        Assert.All(audit.Events.Skip(3), e => Assert.Equal(new AuditActor.Identified("admin"), e.Actor));
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RevisionOperations>();
        var credentials = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>();
        var oldPrincipal = credentials.Resolve(host.DeviceCode);
        await host.Authenticate(); // rotate; direct calls must also deny before touching storage
        Assert.True((await operations.PrepareMcp(oldPrincipal, null!, "private", "revision.create", false, Guid.Empty, 0, null, null, null, null, default)).IsFailure);
        using (var revoke = await host.Revoke()) Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        await Denied();
        foreach (var throws in new[] { false, true })
        {
            audit.Fail = true; audit.Throw = throws; await Denied();
        }
        Assert.Equal(6, logger.Messages.Count);
        Assert.All(logger.Messages, m => Assert.Equal("Unauthorized revision change audit submission failed.", m));
        Assert.All(audit.Events.Where(e => e.Action.StartsWith("revision.", StringComparison.Ordinal)), e =>
        {
            Assert.Contains(e.Action, new[] { "revision.create", "revision.edit", "revision.selection" });
            Assert.IsType<AuditTarget.Unknown>(e.Target); Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
            Assert.Null(e.RevisionChange); Assert.DoesNotContain("private", JsonSerializer.Serialize(e));
            Assert.NotEqual(default, e.OccurredAt);
        });
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task McpRevisionMutationSchemasRequireConfirmationConcurrencyAndExactEndpoints()
    {
        var store = new RevisionGateStore();
        await using var host = new McpTestHost(services =>
        { services.AddSingleton<IRevisionStore>(store); services.AddSingleton<IRevisionChangeTransaction>(store); });
        await host.Login(); await host.Replace(); await host.Authenticate();
        foreach (var name in new[] { "create_draft_revision", "edit_draft_revision", "change_draft_revision_selection" })
        foreach (var arguments in new object[] { new { }, new { productId = "P", confirmed = true, state = "Released" },
            new { productId = "P", confirmed = true, internalId = Guid.NewGuid(), expectedRevision = 0 },
            new { productId = "P", confirmed = "true", identifier = "R", description = "" } })
        {
            using var response = await host.Send("tools/call", new { name, arguments });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await host.Call("create_draft_revision", new { productId = "P", identifier = "R", description = "", confirmed = false }, true);
        await host.Call("edit_draft_revision", new { productId = "P", internalId = Guid.NewGuid(), expectedRevision = 1, identifier = "R", description = "", confirmed = false }, true);
        var endpoint = new { kind = "Requirement", internalId = Guid.NewGuid(), versionId = Guid.NewGuid(), versionNumber = 1 };
        await host.Call("change_draft_revision_selection", new { productId = "P", internalId = Guid.NewGuid(), expectedRevision = 1, operation = "add", target = endpoint, confirmed = false }, true);
        foreach (var target in new object[] { new { kind = "Requirement", internalId = Guid.NewGuid(), versionId = Guid.NewGuid() },
            new { kind = "Requirement", internalId = Guid.NewGuid(), versionId = Guid.NewGuid(), versionNumber = 0 },
            new { kind = "0", internalId = Guid.NewGuid(), versionId = Guid.NewGuid(), versionNumber = 1 },
            new { kind = "Requirement", internalId = Guid.NewGuid(), versionId = Guid.NewGuid(), versionNumber = 1, actor = "spoof" } })
        {
            using var response = await host.Send("tools/call", new { name = "change_draft_revision_selection", arguments = new
            { productId = "P", internalId = Guid.NewGuid(), expectedRevision = 1, operation = "add", target, confirmed = true } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(0, store.Calls);
    }
}

public sealed partial class PostgresProductTests
{
    private static object McpEndpoint(RevisionSelection e) => new { kind = e.Kind.ToString(), e.InternalId, e.VersionId, e.VersionNumber };

    [PostgresFact]
    public async Task McpRevisionPreparationPreservesExactVersionsAndReferencesWithAtomicAuditAndExcludedEvents()
    {
        await using var db = await Database.Create();
        var product = await AreaProduct(db.ConnectionString);
        var products = new PostgresProductStore(db.ConnectionString);
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var first = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "Selected"));
        var second = (await requirements.CreateSuccessor(first, "next", RevActor, RevTime)).Value;
        var e1 = new RevisionSelection(RevisionEntityKind.Requirement, first.Requirement.InternalId, first.Requirement.VersionId, 1);
        var e2 = e1 with { VersionId = second.Requirement.VersionId, VersionNumber = 2 };
        var store = new PostgresRevisionStore(db.ConnectionString);
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IProductStore>(products); services.AddSingleton<IRevisionStore>(store);
            services.AddSingleton<IRevisionChangeTransaction>(store);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var initial = await host.Call("create_draft_revision", new { productId = product.Id, identifier = "MCP", description = "Initial", confirmed = true });
        var id = initial.GetProperty("internalId").GetGuid(); Assert.Empty(initial.GetProperty("selections").EnumerateArray());
        object Metadata(long revision, string identifier = "Changed", bool confirmed = true) => new
        { productId = product.Id, internalId = id, expectedRevision = revision, identifier, description = "Metadata", confirmed };
        object Selection(long revision, string operation, RevisionSelection target, RevisionSelection? replacement = null) => new
        { productId = product.Id, internalId = id, expectedRevision = revision, operation, target = McpEndpoint(target), replacement = replacement is null ? null : McpEndpoint(replacement), confirmed = true };
        await host.Call("edit_draft_revision", Metadata(1));
        await host.Call("edit_draft_revision", Metadata(1), true); // stale
        await host.Call("edit_draft_revision", Metadata(2)); // no-op
        await host.Call("edit_draft_revision", Metadata(2, ""), true); // validation
        await host.Call("create_draft_revision", new { productId = product.Id, identifier = "Changed", description = "Duplicate", confirmed = true }, true);
        await host.Call("change_draft_revision_selection", Selection(2, "add", e1));
        var read = await host.Call("read_revision", new { productId = product.Id, internalId = id });
        Assert.Equal(e1.VersionId, read.GetProperty("revision").GetProperty("selections")[0].GetProperty("versionId").GetGuid());
        await host.Call("change_draft_revision_selection", Selection(3, "add", e1)); // no-op
        await host.Call("change_draft_revision_selection", Selection(3, "add", e2 with { VersionNumber = 9 }), true);
        await host.Call("change_draft_revision_selection", Selection(3, "replace", e1, e2));
        await host.Call("change_draft_revision_selection", Selection(4, "remove", e2));
        await host.Call("change_draft_revision_selection", Selection(5, "remove", e2)); // no-op
        var history = (await store.History(product.InternalId, id)).Value;
        Assert.Equal(new[] { "revision.create", "revision.edit", "revision.selection.add", "revision.selection.replace", "revision.selection.remove" }, history.Select(h => h.Action));
        Assert.Empty(history[0].Change.After!.Selections); Assert.Null(history[0].Change.Before);
        Assert.Equal("Initial", history[1].Change.Before!.Description); Assert.Equal("Metadata", history[1].Change.After!.Description);
        Assert.Equal(e1, Assert.Single(history[3].Change.Before!.Selections)); Assert.Equal(e2, Assert.Single(history[3].Change.After!.Selections));
        var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Where(e => e.RevisionChange?.InternalId == id).ToArray();
        Assert.Equal(5, events.Length); Assert.All(events, e =>
        {
            Assert.Equal(new AuditActor.Identified("admin"), e.Actor); Assert.Equal(AuditActionOutcome.Success, e.Outcome);
            Assert.Equal(new AuditTarget.Identified(id.ToString()), e.Target); Assert.Equal(product.InternalId, e.RevisionChange!.ProductInternalId);
            Assert.NotEqual(default, e.OccurredAt); Assert.NotEqual(Guid.Empty, e.RevisionChange.OperationId);
        });
        Assert.Equal(second, (await requirements.Find(first.Requirement.InternalId)).Value.Value);
        Assert.Equal(first, (await requirements.Find(first.Requirement.InternalId, versionId: first.Requirement.VersionId)).Value.Value);
        var graph = await PrepareRelease(db.ConnectionString);
        var released = (await ReleaseNow(store, await ReleasePreview(store, graph.Row))).Value;
        await host.Call("edit_draft_revision", new { productId = graph.Product.Id, internalId = released.Value.InternalId, expectedRevision = released.Revision, identifier = "No", description = "", confirmed = true }, true);
        await host.Call("change_draft_revision_selection", new { productId = graph.Product.Id, internalId = released.Value.InternalId, expectedRevision = released.Revision, operation = "add", target = McpEndpoint(e1), confirmed = true }, true);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        await host.Call("edit_draft_revision", Metadata(5, "Retired"), true);
        await host.Call("change_draft_revision_selection", Selection(5, "add", e1), true);
        await host.Call("create_draft_revision", new { productId = product.Id, identifier = "Retired", description = "", confirmed = true }, true);
        Assert.Equal(5, (await store.History(product.InternalId, id)).Value.Count);
    }

    [PostgresFact]
    public async Task McpRevisionWritesRecheckCredentialsAfterDatabaseWaitsAndRejectConcurrentStaleProposals()
    {
        await using var db = await Database.Create();
        var product = await AreaProduct(db.ConnectionString);
        var products = new PostgresProductStore(db.ConnectionString);
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var member = await ParentDraft(requirements, product.InternalId, "Member");
        var endpoint = new RevisionSelection(RevisionEntityKind.Requirement, member.Requirement.InternalId, member.Requirement.VersionId, 1);
        var store = new PostgresRevisionStore(db.ConnectionString);
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IProductStore>(products); services.AddSingleton<IRevisionStore>(store);
            services.AddSingleton<IRevisionChangeTransaction>(store);
            services.AddSingleton<IAuditSubmission>(new PostgresAuditSubmission(db.ConnectionString));
        });
        await host.Login(); await host.Replace();
        foreach (var action in new[] { "create", "edit", "add", "remove", "replace" })
        {
            await host.Authenticate();
            var credentials = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>();
            var principal = credentials.Resolve(host.DeviceCode);
            var row = await RevCreate(store, product.InternalId, Guid.NewGuid().ToString());
            if (action is "remove" or "replace") row = (await store.Select(Guid.NewGuid(), row, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
            var before = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            await using var blocker = new NpgsqlConnection(db.ConnectionString); await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var rowLock = new NpgsqlCommand("SELECT internal_id FROM retravion_product.product WHERE internal_id=$1 FOR UPDATE", blocker, transaction))
            { rowLock.Parameters.AddWithValue(product.InternalId); await rowLock.ExecuteScalarAsync(); }
            var mutation = action switch
            {
                "create" => host.Call("create_draft_revision", new { productId = product.Id, identifier = "Denied", description = "", confirmed = true }, true),
                "edit" => host.Call("edit_draft_revision", new { productId = product.Id, internalId = row.Value.InternalId, expectedRevision = row.Revision, identifier = "Denied", description = "", confirmed = true }, true),
                _ => host.Call("change_draft_revision_selection", new { productId = product.Id, internalId = row.Value.InternalId, expectedRevision = row.Revision, operation = action, target = McpEndpoint(endpoint), replacement = action == "replace" ? McpEndpoint(endpoint) : null, confirmed = true }, true)
            };
            try
            {
                await using var observer = new NpgsqlConnection(db.ConnectionString); await observer.OpenAsync();
                var waiting = false;
                for (var i = 0; i < 200 && !waiting; i++)
                {
                    await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%retravion_product.product%')", observer);
                    waiting = await check.ExecuteScalarAsync() is true;
                    if (!waiting) await Task.Delay(25);
                }
                Assert.True(waiting); Assert.False(mutation.IsCompleted);
                credentials.Revoke(principal);
            }
            finally { await transaction.RollbackAsync(); }
            Assert.Equal("Access denied.", (await mutation.WaitAsync(TimeSpan.FromSeconds(20))).GetString());
            var saved = (await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!;
            Assert.Equal(row.Revision, saved.Revision); Assert.True(row.Value.SameValues(saved.Value));
            Assert.Equal(action is "remove" or "replace" ? 2 : 1, (await store.History(product.InternalId, row.Value.InternalId)).Value.Count);
            var events = await new PostgresAuditQuery(db.ConnectionString).FindAsync(new());
            Assert.Equal(before + 1, events.Count);
            var denied = events.First(); Assert.Equal(AuditActionOutcome.Failure, denied.Outcome); Assert.IsType<AuditTarget.Unknown>(denied.Target); Assert.Null(denied.RevisionChange);
        }
        await host.Authenticate();
        var current = await RevCreate(store, product.InternalId, "Concurrent");
        var proposals = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => host.Rpc("tools/call", new
        {
            name = "edit_draft_revision", arguments = new { productId = product.Id, internalId = current.Value.InternalId, expectedRevision = current.Revision, identifier = "Proposal" + i, description = "", confirmed = true }
        })));
        Assert.Single(proposals, p => !p.TryGetProperty("isError", out var error) || !error.GetBoolean());
        Assert.Single(proposals, p => p.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal(2, (await store.History(product.InternalId, current.Value.InternalId)).Value.Count);
    }

    [PostgresFact]
    public async Task McpRevisionPreparationRollsBackAllWriteFailuresAndReportsBothUncertainOutcomesWithoutRetry()
    {
        await using var db = await Database.Create();
        var product = await AreaProduct(db.ConnectionString);
        var products = new PostgresProductStore(db.ConnectionString);
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var first = await ParentDraft(requirements, product.InternalId, "Target");
        var e1 = new RevisionSelection(RevisionEntityKind.Requirement, first.Requirement.InternalId, first.Requirement.VersionId, 1);
        var second = await ParentDraft(requirements, product.InternalId, "Replacement");
        var e2 = new RevisionSelection(RevisionEntityKind.Requirement, second.Requirement.InternalId, second.Requirement.VersionId, 1);
        var store = new PostgresRevisionStore(db.ConnectionString);
        var mode = "normal"; var attempts = 0;
        var injected = new PostgresRevisionStore(db.ConnectionString, async (t, token) =>
        {
            attempts++;
            if (mode is "normal" or "committed") await t.CommitAsync(token);
            if (mode != "normal") throw new OperationCanceledException("private provider details");
        });
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IProductStore>(products); services.AddSingleton<IRevisionStore>(store);
            services.AddSingleton<IRevisionChangeTransaction>(injected);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        foreach (var action in new[] { "create", "edit", "add", "remove", "replace" })
        foreach (var failure in new[] { "business", "history", "audit", "uncommitted", "committed" })
        {
            var row = await RevCreate(store, product.InternalId, Guid.NewGuid().ToString());
            if (action is "remove" or "replace") row = (await store.Select(Guid.NewGuid(), row, "add", e1, null, RevActor, RevTime, RevAllowed)).Value;
            var identifier = Guid.NewGuid().ToString();
            var table = failure switch { "business" => "retravion_revision.revision", "history" => "retravion_revision.history", "audit" => "retravion_audit.event", _ => null };
            if (table is not null)
            {
                await using var inject = new NpgsqlCommand($"""
                    CREATE OR REPLACE FUNCTION public.reject_mcp_revision() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private provider details' USING ERRCODE='42501'; END $$;
                    CREATE TRIGGER reject_mcp_revision BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_mcp_revision();
                    """, c);
                await inject.ExecuteNonQueryAsync();
            }
            mode = table is null ? failure : "normal";
            var before = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            attempts = 0;
            var error = action switch
            {
                "create" => await host.Call("create_draft_revision", new { productId = product.Id, identifier, description = "Attempt", confirmed = true }, true),
                "edit" => await host.Call("edit_draft_revision", new { productId = product.Id, internalId = row.Value.InternalId, expectedRevision = row.Revision, identifier, description = "Attempt", confirmed = true }, true),
                _ => await host.Call("change_draft_revision_selection", new { productId = product.Id, internalId = row.Value.InternalId, expectedRevision = row.Revision, operation = action, target = McpEndpoint(e1), replacement = action == "replace" ? McpEndpoint(e2) : null, confirmed = true }, true)
            };
            Assert.DoesNotContain("private", error.GetString());
            Assert.Equal(table is null ? 1 : 0, attempts);
            if (table is null)
            { Assert.Contains("could not be confirmed", error.GetString()); Assert.Contains("never retry automatically", error.GetString()); Assert.Contains("if ambiguous", error.GetString()); }
            else
            { await using var drop = new NpgsqlCommand($"DROP TRIGGER reject_mcp_revision ON {table}", c); await drop.ExecuteNonQueryAsync(); }
            var committed = failure == "committed";
            Assert.Equal(before + (committed ? 1 : 0), (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            if (action == "create")
            {
                var list = await host.Call("list_revisions", new { productId = product.Id });
                Assert.Equal(committed ? 1 : 0, list.EnumerateArray().Count(r => r.GetProperty("identifier").GetString() == identifier));
            }
            else
            {
                var retrieved = (await store.Retrieve(product.InternalId, row.Value.InternalId)).Value.Record!;
                Assert.Equal(row.Revision + (committed ? 1 : 0), retrieved.Revision);
                if (!committed) Assert.True(row.Value.SameValues(retrieved.Value));
                Assert.Equal((action is "remove" or "replace" ? 2 : 1) + (committed ? 1 : 0), (await store.History(product.InternalId, row.Value.InternalId)).Value.Count);
            }
        }
    }
}
