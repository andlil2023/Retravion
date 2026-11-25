using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Product;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    private const string SubmitAiReviewAction = "specification.submit-ai-review";

    [PostgresFact]
    public async Task SpecificationAiReviewRequiresApprovalAndStrictUtf8AndReplacesTheSingleCurrentReview()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var other = await Product(products, "second");
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var user = await Principal(sessions, "User", now: CreateTime);
        var restricted = await Principal(sessions, "Administrator", restricted: true, now: CreateTime);
        var draft = (await ops.Create(user, product.InternalId, "DEV-1", "Title", "Description")).Value;
        var id = draft.InternalId;
        async Task<SpecificationDetail> Detail() => (await ops.FindDetail(user, product.InternalId, id)).Value.Value;
        var atLimit = new string('é', 512 * 1024);
        var uploaded = new SpecificationAiReviewInput.Uploaded(Encoding.UTF8.GetBytes(atLimit));
        var entered = new SpecificationAiReviewInput.Entered(atLimit);
        Assert.Null((await Detail()).CurrentAiReview);
        foreach (var input in new SpecificationAiReviewInput[] { entered, uploaded })
            Assert.Contains("approval", (await ops.SubmitAiReview(user, product.InternalId, id, input, false)).Error);
        foreach (var input in new SpecificationAiReviewInput[]
                 {
                     new SpecificationAiReviewInput.Entered(atLimit + "x"),
                     new SpecificationAiReviewInput.Uploaded(Encoding.UTF8.GetBytes(atLimit + "x"))
                 })
            Assert.Contains("1 MB", (await ops.SubmitAiReview(user, product.InternalId, id, input, true)).Error);
        foreach (var input in new SpecificationAiReviewInput[]
                 {
                     new SpecificationAiReviewInput.Uploaded(new byte[] { 0xC3, 0x28 }),
                     new SpecificationAiReviewInput.Entered("\uD800"),
                     new SpecificationAiReviewInput.Entered("# Heading\0")
                 })
            Assert.Contains("valid UTF-8", (await ops.SubmitAiReview(user, product.InternalId, id, input, true)).Error);
        Assert.Contains("not found", (await ops.SubmitAiReview(user, other.InternalId, id, entered, true)).Error);
        Assert.True((await ops.FindDetail(user, other.InternalId, id)).Value.HasNoValue);
        Assert.Empty(await SpecificationEvents(db, SubmitAiReviewAction));
        Assert.Equal("Access denied.", (await ops.SubmitAiReview(restricted, product.InternalId, id, entered, true)).Error);
        Assert.Equal("Access denied.", (await ops.FindDetail(restricted, product.InternalId, id)).Error);

        Assert.True((await ops.SubmitAiReview(user, product.InternalId, id, uploaded, true)).IsSuccess);
        Assert.Equal(atLimit, (await Detail()).CurrentAiReview!.Markdown);
        Assert.True((await ops.SubmitAiReview(user, product.InternalId, id, entered, true)).IsSuccess); // Same effective value.
        Assert.True((await ops.SubmitForReview(user, product.InternalId, id)).IsSuccess);
        clock.Now = CreateTime.AddTicks(20);
        const string replacement = "# Review\r\n- Gaps and risks\n";
        var bomUpload = new SpecificationAiReviewInput.Uploaded(Encoding.UTF8.GetBytes("\uFEFF" + replacement));
        Assert.Contains("approval", (await ops.SubmitAiReview(user, product.InternalId, id, bomUpload, false)).Error);
        Assert.Equal(atLimit, (await Detail()).CurrentAiReview!.Markdown);
        Assert.True((await ops.SubmitAiReview(user, product.InternalId, id, bomUpload, true)).IsSuccess);
        var detail = await Detail(); // The single-row detail mapping must return the replaced value.
        Assert.IsType<InReviewSpecification>(detail.Specification);
        Assert.Equal(replacement, detail.CurrentAiReview!.Markdown);
        Assert.False(detail.CurrentAiReview.IsOutdated);
        Assert.True((await ops.Approve(user, product.InternalId, id)).IsSuccess);
        Assert.Contains("Draft or In Review", (await ops.SubmitAiReview(user, product.InternalId, id, entered, true)).Error);
        Assert.Equal(detail.CurrentAiReview, (await Detail()).CurrentAiReview);
        Assert.True((await ops.Reopen(user, product.InternalId, id, "Revise", true)).IsSuccess);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await ops.SubmitAiReview(user, product.InternalId, id, entered, true)).Error);
        Assert.Equal(replacement, (await Detail()).CurrentAiReview!.Markdown);

        var events = await SpecificationEvents(db, SubmitAiReviewAction);
        Assert.Equal(3, events.Count);
        var draftValues = new SpecificationAuditValues(product.InternalId, draft.Id, draft.Title, draft.Description, "Draft");
        var reviewValues = draftValues with { Status = "In Review" };
        Assert.Equal(new CreateEvent("Administrator", CreateTime.UtcTicks, "unknown", null, "failure", null), events[0]);
        Assert.Equal(new CreateEvent("User", CreateTime.UtcTicks, "identified", id.ToString(), "success",
            new(id, draftValues, draftValues, AiReview: new(null, new(atLimit, false), true))), events[1]);
        Assert.Equal(new CreateEvent("User", clock.Now.UtcTicks, "identified", id.ToString(), "success",
            new(id, reviewValues, reviewValues, AiReview: new(new(atLimit, false), new(replacement, false), true))), events[2]);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM retravion_specification.ai_review", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task SpecificationAiReviewInvalidatesOnlyOnEffectiveContentAndLinkChangesAndOnReopen()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var target = (await CreateLinkTargets(db, product))[0];
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await ops.Create(user, product, "DEV-1", "Title", "Description")).Value;
        var id = draft.InternalId;
        const string text = "# Retained review";
        var entered = new SpecificationAiReviewInput.Entered(text);
        async Task<SpecificationAiReview> Review() => (await ops.FindDetail(user, product, id)).Value.Value.CurrentAiReview!;
        Assert.True((await ops.SubmitAiReview(user, product, id, entered, true)).IsSuccess);
        Assert.True((await ops.EditContent(user, product, draft, draft.Id, draft.Title, draft.Description)).IsSuccess);
        Assert.True((await ops.EditLinks(user, product, id, [])).IsSuccess);
        Assert.True((await ops.EditLinks(user, product, id, [new(SpecificationLinkKind.UserNeed, Guid.NewGuid())])).IsFailure);
        Assert.False((await Review()).IsOutdated);
        Assert.Empty(await ContentEvents(db));
        Assert.Empty(await LinkEvents(db));

        var changed = (await ops.EditContent(user, product, draft, draft.Id, "Changed", draft.Description)).Value;
        Assert.Equal((text, true), ((await Review()).Markdown, (await Review()).IsOutdated));
        var invalidation = new SpecificationAuditAiReviewChange(new(text, false), new(text, true));
        Assert.Equal(invalidation, Assert.Single(await ContentEvents(db)).Change!.AiReview);
        Assert.True((await ops.EditContent(user, product, changed, draft.Id, "Changed again", draft.Description)).IsSuccess);
        Assert.Null((await ContentEvents(db))[1].Change!.AiReview);
        Assert.True((await ops.SubmitAiReview(user, product, id, entered, true)).IsSuccess);
        Assert.False((await Review()).IsOutdated);
        Assert.Equal(new SpecificationAuditAiReviewChange(new(text, true), new(text, false), true),
            (await SpecificationEvents(db, SubmitAiReviewAction))[1].Change!.AiReview);

        Assert.True((await ops.EditLinks(user, product, id, [target])).IsSuccess);
        Assert.Equal(invalidation, Assert.Single(await LinkEvents(db)).Change!.AiReview);
        Assert.Equal((text, true), ((await Review()).Markdown, (await Review()).IsOutdated));
        Assert.True((await ops.EditLinks(user, product, id, [])).IsSuccess);
        Assert.True((await ops.SubmitAiReview(user, product, id, entered, true)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, product, id)).IsSuccess);
        Assert.True((await ops.ReturnToDraft(user, product, id)).IsSuccess);
        Assert.False((await Review()).IsOutdated);
        Assert.True((await ops.SubmitForReview(user, product, id)).IsSuccess);
        Assert.True((await ops.Approve(user, product, id)).IsSuccess);
        Assert.True((await ops.Reopen(user, product, id, "Revise", false)).IsFailure);
        Assert.False((await Review()).IsOutdated);
        Assert.True((await ops.Reopen(user, product, id, "Revise", true)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, product, id)).IsSuccess);
        Assert.True((await ops.Approve(user, product, id)).IsSuccess);
        Assert.Equal((text, true), ((await Review()).Markdown, (await Review()).IsOutdated));
        var reopen = Assert.Single(await SpecificationEvents(db, "specification.reopen"));
        Assert.Equal(invalidation, reopen.Change!.AiReview);
        Assert.Equal("Revise", reopen.Change.Reason);
        Assert.True(reopen.Change.NoActiveOrUncertainRunAttested);
    }

    [PostgresFact]
    public async Task SpecificationAiReviewAndInvalidationsRollBackWithTheirOwningChangesWhenAuditFails()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var target = (await CreateLinkTargets(db, product))[0];
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await ops.Create(user, product, "DEV-1", "Title", "Description")).Value;
        var other = (await ops.Create(user, product, "DEV-2", "Title", "Description")).Value;
        var entered = new SpecificationAiReviewInput.Entered("Original review");
        foreach (var id in new[] { draft.InternalId, other.InternalId })
            Assert.True((await ops.SubmitAiReview(user, product, id, entered, true)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, product, other.InternalId)).IsSuccess);
        var approved = (await ops.Approve(user, product, other.InternalId)).Value.Specification;
        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        const string failed = "The Specification operation could not be completed.";
        Assert.Equal(failed, (await ops.SubmitAiReview(user, product, draft.InternalId, new SpecificationAiReviewInput.Entered("New review"), true)).Error);
        Assert.Equal(failed, (await ops.EditContent(user, product, draft, draft.Id, "Changed", draft.Description)).Error);
        Assert.Equal(failed, (await ops.EditLinks(user, product, draft.InternalId, [target])).Error);
        Assert.Equal(failed, (await ops.Reopen(user, product, approved.InternalId, "Revise", true)).Error);
        foreach (var expected in new DevelopmentSpecification[] { draft, approved })
        {
            var detail = (await ops.FindDetail(user, product, expected.InternalId)).Value.Value;
            Assert.Equal(expected, detail.Specification);
            Assert.Equal(entered.Markdown, detail.CurrentAiReview!.Markdown);
            Assert.False(detail.CurrentAiReview.IsOutdated);
        }
        Assert.Equal(2, (await SpecificationEvents(db, SubmitAiReviewAction)).Count);
        Assert.Empty(await ContentEvents(db));
        Assert.Empty(await LinkEvents(db));
        Assert.Empty(await SpecificationEvents(db, "specification.reopen"));
    }

    [PostgresFact]
    public async Task SpecificationAiReviewUpgradesExistingStorageAndRejectsCorruptRetainedMarkdownOnDetailRead()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await ops.Create(user, product, "DEV-1", "Title", "Description")).Value;
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand("DROP TABLE retravion_specification.ai_review", connection);
        await drop.ExecuteNonQueryAsync();
        var upgraded = (await ops.FindDetail(user, product, draft.InternalId)).Value.Value;
        Assert.Equal(draft, upgraded.Specification);
        Assert.Null(upgraded.CurrentAiReview);

        await using var disableCheck = new NpgsqlCommand("""
            ALTER TABLE retravion_specification.ai_review DROP CONSTRAINT ai_review_markdown_check
            """, connection);
        await disableCheck.ExecuteNonQueryAsync();
        await using var corrupt = new NpgsqlCommand("INSERT INTO retravion_specification.ai_review VALUES ($1, '', true)", connection);
        corrupt.Parameters.AddWithValue(draft.InternalId);
        await corrupt.ExecuteNonQueryAsync();
        const string invalid = "The persisted Specification is invalid.";
        Assert.Equal(invalid, (await ops.FindDetail(user, product, draft.InternalId)).Error);

        await using var nullPayload = new NpgsqlCommand("""
            ALTER TABLE retravion_specification.ai_review ALTER COLUMN markdown DROP NOT NULL, ALTER COLUMN is_outdated DROP NOT NULL;
            UPDATE retravion_specification.ai_review SET markdown = NULL, is_outdated = NULL;
            """, connection);
        await nullPayload.ExecuteNonQueryAsync();
        Assert.Equal(invalid, (await ops.FindDetail(user, product, draft.InternalId)).Error);
        Assert.Empty(await SpecificationEvents(db, SubmitAiReviewAction));
    }
}
