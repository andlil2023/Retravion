using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Edit;
using Retravion.Application.Product.Description;
using Retravion.Application.Product.Status;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Xunit;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static readonly AuditActor ProductActor = new AuditActor.Identified("product-author");
    private static readonly DateTimeOffset ProductTime = DateTimeOffset.Parse("2026-01-02T03:04:05.1234567+02:00");

    [PostgresFact]
    public async Task ProductChangesRetainExactSnapshotsAndNoOpsReadsValidationAndStaleWritesProduceNoEvents()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        Task<Result<ProductState>> Change(Func<IProductStore, Task<Result<ProductState>>> change) =>
            store.Execute(ProductActor, ProductTime, change);
        var original = (await Change(s => new CreateProduct(s).Execute("original", "Name", ProductKind.System, "# Initial 😀"))).Value;
        var edited = (await Change(s => new EditProduct(s).Execute(original, "renamed", "New name", ProductKind.Firmware, "after", false))).Value;
        var description = (await Change(s => new UpdateProductDescription(s).Execute(edited.Id, edited.InternalId, edited.Description, "## Description"))).Value;
        var retired = (await Change(s => new ChangeProductStatus(s).Execute(description.Id, description.InternalId, false))).Value;
        var reactivated = (await Change(s => new EditProduct(s).Execute(retired, "renamed-again", "Reactivated", ProductKind.HardwareDevice, "combined", true))).Value;
        Assert.True((await Change(s => new EditProduct(s).Execute(reactivated, reactivated.Id, reactivated.Name, reactivated.Kind, reactivated.Description, false))).IsSuccess);
        Assert.True((await Change(s => new CreateProduct(s).Execute("invalid", " ", ProductKind.System, ""))).IsFailure);
        Assert.True((await Change(s => new CreateProduct(s).Execute(reactivated.Id, "Duplicate", ProductKind.System, ""))).IsFailure);
        Assert.True((await Change(s => new EditProduct(s).Execute(edited, "stale", "Stale", ProductKind.System, "", false))).IsFailure);
        Assert.True((await store.Find(reactivated.Id)).IsSuccess);
        Assert.True((await store.List()).IsSuccess);
        // Reusing an old display ID must not retarget the original Product's history.
        var other = (await Change(s => new CreateProduct(s).Execute(original.Id, "Other", ProductKind.SoftwareApplication, ""))).Value;
        Assert.NotEqual(original.InternalId, other.InternalId);
        var events = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Reverse().ToArray();
        Assert.Equal(new[] { "product.create", "product.edit", "product.edit", "product.retire", "product.reactivate", "product.create" }, events.Select(e => e.Action));
        var after = new[] { original, edited, description, retired, reactivated, other };
        ProductState?[] before = [null, original, edited, description, retired, null];
        static ProductAuditValues Values(ProductState p) => new(p.Id, p.Name, p.Kind.ToString(), p.Description, p.IsActive);
        for (var i = 0; i < events.Length; i++)
        {
            Assert.Equal(ProductActor, events[i].Actor);
            Assert.Equal(ProductTime.UtcTicks, events[i].OccurredAt.UtcTicks);
            Assert.Equal(AuditActionOutcome.Success, events[i].Outcome);
            Assert.Equal(new AuditTarget.Identified(after[i].InternalId.ToString()), events[i].Target);
            Assert.Equal(new ProductAuditDetails(after[i].InternalId, before[i] is { } prior ? Values(prior) : null, Values(after[i])), events[i].ProductChange);
        }
        Assert.Equal(reactivated, (await new PostgresProductStore(database.ConnectionString).Find(reactivated.Id)).Value.Value);
    }

    [PostgresFact]
    public async Task AuditFailureRollsBackEveryProductMutationAndLostCommitAcknowledgementIsUncertain()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        Task<Result<ProductState>> Change(Func<IProductStore, Task<Result<ProductState>>> change) => store.Execute(ProductActor, ProductTime, change);
        var active = (await Change(s => new CreateProduct(s).Execute("active", "Active", ProductKind.System, "before"))).Value;
        var retired = (await Change(s => new CreateProduct(s).Execute("retired", "Retired", ProductKind.System, "before"))).Value.Retire().Value;
        var retire = await Change(async s =>
        {
            var current = (await s.Find(retired.Id)).Value.Value;
            var result = await s.Update(current, retired);
            return result.IsSuccess ? Result.Success(retired) : Result.Failure<ProductState>(result.Error);
        });
        Assert.True(retire.IsSuccess);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var inject = new NpgsqlCommand("""
            CREATE FUNCTION retravion_audit.reject_product() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'private failure' USING ERRCODE = '42501'; END $$;
            CREATE TRIGGER reject_product BEFORE INSERT ON retravion_audit.event
            FOR EACH ROW EXECUTE FUNCTION retravion_audit.reject_product();
            """, connection)) await inject.ExecuteNonQueryAsync();
        Assert.True((await Change(s => new CreateProduct(s).Execute("blocked", "Blocked", ProductKind.System, ""))).IsFailure);
        Assert.True((await Change(s => new EditProduct(s).Execute(active, "changed", "Changed", ProductKind.Firmware, "after", false))).IsFailure);
        Assert.True((await Change(s => new UpdateProductDescription(s).Execute(active.Id, active.InternalId, active.Description, "after"))).IsFailure);
        Assert.True((await Change(s => new ChangeProductStatus(s).Execute(active.Id, active.InternalId, false))).IsFailure);
        Assert.True((await Change(s => new EditProduct(s).Execute(retired, "reactivated", "Changed", ProductKind.Firmware, "after", true))).IsFailure);
        Assert.True((await Change(s => new ChangeProductStatus(s).Execute(retired.Id, retired.InternalId, true))).IsFailure);
        Assert.True((await store.Find("blocked")).Value.HasNoValue);
        Assert.Equal(active, (await store.Find(active.Id)).Value.Value);
        Assert.Equal(retired, (await store.Find(retired.Id)).Value.Value);
        Assert.Equal(3, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
        await using (var remove = new NpgsqlCommand("DROP TRIGGER reject_product ON retravion_audit.event", connection)) await remove.ExecuteNonQueryAsync();
        foreach (var cancel in new[] { false, true })
        {
            var uncertainStore = new PostgresProductStore(database.ConnectionString, async (tx, token) =>
            {
                await tx.CommitAsync(token);
                if (cancel) throw new OperationCanceledException();
                throw new IOException("lost acknowledgement");
            });
            var id = cancel ? "uncertain-cancel" : "uncertain";
            var result = await uncertainStore.Execute(ProductActor, ProductTime,
                s => new CreateProduct(s).Execute(id, "Uncertain", ProductKind.System, ""));
            Assert.True(result.IsFailure);
            Assert.Contains("could not be confirmed", result.Error);
            var committed = (await store.Find(id)).Value.Value;
            Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new(Target: new AuditTarget.Identified(committed.InternalId.ToString()))));
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Execute(ProductActor, ProductTime,
            s => new CreateProduct(s).Execute("cancelled", "Cancelled", ProductKind.System, ""), cancelled.Token));
        Assert.True((await store.Find("cancelled")).Value.HasNoValue);
    }

    [PostgresFact]
    public async Task AdditiveAuditUpgradePreservesLegacyEventsAndConcurrentChangesHaveOnlyOneWinner()
    {
        await using var database = await Database.Create();
        var submission = new PostgresAuditSubmission(database.ConnectionString);
        var legacy = new AuditInformation(new AuditActor.Unknown(), ProductTime, "legacy", new AuditTarget.NotApplicable(), AuditActionOutcome.Success);
        Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(legacy));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var oldSchema = new NpgsqlCommand("ALTER TABLE retravion_audit.event DROP COLUMN product_change", connection)) await oldSchema.ExecuteNonQueryAsync();
        Assert.Equal(legacy, Assert.Single(await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())));
        var store = new PostgresProductStore(database.ConnectionString);
        var original = (await store.Execute(ProductActor, ProductTime, s => new CreateProduct(s).Execute("concurrent", "Before", ProductKind.System, ""))).Value;
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => store.Execute(ProductActor, ProductTime,
            s => new EditProduct(s).Execute(original, original.Id, $"Winner {i}", original.Kind, "", false))));
        Assert.Single(results, r => r.IsSuccess);
        var events = await new PostgresAuditQuery(database.ConnectionString).FindAsync(new());
        Assert.Equal(3, events.Count);
        Assert.Equal(legacy, events.Single(e => e.Action == "legacy"));
        Assert.Equal(results.Single(r => r.IsSuccess).Value.Name, events.Single(e => e.Action == "product.edit").ProductChange!.After.Name);
    }
}
