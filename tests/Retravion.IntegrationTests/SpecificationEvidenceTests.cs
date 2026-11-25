using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Product;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    private const string SaveEvidenceAction = "specification.save-evidence";
    private const string ConfirmEvidenceAction = "specification.confirm-evidence-current";
    private const string EvidenceUrl = "https://forge.example:8443/project/pull-requests/42?tab=diff#files";

    [PostgresFact]
    public async Task SpecificationEvidenceSaveValidatesSizeUrlStateAndAccessAndAuditsEffectiveReplacements()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var pid = product.InternalId;
        var other = (await Product(products, "second")).InternalId;
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var user = await Principal(sessions, "User", now: CreateTime);
        var restricted = await Principal(sessions, "Administrator", restricted: true, now: CreateTime);
        var draft = (await ops.Create(user, pid, "DEV-1", "Title", "Description")).Value;
        var id = draft.InternalId;
        async Task<SpecificationEvidence?> Read() => (await ops.FindDetail(user, pid, id)).Value.Value.Evidence;
        Assert.Contains("Approved", (await ops.SaveEvidence(user, pid, id, "# Report", null)).Error);
        Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
        Assert.Contains("Approved", (await ops.SaveEvidence(user, pid, id, "# Report", null)).Error);
        Assert.True((await ops.Approve(user, pid, id)).IsSuccess);
        Assert.Contains("no retained", (await ops.ConfirmEvidenceCurrent(user, pid, id, null)).Error);
        Assert.Contains("not found", (await ops.SaveEvidence(user, other, id, "# Report", null)).Error);
        Assert.True((await ops.FindDetail(user, other, id)).Value.HasNoValue);

        var atLimit = new string('é', 512 * 1024);
        Assert.Contains("1 MB", (await ops.SaveEvidence(user, pid, id, atLimit + "x", null)).Error);
        foreach (var invalid in new[] { "\uD800", "# Report\0" })
            Assert.Contains("valid UTF-8", (await ops.SaveEvidence(user, pid, id, invalid, null)).Error);
        Assert.Contains("Enter Markdown", (await ops.SaveEvidence(user, pid, id, " \t", null)).Error);
        foreach (var invalid in new[] { "", "http://forge.example/pr/42", "/pr/42", "//forge.example/pr/42", "https:pr/42",
                     "https://", "https://forge.example/pr/4 2", "https://forge.example/pr/42\r\n", "javascript:alert(1)" })
            Assert.Contains("absolute HTTPS", (await ops.SaveEvidence(user, pid, id, "# Report", invalid)).Error);
        Assert.Null(await Read());
        Assert.Empty(await SpecificationEvents(db, SaveEvidenceAction));
        Assert.Empty(await SpecificationEvents(db, ConfirmEvidenceAction));

        Assert.Equal("Access denied.", (await ops.SaveEvidence(restricted, pid, id, "# Secret", EvidenceUrl)).Error);
        Assert.Equal("Access denied.", (await ops.ConfirmEvidenceCurrent(restricted, pid, id, null)).Error);
        Assert.Equal("Access denied.", (await ops.FindDetail(restricted, pid, id)).Error);
        Assert.True((await ops.SaveEvidence(user, pid, id, atLimit, null)).IsSuccess);
        Assert.Equal((atLimit, null, false), ((await Read())!.Markdown, (await Read())!.PullRequestUrl, (await Read())!.IsOutdated));
        clock.Now = CreateTime.AddTicks(20);
        Assert.True((await ops.SaveEvidence(user, pid, id, atLimit, EvidenceUrl)).IsSuccess); // URL-only change.
        Assert.True((await ops.SaveEvidence(user, pid, id, atLimit, EvidenceUrl)).IsSuccess); // No-op.
        const string replacement = "# Outcomes\r\n- Limitations\n";
        var saved = (await ops.SaveEvidence(user, pid, id, replacement, null)).Value;
        Assert.Equal((replacement, null, false), (saved.Markdown, saved.PullRequestUrl, saved.IsOutdated));
        Assert.Equal(saved, await Read());

        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await ops.SaveEvidence(user, pid, id, "# New", EvidenceUrl)).Error);
        Assert.Contains("retired", (await ops.ConfirmEvidenceCurrent(user, pid, id, saved)).Error);
        Assert.Equal(saved, await Read());

        var snapshot = new SpecificationAuditValues(pid, draft.Id, draft.Title, draft.Description, "Approved");
        var events = await SpecificationEvents(db, SaveEvidenceAction);
        Assert.Equal(4, events.Count);
        var denied = new CreateEvent("Administrator", CreateTime.UtcTicks, "unknown", null, "failure", null);
        Assert.Equal(denied, events[0]);
        Assert.Equal(denied, Assert.Single(await SpecificationEvents(db, ConfirmEvidenceAction)));
        CreateEvent Success(DateTimeOffset time, SpecificationAuditEvidenceValue? before, SpecificationAuditEvidenceValue after) =>
            new("User", time.UtcTicks, "identified", id.ToString(), "success",
                new(id, snapshot, snapshot, Evidence: new(before, after)));
        Assert.Equal(Success(CreateTime, null, new(atLimit, null, false)), events[1]);
        Assert.Equal(Success(clock.Now, new(atLimit, null, false), new(atLimit, EvidenceUrl, false)), events[2]);
        Assert.Equal(Success(clock.Now, new(atLimit, EvidenceUrl, false), new(replacement, null, false)), events[3]);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM retravion_specification.evidence", connection);
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task SpecificationEvidenceReopenRetainsEvidenceUntilExplicitConfirmationOrReplacementAfterReapproval()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await ops.Create(user, pid, "DEV-1", "Title", "Description")).Value;
        var id = draft.InternalId;
        async Task Approve()
        {
            Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
            Assert.True((await ops.Approve(user, pid, id)).IsSuccess);
        }
        async Task<SpecificationEvidence> Read() => (await ops.FindDetail(user, pid, id)).Value.Value.Evidence!;
        await Approve();
        var original = (await ops.SaveEvidence(user, pid, id, "# Original", EvidenceUrl)).Value;
        var stale = original.MarkOutdated();
        Assert.True((await ops.Reopen(user, pid, id, "Revise", false)).IsFailure);
        Assert.Equal(original, await Read());
        clock.Now = CreateTime.AddTicks(10);
        Assert.True((await ops.Reopen(user, pid, id, "Revise", true)).IsSuccess);
        Assert.Equal(stale, await Read());
        Assert.Contains("Approved", (await ops.ConfirmEvidenceCurrent(user, pid, id, stale)).Error);
        await Approve();
        Assert.Equal(stale, await Read());
        var unchanged = await ops.SaveEvidence(user, pid, id, original.Markdown, original.PullRequestUrl);
        Assert.Equal(stale, unchanged.Value);
        Assert.Equal(stale, await Read());
        Assert.Single(await SpecificationEvents(db, SaveEvidenceAction)); // Only the original save was audited.
        Assert.Contains("reviewed", (await ops.ConfirmEvidenceCurrent(user, pid, id, null)).Error);
        Assert.Equal(stale, await Read());
        Assert.Empty(await SpecificationEvents(db, ConfirmEvidenceAction));
        clock.Now = CreateTime.AddTicks(20);
        Assert.Equal(original, (await ops.ConfirmEvidenceCurrent(user, pid, id, stale)).Value);
        Assert.Equal(original, await Read());
        Assert.Equal(original, (await ops.ConfirmEvidenceCurrent(user, pid, id, original)).Value); // No-op.

        var snapshot = new SpecificationAuditValues(pid, draft.Id, draft.Title, draft.Description, "Approved");
        var currentValue = new SpecificationAuditEvidenceValue(original.Markdown, EvidenceUrl, false);
        var staleValue = currentValue with { IsOutdated = true };
        Assert.Equal(new CreateEvent("User", CreateTime.AddTicks(10).UtcTicks, "identified", id.ToString(), "success",
            new(id, snapshot, snapshot with { Status = "Draft" }, "Revise", true, Evidence: new(currentValue, staleValue))),
            Assert.Single(await SpecificationEvents(db, "specification.reopen")));
        Assert.Equal(new CreateEvent("User", clock.Now.UtcTicks, "identified", id.ToString(), "success",
            new(id, snapshot, snapshot, Evidence: new(staleValue, currentValue))),
            Assert.Single(await SpecificationEvents(db, ConfirmEvidenceAction)));

        Assert.True((await ops.Reopen(user, pid, id, "Revise again", true)).IsSuccess);
        await Approve();
        Assert.True((await ops.Reopen(user, pid, id, "Still outdated", true)).IsSuccess);
        Assert.Null((await SpecificationEvents(db, "specification.reopen"))[^1].Change!.Evidence);
        await Approve();
        var reader = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "Administrator", now: CreateTime);
        var displayed = (await ops.FindDetail(reader, pid, id)).Value.Value.Evidence!;
        Assert.Equal(stale, displayed);
        const string replacement = "# Updated report";
        Assert.False((await ops.SaveEvidence(user, pid, id, replacement, EvidenceUrl)).Value.IsOutdated);
        Assert.Equal(replacement, (await Read()).Markdown);
        Assert.False((await Read()).IsOutdated);
        Assert.Equal(new SpecificationAuditEvidenceChange(staleValue, new(replacement, EvidenceUrl, false)),
            (await SpecificationEvents(db, SaveEvidenceAction))[^1].Change!.Evidence);

        // A second session has replaced the displayed report and reopened it for a new approval.
        Assert.True((await ops.Reopen(user, pid, id, "Revise replacement", true)).IsSuccess);
        await Approve();
        var beforeConfirmation = (await SpecificationEvents(db, ConfirmEvidenceAction)).Count;
        Assert.Contains("Review the refreshed evidence", (await ops.ConfirmEvidenceCurrent(reader, pid, id, displayed)).Error);
        var refreshed = (await ops.FindDetail(reader, pid, id)).Value.Value.Evidence!;
        Assert.Equal(replacement, refreshed.Markdown);
        Assert.True(refreshed.IsOutdated);
        Assert.Equal(beforeConfirmation, (await SpecificationEvents(db, ConfirmEvidenceAction)).Count);
        Assert.False((await ops.ConfirmEvidenceCurrent(reader, pid, id, refreshed)).Value.IsOutdated);
        Assert.Equal(beforeConfirmation + 1, (await SpecificationEvents(db, ConfirmEvidenceAction)).Count);
    }

    [PostgresFact]
    public async Task SpecificationEvidenceWritesAndReopeningRollBackTogetherWithAudit()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        async Task<Guid> CreateApproved(string label)
        {
            var id = (await ops.Create(user, pid, label, "Title", "Description")).Value.InternalId;
            Assert.True((await ops.SubmitForReview(user, pid, id)).IsSuccess);
            Assert.True((await ops.Approve(user, pid, id)).IsSuccess);
            return id;
        }
        var first = await CreateApproved("DEV-1");
        var second = await CreateApproved("DEV-2");
        var original = (await ops.SaveEvidence(user, pid, first, "# Original", EvidenceUrl)).Value;
        Assert.True((await ops.SaveEvidence(user, pid, second, original.Markdown, EvidenceUrl)).IsSuccess);
        Assert.True((await ops.Reopen(user, pid, second, "Revise", true)).IsSuccess);
        Assert.True((await ops.SubmitForReview(user, pid, second)).IsSuccess);
        Assert.True((await ops.Approve(user, pid, second)).IsSuccess);
        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        const string failed = "The Specification operation could not be completed.";
        Assert.Equal(failed, (await ops.SaveEvidence(user, pid, first, "# Changed", null)).Error);
        Assert.Equal(failed, (await ops.Reopen(user, pid, first, "Revise", true)).Error);
        Assert.Equal(failed, (await ops.ConfirmEvidenceCurrent(user, pid, second, original.MarkOutdated())).Error);
        var current = (await ops.FindDetail(user, pid, first)).Value.Value;
        Assert.IsType<ApprovedSpecification>(current.Specification);
        Assert.Equal(original, current.Evidence);
        Assert.Equal(original.MarkOutdated(), (await ops.FindDetail(user, pid, second)).Value.Value.Evidence);
        Assert.Equal(2, (await SpecificationEvents(db, SaveEvidenceAction)).Count);
        Assert.Single(await SpecificationEvents(db, "specification.reopen"));
        Assert.Empty(await SpecificationEvents(db, ConfirmEvidenceAction));
    }

    [PostgresFact]
    public async Task SpecificationEvidenceUpgradesExistingStorageAndRejectsInvalidPersistedValues()
    {
        await using var db = await Database.Create();
        var pid = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var user = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await ops.Create(user, pid, "DEV-1", "Title", "Description")).Value;
        var id = draft.InternalId;
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand("DROP TABLE retravion_specification.evidence", connection);
        await drop.ExecuteNonQueryAsync();
        var upgraded = (await ops.FindDetail(user, pid, id)).Value.Value;
        Assert.Equal(draft, upgraded.Specification);
        Assert.Null(upgraded.Evidence);

        await using var corrupt = new NpgsqlCommand("INSERT INTO retravion_specification.evidence VALUES ($1, '# Report', $2, true)", connection);
        corrupt.Parameters.AddWithValue(id);
        corrupt.Parameters.AddWithValue("http://forge.example/pr/42");
        await corrupt.ExecuteNonQueryAsync();
        const string invalid = "The persisted Specification is invalid.";
        Assert.Equal(invalid, (await ops.FindDetail(user, pid, id)).Error);
        await using var currentInDraft = new NpgsqlCommand("UPDATE retravion_specification.evidence SET pull_request_url = NULL, is_outdated = false", connection);
        await currentInDraft.ExecuteNonQueryAsync();
        Assert.Equal(invalid, (await ops.FindDetail(user, pid, id)).Error);
        Assert.Empty(await SpecificationEvents(db, SaveEvidenceAction));
    }
}
