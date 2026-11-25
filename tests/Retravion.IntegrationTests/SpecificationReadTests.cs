using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Product;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Identity.PasswordReplacement;
using Retravion.Web.Features.Specification;
using Xunit;
using ProductRecord = Retravion.Domain.Product.Product;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task StorageEnforcesTrimmedCaseInsensitiveIdentityWithinProductAndRequiresExistingProduct()
    {
        await using var database = await Database.Create();
        var specifications = new PostgresSpecificationReadStore(database.ConnectionString);
        var products = new PostgresProductStore(database.ConnectionString);
        // First use on an empty database, including competing schema initialization by both owners.
        var productRead = products.List();
        var empty = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => specifications.List(Guid.NewGuid())));
        Assert.All(empty, result => Assert.Empty(result.Value));
        Assert.Empty((await productRead).Value);
        var first = await Product(products, "first");
        var second = await Product(products, "second");
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await Seed(connection, first.InternalId, Guid.NewGuid(), "\tSpÉc-42\u2003");
        await Seed(connection, second.InternalId, Guid.NewGuid(), "spéc-42");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => Seed(connection, first.InternalId, Guid.NewGuid(), " spéc-42 "));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("specification_product_id_unique", duplicate.ConstraintName);
        var missing = await Assert.ThrowsAsync<PostgresException>(() => Seed(connection, Guid.NewGuid(), Guid.NewGuid(), "orphan"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, missing.SqlState);
    }

    [PostgresFact]
    public async Task AuthorizedReadsAreProductScopedMapDraftAndDoNotAudit()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var first = await Product(products, "first");
        var second = await Product(products, "second", retired: true);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = database.ConnectionString,
                    ["InitialAdmin:Username"] = "specification-test-admin",
                    ["InitialAdmin:Password"] = "SpecificationTest123!"
                })));
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditSubmission>();
        Assert.Equal(AuditSubmissionResult.Success, await audit.SubmitAsync(new AuditInformation(new AuditActor.Unknown(),
            DateTimeOffset.UtcNow, "test.baseline", new AuditTarget.NotApplicable(), AuditActionOutcome.Success)));
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var user = await Principal(sessions, "User");
        var admin = await Principal(sessions, "Administrator");
        var restricted = await Principal(sessions, "User", restricted: true);
        var wrongRole = await Principal(sessions, "Other");
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        foreach (var denied in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted, wrongRole })
        {
            Assert.Equal("Access denied.", (await operations.List(denied, first.InternalId)).Error);
            Assert.Equal("Access denied.", (await operations.Find(denied, first.InternalId, firstId)).Error);
        }
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_specification.specification') IS NOT NULL", connection))
            Assert.Equal(false, await exists.ExecuteScalarAsync());

        Assert.Empty((await operations.List(user, first.InternalId)).Value);
        await Seed(connection, first.InternalId, firstId, " DEV-1 ", "# Markdown");
        await Seed(connection, second.InternalId, secondId, "DEV-1");
        Assert.Equal(firstId, Assert.Single((await operations.List(user, first.InternalId)).Value).InternalId);
        Assert.Equal(secondId, Assert.Single((await operations.List(admin, second.InternalId)).Value).InternalId);
        Assert.True((await operations.Find(user, first.InternalId, secondId)).Value.HasNoValue);
        Assert.True((await operations.Find(admin, second.InternalId, firstId)).Value.HasNoValue);
        Assert.True((await operations.Find(user, first.InternalId, Guid.NewGuid())).Value.HasNoValue);
        var draft = Assert.IsType<DraftSpecification>((await operations.Find(user, first.InternalId, firstId)).Value.Value);
        Assert.Equal(firstId, draft.InternalId);
        Assert.Equal(first.InternalId, draft.ProductInternalId);
        Assert.Equal("DEV-1", draft.Id);
        Assert.Equal("Seeded specification", draft.Title);
        Assert.Equal("# Markdown", draft.Description);
        Assert.Equal("", (await operations.Find(admin, second.InternalId, secondId)).Value.Value.Description);

        // A fixture-only external change to the editable ID does not change the detail lookup key.
        await using (var rename = new NpgsqlCommand("UPDATE retravion_specification.specification SET id = 'DEV-2' WHERE internal_id = $1", connection))
        {
            rename.Parameters.AddWithValue(firstId);
            Assert.Equal(1, await rename.ExecuteNonQueryAsync());
        }
        var renamed = (await operations.Find(user, first.InternalId, firstId)).Value.Value;
        Assert.Equal(draft.InternalId, renamed.InternalId);
        Assert.Equal("DEV-2", renamed.Id);
        await sessions.RemoveAsync(user.FindFirstValue(SessionTicketStore.SessionClaim)!);
        Assert.Equal("Access denied.", (await operations.List(user, first.InternalId)).Error);
        Assert.Equal("Access denied.", (await operations.Find(user, first.InternalId, firstId)).Error);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM retravion_audit.event", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task ReadsRejectInvalidPersistedDraftContentRatherThanReturningARecord()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = await Product(products, "first");
        var specifications = new PostgresSpecificationReadStore(database.ConnectionString);
        Assert.Empty((await specifications.List(product.InternalId)).Value);
        var id = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await Seed(connection, product.InternalId, id, "DEV-1");
        // Simulate corrupted legacy storage by bypassing the title constraint.
        await using var corrupt = new NpgsqlCommand("""
            ALTER TABLE retravion_specification.specification DROP CONSTRAINT specification_title_check;
            UPDATE retravion_specification.specification SET title = E'\t ';
            """, connection);
        await corrupt.ExecuteNonQueryAsync();
        Assert.Equal("The persisted Specification is invalid.", (await specifications.List(product.InternalId)).Error);
        Assert.Equal("The persisted Specification is invalid.", (await specifications.Find(product.InternalId, id)).Error);
    }

    private static async Task<ClaimsPrincipal> Principal(SessionTicketStore sessions, string role, bool restricted = false,
        DateTimeOffset? now = null)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, role), new Claim(ClaimTypes.Role, role)], "Cookies");
        if (restricted) identity.AddClaim(new Claim(PasswordReplacementEndpoint.RestrictedClaim, "true"));
        var key = await sessions.StoreAsync(new AuthenticationTicket(new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = (now ?? DateTimeOffset.UtcNow).AddMinutes(10) }, "Cookies"));
        return (await sessions.RetrieveAsync(key))!.Principal;
    }

    private static async Task<ProductRecord> Product(PostgresProductStore products, string id, bool retired = false)
    {
        var product = ProductRecord.Create(id, id, ProductKind.System, "").Value;
        if (retired) product = product.Retire().Value;
        Assert.True((await products.Create(product)).IsSuccess);
        return product;
    }

    private static async Task Seed(NpgsqlConnection connection, Guid productId, Guid internalId, string id, string description = "")
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO retravion_specification.specification (product_internal_id, internal_id, id, title, description, status)
            VALUES ($1, $2, $3, 'Seeded specification', $4, 'Draft')
            """, connection);
        insert.Parameters.AddWithValue(productId);
        insert.Parameters.AddWithValue(internalId);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(description);
        await insert.ExecuteNonQueryAsync();
    }

    private sealed class Database(string serverConnection, string name, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;

        public static async Task<Database> Create()
        {
            var serverConnection = Environment.GetEnvironmentVariable("RETRAVION_TEST_POSTGRES")!;
            var name = $"retravion_specification_{Guid.NewGuid():N}";
            await using var server = new NpgsqlConnection(serverConnection);
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", server);
            await create.ExecuteNonQueryAsync();
            return new(serverConnection, name, new NpgsqlConnectionStringBuilder(serverConnection) { Database = name, Pooling = false }.ConnectionString);
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
