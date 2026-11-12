using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Requirement;
using Retravion.Application.TestCase;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Xunit;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpClassificationRequiresExplicitStrictExactVersionArguments()
    {
        var requirements = new RequirementStoreDouble(); var cases = new CaseGateStore();
        await using var host = new McpTestHost(s =>
        {
            s.AddSingleton<IRequirementStore>(requirements); s.AddSingleton<IRequirementChangeTransaction>(requirements);
            s.AddSingleton<ITestCaseStore>(cases); s.AddSingleton<ITestCaseChangeTransaction>(cases);
        });
        await host.Login(); await host.Replace(); await host.Authenticate();
        var tools = (await host.Rpc("tools/list")).GetProperty("tools");
        foreach (var name in new[] { "edit_draft_requirement_classification", "edit_draft_test_case_classification" })
        {
            var schema = tools.EnumerateArray().Single(t => t.GetProperty("name").GetString() == name).GetProperty("inputSchema");
            Assert.Equal(new[] { "areaId", "confirmed", "expectedRevision", "internalId", "versionId" },
                schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order().ToArray());
            var valid = new Dictionary<string, object?>
            {
                ["internalId"] = Guid.NewGuid(), ["versionId"] = Guid.NewGuid(), ["expectedRevision"] = 1,
                ["areaId"] = null, ["confirmed"] = true
            };
            async Task Reject(object arguments)
            {
                using var response = await host.Send("tools/call", new { name, arguments });
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            foreach (var key in valid.Keys)
            {
                var missing = new Dictionary<string, object?>(valid); missing.Remove(key); await Reject(missing);
                await Reject(new Dictionary<string, object?>(valid) { [key] = "invalid" });
            }
            foreach (var revision in new object[] { 0, -1, 1.5, "1" })
                await Reject(new Dictionary<string, object?>(valid) { ["expectedRevision"] = revision });
            foreach (var key in new[] { "status", "actor", "title", "operationId" })
                await Reject(new Dictionary<string, object?>(valid) { [key] = "injected" });
        }
        Assert.Equal(0, requirements.Calls); Assert.Equal(0, cases.Calls);
    }
}

public sealed partial class PostgresProductTests
{
    private static McpTestHost AreaMcpHost(string connection, PostgresRequirementStore? requirementWrites = null,
        PostgresTestCaseStore? caseWrites = null) => new(s =>
    {
        var requirements = new PostgresRequirementStore(connection); var cases = new PostgresTestCaseStore(connection);
        s.AddSingleton<IProductStore>(new PostgresProductStore(connection));
        s.AddSingleton<IProductAreaStore>(new PostgresProductAreaStore(connection));
        s.AddSingleton<IRequirementStore>(requirements); s.AddSingleton<IRequirementLinkStore>(requirements);
        s.AddSingleton<IRequirementChangeTransaction>(requirementWrites ?? requirements);
        s.AddSingleton<ITestCaseStore>(cases); s.AddSingleton<ITestCaseChangeTransaction>(caseWrites ?? cases);
    });

    [PostgresFact]
    public async Task McpClassificationFinalCredentialRecheckRollsBackAndAuditsSanitizedDenial()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var a = await AreaCreate(new PostgresProductAreaStore(db.ConnectionString), p.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var r = (await requirements.Create(RequirementState.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RevActor, RevTime)).Value;
        var tc = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime)).Value;
        foreach (var kind in new[] { "requirement", "test_case" })
        {
            var requirementHook = new ClassificationAuthorityHook(requirements); var caseHook = new McpCaseAuthorityHook(cases);
            await using var host = new McpTestHost(s =>
            {
                s.AddSingleton<IProductStore>(new PostgresProductStore(db.ConnectionString));
                s.AddSingleton<IRequirementStore>(requirements); s.AddSingleton<IRequirementLinkStore>(requirements);
                s.AddSingleton<IRequirementChangeTransaction>(requirementHook);
                s.AddSingleton<ITestCaseStore>(cases); s.AddSingleton<ITestCaseChangeTransaction>(caseHook);
                s.AddSingleton<IAuditSubmission>(new PostgresAuditSubmission(db.ConnectionString));
            });
            await host.Login(); await host.Replace(); var credential = await host.Authenticate();
            var credentials = host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>();
            var principal = credentials.Resolve(credential);
            requirementHook.BeforeFinal = () => credentials.Revoke(principal);
            caseHook.BeforeFinalAuthorization = () => credentials.Revoke(principal);
            var id = kind == "requirement" ? r.Requirement.InternalId : tc.TestCase.InternalId;
            var row = (await host.Call($"read_{kind}", new { internalId = id })).GetProperty(kind == "requirement" ? "requirement" : "testCase");
            Assert.Equal("Access denied.", (await host.Call($"edit_draft_{kind}_classification", Classification(row, a.Area.InternalId), true)).GetString());
            Assert.Equal(2, kind == "requirement" ? requirementHook.Checks : caseHook.Checks);
            Assert.Null((await requirements.Find(r.Requirement.InternalId)).Value.Value.Requirement.AreaId);
            Assert.Null((await cases.Find(tc.TestCase.InternalId)).Value.Value.TestCase.AreaId);
            Assert.Equal(r.Revision, (await requirements.Find(r.Requirement.InternalId)).Value.Value.Revision);
            Assert.Equal(tc.Revision, (await cases.Find(tc.TestCase.InternalId)).Value.Value.Revision);
            var denied = Assert.Single(await new PostgresAuditQuery(db.ConnectionString).FindAsync(new()),
                e => e.Action == (kind == "requirement" ? "requirement.edit" : "test-case.edit") && e.Outcome == AuditActionOutcome.Failure);
            Assert.Equal(new AuditActor.Identified("admin"), denied.Actor); Assert.IsType<AuditTarget.Unknown>(denied.Target);
            Assert.Null(denied.RequirementChange); Assert.Null(denied.TestCaseChange);
        }
    }

    [PostgresFact]
    public async Task McpClassificationRejectsReviewAndReleasedVersionsWithoutChangingFrozenEvidence()
    {
        await using var db = await Database.Create(); var graph = await PrepareRelease(db.ConnectionString);
        var revisions = new Retravion.Infrastructure.Revision.PostgresRevisionStore(db.ConnectionString);
        Assert.True((await ReleaseNow(revisions, await ReleasePreview(revisions, graph.Row))).IsSuccess);
        var baseline = JsonSerializer.Serialize((await revisions.Baseline(graph.Product.InternalId, graph.Row.Value.InternalId)).Value);
        var a = await AreaCreate(new PostgresProductAreaStore(db.ConnectionString), graph.Product.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        await using var host = AreaMcpHost(db.ConnectionString);
        await host.Login(); await host.Replace(); await host.Authenticate();
        async Task Reject(string kind, Guid id, Guid versionId)
        {
            var row = (await host.Call($"read_{kind}", new { internalId = id, versionId })).GetProperty(kind == "requirement" ? "requirement" : "testCase");
            var count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            await host.Call($"edit_draft_{kind}_classification", Classification(row, a.Area.InternalId), true);
            Assert.Equal(count, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
        }
        await Reject("requirement", graph.Child.Requirement.InternalId, graph.Child.Requirement.VersionId);
        await Reject("test_case", graph.Case1.TestCase.InternalId, graph.Case1.TestCase.VersionId);
        var r = (await requirements.CreateSuccessor(graph.Child, "Next", RevActor, RevTime)).Value;
        var tc = (await cases.CreateSuccessor(graph.Case1, "Next", RevActor, RevTime)).Value;
        r = (await requirements.Update(r, r.Requirement.SubmitForReview().Value, RevActor, RevTime, "InReview")).Value;
        tc = await CaseReview(cases, tc, "InReview");
        await Reject("requirement", r.Requirement.InternalId, r.Requirement.VersionId);
        await Reject("test_case", tc.TestCase.InternalId, tc.TestCase.VersionId);
        // Historical versions remain ineligible even when another version is working.
        await Reject("requirement", graph.Child.Requirement.InternalId, graph.Child.Requirement.VersionId);
        await Reject("test_case", graph.Case1.TestCase.InternalId, graph.Case1.TestCase.VersionId);
        Assert.Equal(baseline, JsonSerializer.Serialize((await revisions.Baseline(graph.Product.InternalId, graph.Row.Value.InternalId)).Value));
    }

    private sealed class ClassificationAuthorityHook(PostgresRequirementStore store) : IRequirementChangeTransaction
    {
        internal Action? BeforeFinal;
        internal int Checks;
        private async Task<bool> Check(Func<Task<bool>>? authorized)
        {
            if (++Checks == 2) BeforeFinal?.Invoke();
            return await authorized!();
        }
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> Update(RequirementSnapshot expected, RequirementState replacement,
            AuditActor actor, DateTimeOffset time, string? decision = null, string? comment = null, CancellationToken cancellationToken = default,
            Guid? operationId = null, Func<Task<bool>>? authorized = null) =>
            store.Update(expected, replacement, actor, time, decision, comment, cancellationToken, operationId, () => Check(authorized));
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> Create(RequirementState value, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> Derive(RequirementSnapshot source, RequirementState destination, AuditActor actor, DateTimeOffset time, CancellationToken token = default) => throw new NotSupportedException();
        public Task<CSharpFunctionalExtensions.Result<RequirementSnapshot>> CreateSuccessor(RequirementSnapshot expected, string? reason, AuditActor actor, DateTimeOffset time, CancellationToken token = default, Guid? operationId = null, Func<Task<bool>>? authorized = null) => throw new NotSupportedException();
    }

    private static object Classification(JsonElement row, Guid? areaId, bool confirmed = true, long? revision = null) => new
    {
        internalId = row.GetProperty("internalId").GetGuid(), versionId = row.GetProperty("versionId").GetGuid(),
        expectedRevision = revision ?? row.GetProperty("revision").GetInt64(), areaId, confirmed
    };

    [PostgresFact]
    public async Task McpAreasClassifyOnlyExactEligibleDraftsAndRetainEventTimeAuditWithoutContentOrLinkChanges()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString); var q = await AreaProduct(db.ConnectionString, "Q");
        var areas = new PostgresProductAreaStore(db.ConnectionString);
        var a = await AreaCreate(areas, p.InternalId); var b = await AreaCreate(areas, p.InternalId, "HW", "Hardware");
        var retired = await AreaCreate(areas, p.InternalId, "OLD", "Old");
        retired = (await areas.Save(Guid.NewGuid(), retired, retired.Area.Retire(), RevActor, RevTime, RevAllowed)).Value;
        var foreign = await AreaCreate(areas, q.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var target = await ParentApprove(requirements, (await requirements.Create(RequirementState.Create(p.InternalId, "Target", "Target", "Statement", RequirementType.System).Value, RevActor, RevTime)).Value);
        var r = (await requirements.Create(RequirementState.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RevActor, RevTime)).Value;
        var tc = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime)).Value;
        tc = (await cases.ChangeLink(tc, "add", CaseEndpoint(target), null, RevActor, RevTime)).Value;
        await using var host = AreaMcpHost(db.ConnectionString);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var listed = await host.Call("list_product_areas", new { productId = "P" });
        Assert.Equal(3, listed.GetArrayLength());
        Assert.Equal("Retired", listed.EnumerateArray().Single(x => x.GetProperty("internalId").GetGuid() == retired.Area.InternalId).GetProperty("status").GetString());
        Assert.All(listed.EnumerateArray(), x => Assert.Equal(p.InternalId, x.GetProperty("productInternalId").GetGuid()));
        await host.Call("list_product_areas", new { productId = "missing" }, true);
        foreach (var (kind, id) in new[] { ("requirement", r.Requirement.InternalId), ("test_case", tc.TestCase.InternalId) })
        {
            var owner = kind == "requirement" ? "requirement" : "testCase";
            var tool = $"edit_draft_{kind}_classification";
            var row = (await host.Call($"read_{kind}", new { internalId = id })).GetProperty(owner);
            var initial = row;
            var eventsBefore = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            await host.Call(tool, Classification(row, a.Area.InternalId, confirmed: false), true);
            foreach (var invalid in new[] { retired.Area.InternalId, foreign.Area.InternalId, Guid.NewGuid() })
                await host.Call(tool, Classification(row, invalid), true);
            Assert.Equal(eventsBefore, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            row = await host.Call(tool, Classification(row, a.Area.InternalId));
            Assert.Equal(a.Area.InternalId, row.GetProperty("areaId").GetGuid());
            await host.Call(tool, Classification(row, a.Area.InternalId)); // no-op
            await host.Call(tool, Classification(initial, b.Area.InternalId), true); // stale
            row = await host.Call(tool, Classification(row, b.Area.InternalId));
            row = await host.Call(tool, Classification(row, null));
            Assert.Equal(JsonValueKind.Null, row.GetProperty("areaId").ValueKind);
            foreach (var property in initial.EnumerateObject().Where(x => x.Name is not "revision" and not "areaId"))
                Assert.Equal(property.Value.GetRawText(), row.GetProperty(property.Name).GetRawText());
            var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new()))
                .Where(e => e.Target == new AuditTarget.Identified(id.ToString()) && e.Actor == new AuditActor.Identified("admin"))
                .OrderBy(e => e.OccurredAt).ToArray();
            Assert.Equal(3, events.Length);
            Assert.All(events, e =>
            {
                Assert.Equal(new AuditActor.Identified("admin"), e.Actor); Assert.Equal(AuditActionOutcome.Success, e.Outcome);
                Assert.Equal(new AuditTarget.Identified(id.ToString()), e.Target);
                Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
                Assert.Equal(kind == "requirement" ? "requirement.edit" : "test-case.edit", e.Action);
            });
            var before = kind == "requirement" ? events[1].RequirementChange!.Before!.Area : events[1].TestCaseChange!.Before!.Area;
            var after = kind == "requirement" ? events[1].RequirementChange!.After.Area : events[1].TestCaseChange!.After.Area;
            Assert.Equal(a.Area.InternalId, before!.Area!.InternalId); Assert.Equal("Software", before.Area.Label);
            Assert.Equal(b.Area.InternalId, after!.Area!.InternalId); Assert.Equal("Hardware", after.Area.Label);
            Assert.Equal(p.InternalId, kind == "requirement" ? events[1].RequirementChange!.After.ProductInternalId : events[1].TestCaseChange!.After.ProductInternalId);
            Assert.Equal(initial.GetProperty("versionId").GetGuid(), kind == "requirement" ? events[1].RequirementChange!.VersionId : events[1].TestCaseChange!.VersionId);
        }
        // Inherited retired classifications survive ordinary edits, but cannot be reselected after clearing.
        r = (await requirements.Find(r.Requirement.InternalId)).Value.Value;
        r = (await requirements.Update(r, r.Requirement.Classify(a.Area.InternalId).Value, RevActor, RevTime)).Value;
        tc = (await cases.Find(tc.TestCase.InternalId)).Value.Value;
        tc = (await cases.Update(tc, tc.TestCase.Content, RevActor, RevTime, changeArea: true, areaId: a.Area.InternalId)).Value;
        r = await ParentApprove(requirements, r); tc = await CaseApprove(cases, tc);
        a = (await areas.Save(Guid.NewGuid(), a, a.Area.Retire(), RevActor, RevTime, RevAllowed)).Value;
        foreach (var (kind, id) in new[] { ("requirement", r.Requirement.InternalId), ("test_case", tc.TestCase.InternalId) })
        {
            var row = (await host.Call($"read_{kind}", new { internalId = id })).GetProperty(kind == "requirement" ? "requirement" : "testCase");
            await host.Call($"edit_draft_{kind}_classification", Classification(row, null), true);
        }
        var successor = (await requirements.CreateSuccessor(r, "Next", RevActor, RevTime)).Value;
        var caseSuccessor = (await cases.CreateSuccessor(tc, "Next", RevActor, RevTime)).Value;
        var edited = await host.Call("edit_draft_requirement", new { internalId = successor.Requirement.InternalId, expectedRevision = successor.Revision, id = "R", title = "Other", statement = "Statement", type = "System" });
        Assert.Equal(a.Area.InternalId, edited.GetProperty("areaId").GetGuid());
        var caseEdited = await host.Call("edit_draft_test_case", CaseEdit(caseSuccessor));
        Assert.Equal(a.Area.InternalId, caseEdited.GetProperty("areaId").GetGuid());
        foreach (var (kind, row) in new[] { ("requirement", edited), ("test_case", caseEdited) })
        {
            var cleared = await host.Call($"edit_draft_{kind}_classification", Classification(row, null));
            await host.Call($"edit_draft_{kind}_classification", Classification(cleared, a.Area.InternalId), true);
            // Exact older version is never silently substituted with the successor.
            await host.Call($"edit_draft_{kind}_classification", Classification(row, b.Area.InternalId) , true);
        }
        Assert.Equal(a.Area.InternalId, (await requirements.Find(r.Requirement.InternalId, versionId: r.Requirement.VersionId)).Value.Value.Requirement.AreaId);
        Assert.Equal(a.Area.InternalId, (await cases.Find(tc.TestCase.InternalId, tc.TestCase.VersionId)).Value.Value.TestCase.AreaId);
        Assert.True((await new PostgresProductStore(db.ConnectionString).Update(p, p.Retire().Value)).IsSuccess);
        Assert.Equal(3, (await host.Call("list_product_areas", new { productId = "P" })).GetArrayLength());
        foreach (var (kind, id) in new[] { ("requirement", r.Requirement.InternalId), ("test_case", tc.TestCase.InternalId) })
        {
            var row = (await host.Call($"read_{kind}", new { internalId = id })).GetProperty(kind == "requirement" ? "requirement" : "testCase");
            await host.Call($"edit_draft_{kind}_classification", Classification(row, b.Area.InternalId), true);
        }
    }

    [PostgresFact]
    public async Task McpClassificationAtomicFailuresRollbackAndUncertainCommitsRequireReadWithoutRetry()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var a = await AreaCreate(new PostgresProductAreaStore(db.ConnectionString), p.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var r = (await requirements.Create(RequirementState.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System).Value, RevActor, RevTime)).Value;
        var tc = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime)).Value;
        await using var connection = new NpgsqlConnection(db.ConnectionString); await connection.OpenAsync();
        foreach (var table in new[] { "retravion_audit.event", "retravion_requirement.requirement", "retravion_requirement.review", "retravion_requirement.requirement_version", "retravion_test_case.test_case", "retravion_test_case.test_case_version", "retravion_test_case.history" })
        {
            var kind = table.Contains("test_case", StringComparison.Ordinal) ? "test_case" : "requirement";
            await using var host = AreaMcpHost(db.ConnectionString);
            await host.Login(); await host.Replace(); await host.Authenticate();
            var id = kind == "requirement" ? r.Requirement.InternalId : tc.TestCase.InternalId;
            var row = (await host.Call($"read_{kind}", new { internalId = id })).GetProperty(kind == "requirement" ? "requirement" : "testCase");
            var before = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
            await using (var inject = new NpgsqlCommand($"CREATE OR REPLACE FUNCTION public.reject_mcp_area() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private provider' USING ERRCODE='42501'; END $$; CREATE TRIGGER reject_mcp_area BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_mcp_area();", connection)) await inject.ExecuteNonQueryAsync();
            await host.Call($"edit_draft_{kind}_classification", Classification(row, a.Area.InternalId), true);
            await using (var remove = new NpgsqlCommand($"DROP TRIGGER reject_mcp_area ON {table}", connection)) await remove.ExecuteNonQueryAsync();
            Assert.Equal(before, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
            Assert.Null((await requirements.Find(r.Requirement.InternalId)).Value.Value.Requirement.AreaId);
            Assert.Null((await cases.Find(tc.TestCase.InternalId)).Value.Value.TestCase.AreaId);
            Assert.Empty((await requirements.History(r.Requirement.InternalId)).Value);
            Assert.Single((await cases.History(tc.TestCase.InternalId)).Value);
        }
        foreach (var committed in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        {
            var attempts = 0;
            async Task LoseConfirmation(NpgsqlTransaction transaction, CancellationToken token)
            {
                attempts++; if (committed) await transaction.CommitAsync(token);
                if (cancelled) throw new OperationCanceledException("private provider");
                throw new IOException("private provider");
            }
            await using var host = AreaMcpHost(db.ConnectionString,
                new PostgresRequirementStore(db.ConnectionString, LoseConfirmation), new PostgresTestCaseStore(db.ConnectionString, LoseConfirmation));
            await host.Login(); await host.Replace(); await host.Authenticate();
            foreach (var (kind, id) in new[] { ("requirement", r.Requirement.InternalId), ("test_case", tc.TestCase.InternalId) })
            {
                var owner = kind == "requirement" ? "requirement" : "testCase";
                var row = (await host.Call($"read_{kind}", new { internalId = id })).GetProperty(owner);
                var selected = row.GetProperty("areaId").ValueKind == JsonValueKind.Null ? a.Area.InternalId : (Guid?)null;
                var before = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
                var error = await host.Call($"edit_draft_{kind}_classification", Classification(row, selected), true);
                Assert.Contains("could not be confirmed", error.GetString()); Assert.DoesNotContain("private", error.GetString());
                var current = (await host.Call($"read_{kind}", new { internalId = id })).GetProperty(owner);
                Assert.Equal(row.GetProperty("revision").GetInt64() + (committed ? 1 : 0), current.GetProperty("revision").GetInt64());
                Assert.Equal(before + (committed ? 1 : 0), (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
                Assert.Equal(committed ? JsonSerializer.Serialize(selected) : row.GetProperty("areaId").GetRawText(), current.GetProperty("areaId").GetRawText());
            }
            Assert.Equal(2, attempts);
        }
    }
}
