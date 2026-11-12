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
using Retravion.Application.Requirement;
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
using RequirementListPage = Retravion.Web.Features.Requirement.RequirementList;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static async Task<RequirementSnapshot> ApprovedRequirement(PostgresRequirementStore store, Guid product, string id)
    {
        var draft = (await store.Create(RequirementState.Create(product, id, "Original title", "# Original", RequirementType.Safety, "Original rationale").Value, RequirementActor, RequirementTime)).Value;
        var review = (await store.Update(draft, draft.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        return (await store.Update(review, review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
    }

    [PostgresFact]
    public async Task RequirementVersionsPreserveSourcesAndRejectStaleOlderAndParallelWorkingVersions()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var first = await ApprovedRequirement(store, product.InternalId, "ID");
        var reopened = (await store.Update(first, first.Requirement.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction")).Value;
        Assert.Equal(first.Requirement.VersionId, reopened.Requirement.VersionId); Assert.Equal(1, reopened.Requirement.VersionNumber);
        Assert.Null(reopened.LatestApprovedVersionNumber);
        Assert.True(reopened.Requirement.Edit("Renamed", "Title", "Statement", RequirementType.System, null).IsFailure);
        var review = (await store.Update(reopened, reopened.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        first = (await store.Update(review, review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        Assert.True((await store.CreateSuccessor(first, " ", RequirementActor, RequirementTime)).IsFailure);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.CreateSuccessor(first, "Next", RequirementActor, RequirementTime)));
        var second = Assert.Single(attempts, r => r.IsSuccess).Value;
        Assert.Equal(3, attempts.Count(r => r.IsFailure));
        Assert.Equal(2, second.Requirement.VersionNumber); Assert.NotEqual(first.Requirement.VersionId, second.Requirement.VersionId);
        Assert.Equal(first.Requirement.VersionId, second.Requirement.SourceVersionId); Assert.Equal("Next", second.Requirement.CreationReason);
        Assert.Equal(first.Requirement.Type, second.Requirement.Type); Assert.Equal(first.Requirement.Rationale, second.Requirement.Rationale);
        Assert.Equal(1, second.LatestApprovedVersionNumber);
        Assert.Equal(second, Assert.Single((await store.List(product.InternalId)).Value));
        Assert.Equal(second, (await store.Find(first.Requirement.InternalId)).Value.Value);
        Assert.Equal(first.Requirement, (await store.Find(first.Requirement.InternalId, versionId: first.Requirement.VersionId)).Value.Value.Requirement);
        Assert.True((await store.Find(first.Requirement.InternalId, versionId: Guid.NewGuid())).Value.HasNoValue);
        var excludedBaseline = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        Assert.True((await store.Update(first, first.Requirement.Reopen("Older").Value, RequirementActor, RequirementTime, "Reopened", "Older")).IsFailure);
        Assert.True((await store.CreateSuccessor(first, "Another", RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.CreateSuccessor(second, "Another", RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Update(second, second.Requirement, RequirementActor, RequirementTime)).IsSuccess);
        Assert.Equal(excludedBaseline, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        var edited = (await store.Update(second, second.Requirement.Edit("ID", "New title", "New statement", RequirementType.Software, null).Value, RequirementActor, RequirementTime)).Value;
        Assert.Equal(second.Requirement.VersionId, edited.Requirement.VersionId);
        Assert.Null(edited.Requirement.Rationale); Assert.Equal(RequirementType.Software, edited.Requirement.Type);
        Assert.Equal(2, (await store.Versions(first.Requirement.InternalId)).Value.Count);
        review = (await store.Update(edited, edited.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        Assert.True((await store.CreateSuccessor(first, "Another", RequirementActor, RequirementTime)).IsFailure);
        var approved = (await store.Update(review, review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        Assert.True((await store.Update(first, first.Requirement.Reopen("Older").Value, RequirementActor, RequirementTime, "Reopened", "Older")).IsFailure);
        var races = await Task.WhenAll(store.CreateSuccessor(approved, "v3", RequirementActor, RequirementTime),
            store.Update(approved, approved.Requirement.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction"));
        Assert.Single(races, r => r.IsSuccess);
        Assert.Single((await store.Versions(first.Requirement.InternalId)).Value, v => v.Requirement.Status != RequirementStatus.Approved);
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        var successor = Assert.Single(events, e => e.Action == "requirement.version.create" && e.RequirementChange!.VersionNumber == 2).RequirementChange!;
        Assert.Equal(first.Requirement.VersionId, successor.SourceVersionId); Assert.Equal(second.Requirement.VersionId, successor.VersionId);
        Assert.Equal("Next", successor.Reason); Assert.Equal(successor.Before, successor.After with { Status = "Approved" });
        var history = (await store.History(first.Requirement.InternalId)).Value;
        Assert.Contains(history, h => h.Decision == "Reopened" && h.Comment == "Correction" && h.VersionId == first.Requirement.VersionId);
        Assert.All(history, h => { Assert.NotNull(h.VersionId); Assert.NotNull(h.VersionNumber); Assert.Equal("ID", h.Id); });
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        var current = (await store.Find(first.Requirement.InternalId)).Value.Value;
        Assert.True((await store.CreateSuccessor(approved, "Retired", RequirementActor, RequirementTime)).IsFailure);
        Assert.True((await store.Update(approved, approved.Requirement.Reopen("Retired").Value, RequirementActor, RequirementTime, "Reopened", "Retired")).IsFailure);
        Assert.Equal(current, (await store.Find(first.Requirement.InternalId)).Value.Value);
        Assert.Equal(history, (await store.History(first.Requirement.InternalId)).Value);
        Assert.Equal(first.Requirement, (await store.Find(first.Requirement.InternalId, versionId: first.Requirement.VersionId)).Value.Value.Requirement);
    }

    [PostgresFact]
    public async Task RequirementCreationRejectsVersionIdentityOwnedByAnotherEntityWithoutChangingAnyEvidence()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var otherProduct = (await new CreateProduct(products).Execute("Other", "Other Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var original = await ApprovedRequirement(store, product.InternalId, "Original");
        var history = (await store.History(original.Requirement.InternalId)).Value;
        var audit = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        var collisionId = Guid.NewGuid();
        foreach (var owner in new[] { product.InternalId, otherProduct.InternalId })
        {
            var collision = RequirementState.Restore(collisionId, owner, "Collision", "Overwrite", "Overwrite", RequirementType.Hardware, null,
                RequirementStatus.Draft, original.Requirement.VersionId).Value;
            var rejected = await store.Create(collision, RequirementActor, RequirementTime);
            Assert.True(rejected.IsFailure);
            Assert.True((await store.Find(collisionId)).Value.HasNoValue);
            Assert.Empty((await store.Versions(collisionId)).Value);
            Assert.Equal(original, (await store.Find(original.Requirement.InternalId)).Value.Value);
            Assert.Equal(original, (await store.Find(original.Requirement.InternalId, versionId: original.Requirement.VersionId)).Value.Value);
            Assert.Equal(history, (await store.History(original.Requirement.InternalId)).Value);
            Assert.Equal(audit, await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()));
        }
        Assert.Single((await store.List(product.InternalId)).Value);
        Assert.Empty((await store.List(otherProduct.InternalId)).Value);
        // Rejection rolled back the entity/ID reservation as well as all history/audit writes.
        var valid = RequirementState.Restore(collisionId, product.InternalId, "Collision", "Valid", "Valid", RequirementType.System, null,
            RequirementStatus.Draft, Guid.NewGuid()).Value;
        Assert.True((await store.Create(valid, RequirementActor, RequirementTime)).IsSuccess);
    }

    [PostgresFact]
    public async Task RequirementMigrationPreservesEveryCurrentStatusAndLegacySnapshotWithoutInventedVersionAttribution()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var setup = new NpgsqlCommand("""
            CREATE SCHEMA retravion_requirement;
            CREATE TABLE retravion_requirement.requirement (internal_id uuid PRIMARY KEY,product_internal_id uuid NOT NULL,
                id text NOT NULL,title text NOT NULL,statement text NOT NULL,type integer NOT NULL,rationale text,status integer NOT NULL,revision bigint NOT NULL);
            CREATE TABLE retravion_requirement.review (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,requirement_internal_id uuid NOT NULL,
                actor_identifier text NOT NULL,occurred_at_utc_ticks bigint NOT NULL,decision text NOT NULL,comment text,manual_id text NOT NULL,
                title text NOT NULL,statement text NOT NULL,type integer NOT NULL,rationale text);
            """, connection)) await setup.ExecuteNonQueryAsync();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        for (var status = 0; status < 3; status++)
        {
            await using var insert = new NpgsqlCommand("WITH inserted AS (INSERT INTO retravion_requirement.requirement VALUES ($1,$2,$3,'Current','Current statement',3,'Current rationale',$4,7) RETURNING internal_id) INSERT INTO retravion_requirement.review (requirement_internal_id,actor_identifier,occurred_at_utc_ticks,decision,comment,manual_id,title,statement,type,rationale) SELECT internal_id,'legacy',0,'Approved','Old comment','Old ID','Old title','Old statement',1,'Old rationale' FROM inserted", connection);
            insert.Parameters.AddWithValue(ids[status]); insert.Parameters.AddWithValue(product.InternalId); insert.Parameters.AddWithValue("ID-" + status); insert.Parameters.AddWithValue(status);
            await insert.ExecuteNonQueryAsync();
        }
        var store = new PostgresRequirementStore(database.ConnectionString);
        var beforeEvents = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        foreach (var id in ids)
        {
            var snapshot = (await store.Find(id)).Value.Value;
            Assert.Equal(id, snapshot.Requirement.InternalId); Assert.Equal(product.InternalId, snapshot.Requirement.ProductInternalId);
            Assert.Equal(1, snapshot.Requirement.VersionNumber); Assert.NotEqual(id, snapshot.Requirement.VersionId); Assert.Equal(7, snapshot.Revision);
            Assert.Equal(Array.IndexOf(ids, id), (int)snapshot.Requirement.Status);
            Assert.Equal("Current", snapshot.Requirement.Title); Assert.Equal("Current statement", snapshot.Requirement.Statement);
            Assert.Equal(RequirementType.Safety, snapshot.Requirement.Type); Assert.Equal("Current rationale", snapshot.Requirement.Rationale);
            Assert.True(snapshot.Requirement.HasBeenApproved); Assert.Single((await store.Versions(id)).Value);
            var old = Assert.Single((await store.History(id)).Value);
            Assert.Null(old.VersionId); Assert.Null(old.VersionNumber); Assert.Null(old.SourceVersionId);
            Assert.Equal("Old ID", old.Id); Assert.Equal("Old title", old.Title); Assert.Equal("Old statement", old.Statement);
            Assert.Equal(RequirementType.Software, old.Type); Assert.Equal("Old rationale", old.Rationale); Assert.Equal("Old comment", old.Comment);
            Assert.Equal(snapshot, (await new PostgresRequirementStore(database.ConnectionString).Find(id)).Value.Value);
        }
        Assert.Equal(beforeEvents, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task RequirementSuccessorAndReopeningRollbackOnBusinessVersionHistoryAndAuditFailures()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var first = await ApprovedRequirement(store, product.InternalId, "ID");
        var history = (await store.History(first.Requirement.InternalId)).Value;
        var count = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var function = new NpgsqlCommand("CREATE FUNCTION public.reject_requirement_version() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$", connection)) await function.ExecuteNonQueryAsync();
        foreach (var table in new[] { "retravion_requirement.requirement", "retravion_requirement.requirement_version", "retravion_requirement.review", "retravion_audit.event" })
        {
            var versionTable = table == "retravion_requirement.requirement_version";
            var triggerSql = versionTable ? """
                CREATE TRIGGER reject_requirement_version BEFORE INSERT ON retravion_requirement.requirement_version
                    FOR EACH ROW WHEN (NEW.version_number > 1) EXECUTE FUNCTION public.reject_requirement_version();
                CREATE TRIGGER reject_requirement_version_update BEFORE UPDATE ON retravion_requirement.requirement_version
                    FOR EACH ROW EXECUTE FUNCTION public.reject_requirement_version();
                CREATE SEQUENCE public.projection_attempt;
                CREATE FUNCTION public.mark_projection_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN PERFORM nextval('public.projection_attempt'); RETURN NEW; END $$;
                CREATE TRIGGER mark_projection_attempt AFTER UPDATE ON retravion_requirement.requirement
                    FOR EACH ROW EXECUTE FUNCTION public.mark_projection_attempt();
                """ : $"CREATE TRIGGER reject_requirement_version BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_requirement_version()";
            await using (var trigger = new NpgsqlCommand(triggerSql, connection)) await trigger.ExecuteNonQueryAsync();
            Assert.True((await store.CreateSuccessor(first, "Next", RequirementActor, RequirementTime)).IsFailure);
            Assert.True((await store.Update(first, first.Requirement.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction")).IsFailure);
            if (versionTable)
            {
                // Sequence increments survive rollback: both failures must occur after the business UPDATE,
                // not during setup's INSERT ... ON CONFLICT DO NOTHING backfill.
                await using var reachedMutation = new NpgsqlCommand("SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.projection_attempt", connection);
                Assert.Equal(2L, await reachedMutation.ExecuteScalarAsync());
                await using var removeMarkers = new NpgsqlCommand("DROP TRIGGER mark_projection_attempt ON retravion_requirement.requirement; DROP TRIGGER reject_requirement_version_update ON retravion_requirement.requirement_version", connection);
                await removeMarkers.ExecuteNonQueryAsync();
            }
            await using (var drop = new NpgsqlCommand($"DROP TRIGGER reject_requirement_version ON {table}", connection)) await drop.ExecuteNonQueryAsync();
            Assert.Equal(first, (await store.Find(first.Requirement.InternalId)).Value.Value);
            Assert.Single((await store.Versions(first.Requirement.InternalId)).Value);
            Assert.Equal(history, (await store.History(first.Requirement.InternalId)).Value);
            Assert.Equal(count, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        }
    }

    [PostgresFact]
    public async Task RequirementVersionMutationsReportBothUncertainOutcomesWithoutRetry()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        foreach (var successor in new[] { false, true })
        foreach (var commits in new[] { false, true })
        {
            var first = await ApprovedRequirement(store, product.InternalId, $"{successor}-{commits}");
            var calls = 0;
            var uncertain = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
            { calls++; if (commits) await transaction.CommitAsync(token); throw new IOException("private"); });
            var result = successor ? await uncertain.CreateSuccessor(first, "Next", RequirementActor, RequirementTime) :
                await uncertain.Update(first, first.Requirement.Reopen("Correction").Value, RequirementActor, RequirementTime, "Reopened", "Correction");
            Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("Retrieve", result.Error);
            Assert.DoesNotContain("private", result.Error); Assert.Equal(1, calls);
            var current = (await store.Find(first.Requirement.InternalId)).Value.Value;
            Assert.Equal(commits ? RequirementStatus.Draft : RequirementStatus.Approved, current.Requirement.Status);
            Assert.Equal(commits && successor ? 2 : 1, (await store.Versions(first.Requirement.InternalId)).Value.Count);
            Assert.Equal(commits ? 3 : 2, (await store.History(first.Requirement.InternalId)).Value.Count);
            var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
            Assert.Equal(commits ? 1 : 0, events.Count(e => e.RequirementChange?.InternalId == first.Requirement.InternalId && e.Action == (successor ? "requirement.version.create" : "requirement.reopen")));
        }
    }

    [PostgresFact]
    public async Task RequirementVersionWebCallbacksAndMcpSelectExactVersionsWithoutLifecycleTools()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        var first = await ApprovedRequirement(store, product.InternalId, "ID");
        await using var host = RequirementMcpHost(products, store, store, database.ConnectionString);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var navigation = new RequirementNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RequirementOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductAreaOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = first.Requirement.InternalId });
            var actions = renderer.Components<MudButton>().Where(b => b.OnClick.HasDelegate).ToArray();
            Assert.Equal(2, actions.Length);
            Assert.All(actions, b => { Assert.Equal(Variant.Filled, b.Variant); Assert.Equal(Color.Primary, b.Color); });
            await renderer.Click("Reopen to Draft"); Assert.Contains("reason is required", renderer.Text());
            await renderer.Field("Change or reopening reason").ValueChanged.InvokeAsync("Correct wording");
            await renderer.Click("Reopen to Draft"); Assert.Contains("Draft", renderer.Text());
            await renderer.Click("Submit for review"); await renderer.Click("Approve");
            await renderer.Field("Change or reopening reason").ValueChanged.InvokeAsync("New behavior");
            await renderer.Click("Create successor Draft"); Assert.Contains("Successor creation reason", renderer.Text()); Assert.Contains("New behavior", renderer.Text());
            navigation.Go($"requirements/edit?id={first.Requirement.InternalId}");
            await renderer.Mount(typeof(RequirementEditor), new() { [nameof(RequirementEditor.Id)] = first.Requirement.InternalId });
            Assert.True(renderer.Field("Requirement ID").Disabled);
            await renderer.Field("Title").ValueChanged.InvokeAsync("Changed title");
            await renderer.Field("Rationale (optional, plain text)").ValueChanged.InvokeAsync("Changed rationale");
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Hardware);
            await renderer.Event("oninput", new ChangeEventArgs { Value = "Updated statement" }); await renderer.Event("onsubmit", EventArgs.Empty);
            var changed = (await store.Find(first.Requirement.InternalId)).Value.Value;
            Assert.Equal("Changed title", changed.Requirement.Title); Assert.Equal(RequirementType.Hardware, changed.Requirement.Type); Assert.Equal("Changed rationale", changed.Requirement.Rationale);
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = first.Requirement.InternalId, [nameof(RequirementDetails.VersionId)] = first.Requirement.VersionId });
            Assert.Contains("earlier version is read-only", renderer.Text()); Assert.Contains("Original title", renderer.Text()); Assert.Contains("Original rationale", renderer.Text());
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.OnClick.HasDelegate);
            await renderer.Mount(typeof(RequirementListPage), new() { [nameof(RequirementListPage.ProductId)] = "P" });
            Assert.Contains("Changed title", renderer.Text()); Assert.Contains("v2", renderer.Text()); Assert.Contains("v1", renderer.Text());
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Safety);
            Assert.Contains("No Requirements match", renderer.Text());
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Hardware);
            Assert.Contains("Changed title", renderer.Text());
            Assert.Single((await store.List(product.InternalId)).Value);
        });
        Assert.Empty(renderer.Errors);
        var exact = await host.Call("read_requirement", new { internalId = first.Requirement.InternalId, versionId = first.Requirement.VersionId });
        var value = exact.GetProperty("requirement");
        Assert.Equal("Original title", value.GetProperty("title").GetString()); Assert.Equal("Safety", value.GetProperty("type").GetString());
        Assert.Equal("Original rationale", value.GetProperty("rationale").GetString()); Assert.Equal(first.Requirement.VersionId, value.GetProperty("versionId").GetGuid());
        Assert.Equal(2, exact.GetProperty("versions").GetArrayLength());
        var current = (await host.Call("read_requirement", new { internalId = first.Requirement.InternalId })).GetProperty("requirement");
        Assert.Equal(2, current.GetProperty("versionNumber").GetInt32());
        await host.Call("read_requirement", new { internalId = first.Requirement.InternalId, versionId = Guid.NewGuid() }, true);
        await host.Call("edit_draft_requirement", new { internalId = first.Requirement.InternalId, expectedRevision = first.Revision, id = "ID", title = "Stale", statement = "Stale", type = "Hardware" }, true);
        await host.Call("edit_draft_requirement", new { internalId = first.Requirement.InternalId, expectedRevision = current.GetProperty("revision").GetInt64(), id = "Renamed", title = "Changed", statement = "Changed", type = "Hardware" }, true);
        var tools = (await host.Rpc("tools/list")).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "create_draft_requirement", "edit_draft_requirement", "edit_draft_requirement_classification", "list_requirements", "read_requirement" }, tools.Where(n => n!.Contains("requirement")).Order().ToArray());
    }
}
