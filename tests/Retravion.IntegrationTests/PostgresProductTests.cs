using Npgsql;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Description;
using Retravion.Application.Product.Retrieve;
using Retravion.Application.Product.Status;
using Retravion.Infrastructure.Product;
using Xunit;
using ProductState = Retravion.Domain.Product.Product;

namespace Retravion.IntegrationTests;

public sealed class PostgresProductTests
{
    [PostgresFact]
    public async Task ProductAcceptanceCasesPersistAcrossStoresAndRejectDuplicateIdsAndMissingNames()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        var create = new CreateProduct(store);
        var firstId = Guid.NewGuid().ToString();
        var secondId = "Custom / ID ? # & + 😀";
        // TC-PRODUCT-001: distinct generated/custom IDs, optional description, active default.
        Assert.True((await create.Execute(firstId, "Shared name", "Supplied description")).IsSuccess);
        Assert.True((await create.Execute(secondId, "Shared name", null)).IsSuccess);
        var retrieve = new RetrieveProduct(new PostgresProductStore(database.ConnectionString));
        var first = (await retrieve.Execute(firstId)).Value;
        var second = (await retrieve.Execute(secondId)).Value;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Shared name", first.Name);
        Assert.Equal("Shared name", second.Name);
        Assert.Equal("Supplied description", first.Description);
        Assert.Equal("", second.Description);
        Assert.True(first.IsActive);
        Assert.True(second.IsActive);
        Assert.True((await create.Execute(firstId, "Overwrite", "No")).IsFailure);
        Assert.Equal(first, (await retrieve.Execute(firstId)).Value);
        // TC-PRODUCT-002: invalid names create no retrievable Product.
        foreach (var name in new string?[] { null, "", " \t\n" })
        {
            var invalidId = Guid.NewGuid().ToString();
            Assert.True((await create.Execute(invalidId, name, "")).IsFailure);
            Assert.True((await store.Find(invalidId)).Value.HasNoValue);
        }
        // TC-PRODUCT-004: retirement/reactivation survive a new store instance.
        var status = new ChangeProductStatus(new PostgresProductStore(database.ConnectionString));
        Assert.True((await status.Execute(firstId, false)).IsSuccess);
        Assert.False((await retrieve.Execute(firstId)).Value.IsActive);
        Assert.True((await status.Execute(firstId, false)).IsFailure);
        Assert.True((await status.Execute(firstId, true)).IsSuccess);
        Assert.Equal(first, (await retrieve.Execute(firstId)).Value);
        Assert.True((await status.Execute(firstId, true)).IsFailure);
        Assert.True((await status.Execute("missing", false)).IsFailure);
        Assert.True((await retrieve.Execute("missing")).IsFailure);
        Assert.Equal(second, (await retrieve.Execute(secondId)).Value);
        // Text IDs match exactly and cannot be changed by the status persistence operation.
        Assert.True((await create.Execute("Case", "Upper", "")).IsSuccess);
        Assert.True((await create.Execute("case", "Lower", "")).IsSuccess);
        Assert.True((await create.Execute(" case ", "Spaces", "")).IsSuccess);
        Assert.True((await store.ChangeStatus(first, ProductState.Create("changed-id", first.Name, first.Description).Value.Retire().Value)).IsFailure);
        Assert.Equal(first, (await retrieve.Execute(firstId)).Value);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Find(firstId, cancelled.Token));
    }

    [PostgresFact]
    public async Task IdLengthBoundaryPersistsUnicodeAndRejectsOversizedIdsWithoutProviderErrors()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        var create = new CreateProduct(store);
        var retrieve = new RetrieveProduct(new PostgresProductStore(database.ConnectionString));
        foreach (var character in new[] { "a", "😀" })
        {
            var boundary = string.Concat(Enumerable.Repeat(character, 100));
            var created = await create.Execute(boundary, "Boundary", "");
            Assert.True(created.IsSuccess);
            Assert.Equal(boundary, (await retrieve.Execute(boundary)).Value.Id);
            Assert.True((await create.Execute(boundary, "Duplicate", "")).IsFailure);
            var oversized = boundary + character;
            var rejected = await create.Execute(oversized, "Too long", "");
            Assert.True(rejected.IsFailure);
            Assert.Equal("A Product ID must contain at most 100 Unicode characters.", rejected.Error);
            Assert.True((await store.Find(oversized)).Value.HasNoValue);
        }
        // Regression: this size previously caused PostgreSQL SQLSTATE 54000.
        var longId = string.Concat(Enumerable.Range(0, 160).Select(_ => Guid.NewGuid().ToString("N")));
        Assert.Equal(5120, longId.Length);
        var result = await create.Execute(longId, "Too long", "");
        Assert.True(result.IsFailure);
        Assert.Equal("A Product ID must contain at most 100 Unicode characters.", result.Error);
        Assert.True((await store.Find(longId)).Value.HasNoValue);
    }

    [PostgresFact]
    public async Task ConcurrentFirstUseEnforcesUniqueIdsAndStaleStatusWritesDoNotOverwriteState()
    {
        await using var database = await Database.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("same-id", $"Name {index}", "")));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(7, results.Count(result => result.IsFailure));
        var store = new PostgresProductStore(database.ConnectionString);
        var current = (await store.Find("same-id")).Value.Value;
        var retired = current.Retire().Value;
        Assert.True((await store.ChangeStatus(current, retired)).IsSuccess);
        Assert.True((await new PostgresProductStore(database.ConnectionString).ChangeStatus(current, retired)).IsFailure);
        Assert.Equal(retired, (await store.Find("same-id")).Value.Value);
    }

    [PostgresFact]
    public async Task InvalidPersistedProductReturnsFailureAndDatabaseConstraintsRejectInvalidRows()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        await store.Find("initialize-schema");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
        {
            "INSERT INTO retravion_product.product VALUES ('', 'Name', '', TRUE)",
            "INSERT INTO retravion_product.product VALUES ('id', '', '', TRUE)",
            "INSERT INTO retravion_product.product VALUES ('id', 'Name', NULL, TRUE)",
            "INSERT INTO retravion_product.product VALUES (repeat('a', 101), 'Name', '', TRUE)"
        })
        {
            await using var invalid = new NpgsqlCommand(sql, connection);
            await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
        }
        // PostgreSQL btrim does not remove tabs; domain restoration must still reject them.
        await using (var corrupt = new NpgsqlCommand("INSERT INTO retravion_product.product VALUES ('invalid', E'\\t', '', TRUE)", connection))
            await corrupt.ExecuteNonQueryAsync();
        var found = await store.Find("invalid");
        Assert.True(found.IsFailure);
        Assert.Equal("The persisted Product is invalid.", found.Error);
    }

    [PostgresFact]
    public async Task DescriptionEditsPersistAndRejectStaleWritesWithoutChangingOtherFields()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        var original = ProductState.Create("description-id", "Name", "").Value;
        Assert.True((await store.Create(original)).IsSuccess);
        var update = new UpdateProductDescription(store);
        var markdown = "# Heading\n\n**Formatted** 😀";
        Assert.True((await update.Execute(original.Id, "", markdown)).IsSuccess);
        var retrieve = new RetrieveProduct(new PostgresProductStore(database.ConnectionString));
        var first = (await retrieve.Execute(original.Id)).Value;
        Assert.Equal(original.UpdateDescription(markdown), first);
        Assert.True((await update.Execute(original.Id, markdown, "## Updated\n\n*Again*")).IsSuccess);
        var second = (await retrieve.Execute(original.Id)).Value;
        Assert.Equal("## Updated\n\n*Again*", second.Description);
        Assert.Equal(original.Id, second.Id);
        Assert.Equal(original.Name, second.Name);
        Assert.True(second.IsActive);
        Assert.True((await update.Execute(original.Id, markdown, "stale editor")).IsFailure);
        Assert.True((await store.UpdateDescription(first, first.UpdateDescription("stale persistence"))).IsFailure);
        Assert.Equal(second, (await retrieve.Execute(original.Id)).Value);
        Assert.True((await store.UpdateDescription(second, second.Retire().Value)).IsFailure);
        Assert.True((await store.UpdateDescription(second, ProductState.Create("other-id", "Name", "new").Value)).IsFailure);
        Assert.True((await store.UpdateDescription(second, ProductState.Create(second.Id, "Other name", "new").Value)).IsFailure);
        // A concurrent status change is preserved by the description-only UPDATE.
        Assert.True((await store.ChangeStatus(second, second.Retire().Value)).IsSuccess);
        Assert.True((await store.UpdateDescription(second, second.UpdateDescription("retired edit"))).IsSuccess);
        var retired = (await retrieve.Execute(original.Id)).Value;
        Assert.False(retired.IsActive);
        Assert.Equal("retired edit", retired.Description);
        Assert.True((await update.Execute(original.Id, retired.Description, "")).IsSuccess);
        Assert.Equal("", (await retrieve.Execute(original.Id)).Value.Description);
        Assert.True((await update.Execute("missing", "", "new")).IsFailure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateDescription(retired, retired.UpdateDescription("new"), cancelled.Token));
    }

    private sealed class Database(string connectionString, string serverConnection, string name) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public static async Task<Database> Create()
        {
            var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
            var name = $"retravion_product_{Guid.NewGuid():N}";
            await using var server = new NpgsqlConnection(serverConnection);
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", server);
            await create.ExecuteNonQueryAsync();
            return new(new NpgsqlConnectionStringBuilder(serverConnection) { Database = name, Pooling = false }.ConnectionString,
                serverConnection, name);
        }
        public async ValueTask DisposeAsync()
        {
            await using var server = new NpgsqlConnection(serverConnection);
            await server.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", server);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
