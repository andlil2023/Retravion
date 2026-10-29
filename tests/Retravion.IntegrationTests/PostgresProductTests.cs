using Npgsql;
using Retravion.Application.Product.Create;
using Retravion.Application.Product.Description;
using Retravion.Application.Product.Retrieve;
using Retravion.Application.Product.Status;
using Retravion.Application.Product.Edit;
using Retravion.Domain.Product;
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
        Assert.True((await create.Execute(firstId, "Shared name", ProductKind.System, "Supplied description")).IsSuccess);
        Assert.True((await create.Execute(secondId, "Shared name", ProductKind.Firmware, null)).IsSuccess);
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
        Assert.True((await create.Execute(firstId, "Overwrite", ProductKind.System, "No")).IsFailure);
        Assert.Equal(first, (await retrieve.Execute(firstId)).Value);
        // TC-PRODUCT-002: invalid names create no retrievable Product.
        foreach (var name in new string?[] { null, "", " \t\n" })
        {
            var invalidId = Guid.NewGuid().ToString();
            Assert.True((await create.Execute(invalidId, name, ProductKind.System, "")).IsFailure);
            Assert.True((await store.Find(invalidId)).Value.HasNoValue);
        }
        // TC-PRODUCT-004: retirement/reactivation survive a new store instance.
        var status = new ChangeProductStatus(new PostgresProductStore(database.ConnectionString));
        Assert.True((await status.Execute(firstId, first.InternalId, false)).IsSuccess);
        Assert.False((await retrieve.Execute(firstId)).Value.IsActive);
        Assert.True((await status.Execute(firstId, first.InternalId, false)).IsFailure);
        Assert.True((await status.Execute(firstId, first.InternalId, true)).IsSuccess);
        Assert.Equal(first, (await retrieve.Execute(firstId)).Value);
        Assert.True((await status.Execute(firstId, first.InternalId, true)).IsFailure);
        Assert.True((await status.Execute("missing", first.InternalId, false)).IsFailure);
        Assert.True((await retrieve.Execute("missing")).IsFailure);
        Assert.Equal(second, (await retrieve.Execute(secondId)).Value);
        // IDs are trimmed and compared without regard to case.
        Assert.True((await create.Execute("Case", "Upper", ProductKind.System, "")).IsSuccess);
        Assert.True((await create.Execute("case", "Lower", ProductKind.System, "")).IsFailure);
        Assert.True((await create.Execute(" case ", "Spaces", ProductKind.System, "")).IsFailure);
        Assert.True((await store.Update(first, ProductState.Create("changed-id", first.Name, first.Kind, first.Description).Value.Retire().Value)).IsFailure);
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
            var created = await create.Execute(boundary, "Boundary", ProductKind.System, "");
            Assert.True(created.IsSuccess);
            Assert.Equal(boundary, (await retrieve.Execute(boundary)).Value.Id);
            Assert.True((await create.Execute(boundary, "Duplicate", ProductKind.System, "")).IsFailure);
            var oversized = boundary + character;
            var rejected = await create.Execute(oversized, "Too long", ProductKind.System, "");
            Assert.True(rejected.IsFailure);
            Assert.Equal("A Product ID must contain at most 100 Unicode characters.", rejected.Error);
            Assert.True((await store.Find(oversized)).Value.HasNoValue);
        }
        // Regression: this size previously caused PostgreSQL SQLSTATE 54000.
        var longId = string.Concat(Enumerable.Range(0, 160).Select(_ => Guid.NewGuid().ToString("N")));
        Assert.Equal(5120, longId.Length);
        var result = await create.Execute(longId, "Too long", ProductKind.System, "");
        Assert.True(result.IsFailure);
        Assert.Equal("A Product ID must contain at most 100 Unicode characters.", result.Error);
        Assert.True((await store.Find(longId)).Value.HasNoValue);
    }

    [PostgresFact]
    public async Task ConcurrentFirstUseEnforcesUniqueIdsAndStaleStatusWritesDoNotOverwriteState()
    {
        await using var database = await Database.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            new CreateProduct(new PostgresProductStore(database.ConnectionString)).Execute("same-id", $"Name {index}", ProductKind.System, "")));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(7, results.Count(result => result.IsFailure));
        var store = new PostgresProductStore(database.ConnectionString);
        var current = (await store.Find("same-id")).Value.Value;
        var retired = current.Retire().Value;
        Assert.True((await store.Update(current, retired)).IsSuccess);
        Assert.True((await new PostgresProductStore(database.ConnectionString).Update(current, retired)).IsFailure);
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
            "INSERT INTO retravion_product.product VALUES (gen_random_uuid(), '', 'Name', 0, '', TRUE)",
            "INSERT INTO retravion_product.product VALUES (gen_random_uuid(), 'id', '', 0, '', TRUE)",
            "INSERT INTO retravion_product.product VALUES (gen_random_uuid(), 'id', 'Name', 0, NULL, TRUE)",
            "INSERT INTO retravion_product.product VALUES (gen_random_uuid(), repeat('a', 101), 'Name', 0, '', TRUE)",
            "INSERT INTO retravion_product.product VALUES (gen_random_uuid(), 'id', 'Name', 4, '', TRUE)"
        })
        {
            await using var invalid = new NpgsqlCommand(sql, connection);
            await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
        }
        // PostgreSQL btrim does not remove tabs; domain restoration must still reject them.
        await using (var corrupt = new NpgsqlCommand("INSERT INTO retravion_product.product VALUES (gen_random_uuid(), 'invalid', E'\\t', 0, '', TRUE)", connection))
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
        var original = ProductState.Create("description-id", "Name", ProductKind.System, "").Value;
        Assert.True((await store.Create(original)).IsSuccess);
        var update = new UpdateProductDescription(store);
        var markdown = "# Heading\n\n**Formatted** 😀";
        Assert.True((await update.Execute(original.Id, original.InternalId, "", markdown)).IsSuccess);
        var retrieve = new RetrieveProduct(new PostgresProductStore(database.ConnectionString));
        var first = (await retrieve.Execute(original.Id)).Value;
        Assert.Equal(original.Edit(original.Id, original.Name, original.Kind, markdown).Value, first);
        Assert.True((await update.Execute(original.Id, original.InternalId, markdown, "## Updated\n\n*Again*")).IsSuccess);
        var second = (await retrieve.Execute(original.Id)).Value;
        Assert.Equal("## Updated\n\n*Again*", second.Description);
        Assert.Equal(original.Id, second.Id);
        Assert.Equal(original.Name, second.Name);
        Assert.True(second.IsActive);
        Assert.True((await update.Execute(original.Id, original.InternalId, markdown, "stale editor")).IsFailure);
        Assert.True((await store.Update(first, first.Edit(first.Id, first.Name, first.Kind, "stale persistence").Value)).IsFailure);
        Assert.Equal(second, (await retrieve.Execute(original.Id)).Value);
        Assert.True((await store.Update(second, second.Edit(second.Id, second.Name, second.Kind, "new").Value.Retire().Value)).IsFailure);
        Assert.True((await store.Update(second, ProductState.Create("other-id", "Name", ProductKind.System, "new").Value)).IsFailure);
        Assert.True((await store.Update(second, ProductState.Create(second.Id, "Other name", ProductKind.System, "new").Value)).IsFailure);
        // A concurrent retirement rejects a stale description update and all retired edits.
        Assert.True((await store.Update(second, second.Retire().Value)).IsSuccess);
        Assert.True((await store.Update(second, second.Edit(second.Id, second.Name, second.Kind, "retired edit").Value)).IsFailure);
        var retired = (await retrieve.Execute(original.Id)).Value;
        Assert.False(retired.IsActive);
        Assert.Equal(second.Description, retired.Description);
        Assert.True((await update.Execute(original.Id, original.InternalId, retired.Description, "")).IsFailure);
        Assert.Equal(second.Description, (await retrieve.Execute(original.Id)).Value.Description);
        Assert.True((await update.Execute("missing", original.InternalId, "", "new")).IsFailure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.Update(retired, retired.Reactivate().Value, cancelled.Token));
    }

    [PostgresFact]
    public async Task ReactivationAndEditsAreAtomicAndOldIdCanBeReused()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        var create = new CreateProduct(store);
        var first = (await create.Execute("  Ä Case  ", " Name ", ProductKind.System, "before")).Value;
        Assert.Equal("Ä Case", first.Id);
        Assert.Equal("Name", first.Name);
        Assert.True((await create.Execute("ä case", "duplicate", ProductKind.Firmware, "")).IsFailure);
        Assert.True((await new ChangeProductStatus(store).Execute(first.Id, first.InternalId, false)).IsSuccess);
        Assert.True((await create.Execute("Ä CASE", "duplicate", ProductKind.Firmware, "")).IsFailure);
        var edit = new EditProduct(store);
        var retired = (await store.Find(first.Id)).Value.Value;
        Assert.True((await edit.Execute(retired, "new", "other", ProductKind.HardwareDevice, "after", false)).IsFailure);
        Assert.False(retired.IsActive);
        Assert.Equal("before", retired.Description);
        var changed = await edit.Execute(retired, "new", "other", ProductKind.HardwareDevice, "after", true);
        Assert.True(changed.IsSuccess);
        Assert.Equal(first.InternalId, changed.Value.InternalId);
        Assert.True(changed.Value.IsActive);
        Assert.Equal("after", changed.Value.Description);
        Assert.Equal(ProductKind.HardwareDevice, changed.Value.Kind);
        Assert.True((await create.Execute(first.Id, "new owner", ProductKind.SoftwareApplication, "")).IsSuccess);
        Assert.True((await edit.Execute(changed.Value, first.Id, "conflict", ProductKind.System, "", false)).IsFailure);
        Assert.Equal(changed.Value, (await store.Find("new")).Value.Value);
    }

    [PostgresFact]
    public async Task NonAsciiIdsAreCaseInsensitiveInCLocaleDatabase()
    {
        await using var database = await Database.Create(useCLocale: true);
        var store = new PostgresProductStore(database.ConnectionString);
        var create = new CreateProduct(store);
        var original = (await create.Execute("Ä Case", "Name", ProductKind.System, "")).Value;
        Assert.True((await create.Execute("ä case", "Duplicate", ProductKind.Firmware, "")).IsFailure);
        Assert.Equal(original, (await store.Find("ä CASE")).Value.Value);
        Assert.True((await new ChangeProductStatus(store).Execute(original.Id, original.InternalId, false)).IsSuccess);
        Assert.True((await create.Execute("ä case", "Duplicate", ProductKind.System, "")).IsFailure);
        var sigma = (await create.Execute("Σ", "Greek", ProductKind.System, "")).Value;
        Assert.True((await create.Execute("ς", "Duplicate", ProductKind.Firmware, "")).IsFailure);
        Assert.Equal(sigma, (await store.Find("ς")).Value.Value);
        Assert.True((await new ChangeProductStatus(store).Execute(sigma.Id, sigma.InternalId, false)).IsSuccess);
        Assert.True((await create.Execute("ς", "Duplicate", ProductKind.System, "")).IsFailure);
        Assert.False((await store.Find("ς")).Value.Value.IsActive);
    }

    [PostgresFact]
    public async Task StaleActionsCannotTargetReusedIdsOrOverwriteNewerEdits()
    {
        await using var database = await Database.Create();
        var store = new PostgresProductStore(database.ConnectionString);
        var create = new CreateProduct(store);
        var edit = new EditProduct(store);
        var original = (await create.Execute("old", "original", ProductKind.System, "")).Value;
        var changed = await edit.Execute(original, "new", "new name", ProductKind.Firmware, "", false);
        Assert.True(changed.IsSuccess);
        Assert.True((await edit.Execute(original, "old", "stale", ProductKind.System, "lost", false)).IsFailure);
        Assert.Equal(changed.Value, (await store.Find("new")).Value.Value);
        var other = (await create.Execute("old", "other", ProductKind.System, "")).Value;
        Assert.NotEqual(original.InternalId, other.InternalId);
        Assert.True((await new UpdateProductDescription(store).Execute("old", original.InternalId, "", "wrong target")).IsFailure);
        Assert.True((await new ChangeProductStatus(store).Execute("old", original.InternalId, false)).IsFailure);
        Assert.True((await edit.Execute(original, "old", "wrong target", ProductKind.System, "", false)).IsFailure);
        Assert.Equal(other, (await store.Find("old")).Value.Value);
        Assert.Equal(changed.Value, (await store.Find("new")).Value.Value);
    }

    private sealed class Database(string connectionString, string serverConnection, string name) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public static async Task<Database> Create(bool useCLocale = false)
        {
            var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
            var name = $"retravion_product_{Guid.NewGuid():N}";
            await using var server = new NpgsqlConnection(serverConnection);
            await server.OpenAsync();
            var locale = useCLocale ? " TEMPLATE template0 LC_COLLATE 'C' LC_CTYPE 'C'" : "";
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}{locale}", server);
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
