using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using MudBlazor.Extensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Requirement;
using Retravion.Application.TestCase;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Requirement;
using Retravion.Web.Features.TestCase;
using Xunit;
using Requirement = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task AreaComponentsManageCodesAndLabelsAndFilterEachDisplayedExactVersion()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var products = new PostgresProductStore(db.ConnectionString); var requirements = new PostgresRequirementStore(db.ConnectionString);
        var cases = new PostgresTestCaseStore(db.ConnectionString); var areas = new PostgresProductAreaStore(db.ConnectionString);
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<IProductStore>(products); s.AddSingleton<IProductAreaStore>(areas);
            s.AddSingleton<IRequirementStore>(requirements); s.AddSingleton<IRequirementChangeTransaction>(requirements); s.AddSingleton<IRequirementLinkStore>(requirements);
            s.AddSingleton<ITestCaseStore>(cases); s.AddSingleton<ITestCaseChangeTransaction>(cases);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope(); var navigation = new RequirementNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductAreaOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RequirementOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<TestCaseOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(ProductAreas), new() { [nameof(ProductAreas.ProductId)] = "P" });
            await renderer.Field("Area code").ValueChanged.InvokeAsync("sw-9");
            await renderer.Field("Area label").ValueChanged.InvokeAsync("Software");
            await renderer.Click("Create area");
            Assert.Contains("SW-9", renderer.Text());
            var area = Assert.Single((await areas.Retrieve(p.InternalId)).Value.Areas);
            Assert.True(renderer.Field("Area code").Disabled);
            await renderer.Field("Area label").ValueChanged.InvokeAsync("Renamed label"); await renderer.Click("Save label");
            Assert.Equal("Renamed label", Assert.Single((await areas.Retrieve(p.InternalId)).Value.Areas).Area.Label);
            await renderer.Mount(typeof(RequirementEditor), new() { [nameof(RequirementEditor.ProductId)] = "P" });
            await renderer.Field("Requirement ID").ValueChanged.InvokeAsync("R-AREA"); await renderer.Field("Title").ValueChanged.InvokeAsync("Area title");
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Software);
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(area.Area.InternalId);
            await renderer.Event("oninput", new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Statement" });
            await renderer.Event("onsubmit", EventArgs.Empty);
            var original = Assert.Single((await requirements.List(p.InternalId)).Value);
            Assert.Equal(area.Area.InternalId, original.Requirement.AreaId);
            original = await ParentApprove(requirements, original);
            var next = (await requirements.CreateSuccessor(original, "Next", RevActor, RevTime)).Value;
            next = (await requirements.Update(next, next.Requirement.Classify(null).Value, RevActor, RevTime)).Value;
            var tc = (await cases.Create(p.InternalId, CaseContent("Classified case"), RevActor, RevTime, areaId: area.Area.InternalId)).Value;
            await cases.Create(p.InternalId, CaseContent("Unclassified case"), RevActor, RevTime);
            await renderer.Mount(typeof(ProductAreas), new() { [nameof(ProductAreas.ProductId)] = "P" });
            await renderer.Click("Manage"); await renderer.Click("Retire area…");
            Assert.True(Assert.Single((await areas.Retrieve(p.InternalId)).Value.Areas).Area.IsActive);
            await renderer.Click("Confirm retirement"); Assert.False(Assert.Single((await areas.Retrieve(p.InternalId)).Value.Areas).Area.IsActive);
            await renderer.Mount(typeof(Retravion.Web.Features.Requirement.RequirementList), new() { [nameof(Retravion.Web.Features.Requirement.RequirementList.ProductId)] = "P" });
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(area.Area.InternalId);
            Assert.Contains("No Requirements match", renderer.Text()); // Working v2 is unclassified, despite classified Approved v1.
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(Guid.Empty);
            Assert.Contains("R-AREA", renderer.Text());
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("missing"); Assert.Contains("No Requirements match", renderer.Text());
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = original.Requirement.InternalId, [nameof(RequirementDetails.VersionId)] = original.Requirement.VersionId });
            Assert.Contains("Area: SW-9", renderer.Text()); Assert.Contains("Renamed label", renderer.Text()); Assert.Contains("Retired", renderer.Text());
            await renderer.Mount(typeof(TestCaseList), new() { [nameof(TestCaseList.ProductId)] = "P" });
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(area.Area.InternalId);
            Assert.Contains("Classified case", renderer.Text()); Assert.DoesNotContain("Unclassified case", renderer.Text());
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(Guid.Empty);
            Assert.DoesNotContain("Classified case", renderer.Text()); Assert.Contains("Unclassified case", renderer.Text());
            await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(null);
            Assert.Contains("Classified case", renderer.Text()); Assert.Contains("Unclassified case", renderer.Text());
            navigation.Go($"test-cases/edit?id={tc.TestCase.InternalId}");
            await renderer.Mount(typeof(TestCaseEditor), new() { [nameof(TestCaseEditor.Id)] = tc.TestCase.InternalId });
            Assert.Equal(area.Area.InternalId, renderer.Components<MudSelect<Guid?>>().Single().GetState(x => x.Value));
            await renderer.Event("onsubmit", EventArgs.Empty); // Retaining a retired classification is not a new selection.
            Assert.Equal(area.Area.InternalId, (await cases.Find(tc.TestCase.InternalId)).Value.Value.TestCase.AreaId);
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    public async Task RequirementAreaEditorResolvesBothUncertainCreateOutcomesBeforeAnyRetry()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var areas = new PostgresProductAreaStore(db.ConnectionString); var area = await AreaCreate(areas, p.InternalId);
        var store = new PostgresRequirementStore(db.ConnectionString); var hook = new RequirementWriteHook(store);
        await using var host = RequirementMcpHost(new PostgresProductStore(db.ConnectionString), store, hook, db.ConnectionString);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope(); var navigation = new RequirementNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductAreaOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RequirementOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            foreach (var committed in new[] { false, true })
            {
                navigation.Go("requirements/create?product=P");
                await renderer.Mount(typeof(RequirementEditor), new() { [nameof(RequirementEditor.ProductId)] = "P" });
                await renderer.Field("Requirement ID").ValueChanged.InvokeAsync("R-" + committed);
                await renderer.Field("Title").ValueChanged.InvokeAsync("Entered title");
                await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.System);
                await renderer.Components<MudSelect<Guid?>>().Single().ValueChanged.InvokeAsync(area.Area.InternalId);
                await renderer.Event("oninput", new ChangeEventArgs { Value = "Entered statement" });
                var attempts = 0;
                hook.Store = new PostgresRequirementStore(db.ConnectionString, async (transaction, cancellation) =>
                { attempts++; if (committed) await transaction.CommitAsync(cancellation); throw new IOException("provider-private"); });
                await renderer.Event("onsubmit", EventArgs.Empty);
                Assert.Contains("outcome could not be confirmed", renderer.Text()); Assert.Equal(1, attempts);
                await renderer.Event("onsubmit", EventArgs.Empty); Assert.Equal(1, attempts);
                Assert.Equal("Entered title", renderer.Field("Title").GetState(x => x.Value));
                await renderer.Click("Retrieve operation outcome before retrying");
                hook.Store = store;
                if (!committed)
                {
                    Assert.Contains("No change committed", renderer.Text());
                    await renderer.Event("onsubmit", EventArgs.Empty);
                }
                Assert.Contains("requirements/details?", navigation.Uri);
                var saved = Assert.Single((await store.List(p.InternalId)).Value, r => r.Requirement.Id == "R-" + committed);
                Assert.Equal(area.Area.InternalId, saved.Requirement.AreaId);
                var creation = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Single(e => e.RequirementChange?.InternalId == saved.Requirement.InternalId);
                Assert.NotNull(creation.RequirementChange!.OperationId);
                Assert.True((await store.Outcome(p.InternalId, creation.RequirementChange.OperationId.Value)).Value.HasValue);
            }
        });
        Assert.Empty(renderer.Errors);
    }

    [PostgresFact]
    public async Task ExistingMcpEditsPreserveAreaWithoutNewToolArgumentsOrResponseFields()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var area = await AreaCreate(new PostgresProductAreaStore(db.ConnectionString), p.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var original = (await requirements.Create(Requirement.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime)).Value;
        await using var host = RequirementMcpHost(new PostgresProductStore(db.ConnectionString), requirements, requirements);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var tools = await host.Rpc("tools/list");
        foreach (var tool in tools.GetProperty("tools").EnumerateArray())
        {
            // Revision reads now describe frozen area evidence, but no tool accepts area authoring.
            Assert.DoesNotContain("area", tool.GetProperty("inputSchema").GetRawText(), StringComparison.OrdinalIgnoreCase);
            if (tool.GetProperty("name").GetString() != "read_revision")
                Assert.DoesNotContain("area", tool.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var result = await host.Call("edit_draft_requirement", new { internalId = original.Requirement.InternalId, expectedRevision = original.Revision, id = "R", title = "Changed", statement = "Statement", type = "System" });
        Assert.DoesNotContain("areaId", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(area.Area.InternalId, (await requirements.Find(original.Requirement.InternalId)).Value.Value.Requirement.AreaId);
        var edit = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Single(e => e.Action == "requirement.edit");
        Assert.Equal(area.Area.InternalId, edit.RequirementChange!.Before!.Area!.Area!.InternalId);
        Assert.Equal(edit.RequirementChange.Before.Area, edit.RequirementChange.After.Area);
    }

    [PostgresFact]
    public async Task AreaAuthorizationDeniesBeforeValidationAndSanitizesEventsEvenWhenAuditFails()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var areas = new PostgresProductAreaStore(db.ConnectionString); var area = await AreaCreate(areas, p.InternalId);
        var audit = new AreaDeniedAudit(); var logger = new AreaDeniedLogger();
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<IProductAreaStore>(areas); s.AddSingleton<IAuditSubmission>(audit);
            s.AddSingleton<ILogger<ProductAreaOperations>>(logger);
        });
        using var scope = host.Factory.Services.CreateScope(); var operations = scope.ServiceProvider.GetRequiredService<ProductAreaOperations>();
        foreach (var principal in new[] { new ClaimsPrincipal(), new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "known-author")], "Cookies")) })
        foreach (var mode in new[] { "ok", "fail", "throw" })
        {
            audit.Mode = mode;
            foreach (var path in new[] { (Expected: (ProductAreaSnapshot?)null, Retire: false), (Expected: (ProductAreaSnapshot?)area, Retire: false), (Expected: (ProductAreaSnapshot?)area, Retire: true) })
            {
                var result = await operations.SaveArea(principal, Guid.NewGuid(), p.InternalId, path.Expected, "submitted-private-code", "submitted-private-label", path.Retire);
                Assert.Equal("Access denied.", result.Error);
            }
        }
        Assert.Equal(18, audit.Events.Count);
        Assert.Equal(9, audit.Events.Count(e => e.Actor is AuditActor.Unknown));
        Assert.Equal(9, audit.Events.Count(e => e.Actor == new AuditActor.Identified("known-author")));
        Assert.All(audit.Events, e => { Assert.IsType<AuditTarget.Unknown>(e.Target); Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.Null(e.ProductAreaChange); Assert.DoesNotContain("submitted", e.Action); });
        Assert.Equal(12, logger.Messages.Count); Assert.All(logger.Messages, m => { Assert.DoesNotContain("submitted", m); Assert.DoesNotContain("provider-private", m); });
        Assert.True((await operations.Areas(new ClaimsPrincipal(), p.InternalId, Guid.NewGuid())).IsFailure);
        Assert.Equal(18, audit.Events.Count); // Reads do not create mutation events.
        Assert.Single((await areas.Retrieve(p.InternalId)).Value.History);
        var calls = 0;
        Assert.True((await areas.Save(Guid.NewGuid(), area, area.Area.Rename("Denied late").Value, RevActor, RevTime, () => Task.FromResult(calls++ == 0))).IsFailure);
        Assert.Equal(area, Assert.Single((await areas.Retrieve(p.InternalId)).Value.Areas));
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        calls = 0;
        Assert.True((await requirements.Create(Requirement.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime, authorized: () => Task.FromResult(calls++ == 0))).IsFailure);
        calls = 0;
        Assert.True((await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: area.Area.InternalId, authorized: () => Task.FromResult(calls++ == 0))).IsFailure);
        Assert.Empty((await requirements.List(p.InternalId)).Value); Assert.Empty((await cases.List(p.InternalId)).Value);
    }

    [PostgresFact]
    public async Task ExistingCurrentVersionsMigrateAsUnclassifiedWithoutInventedHistory()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var r = await ParentDraft(requirements, p.InternalId, "Legacy"); var tc = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime)).Value;
        var history = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        await using (var legacy = new NpgsqlCommand("ALTER TABLE retravion_requirement.requirement DROP COLUMN area_id; ALTER TABLE retravion_requirement.requirement_version DROP COLUMN area_id; ALTER TABLE retravion_test_case.test_case DROP COLUMN area_id; ALTER TABLE retravion_test_case.test_case_version DROP COLUMN area_id", c)) await legacy.ExecuteNonQueryAsync();
        Assert.Null((await requirements.Find(r.Requirement.InternalId)).Value.Value.Requirement.AreaId);
        Assert.Null((await cases.Find(tc.TestCase.InternalId)).Value.Value.TestCase.AreaId);
        Assert.All((await requirements.Versions(r.Requirement.InternalId)).Value, v => Assert.Null(v.Requirement.AreaId));
        Assert.All((await cases.Versions(tc.TestCase.InternalId)).Value, v => Assert.Null(v.TestCase.AreaId));
        Assert.Equal(history, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
    }

    private sealed class AreaDeniedAudit : IAuditSubmission
    {
        internal string Mode = "ok";
        internal readonly List<AuditInformation> Events = [];
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken token = default)
        {
            if (!information.Action.StartsWith("product.area.", StringComparison.Ordinal)) return Task.FromResult(AuditSubmissionResult.Success);
            Events.Add(information);
            if (Mode == "throw") throw new IOException("provider-private submitted-private-label");
            return Task.FromResult(Mode == "fail" ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
        }
    }
    private sealed class AreaDeniedLogger : ILogger<ProductAreaOperations>
    {
        internal readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Assert.Null(exception); Messages.Add(formatter(state, exception)); }
    }
}
#pragma warning restore BL0006
