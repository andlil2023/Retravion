using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.TestCase;
using Retravion.Domain.Product;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.TestCase;
using Xunit;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static readonly AuditActor CaseActor = new AuditActor.Identified("case-author");
    private static readonly DateTimeOffset CaseTime = DateTimeOffset.Parse("2026-01-02T03:04:05.1234567+02:00");
    private static TestCaseContent CaseContent(string title = "Title", IEnumerable<TestCaseStep>? steps = null) =>
        TestCaseContent.Create(title, "# Description\n\n| A |\n| --- |\n| B |", "Preconditions", steps ?? [new("First", "Result 1"), new("Second", "Result 2")]).Value;

    [PostgresFact]
    public async Task TestCaseNumbersAreConcurrentProductScopedAndExactReadsNeverSubstituteVersions()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            new PostgresTestCaseStore(database.ConnectionString).Create(product.InternalId, CaseContent($"Case {i}"), CaseActor, CaseTime)));
        Assert.All(results, result => Assert.True(result.IsSuccess, result.IsFailure ? result.Error : ""));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), results.Select(result => result.Value.TestCase.Number).Order());
        Assert.Equal(8, results.Select(result => result.Value.TestCase.InternalId).Distinct().Count());
        Assert.Equal(8, results.Select(result => result.Value.TestCase.VersionId).Distinct().Count());
        var q = (await store.Create(other.InternalId, TestCaseContent.Create(null, null, null, null).Value, CaseActor, CaseTime)).Value;
        Assert.Equal("TC-001", q.TestCase.Id);
        Assert.Empty(q.TestCase.Content.Steps);
        Assert.Equal(8, (await store.List(product.InternalId)).Value.Count);
        Assert.Single((await store.List(other.InternalId)).Value);
        Assert.Empty((await store.List(Guid.NewGuid())).Value);
        var first = results[0].Value;
        var restarted = new PostgresTestCaseStore(database.ConnectionString);
        var exact = (await restarted.Find(first.TestCase.InternalId, first.TestCase.VersionId)).Value.Value;
        Assert.Equal(first.TestCase.InternalId, exact.TestCase.InternalId);
        Assert.Equal(first.TestCase.VersionId, exact.TestCase.VersionId);
        Assert.True(first.TestCase.Content.SameContent(exact.TestCase.Content));
        Assert.True((await restarted.Find(first.TestCase.InternalId, q.TestCase.VersionId)).Value.HasNoValue);
        Assert.True((await restarted.Find(first.TestCase.InternalId, Guid.NewGuid())).Value.HasNoValue);
        Assert.True((await restarted.Find(Guid.NewGuid())).Value.HasNoValue);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Where(e => e.TestCaseChange is not null).ToArray();
        Assert.Equal(9, events.Length);
        Assert.All(events, e =>
        {
            Assert.Equal(CaseActor, e.Actor); Assert.Equal(CaseTime.UtcTicks, e.OccurredAt.UtcTicks);
            Assert.Equal("test-case.create", e.Action); Assert.Equal(AuditActionOutcome.Success, e.Outcome);
            Assert.Null(e.TestCaseChange!.Before); Assert.Equal(1, e.TestCaseChange.VersionNumber);
            Assert.Equal(new AuditTarget.Identified(e.TestCaseChange.InternalId.ToString()), e.Target);
        });
    }

    [PostgresFact]
    public async Task TestCaseEditsPreserveIdentityOrderAuditAndRejectStaleForgedAndRetiredSnapshots()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var first = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        var originalEvent = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Single(e => e.TestCaseChange is not null);
        var current = first;
        foreach (var content in CaseEdits())
        {
            var prior = current;
            current = (await store.Update(current, content, CaseActor, CaseTime)).Value;
            Assert.Equal(prior.Revision + 1, current.Revision);
            Assert.Equal(first.TestCase.InternalId, current.TestCase.InternalId);
            Assert.Equal(first.TestCase.VersionId, current.TestCase.VersionId);
            Assert.Equal(first.TestCase.Id, current.TestCase.Id);
            var persisted = (await store.Find(current.TestCase.InternalId, current.TestCase.VersionId)).Value.Value;
            Assert.True(content.SameContent(persisted.TestCase.Content));
            Assert.Equal(content.Steps.ToArray(), persisted.TestCase.Content.Steps.ToArray());
        }
        var query = new PostgresAuditQuery(database.ConnectionString);
        var events = (await query.FindAsync(new())).Where(e => e.TestCaseChange is not null).ToArray();
        Assert.Equal(1 + CaseEdits().Length, events.Length);
        Assert.True(originalEvent.TestCaseChange!.After.Steps.SequenceEqual(first.TestCase.Content.Steps));
        Assert.Equal("Title", events.Single(e => e.Action == "test-case.create").TestCaseChange!.After.Title);
        Assert.All(events.Where(e => e.Action == "test-case.edit"), e => Assert.NotNull(e.TestCaseChange!.Before));
        var orderedEdits = events.Where(e => e.Action == "test-case.edit").Reverse().ToArray();
        var priorContent = first.TestCase.Content;
        for (var i = 0; i < orderedEdits.Length; i++)
        {
            var details = orderedEdits[i].TestCaseChange!;
            var expectedContent = CaseEdits()[i];
            Assert.Equal(priorContent.Title, details.Before!.Title);
            Assert.Equal(priorContent.Description, details.Before.Description);
            Assert.Equal(priorContent.Preconditions, details.Before.Preconditions);
            Assert.Equal(priorContent.Steps.ToArray(), details.Before.Steps.ToArray());
            Assert.Equal(expectedContent.Title, details.After.Title);
            Assert.Equal(expectedContent.Steps.ToArray(), details.After.Steps.ToArray());
            Assert.Equal(first.TestCase.InternalId, details.InternalId);
            Assert.Equal(first.TestCase.VersionId, details.VersionId);
            priorContent = expectedContent;
        }
        var noOp = await store.Update(current, TestCaseContent.Create(current.TestCase.Content.Title, current.TestCase.Content.Description,
            current.TestCase.Content.Preconditions, current.TestCase.Content.Steps.ToArray()).Value, CaseActor, CaseTime);
        Assert.True(noOp.IsSuccess); Assert.Equal(current.Revision, noOp.Value.Revision);
        Assert.True((await store.Update(first, CaseContent("Stale"), CaseActor, CaseTime)).IsFailure);
        var forged = Case.Restore(first.TestCase.InternalId, product.InternalId, Guid.NewGuid(), first.TestCase.Number, current.TestCase.Content).Value;
        Assert.True((await store.Update(new(forged, current.Revision), CaseContent("Forged"), CaseActor, CaseTime)).IsFailure);
        var wrongNumber = Case.Restore(first.TestCase.InternalId, product.InternalId, first.TestCase.VersionId, 999, current.TestCase.Content).Value;
        Assert.True((await store.Update(new(wrongNumber, current.Revision), CaseContent("Forged"), CaseActor, CaseTime)).IsFailure);
        Assert.Equal(events.Length, (await query.FindAsync(new())).Count(e => e.TestCaseChange is not null));
        var races = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => store.Update(current, CaseContent($"Winner {i}"), CaseActor, CaseTime)));
        Assert.Single(races, result => result.IsSuccess);
        var winner = races.Single(result => result.IsSuccess).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var retirement = new NpgsqlCommand("UPDATE retravion_product.product SET is_active=false WHERE internal_id=$1", connection, transaction);
            retirement.Parameters.AddWithValue(product.InternalId); await retirement.ExecuteNonQueryAsync();
            var pending = store.Update(winner, CaseContent("Retirement race"), CaseActor, CaseTime);
            await transaction.CommitAsync();
            Assert.True((await pending).IsFailure);
        }
        Assert.True((await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.Update(winner, CaseContent(), CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.Find(first.TestCase.InternalId)).Value.HasValue);
        Assert.Single((await store.List(product.InternalId)).Value);
        Assert.True(winner.TestCase.Content.SameContent((await store.Find(first.TestCase.InternalId)).Value.Value.TestCase.Content));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Find(first.TestCase.InternalId, cancellationToken: cancelled.Token));
    }

    private static TestCaseContent[] CaseEdits() =>
    [
        CaseContent(" Fields \n😀"),
        CaseContent(" Fields \n😀", [new("First", "Result 1"), new("Second", "Result 2"), new("Added", "")]),
        CaseContent(" Fields \n😀", [new("Edited", "Changed result"), new("Second", "Result 2"), new("Added", "")]),
        CaseContent(" Fields \n😀", [new("Added", ""), new("Second", "Result 2"), new("Edited", "Changed result")]),
        CaseContent(" Fields \n😀", [new("Added", ""), new("Edited", "Changed result")]),
        TestCaseContent.Create("", "", "", []).Value
    ];

    [PostgresFact]
    public async Task TestCaseAuditAndBusinessFailuresRollbackCreationAllocationAndEveryEditKind()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var first = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var table in new[] { "retravion_audit.event", "retravion_test_case.test_case" })
        {
            await using (var inject = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION public.reject_test_case() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$;
                CREATE TRIGGER reject_test_case BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.reject_test_case();
                """, connection)) await inject.ExecuteNonQueryAsync();
            Assert.True((await store.Create(product.InternalId, CaseContent("Rejected creation"), CaseActor, CaseTime)).IsFailure);
            foreach (var content in CaseEdits())
            {
                Assert.True((await store.Update(first, content, CaseActor, CaseTime)).IsFailure);
                var unchanged = (await store.Find(first.TestCase.InternalId)).Value.Value;
                Assert.Equal(first.Revision, unchanged.Revision);
                Assert.True(first.TestCase.Content.SameContent(unchanged.TestCase.Content));
            }
            Assert.Single((await store.List(product.InternalId)).Value);
            Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new()), e => e.TestCaseChange is not null);
            await using (var remove = new NpgsqlCommand($"DROP TRIGGER reject_test_case ON {table}", connection)) await remove.ExecuteNonQueryAsync();
        }
        var next = (await store.Create(product.InternalId, CaseContent("Next"), CaseActor, CaseTime)).Value;
        Assert.Equal("TC-002", next.TestCase.Id);
        Assert.True((await store.Create(product.InternalId, CaseContent(), new AuditActor.Unknown(), CaseTime)).IsFailure);
        Assert.True((await store.Create(Guid.NewGuid(), CaseContent(), CaseActor, CaseTime)).IsFailure);
        Assert.True((await store.Update(first, CaseContent(), new AuditActor.Unknown(), CaseTime)).IsFailure);
        Assert.Equal(2, (await store.List(product.InternalId)).Value.Count);
    }

    [PostgresFact]
    public async Task TestCaseUncertainCreationAndEveryEditKindRemainAtomicInBothActualOutcomes()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var current = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        foreach (var committed in new[] { false, true })
        {
            var uncertain = new PostgresTestCaseStore(database.ConnectionString, async (transaction, token) =>
            {
                if (committed) await transaction.CommitAsync(token);
                throw new OperationCanceledException("lost confirmation");
            });
            var beforeCount = (await store.List(product.InternalId)).Value.Count;
            var beforeEvents = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
            var creation = await uncertain.Create(product.InternalId, CaseContent("Uncertain"), CaseActor, CaseTime);
            Assert.True(creation.IsFailure); Assert.Contains("could not be confirmed", creation.Error); Assert.Contains("Reload", creation.Error);
            Assert.Equal(beforeCount + (committed ? 1 : 0), (await store.List(product.InternalId)).Value.Count);
            Assert.Equal(beforeEvents + (committed ? 1 : 0), (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            foreach (var content in CaseEdits())
            {
                beforeEvents = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
                var result = await uncertain.Update(current, content, CaseActor, CaseTime);
                Assert.True(result.IsFailure); Assert.Contains("could not be confirmed", result.Error); Assert.Contains("do not retry automatically", result.Error);
                var persisted = (await store.Find(current.TestCase.InternalId)).Value.Value;
                Assert.True((committed ? content : current.TestCase.Content).SameContent(persisted.TestCase.Content));
                Assert.Equal(current.Revision + (committed ? 1 : 0), persisted.Revision);
                Assert.Equal(beforeEvents + (committed ? 1 : 0), (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
                current = persisted;
            }
        }
    }

    [PostgresFact]
    public async Task TestCaseInvalidPersistedStepsFailExplicitlyAndFreshReadsDoNotNeedProductStorage()
    {
        await using var database = await Database.Create();
        var store = new PostgresTestCaseStore(database.ConnectionString);
        Assert.Empty((await store.List(Guid.NewGuid())).Value);
        Assert.True((await store.Create(Guid.NewGuid(), CaseContent(), CaseActor, CaseTime)).IsFailure);
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var created = (await store.Create(product.InternalId, CaseContent(), CaseActor, CaseTime)).Value;
        await using var connection = new NpgsqlConnection(database.ConnectionString); await connection.OpenAsync();
        foreach (var steps in new[] { "[null]", "[{}]", "[{\"Action\":null,\"ExpectedResult\":\"x\"}]", "[1]" })
        {
            await using var corrupt = new NpgsqlCommand("UPDATE retravion_test_case.test_case SET steps=$1::jsonb", connection);
            corrupt.Parameters.AddWithValue(steps); await corrupt.ExecuteNonQueryAsync();
            Assert.True((await store.Find(created.TestCase.InternalId)).IsFailure);
            Assert.True((await store.List(product.InternalId)).IsFailure);
        }
    }
}
