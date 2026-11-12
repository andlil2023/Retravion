using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.TestCase;
using Retravion.Domain.Product;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.TestCase;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpTestCaseSchemasAndStrictArgumentsRejectMalformedInputBeforeStorage()
    {
        var store = new CaseGateStore();
        await using var host = new McpTestHost(services =>
        { services.AddSingleton<ITestCaseStore>(store); services.AddSingleton<ITestCaseChangeTransaction>(store); });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var tools = (await host.Rpc("tools/list")).GetProperty("tools").EnumerateArray()
            .Where(t => t.GetProperty("name").GetString()!.EndsWith("test_case", StringComparison.Ordinal) || t.GetProperty("name").GetString() == "list_test_cases").ToArray();
        Assert.Equal(4, tools.Length);
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();
            var schema = tool.GetProperty("inputSchema");
            var required = schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order().ToArray();
            Assert.Equal(name switch
            {
                "list_test_cases" or "create_draft_test_case" => new[] { "productId" },
                "read_test_case" => new[] { "internalId" },
                _ => new[] { "description", "expectedRevision", "internalId", "preconditions", "steps", "title", "versionId" }
            }, required);
        }
        var valid = new Dictionary<string, object?>
        {
            ["internalId"] = Guid.NewGuid(), ["versionId"] = Guid.NewGuid(), ["expectedRevision"] = 1,
            ["title"] = "", ["description"] = "", ["preconditions"] = "", ["steps"] = Array.Empty<object>()
        };
        async Task Reject(string name, object arguments)
        {
            using var response = await host.Send("tools/call", new { name, arguments });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var key in valid.Keys)
        {
            var missing = new Dictionary<string, object?>(valid); missing.Remove(key); await Reject("edit_draft_test_case", missing);
            var nulled = new Dictionary<string, object?>(valid) { [key] = null }; await Reject("edit_draft_test_case", nulled);
        }
        foreach (var revision in new object[] { 0, -1, 1.5, "1", true })
            await Reject("edit_draft_test_case", new Dictionary<string, object?>(valid) { ["expectedRevision"] = revision });
        foreach (var steps in new object[] { new object?[] { null }, new[] { new { action = "x" } }, new[] { new { action = "x", expectedResult = (string?)null } }, new[] { new { action = "x", expectedResult = "y", number = 1 } }, new[] { new Dictionary<string, object> { ["Action"] = "x", ["expectedResult"] = "y" } }, "steps", new[] { 1 } })
            await Reject("create_draft_test_case", new { productId = "P", steps });
        foreach (var key in new[] { "actor", "status", "links", "areaId", "operationId", "reason", "sourceVersionId", "versionNumber" })
            await Reject("edit_draft_test_case", new Dictionary<string, object?>(valid) { [key] = "injected" });
        await Reject("read_test_case", new { internalId = "invalid" });
        await Reject("read_test_case", new { internalId = Guid.NewGuid(), versionId = "invalid" });
        await Reject("list_test_cases", new { productId = "P", filter = "Draft" });
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task McpTestCaseWritesQueuedBeforeRevocationAreDeniedWithoutLookingUpRecords()
    {
        var store = new CaseGateStore(); var audit = new CaseAuditRecorder();
        await using var host = new McpTestHost(services =>
        { services.AddSingleton<ITestCaseStore>(store); services.AddSingleton<ITestCaseChangeTransaction>(store); services.AddSingleton<IAuditSubmission>(audit); });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>().Resolve(token);
        var sessions = host.Factory.Services.GetRequiredService<SessionTicketStore>();
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<TestCaseOperations>();
        await sessions.MutationGate.WaitAsync();
        Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> create, edit;
        try
        {
            create = operations.CreateMcp(principal, host.Factory.Services.GetRequiredService<Retravion.Application.Product.IProductStore>(), "secret", null, null, null, null, default);
            edit = operations.EditMcp(principal, Guid.NewGuid(), Guid.NewGuid(), 1, "secret", "", "", [], default);
            Assert.False(create.IsCompleted); Assert.False(edit.IsCompleted);
            sessions.RevokeAccountWithinMutation("admin");
        }
        finally { sessions.MutationGate.Release(); }
        Assert.Equal("Access denied.", (await create).Error); Assert.Equal("Access denied.", (await edit).Error);
        Assert.Equal(0, store.Calls);
        Assert.Equal(2, audit.Events.Count(e => e.Action.StartsWith("test-case.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("rotate")]
    [InlineData("expire")]
    public async Task McpTestCaseQueuedWritesRevalidateCredentialsWhileBrowserSessionRemainsActive(string invalidation)
    {
        var store = new CaseGateStore(); var audit = new CaseAuditRecorder(); var clock = new McpClock();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<TimeProvider>(clock); services.AddSingleton<ITestCaseStore>(store);
            services.AddSingleton<ITestCaseChangeTransaction>(store); services.AddSingleton<IAuditSubmission>(audit);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var credentials = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>();
        var principal = credentials.Resolve(token);
        var sessions = host.Factory.Services.GetRequiredService<SessionTicketStore>();
        var session = principal.FindFirstValue(SessionTicketStore.SessionClaim)!;
        // Renew browser activity without renewing the MCP credential's fixed lifetime.
        clock.Now += TimeSpan.FromMinutes(29); Assert.True(sessions.RecordActivity(session));
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<TestCaseOperations>();
        await sessions.MutationGate.WaitAsync();
        Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> create, edit;
        try
        {
            create = operations.CreateMcp(principal, host.Factory.Services.GetRequiredService<Retravion.Application.Product.IProductStore>(), "secret", null, null, null, null, default);
            edit = operations.EditMcp(principal, Guid.NewGuid(), Guid.NewGuid(), 1, "secret", "", "", [], default);
            Assert.False(create.IsCompleted); Assert.False(edit.IsCompleted);
            if (invalidation == "revoke") credentials.Revoke(principal);
            else if (invalidation == "rotate")
                await host.Authenticate(); // Rotate through the real browser credential-issuance endpoint.
            else clock.Now += TimeSpan.FromMinutes(2);
            Assert.True(sessions.IsActive(session)); Assert.False(credentials.IsActive(principal));
        }
        finally { sessions.MutationGate.Release(); }
        Assert.Equal("Access denied.", (await create).Error); Assert.Equal("Access denied.", (await edit).Error);
        Assert.True((await operations.List(principal, Guid.NewGuid())).IsFailure);
        Assert.Equal(0, store.Calls);
        var failures = audit.Events.Where(e => e.Action.StartsWith("test-case.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, e =>
        {
            Assert.Equal(new AuditActor.Identified("admin"), e.Actor); Assert.Equal(AuditActionOutcome.Failure, e.Outcome);
            Assert.IsType<AuditTarget.Unknown>(e.Target); Assert.Null(e.TestCaseChange);
        });
    }

    [Fact]
    public async Task McpTestCaseDenialsAreSanitizedBeforeValidationAtTransportAndOperationBoundaries()
    {
        var audit = new CaseAuditRecorder(); var logger = new CaseAuditLogger(); var store = new CaseGateStore();
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<IAuditSubmission>(audit); services.AddSingleton<ILogger<TestCaseOperations>>(logger);
            services.AddSingleton<ITestCaseStore>(store); services.AddSingleton<ITestCaseChangeTransaction>(store);
        });
        foreach (var state in new[] { "anonymous", "restricted", "active", "revoked" })
        {
            if (state == "restricted") await host.Login();
            if (state == "active") { await host.Replace(); await host.Authenticate(); }
            var principal = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>()
                .Resolve(host.Mcp.DefaultRequestHeaders.Authorization?.Parameter);
            if (state == "revoked") await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(principal.FindFirstValue(SessionTicketStore.SessionClaim)!);
            foreach (var tool in new[] { "create_draft_test_case", "edit_draft_test_case" })
            {
                var count = audit.Events.Count;
                using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret", actor = "spoof", steps = new object?[] { null } } }, state == "active" ? "https://evil.invalid" : null);
                Assert.Equal(state == "active" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, denied.StatusCode);
                Assert.Equal(count + 1, audit.Events.Count);
                var entry = audit.Events.Last();
                Assert.Equal(tool.StartsWith("create", StringComparison.Ordinal) ? "test-case.create" : "test-case.edit", entry.Action);
                Assert.IsType<AuditTarget.Unknown>(entry.Target); Assert.Null(entry.TestCaseChange);
                Assert.Equal(AuditActionOutcome.Failure, entry.Outcome);
                Assert.True(entry.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
                if (state is "active" or "revoked") Assert.Equal(new AuditActor.Identified("admin"), entry.Actor);
                else Assert.IsType<AuditActor.Unknown>(entry.Actor);
            }
            if (state == "revoked")
            {
                using var scope = host.Factory.Services.CreateScope();
                var operations = scope.ServiceProvider.GetRequiredService<TestCaseOperations>();
                Assert.True((await operations.EditMcp(principal, Guid.NewGuid(), Guid.NewGuid(), 0, null!, null!, null!, null!, default)).IsFailure);
                Assert.True((await operations.CreateMcp(principal, host.Factory.Services.GetRequiredService<Retravion.Application.Product.IProductStore>(), "secret", null, null, null, null, default)).IsFailure);
            }
        }
        foreach (var throwing in new[] { false, true })
        {
            audit.Fail = true; audit.Throw = throwing;
            foreach (var tool in new[] { "create_draft_test_case", "edit_draft_test_case" })
            { using var denied = await host.Send("tools/call", new { name = tool, arguments = new { title = "secret" } }); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode); }
        }
        Assert.Equal(4, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.Equal("Unauthorized Test Case change audit submission failed.", message));
        Assert.Equal(0, store.Calls);
    }
}

public sealed partial class PostgresProductTests
{
    private static McpTestHost CaseMcpHost(PostgresProductStore products, PostgresTestCaseStore reads, PostgresTestCaseStore? writes = null) => new(services =>
    { services.AddSingleton<Retravion.Application.Product.IProductStore>(products); services.AddSingleton<ITestCaseStore>(reads); services.AddSingleton<ITestCaseChangeTransaction>(writes ?? reads); });
    private static object CaseEdit(TestCaseSnapshot row, string title = "Changed", long? revision = null, Guid? versionId = null) => new
    {
        internalId = row.TestCase.InternalId, versionId = versionId ?? row.TestCase.VersionId, expectedRevision = revision ?? row.Revision,
        title, description = "# Raw Markdown", preconditions = "", steps = new[] { new { action = "Second", expectedResult = "" }, new { action = "First", expectedResult = "Result" } }
    };

    [PostgresFact]
    public async Task McpTestCasesCreateIncompleteDraftsReplaceExactlyAndRetainProjectedHistoryAndVersions()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString);
        await using var host = CaseMcpHost(products, store);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var created = await host.Call("create_draft_test_case", new { productId = "P" });
        var id = created.GetProperty("internalId").GetGuid();
        Assert.Equal("TC-001", created.GetProperty("id").GetString());
        Assert.Equal("Draft", created.GetProperty("status").GetString());
        Assert.Empty(created.GetProperty("steps").EnumerateArray()); Assert.Empty(created.GetProperty("links").EnumerateArray());
        Assert.Equal(new[] { "creationReason", "description", "id", "internalId", "latestApprovedVersionNumber", "links", "preconditions", "productInternalId", "revision", "sourceVersionId", "status", "steps", "title", "versionId", "versionNumber" }, created.EnumerateObject().Select(p => p.Name).Order().ToArray());
        foreach (var field in new[] { "title", "description", "preconditions" }) Assert.Equal("", created.GetProperty(field).GetString());
        var empty = await host.Call("create_draft_test_case", new { productId = "Q", title = (string?)null, description = (string?)null, preconditions = (string?)null, steps = (object?)null });
        Assert.Equal("TC-001", empty.GetProperty("id").GetString());
        var row = (await store.Find(id)).Value.Value;
        var edited = await host.Call("edit_draft_test_case", CaseEdit(row));
        Assert.Equal(2, edited.GetProperty("revision").GetInt64()); Assert.Equal("Second", edited.GetProperty("steps")[0].GetProperty("action").GetString());
        row = (await store.Find(id)).Value.Value;
        await host.Call("edit_draft_test_case", CaseEdit(row));
        await host.Call("edit_draft_test_case", CaseEdit(row, revision: 1), true);
        await host.Call("edit_draft_test_case", CaseEdit(row, versionId: empty.GetProperty("versionId").GetGuid()), true);
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var target = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "R"));
        // Complete steps for review and add links only through the existing web-owned transaction contracts.
        var area = await AreaCreate(new PostgresProductAreaStore(database.ConnectionString), product.InternalId);
        row = (await store.Update(row, CaseContent("Complete"), CaseActor, CaseTime, changeArea: true, areaId: area.Area.InternalId)).Value;
        row = (await store.ChangeLink(row, "add", CaseEndpoint(target), null, CaseActor, CaseTime)).Value;
        var review = await CaseReview(store, row, "InReview", "Event-time comment");
        await host.Call("edit_draft_test_case", CaseEdit(review), true);
        var approved = await CaseReview(store, review, "Approved");
        await host.Call("edit_draft_test_case", CaseEdit(approved), true);
        var successor = (await store.CreateSuccessor(approved, "Fixed reason", CaseActor, CaseTime)).Value;
        var replacement = await host.Call("edit_draft_test_case", CaseEdit(successor, "Successor edit"));
        Assert.Equal(area.Area.InternalId, (await store.Find(id)).Value.Value.TestCase.AreaId);
        Assert.Equal("Fixed reason", replacement.GetProperty("creationReason").GetString());
        Assert.Equal(approved.TestCase.VersionId, replacement.GetProperty("sourceVersionId").GetGuid());
        Assert.Equal(target.Requirement.VersionId, replacement.GetProperty("links")[0].GetProperty("requirementVersionId").GetGuid());
        var detail = await host.Call("read_test_case", new { internalId = id });
        Assert.Equal(2, detail.GetProperty("testCase").GetProperty("versionNumber").GetInt32());
        Assert.Equal(1, detail.GetProperty("testCase").GetProperty("latestApprovedVersionNumber").GetInt32());
        Assert.Equal(new[] { 1, 2 }, detail.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionNumber").GetInt32()).ToArray());
        Assert.All(detail.GetProperty("versions").EnumerateArray(), v => Assert.Equal(7, v.EnumerateObject().Count()));
        var exact = await host.Call("read_test_case", new { internalId = id, versionId = approved.TestCase.VersionId });
        Assert.Equal("Complete", exact.GetProperty("testCase").GetProperty("title").GetString());
        var history = detail.GetProperty("history").EnumerateArray().ToArray();
        var decision = history.Single(h => h.GetProperty("change").GetProperty("comment").GetString() == "Event-time comment");
        Assert.Equal("Complete", decision.GetProperty("change").GetProperty("after").GetProperty("title").GetString());
        Assert.Equal("case-author", decision.GetProperty("actor").GetString());
        Assert.Equal(approved.TestCase.VersionId, decision.GetProperty("change").GetProperty("versionId").GetGuid());
        Assert.DoesNotContain("operationId", detail.GetRawText()); Assert.DoesNotContain("area", detail.GetRawText());
        Assert.Null(history.Single(h => h.GetProperty("action").GetString() == "test-case.create").GetProperty("change").GetProperty("before").GetString());
        await host.Call("read_test_case", new { internalId = id, versionId = empty.GetProperty("versionId").GetGuid() }, true);
        await host.Call("read_test_case", new { internalId = Guid.NewGuid() }, true);
        var listed = await host.Call("list_test_cases", new { productId = "P" }); Assert.Single(listed.EnumerateArray());
        Assert.Equal(id, listed[0].GetProperty("internalId").GetGuid());
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.TestCaseChange is not null && e.Actor == new AuditActor.Identified("admin")).ToArray();
        Assert.Equal(4, events.Length);
        Assert.All(events, e => { Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5)); Assert.Equal(new AuditTarget.Identified(e.TestCaseChange!.InternalId.ToString()), e.Target); });
        Assert.Equal(AreaEvidence.Unclassified, events.Single(e => e.TestCaseChange!.InternalId == id && e.Action == "test-case.create").TestCaseChange!.After.Area);
        var contentEdit = events.Single(e => e.Action == "test-case.edit" && e.TestCaseChange!.VersionNumber == 2).TestCaseChange!;
        Assert.Equal(successor.TestCase.Content.Title, contentEdit.Before!.Title);
        Assert.Equal(successor.TestCase.Content.Steps.ToArray(), contentEdit.Before.Steps.ToArray());
        Assert.Equal("Successor edit", contentEdit.After.Title);
        Assert.Equal(new[] { new TestCaseStep("Second", ""), new TestCaseStep("First", "Result") }, contentEdit.After.Steps.ToArray());
        Assert.Equal(contentEdit.Before.Links, contentEdit.After.Links);
        Assert.Equal(contentEdit.Before.Area, contentEdit.After.Area);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        await host.Call("list_test_cases", new { productId = "P" }); await host.Call("read_test_case", new { internalId = id });
        await host.Call("create_draft_test_case", new { productId = "P" }, true);
        await host.Call("edit_draft_test_case", CaseEdit((await store.Find(id)).Value.Value), true);
        Assert.Equal(events.Length, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count(e => e.TestCaseChange is not null && e.Actor == new AuditActor.Identified("admin")));
    }

    [PostgresFact]
    public async Task McpTestCasesCannotEditReleasedVersionsAndCanClearOnlyTheirExactDraftSuccessor()
    {
        await using var database = await Database.Create();
        var graph = await PrepareRelease(database.ConnectionString);
        var revisions = new Retravion.Infrastructure.Revision.PostgresRevisionStore(database.ConnectionString);
        Assert.True((await ReleaseNow(revisions, await ReleasePreview(revisions, graph.Row))).IsSuccess);
        var cases = new PostgresTestCaseStore(database.ConnectionString);
        await using var host = CaseMcpHost(new PostgresProductStore(database.ConnectionString), cases);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var before = (await cases.History(graph.Case1.TestCase.InternalId)).Value.Count;
        await host.Call("edit_draft_test_case", CaseEdit(graph.Case1), true);
        Assert.Equal(before, (await cases.History(graph.Case1.TestCase.InternalId)).Value.Count);
        var successor = (await cases.CreateSuccessor(graph.Case1, "Post-release changes", CaseActor, CaseTime)).Value;
        var cleared = await host.Call("edit_draft_test_case", new
        {
            internalId = successor.TestCase.InternalId, versionId = successor.TestCase.VersionId, expectedRevision = successor.Revision,
            title = "", description = "", preconditions = "", steps = Array.Empty<object>()
        });
        Assert.Equal("", cleared.GetProperty("title").GetString()); Assert.Empty(cleared.GetProperty("steps").EnumerateArray());
        Assert.Equal(successor.TestCase.Links.Length, cleared.GetProperty("links").GetArrayLength());
        var exact = await host.Call("read_test_case", new { internalId = graph.Case1.TestCase.InternalId, versionId = graph.Case1.TestCase.VersionId });
        Assert.Equal(graph.Case1.TestCase.Content.Title, exact.GetProperty("testCase").GetProperty("title").GetString());
        Assert.Equal("Approved", exact.GetProperty("testCase").GetProperty("status").GetString());
    }

    [PostgresFact]
    public async Task McpTestCaseCredentialRevocationAtFinalTransactionCheckRollsBackAndAuditsOneDenial()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var original = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        foreach (var operation in new[] { "create", "edit" })
        {
            var writes = new McpCaseAuthorityHook(store);
            await using var host = new McpTestHost(services =>
            {
                services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
                services.AddSingleton<ITestCaseStore>(store); services.AddSingleton<ITestCaseChangeTransaction>(writes);
                services.AddSingleton<IAuditSubmission>(new PostgresAuditSubmission(database.ConnectionString));
            });
            await host.Login(); await host.Replace(); var token = await host.Authenticate();
            var credentials = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>();
            var principal = credentials.Resolve(token);
            writes.BeforeFinalAuthorization = () => credentials.Revoke(principal);
            var historyBefore = (await store.History(original.TestCase.InternalId)).Value.Count;
            var error = operation == "create"
                ? await host.Call("create_draft_test_case", new { productId = "P", title = "secret" }, true)
                : await host.Call("edit_draft_test_case", CaseEdit(original, "secret"), true);
            Assert.Equal("Access denied.", error.GetString()); Assert.Equal(2, writes.Checks);
            Assert.True(host.Factory.Services.GetRequiredService<SessionTicketStore>().IsActive(principal.FindFirstValue(SessionTicketStore.SessionClaim)));
            Assert.Single((await store.List(product.InternalId)).Value);
            var unchanged = (await store.Find(original.TestCase.InternalId)).Value.Value;
            Assert.Equal(original.Revision, unchanged.Revision); Assert.True(original.TestCase.SameState(unchanged.TestCase));
            Assert.Equal(historyBefore, (await store.History(original.TestCase.InternalId)).Value.Count);
            var audit = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
            var denied = Assert.Single(audit, e => e.Action == $"test-case.{operation}" && e.Outcome == AuditActionOutcome.Failure);
            Assert.Equal(new AuditActor.Identified("admin"), denied.Actor); Assert.IsType<AuditTarget.Unknown>(denied.Target);
            Assert.Null(denied.TestCaseChange);
            Assert.Single(audit, e => e.TestCaseChange is not null);
        }
    }

    private sealed class McpCaseAuthorityHook(PostgresTestCaseStore store) : ITestCaseChangeTransaction
    {
        internal Action? BeforeFinalAuthorization;
        internal int Checks;
        private async Task<bool> Check(Func<Task<bool>>? authorized)
        {
            if (++Checks == 2) BeforeFinalAuthorization?.Invoke();
            return await authorized!();
        }
        public Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> Create(Guid product, TestCaseContent content, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? areaId = null, Guid? operationId = null, Func<Task<bool>>? authorized = null) =>
            store.Create(product, content, actor, time, token, areaId, operationId, () => Check(authorized));
        public Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> Update(TestCaseSnapshot expected, TestCaseContent content, AuditActor actor, DateTimeOffset time, CancellationToken token = default, bool changeArea = false, Guid? areaId = null, Guid? operationId = null, Func<Task<bool>>? authorized = null) =>
            store.Update(expected, content, actor, time, token, changeArea, areaId, operationId, () => Check(authorized));
        public Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> Review(TestCaseSnapshot expected, string decision, string? comment, AuditActor actor, DateTimeOffset time, CancellationToken token = default) => throw new NotSupportedException();
        public Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> Reopen(TestCaseSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default) => throw new NotSupportedException();
        public Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> CreateSuccessor(TestCaseSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
        public Task<CSharpFunctionalExtensions.Result<TestCaseSnapshot>> ChangeLink(TestCaseSnapshot expected, string operation, TestCaseRequirementLink target, TestCaseRequirementLink? replacement, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
    }

    [PostgresFact]
    public async Task McpTestCaseAtomicFailuresAndCancelledCommitUncertaintyNeverRetryOrLeakDetails()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var reads = new PostgresTestCaseStore(database.ConnectionString);
        var original = (await reads.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var table in new[] { "retravion_audit.event", "retravion_test_case.test_case", "retravion_test_case.test_case_version" })
        {
            await using (var inject = new NpgsqlCommand($"CREATE OR REPLACE FUNCTION public.reject_mcp_case() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private provider' USING ERRCODE='42501'; END $$; CREATE TRIGGER reject_mcp_case BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_mcp_case();", connection)) await inject.ExecuteNonQueryAsync();
            await using (var host = CaseMcpHost(products, reads))
            {
                await host.Login(); await host.Replace(); await host.Authenticate();
                await host.Call("create_draft_test_case", new { productId = "P" }, true);
                await host.Call("edit_draft_test_case", CaseEdit(original), true);
            }
            Assert.Single((await reads.List(product.InternalId)).Value);
            Assert.Equal(original.Revision, (await reads.Find(original.TestCase.InternalId)).Value.Value.Revision);
            Assert.Single((await reads.History(original.TestCase.InternalId)).Value);
            Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()), e => e.TestCaseChange is not null);
            await using var remove = new NpgsqlCommand($"DROP TRIGGER reject_mcp_case ON {table}", connection); await remove.ExecuteNonQueryAsync();
        }
        foreach (var committed in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        {
            var attempts = 0;
            var writes = new PostgresTestCaseStore(database.ConnectionString, async (transaction, token) =>
            { attempts++; if (committed) await transaction.CommitAsync(token); if (cancelled) throw new OperationCanceledException("private provider"); throw new IOException("private provider"); });
            await using var host = CaseMcpHost(products, reads, writes);
            await host.Login(); await host.Replace(); await host.Authenticate();
            var beforeCount = (await reads.List(product.InternalId)).Value.Count;
            var beforeEvents = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count(e => e.TestCaseChange is not null);
            var result = await host.Call("create_draft_test_case", new { productId = "P" }, true);
            Assert.Contains("could not be confirmed", result.GetString()); Assert.Contains("do not blindly recreate", result.GetString()); Assert.DoesNotContain("private", result.GetString());
            var current = (await reads.Find(original.TestCase.InternalId)).Value.Value;
            var edited = await host.Call("edit_draft_test_case", CaseEdit(current, $"Uncertain {committed} {cancelled}"), true);
            Assert.Contains("Retrieve", edited.GetString()); Assert.Contains("do not retry automatically", edited.GetString()); Assert.Equal(2, attempts);
            Assert.Equal(beforeCount + (committed ? 1 : 0), (await host.Call("list_test_cases", new { productId = "P" })).GetArrayLength());
            var persisted = (await reads.Find(original.TestCase.InternalId)).Value.Value;
            Assert.Equal(current.Revision + (committed ? 1 : 0), persisted.Revision);
            Assert.Equal(committed ? $"Uncertain {committed} {cancelled}" : current.TestCase.Content.Title, persisted.TestCase.Content.Title);
            Assert.Equal(beforeEvents + (committed ? 2 : 0), (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count(e => e.TestCaseChange is not null));
            var retrieved = await host.Call("read_test_case", new { internalId = original.TestCase.InternalId });
            Assert.Equal(persisted.Revision, retrieved.GetProperty("testCase").GetProperty("revision").GetInt64());
            Assert.Equal((await reads.History(original.TestCase.InternalId)).Value.Count, retrieved.GetProperty("history").GetArrayLength());
        }
    }
}
