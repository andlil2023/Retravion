using System.Security.Claims;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Requirement;
using Retravion.Application.TestCase;
using Retravion.Application.UserNeed;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Relationships;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static void RegisterRelationshipCases(IServiceCollection services, string connectionString)
    {
        var cases = new PostgresTestCaseStore(connectionString);
        services.AddSingleton<ITestCaseStore>(cases);
        services.AddSingleton<ITestCaseChangeTransaction>(cases);
    }

    private sealed class CaseRelationshipFixture : IAsyncDisposable
    {
        internal required Database Database;
        internal required PostgresProductStore Products;
        internal required PostgresRequirementStore Requirements;
        internal required PostgresTestCaseStore Cases;
        internal required CaseWriteHook Writes;
        internal required RelationshipData Data;
        internal required TestCaseSnapshot Row;
        internal required McpTestHost Host;
        internal required IServiceScope Scope;
        internal required ClaimsPrincipal Actor;
        internal RelationshipOperations Operations => Scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        internal PostgresAuditQuery Audit => new(Database.ConnectionString);

        internal static async Task<CaseRelationshipFixture> Create(bool linked = false)
        {
            var database = await Database.Create();
            var products = new PostgresProductStore(database.ConnectionString);
            var requirements = new PostgresRequirementStore(database.ConnectionString);
            var needs = new PostgresUserNeedStore(database.ConnectionString);
            var data = await RelationshipSetup(products, requirements, needs);
            var cases = new PostgresTestCaseStore(database.ConnectionString);
            var writes = new CaseWriteHook(cases);
            var row = (await cases.Create(data.Product.InternalId, CaseContent("Relationship procedure"), CaseActor, CaseTime)).Value;
            if (linked) row = (await cases.ChangeLink(row, "add", CaseEndpoint(data.Owner), null, CaseActor, CaseTime)).Value;
            var host = new McpTestHost(services =>
            {
                services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
                services.AddSingleton<IRequirementStore>(requirements);
                services.AddSingleton<IRequirementLinkStore>(requirements);
                services.AddSingleton<IRequirementChangeTransaction>(requirements);
                services.AddSingleton<IUserNeedStore>(needs);
                services.AddSingleton<IUserNeedChangeTransaction>(needs);
                services.AddSingleton<ITestCaseStore>(cases);
                services.AddSingleton<ITestCaseChangeTransaction>(writes);
                services.AddSingleton<IAuditSubmission>(new PostgresAuditSubmission(database.ConnectionString));
            });
            await host.Login(); await host.Replace(); var credential = await host.Authenticate();
            return new()
            {
                Database = database, Products = products, Requirements = requirements, Cases = cases, Writes = writes,
                Data = data, Row = row, Host = host, Scope = host.Factory.Services.CreateScope(),
                Actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential)
            };
        }

        internal async Task<RelationshipNode> Root(bool incoming = false) => (await Operations.Load(Actor,
            incoming ? "requirement" : "test-case", incoming ? Data.Owner.Requirement.InternalId : Row.TestCase.InternalId,
            incoming ? Data.Owner.Requirement.VersionId : Row.TestCase.VersionId)).Value.Center;

        public async ValueTask DisposeAsync()
        {
            Scope.Dispose(); await Host.DisposeAsync(); await Database.DisposeAsync();
        }
    }

    private static async Task PrepareCaseChange(RequirementRenderer renderer, RelationshipNode owner,
        RelationshipNode target, string operation, Guid replacement)
    {
        if (operation == "add")
        {
            await ManagementClick(renderer, $"Add outgoing link from {owner.Label}");
            await SelectExact(renderer, target.VersionId);
        }
        else
        {
            await ManagementClick(renderer, $"{(operation == "remove" ? "Remove" : "Retarget")} {RelationshipOperations.TestCaseRelationship}: {owner.Label} → {target.Label}");
            if (operation == "retarget")
                await renderer.Components<MudSelect<Guid?>>().Single(s => s.Label == "Replacement exact version").ValueChanged.InvokeAsync(replacement);
        }
    }

    private static string CaseConfirm(string operation) => operation switch
    {
        "add" => "Add link", "remove" => "Confirm remove link", _ => "Confirm retarget link"
    };

    [PostgresFact]
    public async Task TestCaseRelationshipCallbacksAddRetargetRemoveFromBothDirectionsAndViewsWithoutDuplicateAudit()
    {
        foreach (var table in new[] { false, true })
        foreach (var incoming in new[] { false, true })
        {
            await using var f = await CaseRelationshipFixture.Create();
            var root = await f.Root(incoming);
            var caseNode = await f.Root();
            var before = (await f.Audit.FindAsync(new())).Count;
            await using var services = ExplorationServices(f.Operations, f.Actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (table) await renderer.Click("Table view");
                if (incoming)
                {
                    await ManagementClick(renderer, $"Add incoming link to {root.Label}");
                    await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync(RelationshipOperations.TestCaseRelationship);
                    await SelectExact(renderer, caseNode.VersionId);
                }
                else
                {
                    Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.UserAttributes?.Values.Contains($"Add incoming link to {caseNode.Label}") == true);
                    await ManagementClick(renderer, $"Add outgoing link from {caseNode.Label}");
                    await SelectExact(renderer, f.Data.Owner.Requirement.VersionId);
                }
                Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
                await renderer.Click("Add link");
                Assert.Contains("Link added.", renderer.Text());
                var target = (await f.Operations.Load(f.Actor, "requirement", f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId)).Value.Center;
                await PrepareCaseChange(renderer, caseNode, target, "retarget", f.Data.Successor.Requirement.VersionId);
                Assert.Contains("old target Requirement Owner · v1 → new target Requirement Owner · v2", renderer.Text());
                Assert.Contains(renderer.Components<MudLink>(), link => link.Href == $"/requirements/details?id={target.InternalId}&version={f.Data.Successor.Requirement.VersionId}");
                Assert.Equal(CaseEndpoint(f.Data.Owner), Assert.Single((await f.Cases.Find(caseNode.InternalId)).Value.Value.TestCase.Links));
                await renderer.Click("Cancel retarget");
                Assert.Equal(before + 1, (await f.Audit.FindAsync(new())).Count);
                await PrepareCaseChange(renderer, caseNode, target, "retarget", f.Data.Successor.Requirement.VersionId);
                await renderer.Click("Confirm retarget link");
                Assert.Contains("Link retargeted.", renderer.Text());
                // The old exact Requirement center no longer has this edge: deliberately navigate to its owner.
                await renderer.Navigate(Parameters(caseNode));
                var replacement = (await f.Operations.Load(f.Actor, "requirement", target.InternalId, f.Data.Successor.Requirement.VersionId)).Value.Center;
                await PrepareCaseChange(renderer, caseNode, replacement, "remove", Guid.Empty);
                Assert.Contains($"Remove {RelationshipOperations.TestCaseRelationship}: {caseNode.Label} → {replacement.Label}?", renderer.Text());
                await renderer.Click("Cancel removal");
                Assert.Equal(before + 2, (await f.Audit.FindAsync(new())).Count);
                await PrepareCaseChange(renderer, caseNode, replacement, "remove", Guid.Empty);
                await renderer.Click("Confirm remove link");
                Assert.Contains("Link removed.", renderer.Text());
            });
            Assert.Empty(renderer.Errors);
            Assert.Empty((await f.Cases.Find(caseNode.InternalId)).Value.Value.TestCase.Links);
            var events = (await f.Audit.FindAsync(new())).Where(e => e.TestCaseChange?.Decision is "LinkAdded" or "LinkRetargeted" or "LinkRemoved").ToArray();
            Assert.Equal(before + 3, (await f.Audit.FindAsync(new())).Count);
            Assert.Equal(3, events.Length);
            Assert.All(events, e =>
            {
                Assert.Equal(new AuditActor.Identified("admin"), e.Actor);
                Assert.NotEqual(default, e.OccurredAt);
                Assert.Equal(AuditActionOutcome.Success, e.Outcome);
                Assert.Equal(new AuditTarget.Identified(caseNode.InternalId.ToString()), e.Target);
                Assert.Equal(caseNode.VersionId, e.TestCaseChange!.VersionId);
                Assert.Equal(1, e.TestCaseChange.VersionNumber);
            });
            var retarget = Assert.Single(events, e => e.Action == "test-case.link.retarget");
            Assert.Equal(CaseEndpoint(f.Data.Owner), Assert.Single(retarget.TestCaseChange!.Before!.Links!));
            Assert.Equal(CaseEndpoint(f.Data.Successor), Assert.Single(retarget.TestCaseChange.After.Links!));
            Assert.Equal(4, (await f.Cases.History(caseNode.InternalId)).Value.Count); // Creation + three changes.
        }
    }

    [PostgresFact]
    public async Task TestCaseRelationshipsKeepHistoricalFocusExploreChainsAndRejectUnavailableAccessWithoutReadEvents()
    {
        await using var f = await CaseRelationshipFixture.Create(linked: true);
        var approved = (await f.Cases.Review(f.Row, "InReview", null, CaseActor, CaseTime)).Value;
        approved = (await f.Cases.Review(approved, "Approved", null, CaseActor, CaseTime)).Value;
        var successor = (await f.Cases.CreateSuccessor(approved, "New procedure", CaseActor, CaseTime)).Value;
        var root = await f.Root();
        Assert.Equal(approved.TestCase.VersionId, root.VersionId);
        Assert.True(root.HasNewerVersion); Assert.False(root.Editable);
        var before = (await f.Audit.FindAsync(new())).Count;
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        foreach (var kind in new[] { "test-case", "requirement", "user-need" })
            Assert.True((await f.Operations.Load(anonymous, kind, root.InternalId, root.VersionId)).IsFailure);
        Assert.True((await f.Operations.Load(f.Actor, "test-case", root.InternalId, Guid.NewGuid())).IsFailure);
        await using var services = ExplorationServices(f.Operations, f.Actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
            Assert.Contains("Center: Test Case TC-001 · v1", renderer.Text());
            Assert.Contains("Newer version available", renderer.Text());
            Assert.True(ManagementButton(renderer, $"Add outgoing link from {root.Label}").Disabled);
            Assert.True(ManagementButton(renderer, $"Remove {RelationshipOperations.TestCaseRelationship}: {root.Label} → Requirement Owner · v1").Disabled);
            await ExplorationClick(renderer, "Expand Requirement Owner · v1");
            Assert.Contains("User Need N · v1", renderer.Text());
            Assert.Contains("Test Case TC-001 · v2", renderer.Text());
            await ExplorationClick(renderer, "Expand Test Case TC-001 · v2");
            await ExplorationClick(renderer, "Expand User Need N · v1");
            var exploration = (await f.Operations.Explore(f.Actor, root.Kind, root.InternalId, root.VersionId,
                [root.VersionId, f.Data.Owner.Requirement.VersionId, successor.TestCase.VersionId, successor.TestCase.VersionId, f.Data.Need.Need.VersionId])).Value;
            Assert.Equal(exploration.Neighborhoods.Count, exploration.Neighborhoods.Select(n => n.Center.VersionId).Distinct().Count());
            var graphLinks = renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray();
            await renderer.Click("Table view");
            Assert.Equal(graphLinks, renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray());
            Assert.Contains("Neighborhood not expanded; more relationships may exist", renderer.Text());
            await ExplorationClick(renderer, "Collapse Requirement Owner · v1");
            Assert.DoesNotContain("User Need N · v1", renderer.Text());
            Assert.Contains("Center: Test Case TC-001 · v1", renderer.Text());
            await f.Host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(f.Actor.FindFirstValue(SessionTicketStore.SessionClaim)!);
            await renderer.Click("Graph view");
            Assert.Contains("Access denied", renderer.Text());
            Assert.DoesNotContain("Relationship procedure", renderer.Text()); Assert.Empty(renderer.Components<MudLink>());
        });
        Assert.Empty(renderer.Errors);
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
        var denied = await f.Operations.Explore(f.Actor, root.Kind, root.InternalId, root.VersionId, []);
        Assert.True(denied.IsFailure);
        using var response = await f.Host.Browser.GetAsync($"/test-cases/details?id={root.InternalId}&version={root.VersionId}");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        // Revocation must not leak details through the HTTP path either.
        Assert.DoesNotContain("Relationship procedure", await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task TestCaseDetailsLinksUseSelectedExactVersionIncludingHistoricalVersions()
    {
        await using var f = await CaseRelationshipFixture.Create(linked: true);
        var approved = (await f.Cases.Review(f.Row, "InReview", null, CaseActor, CaseTime)).Value;
        approved = (await f.Cases.Review(approved, "Approved", null, CaseActor, CaseTime)).Value;
        var next = (await f.Cases.CreateSuccessor(approved, "Next", CaseActor, CaseTime)).Value;
        var before = (await f.Audit.FindAsync(new())).Count;
        foreach (var version in new[] { approved.TestCase.VersionId, next.TestCase.VersionId })
        {
            var html = await f.Host.Browser.GetStringAsync($"/test-cases/details?id={f.Row.TestCase.InternalId}&version={version}");
            Assert.Contains($"/products/links?kind=test-case&amp;id={f.Row.TestCase.InternalId}&amp;version={version}", html);
        }
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task TestCaseRelationshipCallbacksRejectStaleDuplicateRetiredAndRevokedWrites()
    {
        foreach (var table in new[] { false, true })
        {
            await using var f = await CaseRelationshipFixture.Create();
            var root = await f.Root();
            var target = (await f.Operations.Load(f.Actor, "requirement", f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId)).Value.Center;
            await using var services = ExplorationServices(f.Operations, f.Actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (table) await renderer.Click("Table view");
                await PrepareCaseChange(renderer, root, target, "add", Guid.Empty);
                var updated = (await f.Cases.Update(f.Row, CaseContent("Independent edit"), CaseActor, CaseTime)).Value;
                var before = (await f.Audit.FindAsync(new())).Count;
                await renderer.Click("Add link");
                Assert.DoesNotContain("Link added.", renderer.Text());
                Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
                Assert.Empty((await f.Cases.Find(root.InternalId)).Value.Value.TestCase.Links);
                await renderer.Click("Retrieve relationships before another action");
                await PrepareCaseChange(renderer, root, target, "add", Guid.Empty);
                await renderer.Click("Add link");
                await PrepareCaseChange(renderer, root, target, "add", Guid.Empty);
                before = (await f.Audit.FindAsync(new())).Count;
                await renderer.Click("Add link");
                Assert.DoesNotContain("Link added.", renderer.Text()); Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
                await renderer.Click("Retrieve relationships before another action");
                await PrepareCaseChange(renderer, root, target, "remove", Guid.Empty);
                Assert.True((await f.Products.Update(f.Data.Product, f.Data.Product.Retire().Value)).IsSuccess);
                await renderer.Click("Confirm remove link");
                Assert.DoesNotContain("Link removed.", renderer.Text()); Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
                await renderer.Click("Retrieve relationships before another action");
                Assert.Contains("Product retired", renderer.Text());
                Assert.True(ManagementButton(renderer, $"Add outgoing link from {root.Label}").Disabled);
                var retired = (await f.Products.List()).Value.Single(p => p.InternalId == f.Data.Product.InternalId);
                Assert.True((await f.Products.Update(retired, retired.Reactivate().Value)).IsSuccess);
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                await PrepareCaseChange(renderer, root, target, "remove", Guid.Empty);
                await f.Host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(f.Actor.FindFirstValue(SessionTicketStore.SessionClaim)!);
                await renderer.Click("Confirm remove link");
                Assert.Contains("Access denied", renderer.Text()); Assert.DoesNotContain("Link removed.", renderer.Text());
                var failure = Assert.Single(await f.Audit.FindAsync(new()), e => e.Outcome == AuditActionOutcome.Failure);
                Assert.Equal("test-case.link.remove", failure.Action); Assert.IsType<AuditTarget.Unknown>(failure.Target);
                Assert.Null(failure.TestCaseChange);
                Assert.Equal(before + 1, (await f.Audit.FindAsync(new())).Count);
                Assert.Single((await f.Cases.Find(root.InternalId)).Value.Value.TestCase.Links);
            });
            Assert.Empty(renderer.Errors);
        }
    }

    [PostgresFact]
    public async Task TestCaseRelationshipCallbacksRollbackEveryWriteOnAuditBusinessAndHistoryFailure()
    {
        foreach (var operation in new[] { "add", "remove", "retarget" })
        foreach (var table in new[] { "retravion_audit.event", "retravion_test_case.test_case_version", "retravion_test_case.history" })
        {
            await using var f = await CaseRelationshipFixture.Create(linked: operation != "add");
            var root = await f.Root();
            var target = (await f.Operations.Load(f.Actor, "requirement", f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId)).Value.Center;
            var before = (await f.Audit.FindAsync(new())).Count;
            var history = (await f.Cases.History(root.InternalId)).Value.Count;
            await using var services = ExplorationServices(f.Operations, f.Actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                await renderer.Click("Table view");
                await PrepareCaseChange(renderer, root, target, operation, f.Data.Successor.Requirement.VersionId);
                await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync();
                await using var trigger = new NpgsqlCommand($"""
                    CREATE FUNCTION public.reject_case_relationship() RETURNS trigger LANGUAGE plpgsql AS
                    $$ BEGIN RAISE EXCEPTION 'private-provider-details' USING ERRCODE='42501'; END $$;
                    CREATE TRIGGER reject_case_relationship BEFORE INSERT OR UPDATE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION public.reject_case_relationship();
                    """, connection);
                await trigger.ExecuteNonQueryAsync();
                await renderer.Click(CaseConfirm(operation));
                Assert.Contains("Retrieve relationships before another action", renderer.Text());
                Assert.DoesNotContain("Link added.", renderer.Text()); Assert.DoesNotContain("Link removed.", renderer.Text());
                Assert.DoesNotContain("Link retargeted.", renderer.Text()); Assert.DoesNotContain("private-provider-details", renderer.Text());
            });
            Assert.Empty(renderer.Errors);
            Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
            Assert.Equal(history, (await f.Cases.History(root.InternalId)).Value.Count);
            var stored = (await f.Cases.Find(root.InternalId)).Value.Value;
            Assert.Equal(f.Row.Revision, stored.Revision); Assert.True(f.Row.TestCase.SameState(stored.TestCase));
        }
    }

    [PostgresFact]
    public async Task TestCaseRelationshipCallbacksReportBothUncertainOutcomesWithoutRetryUntilRetrieval()
    {
        foreach (var operation in new[] { "add", "remove", "retarget" })
        foreach (var committed in new[] { false, true })
        foreach (var table in new[] { false, true })
        {
            await using var f = await CaseRelationshipFixture.Create(linked: operation != "add");
            var root = await f.Root();
            var target = (await f.Operations.Load(f.Actor, "requirement", f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId)).Value.Center;
            var before = (await f.Audit.FindAsync(new())).Count;
            var history = (await f.Cases.History(root.InternalId)).Value.Count;
            var calls = 0;
            await using var services = ExplorationServices(f.Operations, f.Actor);
            await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                await renderer.Mount(typeof(RelationshipLinks), Parameters(root));
                if (table) await renderer.Click("Table view");
                await PrepareCaseChange(renderer, root, target, operation, f.Data.Successor.Requirement.VersionId);
                var label = CaseConfirm(operation);
                var callback = ManagementButton(renderer, operation switch
                {
                    "add" => "Add selected link", "remove" => "Confirm selected removal", _ => "Confirm selected retarget"
                }).OnClick;
                // Use the visible button for the first invocation; retain its callback to prove direct re-entry cannot retry.
                f.Writes.Store = new PostgresTestCaseStore(f.Database.ConnectionString, async (transaction, token) =>
                {
                    calls++; if (committed) await transaction.CommitAsync(token);
                    throw new IOException("private-provider-details");
                });
                await renderer.Click(label);
                Assert.Contains("outcome could not be confirmed", renderer.Text());
                Assert.Contains("Retrieve", renderer.Text()); Assert.DoesNotContain("private-provider-details", renderer.Text());
                Assert.DoesNotContain("Link added.", renderer.Text()); Assert.DoesNotContain("Link removed.", renderer.Text()); Assert.DoesNotContain("Link retargeted.", renderer.Text());
                Assert.True(ManagementButton(renderer, $"Add outgoing link from {root.Label}").Disabled);
                await callback.InvokeAsync(new MouseEventArgs()); Assert.Equal(1, calls);
                f.Writes.Store = f.Cases;
                await renderer.Click("Retrieve relationships before another action");
                Assert.DoesNotContain("outcome could not be confirmed", renderer.Text()); Assert.Equal(1, calls);
            });
            Assert.Empty(renderer.Errors);
            Assert.Equal(before + (committed ? 1 : 0), (await f.Audit.FindAsync(new())).Count);
            Assert.Equal(history + (committed ? 1 : 0), (await f.Cases.History(root.InternalId)).Value.Count);
            var links = (await f.Cases.Find(root.InternalId)).Value.Value.TestCase.Links;
            if (operation == "remove" && committed || operation == "add" && !committed) Assert.Empty(links);
            else Assert.Equal(CaseEndpoint(operation == "retarget" && committed ? f.Data.Successor : f.Data.Owner), Assert.Single(links));
        }
    }
}
