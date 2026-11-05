using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Application.TestCase;
using Retravion.Domain.Product;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static TestCaseRequirementLink CaseEndpoint(RequirementSnapshot row) => new(row.Requirement.InternalId, row.Requirement.VersionId);
    private static async Task<TestCaseSnapshot> CaseReview(PostgresTestCaseStore store, TestCaseSnapshot row, string decision, string? comment = null) =>
        (await store.Review(row, decision, comment, CaseActor, CaseTime)).Value;
    private static async Task<TestCaseSnapshot> CaseApprove(PostgresTestCaseStore store, TestCaseSnapshot row) =>
        await CaseReview(store, await CaseReview(store, row, "InReview", "Submission evidence"), "Approved", "Approval evidence");

    [PostgresFact]
    public async Task TestCaseReviewSuccessorsAndDefaultReadsRetainExactVersionsAndDecisionSnapshots()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var target = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "R"));
        var target2 = await ParentApprove(requirements, (await requirements.CreateSuccessor(target, "New target", CaseActor, CaseTime)).Value);
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var row = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        row = (await store.ChangeLink(row, "add", CaseEndpoint(target), null, CaseActor, CaseTime)).Value;
        var original = row;
        var review = await CaseReview(store, row, "InReview", " First submission ");
        Assert.Equal(row.TestCase.VersionId, review.TestCase.VersionId);
        Assert.Equal(row.TestCase.VersionNumber, review.TestCase.VersionNumber);
        Assert.True((await store.Update(review, CaseContent("Frozen"), CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(review, "remove", CaseEndpoint(target), null, CaseActor, CaseTime)).IsFailure);
        var rejected = await CaseReview(store, review, "Draft", "Plain <b>comment</b>");
        Assert.Equal(review.TestCase.VersionId, rejected.TestCase.VersionId);
        row = (await store.Update(rejected, CaseContent("Corrected"), CaseActor, CaseTime)).Value;
        row = (await store.ChangeLink(row, "retarget", CaseEndpoint(target), CaseEndpoint(target2), CaseActor, CaseTime)).Value;
        row = await CaseApprove(store, row);
        Assert.Equal("Approved", row.TestCase.Status);
        var evidence = (await store.History(row.TestCase.InternalId)).Value;
        var submitted = Assert.Single(evidence, item => item.Change.Comment == " First submission ");
        Assert.Equal(original.TestCase.Content.Title, submitted.Change.After.Title);
        Assert.Equal(original.TestCase.Content.Steps.ToArray(), submitted.Change.After.Steps.ToArray());
        Assert.Equal(CaseEndpoint(target), Assert.Single(submitted.Change.After.Links!));
        Assert.Equal(CaseActor, submitted.Actor); Assert.Equal(CaseTime, submitted.OccurredAt);
        Assert.Equal(row.TestCase.InternalId, submitted.Change.InternalId); Assert.Equal(row.TestCase.VersionId, submitted.Change.VersionId);
        Assert.Equal("Plain <b>comment</b>", evidence.Single(item => item.Change.Decision == "Draft").Change.Comment);
        var count = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        Assert.True((await store.Update(row, CaseContent("Frozen"), CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.Review(row, "InReview", null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.CreateSuccessor(row, " \n", CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.Reopen(row, "Reason", CaseActor, CaseTime)).IsFailure);
        Assert.Equal(count, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        var approved = row;
        var candidates = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            new PostgresTestCaseStore(database.ConnectionString).CreateSuccessor(approved, "Fixed reason " + i, CaseActor, CaseTime)));
        Assert.All(candidates, result => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : ""));
        Assert.Single(candidates.Select(result => result.Value.TestCase.VersionId).Distinct());
        var successor = candidates[0].Value;
        Assert.Equal(2, successor.TestCase.VersionNumber); Assert.Equal("Draft", successor.TestCase.Status);
        Assert.NotEqual(approved.TestCase.VersionId, successor.TestCase.VersionId);
        Assert.Equal(approved.TestCase.VersionId, successor.TestCase.SourceVersionId);
        Assert.Equal(approved.TestCase.Id, successor.TestCase.Id); Assert.Equal(approved.TestCase.InternalId, successor.TestCase.InternalId);
        Assert.Equal(approved.TestCase.ProductInternalId, successor.TestCase.ProductInternalId);
        Assert.True(approved.TestCase.Content.SameContent(successor.TestCase.Content));
        Assert.Equal(approved.TestCase.Links.ToArray(), successor.TestCase.Links.ToArray());
        var defaultRow = (await store.Find(approved.TestCase.InternalId)).Value.Value;
        Assert.True(successor.TestCase.SameState(defaultRow.TestCase)); Assert.Equal(1, defaultRow.LatestApprovedVersionNumber);
        Assert.True(successor.TestCase.SameState(Assert.Single((await store.List(product.InternalId)).Value).TestCase));
        Assert.True(approved.TestCase.SameState((await store.Find(approved.TestCase.InternalId, approved.TestCase.VersionId)).Value.Value.TestCase));
        Assert.True((await store.Find(approved.TestCase.InternalId, Guid.NewGuid())).Value.HasNoValue);
        Assert.Equal(new[] { 1, 2 }, (await store.Versions(approved.TestCase.InternalId)).Value.Select(item => item.TestCase.VersionNumber));
        var fixedReason = successor.TestCase.CreationReason;
        successor = (await store.Update(successor, CaseContent("Version 2 changes"), CaseActor, CaseTime)).Value;
        Assert.Equal(fixedReason, successor.TestCase.CreationReason);
        Assert.True((await store.Update(approved, CaseContent("Older"), CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.CreateSuccessor(approved, "Duplicate after edit", CaseActor, CaseTime)).IsSuccess);
        successor = await CaseApprove(store, successor);
        var third = (await store.CreateSuccessor(successor, "Version three", CaseActor, CaseTime)).Value;
        Assert.Equal(3, third.TestCase.VersionNumber);
        Assert.True((await store.CreateSuccessor(approved, "Older source", CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.Reopen(approved, "Older", CaseActor, CaseTime)).IsFailure);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(item => item.TestCaseChange?.InternalId == approved.TestCase.InternalId).ToArray();
        var inherited = Assert.Single(events, item => item.Action == "test-case.version.create" && item.TestCaseChange!.VersionNumber == 2);
        Assert.Null(inherited.TestCaseChange!.Before); Assert.Equal(approved.TestCase.VersionId, inherited.TestCaseChange.SourceVersionId);
        Assert.Equal(fixedReason, inherited.TestCaseChange.Reason);
        Assert.Equal(approved.TestCase.Content.Steps.ToArray(), inherited.TestCaseChange.After.Steps.ToArray());
        Assert.Equal(CaseEndpoint(target2), Assert.Single(inherited.TestCaseChange.After.Links!));
        Assert.DoesNotContain(events, item => item.Action == "test-case.link.add" && item.TestCaseChange!.VersionNumber == 2);
        Assert.All(events, item => { Assert.Equal(CaseActor, item.Actor); Assert.Equal(CaseTime, item.OccurredAt); Assert.Equal(AuditActionOutcome.Success, item.Outcome); });
        Assert.Equal(events.Length, (await store.History(approved.TestCase.InternalId)).Value.Count);
        Assert.Equal("Title", (await store.History(approved.TestCase.InternalId)).Value.Single(item => item.Change.Comment == " First submission ").Change.After.Title);
    }

    [PostgresFact]
    public async Task TestCaseUpgradePreservesLegacyDraftIdentitiesContentCounterAndAuditWithoutInventingHistory()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var entity = Guid.NewGuid(); var version = Guid.NewGuid(); var content = CaseContent();
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        await using (var legacy = new NpgsqlCommand("""
            CREATE SCHEMA retravion_test_case;
            CREATE TABLE retravion_test_case.counter(product_internal_id uuid PRIMARY KEY,last_number bigint NOT NULL CHECK(last_number>0));
            CREATE TABLE retravion_test_case.test_case(
                internal_id uuid PRIMARY KEY,product_internal_id uuid NOT NULL,number bigint NOT NULL CHECK(number>0),
                version_id uuid NOT NULL UNIQUE,version_number integer NOT NULL CHECK(version_number=1),status text NOT NULL CHECK(status='Draft'),
                title text NOT NULL,description text NOT NULL,preconditions text NOT NULL,
                steps jsonb NOT NULL CHECK(jsonb_typeof(steps)='array'),revision bigint NOT NULL CHECK(revision>0),UNIQUE(product_internal_id,number));
            """, connection)) await legacy.ExecuteNonQueryAsync();
        await using (var counter = new NpgsqlCommand("INSERT INTO retravion_test_case.counter VALUES($1,42)", connection))
        { counter.Parameters.AddWithValue(product.InternalId); await counter.ExecuteNonQueryAsync(); }
        await using (var legacy = new NpgsqlCommand("INSERT INTO retravion_test_case.test_case VALUES($2,$1,42,$3,1,'Draft',$4,$5,$6,$7::jsonb,9)", connection))
        {
            legacy.Parameters.AddWithValue(product.InternalId); legacy.Parameters.AddWithValue(entity); legacy.Parameters.AddWithValue(version);
            legacy.Parameters.AddWithValue(content.Title); legacy.Parameters.AddWithValue(content.Description); legacy.Parameters.AddWithValue(content.Preconditions);
            legacy.Parameters.AddWithValue(JsonSerializer.Serialize(content.Steps)); await legacy.ExecuteNonQueryAsync();
        }
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var row = (await store.Find(entity)).Value.Value;
        Assert.Equal(version, row.TestCase.VersionId); Assert.Equal(entity, row.TestCase.InternalId); Assert.Equal(9, row.Revision);
        Assert.Equal("TC-042", row.TestCase.Id); Assert.True(content.SameContent(row.TestCase.Content)); Assert.Empty(row.TestCase.Links);
        Assert.True(row.TestCase.SameState(Assert.Single((await store.Versions(entity)).Value).TestCase));
        Assert.Empty((await store.History(entity)).Value);
        row = (await store.Update(row, CaseContent("Edited legacy"), CaseActor, CaseTime)).Value;
        Assert.Equal(version, row.TestCase.VersionId); Assert.Equal(10, row.Revision);
        Assert.Single((await store.History(entity)).Value);
        var next = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        Assert.Equal("TC-043", next.TestCase.Id);
    }

    private sealed record CaseScenario(TestCaseSnapshot Row, TestCaseRequirementLink Target, TestCaseRequirementLink Replacement);
    private static readonly string[] CaseOperations = ["create", "edit", "InReview", "Draft", "Approved", "successor", "add", "remove", "retarget"];

    private static async Task<CaseScenario> PrepareCaseOperation(PostgresTestCaseStore store, PostgresRequirementStore requirements, Guid product, string operation)
    {
        var first = await ParentApprove(requirements, await ParentDraft(requirements, product, "R-" + Guid.NewGuid()));
        var second = await ParentApprove(requirements, (await requirements.CreateSuccessor(first, "Target next", CaseActor, CaseTime)).Value);
        var row = (await store.Create(product, CaseContent(), CaseActor, CaseTime)).Value;
        if (operation != "add") row = (await store.ChangeLink(row, "add", CaseEndpoint(first), null, CaseActor, CaseTime)).Value;
        if (operation is "Draft" or "Approved" or "successor") row = await CaseReview(store, row, "InReview");
        if (operation == "successor") row = await CaseReview(store, row, "Approved");
        return new(row, CaseEndpoint(first), CaseEndpoint(second));
    }
    private static Task<Result<TestCaseSnapshot>> RunCaseOperation(PostgresTestCaseStore store, CaseScenario scenario, string operation) => operation switch
    {
        "create" => store.Create(scenario.Row.TestCase.ProductInternalId, CaseContent("New"), CaseActor, CaseTime),
        "edit" => store.Update(scenario.Row, CaseContent("Edited"), CaseActor, CaseTime),
        "successor" => store.CreateSuccessor(scenario.Row, "Fixed successor reason", CaseActor, CaseTime),
        "add" or "remove" => store.ChangeLink(scenario.Row, operation, scenario.Target, null, CaseActor, CaseTime),
        "retarget" => store.ChangeLink(scenario.Row, operation, scenario.Target, scenario.Replacement, CaseActor, CaseTime),
        _ => store.Review(scenario.Row, operation, "Decision comment", CaseActor, CaseTime)
    };

    private static async Task<string> CaseDatabaseState(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'counter',(SELECT jsonb_agg(to_jsonb(t) ORDER BY product_internal_id) FROM retravion_test_case.counter t),
                'entities',(SELECT jsonb_agg(to_jsonb(t) ORDER BY internal_id) FROM retravion_test_case.test_case t),
                'versions',(SELECT jsonb_agg(to_jsonb(t) ORDER BY version_id) FROM retravion_test_case.test_case_version t),
                'history',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM retravion_test_case.history t),
                'audit',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM retravion_audit.event t))::text
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task TestCaseEveryWriteRollsBackBusinessVersionsHistoryAndAuditOnEachFailureBoundary()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString); var requirements = new PostgresRequirementStore(database.ConnectionString);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var operation in CaseOperations)
        {
            var scenario = await PrepareCaseOperation(store, requirements, product.InternalId, operation);
            foreach (var table in new[] { "retravion_test_case.test_case", "retravion_test_case.test_case_version", "retravion_test_case.history", "retravion_audit.event" })
            {
                var before = await CaseDatabaseState(connection);
                await using (var inject = new NpgsqlCommand($"""
                    CREATE OR REPLACE FUNCTION public.fail_case_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$;
                    CREATE TRIGGER fail_case_write BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.fail_case_write();
                    """, connection)) await inject.ExecuteNonQueryAsync();
                var result = await RunCaseOperation(store, scenario, operation);
                Assert.True(result.IsFailure, operation + " " + table); Assert.DoesNotContain("private", result.Error);
                Assert.Equal(before, await CaseDatabaseState(connection));
                await using (var remove = new NpgsqlCommand($"DROP TRIGGER fail_case_write ON {table}", connection)) await remove.ExecuteNonQueryAsync();
            }
            Assert.True((await RunCaseOperation(store, scenario, operation)).IsSuccess, operation);
        }
    }

    [PostgresFact]
    public async Task TestCaseEveryWriteReportsBothUncertainCommitOutcomesWithoutRetryAndKeepsAtomicEvidence()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString); var requirements = new PostgresRequirementStore(database.ConnectionString);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var operation in CaseOperations)
        foreach (var committed in new[] { false, true })
        {
            var scenario = await PrepareCaseOperation(store, requirements, product.InternalId, operation);
            var state = await CaseDatabaseState(connection);
            var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
            var history = (await store.History(scenario.Row.TestCase.InternalId)).Value.Count;
            var calls = 0;
            var uncertain = new PostgresTestCaseStore(database.ConnectionString, async (transaction, token) =>
            {
                calls++;
                if (committed) await transaction.CommitAsync(token);
                throw new OperationCanceledException("Lost confirmation");
            });
            var result = await RunCaseOperation(uncertain, scenario, operation);
            Assert.Equal(1, calls); Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error);
            Assert.Contains("Reload", result.Error); Assert.Contains("do not retry automatically", result.Error);
            if (!committed) Assert.Equal(state, await CaseDatabaseState(connection));
            else
            {
                Assert.NotEqual(state, await CaseDatabaseState(connection));
                Assert.Equal(events + 1, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
                if (operation == "create")
                {
                    var created = (await store.List(product.InternalId)).Value.OrderBy(row => row.TestCase.Number).Last();
                    Assert.Single((await store.History(created.TestCase.InternalId)).Value);
                    Assert.Single((await store.Versions(created.TestCase.InternalId)).Value);
                }
                else
                {
                    var after = (await store.Find(scenario.Row.TestCase.InternalId)).Value.Value;
                    Assert.Equal(scenario.Row.Revision + 1, after.Revision);
                    var evidence = (await store.History(after.TestCase.InternalId)).Value;
                    Assert.Equal(history + 1, evidence.Count);
                    Assert.Equal(after.TestCase.VersionId, evidence.Last().Change.VersionId);
                    Assert.Equal(after.TestCase.Content.Title, evidence.Last().Change.After.Title);
                    Assert.Equal(after.TestCase.Links.ToArray(), evidence.Last().Change.After.Links!.ToArray());
                    Assert.True(after.TestCase.SameState((await store.Find(after.TestCase.InternalId, after.TestCase.VersionId)).Value.Value.TestCase));
                }
            }
        }
    }
}
