using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task SpecificationContentEditsInPlaceAndAuditsOnlyEffectiveChanges()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var other = await Product(products, "second");
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var initial = (await operations.Create(principal, product.InternalId, "DEV-1", "Original", "# Prior")).Value;
        var otherDraft = (await operations.Create(principal, other.InternalId, "dev-2", "Unrelated", null)).Value;
        var markdown = new string('é', 524288);
        clock.Now = CreateTime.AddMinutes(1);

        var updated = (await operations.EditContent(principal, product.InternalId, initial.InternalId,
            "\t DEV-2\u2003", "New title", markdown)).Value;
        Assert.Equal(initial.InternalId, updated.InternalId);
        Assert.Equal(initial.ProductInternalId, updated.ProductInternalId);
        Assert.Equal("DEV-2", updated.Id);
        Assert.Equal("New title", updated.Title);
        Assert.Equal(markdown, updated.Description);
        Assert.Equal(updated, Assert.Single((await operations.List(principal, product.InternalId)).Value));
        Assert.Equal(otherDraft, Assert.Single((await operations.List(principal, other.InternalId)).Value));
        Assert.Equal(updated, (await operations.EditContent(principal, product.InternalId, initial.InternalId,
            " DEV-2 ", updated.Title, markdown)).Value);

        var audit = Assert.Single(await ContentEvents(db));
        var updatedValues = new SpecificationAuditValues(product.InternalId, "DEV-2", "New title", markdown, "Draft");
        Assert.Equal(new CreateEvent("User", clock.Now.UtcTicks, "identified", initial.InternalId.ToString(), "success",
            new SpecificationAuditDetails(initial.InternalId,
                new SpecificationAuditValues(product.InternalId, "DEV-1", "Original", "# Prior", "Draft"), updatedValues)), audit);

        var recased = (await operations.EditContent(principal, product.InternalId, initial.InternalId,
            "dev-2", updated.Title, markdown)).Value;
        Assert.Equal("dev-2", recased.Id);
        var cleared = (await operations.EditContent(principal, product.InternalId, initial.InternalId,
            recased.Id, recased.Title, null)).Value;
        Assert.Equal("", cleared.Description);
        Assert.Equal(cleared, (await operations.EditContent(principal, product.InternalId, initial.InternalId,
            cleared.Id, cleared.Title, "")).Value);
        Assert.Equal(cleared, (await operations.Find(principal, product.InternalId, initial.InternalId)).Value.Value);
        var events = await ContentEvents(db);
        var recasedValues = updatedValues with { Id = "dev-2" };
        Assert.Equal(3, events.Count);
        Assert.Equal(new SpecificationAuditDetails(initial.InternalId, updatedValues, recasedValues), events[1].Change);
        Assert.Equal(new SpecificationAuditDetails(initial.InternalId, recasedValues, recasedValues with { Description = "" }), events[2].Change);
    }

    [PostgresFact]
    public async Task SpecificationContentRejectsInvalidValuesAndProductLocalIdCollisionsWithoutAuditing()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var original = (await operations.Create(principal, product.InternalId, "DEV-1", "Original", "# Prior")).Value;
        var taken = (await operations.Create(principal, product.InternalId, "SpÉc-42", "Other", null)).Value;
        Assert.Equal("That Specification ID is already in use in this Product.",
            (await operations.EditContent(principal, product.InternalId, original.InternalId, "\t spéc-42\u2003", "Collision", null)).Error);
        var longId = string.Concat(Enumerable.Range(0, 64).Select(i => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i)))));
        Assert.Equal("The Specification ID is too large to store.",
            (await operations.EditContent(principal, product.InternalId, original.InternalId, longId, "Title", null)).Error);
        foreach (var (id, title, description) in new (string?, string?, string?)[]
        {
            (null, "Title", null), ("\t", "Title", null), ("NEW", null, null), ("NEW", "\t", null),
            ("NEW", "Title", new string('é', 524288) + "!"), ("NEW", "Title", "\0"), ("NEW", "\uD800", null)
        })
            Assert.True((await operations.EditContent(principal, product.InternalId, original.InternalId, id, title, description)).IsFailure);
        Assert.Equal(original, (await operations.Find(principal, product.InternalId, original.InternalId)).Value.Value);
        Assert.Equal(taken, (await operations.Find(principal, product.InternalId, taken.InternalId)).Value.Value);
        Assert.Empty(await ContentEvents(db));
    }

    [PostgresFact]
    public async Task SpecificationContentRejectsWrongProductRetirementAndPersistedNonDraftStates()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var other = await Product(products, "second");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await operations.Create(principal, product.InternalId, "DEV-1", "Original", "# Prior")).Value;
        var retiringDraft = (await operations.Create(principal, other.InternalId, "DEV-1", "Other", null)).Value;
        Assert.Equal("The Specification was not found in this Product.",
            (await operations.EditContent(principal, other.InternalId, draft.InternalId, "NEW", "Title", null)).Error);
        Assert.True((await products.Update(other, other.Retire().Value)).IsSuccess);
        Assert.Contains("retired or no longer exists",
            (await operations.EditContent(principal, other.InternalId, retiringDraft.InternalId, "NEW", "Title", null)).Error);
        Assert.Equal(retiringDraft, (await operations.Find(principal, other.InternalId, retiringDraft.InternalId)).Value.Value);

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        // T004 must protect the write even though lifecycle transitions and their schema are not installed yet.
        await using (var enableStates = new NpgsqlCommand("""
            ALTER TABLE retravion_specification.specification DROP CONSTRAINT specification_status_check
            """, connection))
            await enableStates.ExecuteNonQueryAsync();
        foreach (var status in new[] { "In Review", "Approved", "Implemented" })
        {
            await using var move = new NpgsqlCommand("""
                UPDATE retravion_specification.specification SET status = $1 WHERE internal_id = $2
                """, connection);
            move.Parameters.AddWithValue(status);
            move.Parameters.AddWithValue(draft.InternalId);
            Assert.Equal(1, await move.ExecuteNonQueryAsync());
            Assert.Equal("Only Draft Specification content can be edited.",
                (await operations.EditContent(principal, product.InternalId, draft.InternalId, "NEW", "Title", null)).Error);
            await using var inspect = new NpgsqlCommand("""
                SELECT id, title, description, status FROM retravion_specification.specification WHERE internal_id = $1
                """, connection);
            inspect.Parameters.AddWithValue(draft.InternalId);
            await using var row = await inspect.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(draft.Id, row.GetString(0));
            Assert.Equal(draft.Title, row.GetString(1));
            Assert.Equal(draft.Description, row.GetString(2));
            Assert.Equal(status, row.GetString(3));
        }
        Assert.Empty(await ContentEvents(db));
    }

    [PostgresFact]
    public async Task SpecificationContentAuditsDenialAndRollsBackAnEditOnAuditFailure()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "first");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var principal = await Principal(sessions, "User", now: CreateTime);
        var revoked = await Principal(sessions, "User", now: CreateTime);
        var original = (await operations.Create(principal, product.InternalId, "DEV-1", "Original", "# Prior")).Value;
        await sessions.RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        Assert.Equal("Access denied.", (await operations.EditContent(revoked, product.InternalId, original.InternalId,
            "PRIVATE-ID", "PRIVATE-TITLE", "SECRET")).Error);
        Assert.Equal(original, (await operations.Find(principal, product.InternalId, original.InternalId)).Value.Value);
        var denial = new CreateEvent("User", CreateTime.UtcTicks, "unknown", null, "failure", null);
        Assert.Equal(denial, Assert.Single(await ContentEvents(db)));

        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        Assert.Equal("The Specification operation could not be completed.",
            (await operations.EditContent(principal, product.InternalId, original.InternalId, "NEW", "New title", "# Changed")).Error);
        Assert.Equal(original, (await operations.Find(principal, product.InternalId, original.InternalId)).Value.Value);
        Assert.Equal(denial, Assert.Single(await ContentEvents(db)));
    }

    private static Task<List<CreateEvent>> ContentEvents(Database db) => SpecificationEvents(db, "specification.edit-content");
}
