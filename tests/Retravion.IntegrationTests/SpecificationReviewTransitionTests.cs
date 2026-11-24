using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task SpecificationReviewTransitionPreservesContentAndLinksAndAuditsEachStateChange()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var targets = await CreateLinkTargets(db, product);
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var created = (await operations.Create(principal, product, " DEV-1 ", "Original", "# Markdown\n\nKeep this.")).Value;
        var draft = (await operations.EditLinks(principal, product, created.InternalId, targets)).Value;
        var draftValues = new SpecificationAuditValues(product, draft.Id, draft.Title, draft.Description, "Draft");
        var reviewValues = draftValues with { Status = "In Review" };
        var reasons = new[] { "  Revise café wording.\n", null };
        for (var i = 0; i < reasons.Length; i++)
        {
            clock.Now = CreateTime.AddMinutes(2 * i + 1);
            var review = (await operations.SubmitForReview(principal, product, draft.InternalId)).Value;
            Assert.Equal((draft.InternalId, product, draft.Id, draft.Title, draft.Description, draft.Links),
                (review.InternalId, review.ProductInternalId, review.Id, review.Title, review.Description, review.Links));
            Assert.Equal(review, Assert.IsType<InReviewSpecification>((await operations.Find(principal, product, draft.InternalId)).Value.Value));
            Assert.Equal(review, Assert.Single((await operations.List(principal, product)).Value));
            Assert.Equal(TransitionEvent(draft.InternalId, clock.Now, draftValues, reviewValues), (await ReviewSubmissions(db))[i]);

            clock.Now = CreateTime.AddMinutes(2 * i + 2);
            var returned = (await operations.ReturnToDraft(principal, product, draft.InternalId, reasons[i])).Value;
            Assert.Equal(draft, returned);
            Assert.Equal(draft, Assert.IsType<DraftSpecification>((await operations.Find(principal, product, draft.InternalId)).Value.Value));
            Assert.Equal(TransitionEvent(draft.InternalId, clock.Now, reviewValues, draftValues, reasons[i]), (await ReviewReturns(db))[i]);
        }
        Assert.Equal(2, (await ReviewSubmissions(db)).Count);
        Assert.Equal(2, (await ReviewReturns(db)).Count);
    }

    [PostgresFact]
    public async Task SpecificationReviewTransitionRejectsBlankDescriptionsWrongStatesWrongProductAndRetirement()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var other = await Product(products, "second");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        foreach (var (id, description) in new[] { ("EMPTY", ""), ("BLANK", "\t\n\u2003") })
        {
            var blank = (await operations.Create(principal, product.InternalId, id, "Title", description)).Value;
            Assert.Contains("description is required", (await operations.SubmitForReview(principal, product.InternalId, blank.InternalId)).Error);
            Assert.Equal(blank, (await operations.Find(principal, product.InternalId, blank.InternalId)).Value.Value);
        }
        var draft = (await operations.Create(principal, product.InternalId, "DEV-1", "Title", "# Description")).Value;
        var spare = (await operations.Create(principal, product.InternalId, "DEV-2", "Title", "# Description")).Value;
        Assert.Contains("Only In Review", (await operations.ReturnToDraft(principal, product.InternalId, draft.InternalId)).Error);
        Assert.Contains("not found in this Product", (await operations.SubmitForReview(principal, other.InternalId, draft.InternalId)).Error);

        var review = (await operations.SubmitForReview(principal, product.InternalId, draft.InternalId)).Value;
        Assert.Contains("Only Draft", (await operations.SubmitForReview(principal, product.InternalId, draft.InternalId)).Error);
        Assert.Contains("not found in this Product", (await operations.ReturnToDraft(principal, other.InternalId, draft.InternalId)).Error);
        foreach (var reason in new[] { "\0", "\uD800" })
            Assert.Contains("valid UTF-8", (await operations.ReturnToDraft(principal, product.InternalId, draft.InternalId, reason)).Error);
        Assert.Contains("Only Draft", (await operations.EditContent(principal, product.InternalId, draft.InternalId, draft.Id, "Changed", "Changed")).Error);
        Assert.Contains("Only Draft", (await operations.EditLinks(principal, product.InternalId, draft.InternalId, [])).Error);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await operations.SubmitForReview(principal, product.InternalId, spare.InternalId)).Error);
        Assert.Contains("retired", (await operations.ReturnToDraft(principal, product.InternalId, draft.InternalId)).Error);
        Assert.Equal(review, (await operations.Find(principal, product.InternalId, draft.InternalId)).Value.Value);
        Assert.Equal(spare, (await operations.Find(principal, product.InternalId, spare.InternalId)).Value.Value);
        Assert.Single(await ReviewSubmissions(db));
        Assert.Empty(await ReviewReturns(db));
    }

    [PostgresFact]
    public async Task SpecificationReviewTransitionAuditsDenialsAndRollsBackBothTransitionsOnAuditFailure()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var principal = await Principal(sessions, "User", now: CreateTime);
        var revoked = await Principal(sessions, "User", now: CreateTime);
        var draft = (await operations.Create(principal, product, "DEV-1", "Draft title", "# Draft")).Value;
        var second = (await operations.Create(principal, product, "DEV-2", "Review title", "# Review")).Value;
        var review = (await operations.SubmitForReview(principal, product, second.InternalId)).Value;
        await sessions.RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        Assert.Equal("Access denied.", (await operations.SubmitForReview(revoked, product, draft.InternalId)).Error);
        Assert.Equal("Access denied.", (await operations.ReturnToDraft(revoked, product, review.InternalId, "SECRET")).Error);
        var denial = new CreateEvent("User", CreateTime.UtcTicks, "unknown", null, "failure", null);
        Assert.Equal(denial, (await ReviewSubmissions(db))[1]);
        Assert.Equal(denial, Assert.Single(await ReviewReturns(db)));

        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        Assert.Equal("The Specification operation could not be completed.",
            (await operations.SubmitForReview(principal, product, draft.InternalId)).Error);
        Assert.Equal("The Specification operation could not be completed.",
            (await operations.ReturnToDraft(principal, product, review.InternalId, "SECRET")).Error);
        Assert.Equal(draft, (await operations.Find(principal, product, draft.InternalId)).Value.Value);
        Assert.Equal(review, (await operations.Find(principal, product, review.InternalId)).Value.Value);
        Assert.Equal(2, (await ReviewSubmissions(db)).Count);
        Assert.Equal(denial, Assert.Single(await ReviewReturns(db)));
    }

    [PostgresFact]
    public async Task SpecificationReviewTransitionUpgradesExistingDraftStorageAndRejectsInvalidPersistedReview()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var targets = await CreateLinkTargets(db, product);
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var created = (await operations.Create(principal, product, "DEV-1", "Title", "# Description")).Value;
        var draft = (await operations.EditLinks(principal, product, created.InternalId, targets)).Value;
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var legacy = new NpgsqlCommand("""
            ALTER TABLE retravion_specification.specification DROP CONSTRAINT specification_review_description_check,
                DROP CONSTRAINT specification_approved_description_check,
                DROP CONSTRAINT specification_implementation_check,
                DROP COLUMN merge_attested_by,
                DROP COLUMN merge_attested_at_utc_ticks,
                DROP CONSTRAINT specification_status_check,
                ADD CONSTRAINT specification_status_check CHECK (status = 'Draft');
            """, connection))
            await legacy.ExecuteNonQueryAsync();

        var review = (await operations.SubmitForReview(principal, product, draft.InternalId)).Value;
        Assert.Equal(draft.Links, review.Links);
        Assert.Equal(review, (await operations.Find(principal, product, draft.InternalId)).Value.Value);
        await using (var blank = new NpgsqlCommand("UPDATE retravion_specification.specification SET description = ''", connection))
            Assert.Equal("specification_review_description_check", (await Assert.ThrowsAsync<PostgresException>(() => blank.ExecuteNonQueryAsync())).ConstraintName);
        await using (var implement = new NpgsqlCommand("UPDATE retravion_specification.specification SET status = 'Implemented'", connection))
            Assert.Equal("specification_implementation_check", (await Assert.ThrowsAsync<PostgresException>(() => implement.ExecuteNonQueryAsync())).ConstraintName);

        // Bypass the protection to verify that the mapper independently validates the In Review invariant.
        await using (var corrupt = new NpgsqlCommand("""
            ALTER TABLE retravion_specification.specification DROP CONSTRAINT specification_review_description_check,
                ADD CONSTRAINT specification_review_description_check CHECK (true);
            UPDATE retravion_specification.specification SET description = '';
            """, connection))
            await corrupt.ExecuteNonQueryAsync();
        Assert.Equal("The persisted Specification is invalid.", (await operations.Find(principal, product, draft.InternalId)).Error);
        Assert.Equal("The persisted Specification is invalid.", (await operations.List(principal, product)).Error);
        Assert.Equal("The persisted Specification is invalid.", (await operations.ReturnToDraft(principal, product, draft.InternalId)).Error);
        Assert.Single(await ReviewSubmissions(db));
        Assert.Empty(await ReviewReturns(db));
    }

    [PostgresFact]
    public async Task SpecificationReviewTransitionCompetingCommandsCommitOnlyOneEventPerStateChange()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var writes = new PostgresSpecificationWriteStore(db.ConnectionString);
        var draft = DraftSpecification.Create(product, "DEV-1", "Title", "# Description").Value;
        static Task<bool> Authorized() => Task.FromResult(true);
        Assert.True((await writes.Create(draft, CreateActor, CreateTime, Authorized)).IsSuccess);

        var submissions = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            writes.SubmitForReview(product, draft.InternalId, CreateActor, CreateTime, Authorized)));
        Assert.Single(submissions, result => result.IsSuccess);
        Assert.Contains("Only Draft", Assert.Single(submissions, result => result.IsFailure).Error);
        Assert.Single(await ReviewSubmissions(db));

        var reasons = new[] { "First reason", "Second reason" };
        var returns = await Task.WhenAll(reasons.Select(reason =>
            writes.ReturnToDraft(product, draft.InternalId, reason, CreateActor, CreateTime, Authorized)));
        Assert.Equal(draft, Assert.Single(returns, result => result.IsSuccess).Value);
        Assert.Contains("Only In Review", Assert.Single(returns, result => result.IsFailure).Error);
        Assert.Equal(reasons[Array.FindIndex(returns, result => result.IsSuccess)], Assert.Single(await ReviewReturns(db)).Change!.Reason);
        Assert.Equal(draft, (await new PostgresSpecificationReadStore(db.ConnectionString).Find(product, draft.InternalId)).Value.Value);
    }

    private static CreateEvent TransitionEvent(Guid identity, DateTimeOffset now, SpecificationAuditValues before,
        SpecificationAuditValues after, string? reason = null) =>
        new("User", now.UtcTicks, "identified", identity.ToString(), "success", new(identity, before, after, reason));

    private static Task<List<CreateEvent>> ReviewSubmissions(Database db) => SpecificationEvents(db, "specification.submit-for-review");
    private static Task<List<CreateEvent>> ReviewReturns(Database db) => SpecificationEvents(db, "specification.return-to-draft");
}
