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
    [PostgresFact]
    public async Task TestCaseLinksAndReviewEnforceCompletenessCardinalityProductExactnessAndApprovedTargets()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var target = await ParentDraft(requirements, product.InternalId, "R");
        var second = await ParentDraft(requirements, product.InternalId, "S");
        var foreign = await ParentDraft(requirements, other.InternalId, "Foreign");
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var row = (await store.Create(product.InternalId, TestCaseContent.Create(null, null, null, null).Value, CaseActor, CaseTime)).Value;
        var query = new PostgresAuditQuery(database.ConnectionString);
        var count = (await query.FindAsync(new())).Count;
        Assert.True((await store.Review(row, "InReview", null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.Review(row, "Approved", null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(row, "add", CaseEndpoint(foreign), null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(row, "add", new(target.Requirement.InternalId, Guid.NewGuid()), null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(row, "add", new(Guid.NewGuid(), target.Requirement.VersionId), null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(row, "retarget", CaseEndpoint(target), CaseEndpoint(second), CaseActor, CaseTime)).IsFailure);
        Assert.Equal(count, (await query.FindAsync(new())).Count);
        row = (await store.ChangeLink(row, "add", CaseEndpoint(target), null, CaseActor, CaseTime)).Value;
        foreach (var content in new[] {
            TestCaseContent.Create(" \n", "", "", [new("Action", "Result")]).Value,
            TestCaseContent.Create("Title", "", "", []).Value,
            TestCaseContent.Create("Title", "", "", [new(" \t", "Result")]).Value,
            TestCaseContent.Create("Title", "", "", [new("Action", " \n")]).Value })
        {
            row = (await store.Update(row, content, CaseActor, CaseTime)).Value;
            count = (await query.FindAsync(new())).Count;
            Assert.True((await store.Review(row, "InReview", "Invalid", CaseActor, CaseTime)).IsFailure);
            Assert.Equal(count, (await query.FindAsync(new())).Count);
        }
        row = (await store.Update(row, TestCaseContent.Create("Title", "", "", [new("Action", "Result")]).Value, CaseActor, CaseTime)).Value;
        row = (await store.ChangeLink(row, "add", CaseEndpoint(second), null, CaseActor, CaseTime)).Value;
        var another = (await store.Create(product.InternalId, CaseContent("Another"), CaseActor, CaseTime)).Value;
        another = (await store.ChangeLink(another, "add", CaseEndpoint(target), null, CaseActor, CaseTime)).Value;
        Assert.Single(another.TestCase.Links);
        count = (await query.FindAsync(new())).Count;
        Assert.True((await store.ChangeLink(row, "add", CaseEndpoint(target), null, CaseActor, CaseTime)).IsFailure);
        Assert.Equal(count, (await query.FindAsync(new())).Count);
        var review = await CaseReview(store, row, "InReview"); // Draft targets allowed at submission.
        Assert.Equal("InReview", review.TestCase.Status);
        count = (await query.FindAsync(new())).Count;
        Assert.True((await store.Review(review, "Approved", null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(review, "remove", CaseEndpoint(target), null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(review, "add", CaseEndpoint(foreign), null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(review, "retarget", CaseEndpoint(target), CaseEndpoint(second), CaseActor, CaseTime)).IsFailure);
        target = await ParentApprove(requirements, target);
        second = (await requirements.Update(second, second.Requirement.SubmitForReview().Value, CaseActor, CaseTime, "InReview")).Value;
        Assert.True((await store.Review(review, "Approved", null, CaseActor, CaseTime)).IsFailure);
        second = (await requirements.Update(second, second.Requirement.Approve().Value, CaseActor, CaseTime, "Approved")).Value;
        var approved = await CaseReview(store, review, "Approved");
        Assert.True((await store.ChangeLink(approved, "remove", CaseEndpoint(target), null, CaseActor, CaseTime)).IsFailure);
        var target2 = (await requirements.CreateSuccessor(target, "Target v2", CaseActor, CaseTime)).Value;
        var next = (await store.CreateSuccessor(approved, "Case v2", CaseActor, CaseTime)).Value;
        Assert.Equal(CaseEndpoint(target), next.TestCase.Links[0]);
        count = (await query.FindAsync(new())).Count;
        Assert.True((await store.ChangeLink(next, "add", CaseEndpoint(target2), null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(next, "retarget", CaseEndpoint(target), CaseEndpoint(second), CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(next, "retarget", CaseEndpoint(target), new(target.Requirement.InternalId, Guid.NewGuid()), CaseActor, CaseTime)).IsFailure);
        Assert.Equal(count, (await query.FindAsync(new())).Count);
        next = (await store.ChangeLink(next, "retarget", CaseEndpoint(target), CaseEndpoint(target2), CaseActor, CaseTime)).Value;
        Assert.Equal(CaseEndpoint(target2), next.TestCase.Links[0]);
        Assert.Equal(CaseEndpoint(second), next.TestCase.Links[1]);
        Assert.Equal(CaseEndpoint(target), (await store.Find(approved.TestCase.InternalId, approved.TestCase.VersionId)).Value.Value.TestCase.Links[0]);
        var history = (await store.History(next.TestCase.InternalId)).Value;
        var retarget = Assert.Single(history, item => item.Action == "test-case.link.retarget");
        Assert.Equal(CaseEndpoint(target), retarget.Change.Before!.Links![0]);
        Assert.Equal(CaseEndpoint(target2), retarget.Change.After.Links![0]);
        Assert.Equal(CaseActor, retarget.Actor); Assert.Equal(CaseTime, retarget.OccurredAt);
        var audit = Assert.Single(await query.FindAsync(new()), item => item.Action == "test-case.link.retarget");
        Assert.Equal(new AuditTarget.Identified(next.TestCase.InternalId.ToString()), audit.Target);
        Assert.Equal(next.TestCase.VersionId, audit.TestCaseChange!.VersionId);
        Assert.Equal(CaseEndpoint(target), audit.TestCaseChange.Before!.Links![0]);
        Assert.Equal(CaseEndpoint(target2), audit.TestCaseChange.After.Links![0]);
        var beforeNoOp = next;
        next = (await store.ChangeLink(next, "retarget", CaseEndpoint(target2), CaseEndpoint(target2), CaseActor, CaseTime)).Value;
        Assert.Equal(beforeNoOp.Revision, next.Revision);
        count = (await query.FindAsync(new())).Count;
        next = (await store.ChangeLink(next, "remove", CaseEndpoint(foreign), null, CaseActor, CaseTime)).Value;
        Assert.Equal(beforeNoOp.Revision, next.Revision);
        Assert.True((await store.Review(next, "secret", null, CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.ChangeLink(next, "secret", CaseEndpoint(target), null, CaseActor, CaseTime)).IsFailure);
        Assert.Equal(count, (await query.FindAsync(new())).Count);
        next = (await store.ChangeLink(next, "remove", CaseEndpoint(second), null, CaseActor, CaseTime)).Value;
        next = (await store.ChangeLink(next, "remove", CaseEndpoint(target2), null, CaseActor, CaseTime)).Value;
        Assert.Empty(next.TestCase.Links);
        Assert.True((await store.Review(next, "InReview", null, CaseActor, CaseTime)).IsFailure);
    }

    [PostgresFact]
    public async Task TestCaseEveryMutationRejectsRetirementIncludingRacesAndReadsRemainAvailable()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var requirements = new PostgresRequirementStore(database.ConnectionString); var store = new PostgresTestCaseStore(database.ConnectionString);
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var operation in CaseOperations)
        {
            var product = (await new CreateProduct(products).Execute("P-" + operation, "Product", ProductKind.System, "")).Value;
            var scenario = await PrepareCaseOperation(store, requirements, product.InternalId, operation);
            var count = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var retirement = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", connection, transaction);
                retirement.Parameters.AddWithValue(product.InternalId); await retirement.ExecuteNonQueryAsync();
                var pending = RunCaseOperation(store, scenario, operation);
                await transaction.CommitAsync();
                Assert.True((await pending).IsFailure, operation);
            }
            Assert.True((await RunCaseOperation(store, scenario, operation)).IsFailure);
            Assert.True((await store.Reopen(scenario.Row, "Reason", CaseActor, CaseTime)).IsFailure);
            Assert.Equal(count, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            Assert.True(scenario.Row.TestCase.SameState((await store.Find(scenario.Row.TestCase.InternalId)).Value.Value.TestCase));
            Assert.NotEmpty((await store.List(product.InternalId)).Value);
            Assert.NotEmpty((await store.Versions(scenario.Row.TestCase.InternalId)).Value);
            Assert.NotEmpty((await store.History(scenario.Row.TestCase.InternalId)).Value);
        }
    }

    [PostgresFact]
    public async Task TestCaseStaleMutationsAndConcurrentLinkContentAndReviewChangesCannotOverwriteWinners()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(database.ConnectionString); var store = new PostgresTestCaseStore(database.ConnectionString);
        foreach (var operation in CaseOperations.Where(operation => operation != "create"))
        {
            var scenario = await PrepareCaseOperation(store, requirements, product.InternalId, operation);
            var stale = scenario with { Row = scenario.Row with { Revision = scenario.Row.Revision + 1 } };
            var count = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
            Assert.True((await RunCaseOperation(store, stale, operation)).IsFailure, operation);
            Assert.Equal(count, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        }
        var fixture = await PrepareCaseOperation(store, requirements, product.InternalId, "retarget");
        var results = await Task.WhenAll(
            store.Update(fixture.Row, CaseContent("Racing edit"), CaseActor, CaseTime),
            store.ChangeLink(fixture.Row, "remove", fixture.Target, null, CaseActor, CaseTime),
            store.ChangeLink(fixture.Row, "retarget", fixture.Target, fixture.Replacement, CaseActor, CaseTime),
            store.Review(fixture.Row, "InReview", null, CaseActor, CaseTime));
        Assert.Single(results, result => result.IsSuccess);
        var winner = results.Single(result => result.IsSuccess).Value;
        Assert.True(winner.TestCase.SameState((await store.Find(winner.TestCase.InternalId)).Value.Value.TestCase));
        Assert.True((await store.Update(fixture.Row, fixture.Row.TestCase.Content, CaseActor, CaseTime)).IsFailure); // Stale no-op.
        var countAfter = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
        var reopen = await store.Reopen(winner, "Correction", CaseActor, CaseTime);
        Assert.True(reopen.IsFailure); Assert.Equal("Only Approved Test Cases can reopen.", reopen.Error);
        Assert.Equal(countAfter, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
    }

    [PostgresFact]
    public async Task TestCaseApprovalAndRequirementStatusChangesSerializeInBothOrdersWithoutRevokingEarlierApprovals()
    {
        await using var database = await Database.Create();
        var product = (await new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("P", "Product", ProductKind.System, "")).Value;
        var requirements = new PostgresRequirementStore(database.ConnectionString); var store = new PostgresTestCaseStore(database.ConnectionString);
        var target = await ParentApprove(requirements, await ParentDraft(requirements, product.InternalId, "R"));
        var row = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        row = (await store.ChangeLink(row, "add", CaseEndpoint(target), null, CaseActor, CaseTime)).Value;
        row = await CaseReview(store, row, "InReview");
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetWriter = new PostgresRequirementStore(database.ConnectionString, async (transaction, token) =>
        {
            staged.SetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(20), token); await transaction.CommitAsync(token);
        });
        var reopen = targetWriter.Update(target, target.Requirement.Reopen("Correction").Value, CaseActor, CaseTime, "Reopened", "Correction");
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var approval = store.Review(row, "Approved", null, CaseActor, CaseTime);
        finish.SetResult();
        target = (await reopen).Value;
        Assert.True((await approval).IsFailure);
        Assert.Equal("InReview", (await store.Find(row.TestCase.InternalId)).Value.Value.TestCase.Status);
        target = await ParentApprove(requirements, target);
        staged = new(TaskCreationOptions.RunContinuationsAsynchronously); finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var caseWriter = new PostgresTestCaseStore(database.ConnectionString, async (transaction, token) =>
        {
            staged.SetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(20), token); await transaction.CommitAsync(token);
        });
        approval = caseWriter.Review(row, "Approved", null, CaseActor, CaseTime);
        await staged.Task.WaitAsync(TimeSpan.FromSeconds(20));
        reopen = requirements.Update(target, target.Requirement.Reopen("Later correction").Value, CaseActor, CaseTime, "Reopened", "Later correction");
        finish.SetResult();
        Assert.True((await approval).IsSuccess); Assert.True((await reopen).IsSuccess);
        Assert.Equal("Approved", (await store.Find(row.TestCase.InternalId)).Value.Value.TestCase.Status);
        Assert.Single((await store.History(row.TestCase.InternalId)).Value, item => item.Change.Decision == "Approved");
    }
}
