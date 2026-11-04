using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.UserNeed;
using Retravion.Domain.Product;
using Retravion.Domain.UserNeed;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static readonly AuditActor VersionActor = new AuditActor.Identified("version-author");
    private static readonly DateTimeOffset VersionTime = DateTimeOffset.Parse("2026-02-03T04:05:06Z");
    private static async Task<UserNeedSnapshot> ApprovedNeed(PostgresUserNeedStore store, Guid product, string id)
    {
        var draft = (await store.Create(Need.Create(product, id, "Original title", "# Original").Value, VersionActor, VersionTime)).Value;
        var review = (await store.Update(draft, draft.Need.SubmitForReview().Value, VersionActor, VersionTime, "InReview")).Value;
        return (await store.Update(review, review.Need.Approve().Value, VersionActor, VersionTime, "Approved")).Value;
    }

    [PostgresFact]
    public async Task NeedVersionsPreserveSourcesUseExactReadsAndSerializeSuccessorsAndReopening()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var first = await ApprovedNeed(store, product.InternalId, "ID");
        var reopened = (await store.Update(first, first.Need.Reopen("Correction").Value, VersionActor, VersionTime, "Reopened", "Correction")).Value;
        Assert.Equal(first.Need.VersionId, reopened.Need.VersionId);
        Assert.Equal(1, reopened.Need.VersionNumber);
        Assert.Null(reopened.LatestApprovedVersionNumber);
        Assert.True(reopened.Need.Edit("different", "Changed", "Changed").IsFailure);
        var review = (await store.Update(reopened, reopened.Need.SubmitForReview().Value, VersionActor, VersionTime, "InReview")).Value;
        first = (await store.Update(review, review.Need.Approve().Value, VersionActor, VersionTime, "Approved")).Value;
        Assert.True((await store.CreateSuccessor(first, " ", VersionActor, VersionTime)).IsFailure);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.CreateSuccessor(first, "Next", VersionActor, VersionTime)));
        var second = Assert.Single(attempts, r => r.IsSuccess).Value;
        Assert.Equal(3, attempts.Count(r => r.IsFailure));
        Assert.Equal(2, second.Need.VersionNumber);
        Assert.Equal(first.Need.VersionId, second.Need.SourceVersionId);
        Assert.Equal("Next", second.Need.CreationReason);
        Assert.Equal(1, second.LatestApprovedVersionNumber);
        Assert.Equal(second, Assert.Single((await store.List(product.InternalId)).Value));
        Assert.Equal(second, (await store.Find(first.Need.InternalId)).Value.Value);
        Assert.Equal(first.Need, (await store.Find(first.Need.InternalId, versionId: first.Need.VersionId)).Value.Value.Need);
        Assert.True((await store.Find(first.Need.InternalId, versionId: Guid.NewGuid())).Value.HasNoValue);
        Assert.True((await store.Update(first, first.Need.Reopen("Older").Value, VersionActor, VersionTime, "Reopened", "Older")).IsFailure);
        Assert.True((await store.CreateSuccessor(first, "Another", VersionActor, VersionTime)).IsFailure);
        var edited = (await store.Update(second, second.Need.Edit("ID", "New title", "New statement").Value, VersionActor, VersionTime)).Value;
        Assert.Equal(second.Need.VersionId, edited.Need.VersionId);
        Assert.Equal(2, (await store.Versions(first.Need.InternalId)).Value.Count);
        Assert.True((await store.Update(edited, edited.Need, VersionActor, VersionTime)).IsSuccess);
        review = (await store.Update(edited, edited.Need.SubmitForReview().Value, VersionActor, VersionTime, "InReview")).Value;
        Assert.True((await store.CreateSuccessor(first, "Another", VersionActor, VersionTime)).IsFailure);
        var approved = (await store.Update(review, review.Need.Approve().Value, VersionActor, VersionTime, "Approved")).Value;
        Assert.True((await store.Update(first, first.Need.Reopen("Older").Value, VersionActor, VersionTime, "Reopened", "Older")).IsFailure);
        var races = await Task.WhenAll(
            store.CreateSuccessor(approved, "v3", VersionActor, VersionTime),
            store.Update(approved, approved.Need.Reopen("Correction").Value, VersionActor, VersionTime, "Reopened", "Correction"));
        Assert.Single(races, r => r.IsSuccess);
        Assert.Single((await store.Versions(first.Need.InternalId)).Value, v => v.Need.Status != UserNeedStatus.Approved);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.UserNeedChange is not null).ToArray();
        var successor = Assert.Single(events, e => e.Action == "need.version.create" && e.UserNeedChange!.VersionNumber == 2).UserNeedChange!;
        Assert.Equal(first.Need.VersionId, successor.SourceVersionId);
        Assert.Equal(second.Need.VersionId, successor.VersionId);
        Assert.Equal(2, successor.VersionNumber); Assert.Equal("Next", successor.Reason);
        Assert.Equal(successor.Before, successor.After with { Status = "Approved" });
        var history = (await store.History(first.Need.InternalId)).Value;
        Assert.Contains(history, h => h.Decision == "Reopened" && h.Comment == "Correction" && h.VersionId == first.Need.VersionId);
        Assert.All(history, h => { Assert.NotNull(h.VersionId); Assert.NotNull(h.VersionNumber); Assert.Equal("ID", h.ManualId); });
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        var current = (await store.Find(first.Need.InternalId)).Value.Value;
        Assert.True((await store.CreateSuccessor(approved, "retired", VersionActor, VersionTime)).IsFailure);
        Assert.True((await store.Update(approved, approved.Need.Reopen("retired").Value, VersionActor, VersionTime, "Reopened", "retired")).IsFailure);
        Assert.Equal(current, (await store.Find(first.Need.InternalId)).Value.Value);
        Assert.Equal(history, (await store.History(first.Need.InternalId)).Value);
        Assert.Equal(first.Need, (await store.Find(first.Need.InternalId, versionId: first.Need.VersionId)).Value.Value.Need);
    }

    [PostgresFact]
    public async Task NeedMigrationPreservesLegacyRecordsAndDoesNotInventVersionAttributionForOldDecisions()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE SCHEMA retravion_need;
            CREATE TABLE retravion_need.need (internal_id uuid PRIMARY KEY,product_internal_id uuid NOT NULL,
                id text NOT NULL,title text NOT NULL,statement text NOT NULL,status integer NOT NULL,revision bigint NOT NULL);
            CREATE TABLE retravion_need.review (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,need_internal_id uuid NOT NULL,
                actor_identifier text NOT NULL,occurred_at_utc_ticks bigint NOT NULL,decision text NOT NULL,comment text,title text NOT NULL,statement text NOT NULL);
            """, connection)) await setup.ExecuteNonQueryAsync();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        for (var status = 0; status < 3; status++)
        {
            await using var insert = new NpgsqlCommand("WITH inserted AS (INSERT INTO retravion_need.need VALUES ($1,$2,$3,'Current','Current statement',$4,7) RETURNING internal_id) INSERT INTO retravion_need.review (need_internal_id,actor_identifier,occurred_at_utc_ticks,decision,comment,title,statement) SELECT internal_id,'legacy',0,'Approved','Old comment','Old title','Old statement' FROM inserted", connection);
            insert.Parameters.AddWithValue(ids[status]); insert.Parameters.AddWithValue(product.InternalId); insert.Parameters.AddWithValue("ID-" + status); insert.Parameters.AddWithValue(status);
            await insert.ExecuteNonQueryAsync();
        }
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var beforeEvents = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        foreach (var id in ids)
        {
            var snapshot = (await store.Find(id)).Value.Value;
            Assert.Equal(id, snapshot.Need.InternalId); Assert.Equal(1, snapshot.Need.VersionNumber);
            Assert.NotEqual(id, snapshot.Need.VersionId); Assert.Equal(7, snapshot.Revision);
            Assert.Equal(Array.IndexOf(ids, id), (int)snapshot.Need.Status);
            Assert.Equal("Current", snapshot.Need.Title); Assert.Equal("Current statement", snapshot.Need.Statement);
            Assert.True(snapshot.Need.HasBeenApproved);
            Assert.Single((await store.Versions(id)).Value);
            var old = Assert.Single((await store.History(id)).Value);
            Assert.Null(old.VersionId); Assert.Null(old.VersionNumber); Assert.Null(old.ManualId);
            Assert.Equal("Old title", old.Title); Assert.Equal("Old statement", old.Statement); Assert.Equal("Old comment", old.Comment);
            Assert.Equal(snapshot, (await new PostgresUserNeedStore(database.ConnectionString).Find(id)).Value.Value);
        }
        Assert.Equal(beforeEvents, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task NeedSuccessorAndReopeningRollbackOnBusinessVersionHistoryAndAuditFailure()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var original = await ApprovedNeed(store, product.InternalId, "ID");
        var history = (await store.History(original.Need.InternalId)).Value;
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var function = new NpgsqlCommand("CREATE FUNCTION public.reject_version_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await function.ExecuteNonQueryAsync();
        foreach (var table in new[] { "retravion_need.need", "retravion_need.need_version", "retravion_need.review", "retravion_audit.event" })
        {
            await using (var trigger = new NpgsqlCommand($"CREATE TRIGGER reject_version_write BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_version_write()", connection)) await trigger.ExecuteNonQueryAsync();
            var successor = await store.CreateSuccessor(original, "Next", VersionActor, VersionTime);
            var reopen = await store.Update(original, original.Need.Reopen("Correction").Value, VersionActor, VersionTime, "Reopened", "Correction");
            Assert.True(successor.IsFailure); Assert.True(reopen.IsFailure);
            await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_version_write ON {table}", connection)) await drop.ExecuteNonQueryAsync();
            Assert.Equal(original, (await store.Find(original.Need.InternalId)).Value.Value);
            Assert.Single((await store.Versions(original.Need.InternalId)).Value);
            Assert.Equal(history, (await store.History(original.Need.InternalId)).Value);
            Assert.Equal(events, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        }
    }

    [PostgresFact]
    public async Task NeedVersionMutationsReportBothUncertainOutcomesWithoutRetry()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        foreach (var successor in new[] { false, true })
        foreach (var commits in new[] { false, true })
        {
            var first = await ApprovedNeed(store, product.InternalId, $"{successor}-{commits}");
            var calls = 0;
            var uncertain = new PostgresUserNeedStore(database.ConnectionString, async (transaction, token) =>
            { calls++; if (commits) await transaction.CommitAsync(token); throw new IOException("private"); });
            var result = successor ? await uncertain.CreateSuccessor(first, "Next", VersionActor, VersionTime) :
                await uncertain.Update(first, first.Need.Reopen("Correction").Value, VersionActor, VersionTime, "Reopened", "Correction");
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error);
            Assert.Contains("Retrieve", result.Error); Assert.DoesNotContain("private", result.Error); Assert.Equal(1, calls);
            var current = (await store.Find(first.Need.InternalId)).Value.Value;
            Assert.Equal(commits ? UserNeedStatus.Draft : UserNeedStatus.Approved, current.Need.Status);
            Assert.Equal(commits && successor ? 2 : 1, (await store.Versions(first.Need.InternalId)).Value.Count);
            Assert.Equal(commits ? 3 : 2, (await store.History(first.Need.InternalId)).Value.Count);
            var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
            Assert.Equal(commits ? 1 : 0, events.Count(e => e.UserNeedChange?.InternalId == first.Need.InternalId && e.Action == (successor ? "need.version.create" : "need.reopen")));
        }
    }

    [PostgresFact]
    public async Task NeedVersionWebCallbacksAndMcpExposeExactHistoryWithoutLifecycleTools()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresUserNeedStore(database.ConnectionString);
        var first = await ApprovedNeed(store, product.InternalId, "ID");
        await using var host = NeedMcpHost(products, store, store);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var navigation = new RequirementNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<UserNeedOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(UserNeedDetails), new() { [nameof(UserNeedDetails.Id)] = first.Need.InternalId });
            await renderer.Click("Reopen to Draft");
            Assert.Contains("reason is required", renderer.Text());
            await renderer.Field("Change or reopening reason").ValueChanged.InvokeAsync("Correct wording");
            await renderer.Click("Reopen to Draft");
            Assert.Contains("Draft", renderer.Text());
            await renderer.Click("Submit for review"); await renderer.Click("Approve");
            await renderer.Field("Change or reopening reason").ValueChanged.InvokeAsync("New behavior");
            await renderer.Click("Create successor Draft");
            Assert.Contains("Successor creation reason", renderer.Text());
            Assert.Contains("New behavior", renderer.Text());
            var current = (await store.Find(first.Need.InternalId)).Value.Value;
            navigation.Go($"user-needs/edit?id={first.Need.InternalId}");
            await renderer.Mount(typeof(UserNeedEditor), new() { [nameof(UserNeedEditor.Id)] = first.Need.InternalId });
            Assert.True(renderer.Field("User Need ID").Disabled);
            await renderer.Field("Title").ValueChanged.InvokeAsync("Changed title");
            await renderer.Event("oninput", new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Updated statement" });
            await renderer.Event("onsubmit", EventArgs.Empty);
            Assert.Equal("Changed title", (await store.Find(first.Need.InternalId)).Value.Value.Need.Title);
            await renderer.Mount(typeof(UserNeedDetails), new() { [nameof(UserNeedDetails.Id)] = first.Need.InternalId, [nameof(UserNeedDetails.VersionId)] = first.Need.VersionId });
            Assert.Contains("earlier version is read-only", renderer.Text());
            Assert.Contains("Original title", renderer.Text());
            await renderer.Mount(typeof(UserNeedList), new() { [nameof(UserNeedList.ProductId)] = "P" });
            Assert.Contains("Changed title", renderer.Text()); Assert.Contains("v2", renderer.Text()); Assert.Contains("v1", renderer.Text());
            Assert.Single((await store.List(product.InternalId)).Value);
            Assert.Equal(current.Need.VersionId, (await store.Find(first.Need.InternalId)).Value.Value.Need.VersionId);
        });
        Assert.Empty(renderer.Errors);
        var exact = await host.Call("read_user_need", new { internalId = first.Need.InternalId, versionId = first.Need.VersionId });
        Assert.Equal("Original title", exact.GetProperty("need").GetProperty("title").GetString());
        Assert.Equal(first.Need.VersionId, exact.GetProperty("need").GetProperty("versionId").GetGuid());
        Assert.Equal(2, exact.GetProperty("versions").GetArrayLength());
        var currentRead = await host.Call("read_user_need", new { internalId = first.Need.InternalId });
        Assert.Equal(2, currentRead.GetProperty("need").GetProperty("versionNumber").GetInt32());
        await host.Call("read_user_need", new { internalId = first.Need.InternalId, versionId = Guid.NewGuid() }, true);
        await host.Call("edit_draft_user_need", new { internalId = first.Need.InternalId, expectedRevision = first.Revision, id = "ID", title = "Stale", statement = "Stale" }, true);
        var tools = (await host.Rpc("tools/list")).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "create_draft_user_need", "edit_draft_user_need", "list_user_needs", "read_user_need" }, tools.Where(n => n!.Contains("user_need")).Order().ToArray());
    }
}
