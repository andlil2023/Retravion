using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Retravion.Infrastructure.Product;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task SpecificationAssignmentChangesResponsibilityAcrossStatesAndRetainsAuditedHistoryAfterClearing()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var accounts = scope.ServiceProvider.GetRequiredService<IAdministratorStore>();
        var first = await CreateAssignee(accounts, "First.User");
        var second = await CreateAssignee(accounts, "Second.User");
        var draft = (await operations.Create(principal, product, "DEV-1", "Title", "# Description")).Value;
        async Task<SpecificationDetail> Detail() => (await operations.FindDetail(principal, product, draft.InternalId)).Value.Value;
        Assert.Empty((await Detail()).AssignmentHistory);

        clock.Now = CreateTime.AddTicks(1);
        Assert.True((await operations.ChangeAssignment(principal, product, draft.InternalId, "FIRST.USER")).IsSuccess);
        Assert.Equal(first.Username, (await Detail()).Assignee);
        Assert.Equal(draft, (await Detail()).Specification);
        Assert.True((await operations.ChangeAssignment(principal, product, draft.InternalId, "first.user")).IsSuccess);
        var review = (await operations.SubmitForReview(principal, product, draft.InternalId)).Value;
        Assert.Equal(first.Username, (await Detail()).Assignee);

        clock.Now = CreateTime.AddTicks(2);
        Assert.True((await operations.ChangeAssignment(principal, product, draft.InternalId, second.Username)).IsSuccess);
        Assert.Equal(second.Username, (await Detail()).Assignee);
        Assert.Equal(review, (await Detail()).Specification);
        var approved = (await operations.Approve(principal, product, draft.InternalId)).Value.Specification;
        Assert.Equal(second.Username, (await Detail()).Assignee);
        Assert.True((await accounts.ChangeAvailability(second, second.Inactivate("User").Value)).IsSuccess);

        clock.Now = CreateTime.AddTicks(3);
        Assert.True((await operations.ChangeAssignment(principal, product, draft.InternalId, null)).IsSuccess);
        Assert.True((await operations.ChangeAssignment(principal, product, draft.InternalId, null)).IsSuccess);
        var cleared = await Detail();
        Assert.Equal(approved, cleared.Specification);
        Assert.Null(cleared.Assignee);
        SpecificationAssignmentEntry[] expected =
        [
            new("User", CreateTime.AddTicks(1), null, first.Username),
            new("User", CreateTime.AddTicks(2), first.Username, second.Username),
            new("User", CreateTime.AddTicks(3), second.Username, null)
        ];
        Assert.Equal(expected, cleared.AssignmentHistory);
        var events = await AssignmentEvents(db);
        Assert.Equal(expected.Length, events.Count);
        foreach (var (entry, audit, status) in expected.Zip(events, (e, a) => (e, a)).Zip(
                     new[] { "Draft", "In Review", "Approved" }, (pair, status) => (pair.e, pair.a, status)))
        {
            var snapshot = new SpecificationAuditValues(product, draft.Id, draft.Title, draft.Description, status);
            Assert.Equal(new CreateEvent(entry.Actor, entry.OccurredAt.UtcTicks, "identified", draft.InternalId.ToString(), "success",
                new SpecificationAuditDetails(draft.InternalId, snapshot, snapshot,
                    Assignment: new(entry.OldAssignee, entry.NewAssignee))), audit);
        }
    }

    [PostgresFact]
    public async Task SpecificationAssignmentRejectsInvalidAccountsWrongScopeAndUnauthorizedAccess()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "first");
        var other = await Product(products, "second");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var principal = await Principal(sessions, "User", now: CreateTime);
        var restricted = await Principal(sessions, "Administrator", restricted: true, now: CreateTime);
        var accounts = scope.ServiceProvider.GetRequiredService<IAdministratorStore>();
        var inactive = await CreateAssignee(accounts, "Inactive.User");
        Assert.True((await accounts.ChangeAvailability(inactive, inactive.Inactivate("User").Value)).IsSuccess);
        var assigned = await CreateAssignee(accounts, "Administrator");
        var draft = (await operations.Create(principal, product.InternalId, "DEV-1", "Title", "# Description")).Value;
        foreach (var invalid in new[] { inactive.Username, "Unknown", "", "\0", "\uD800" })
            Assert.Contains("active user", (await operations.ChangeAssignment(principal, product.InternalId, draft.InternalId, invalid)).Error);
        Assert.Empty(await AssignmentEvents(db));
        Assert.Contains("not found", (await operations.ChangeAssignment(principal, other.InternalId, draft.InternalId, assigned.Username)).Error);
        Assert.True((await operations.FindDetail(principal, other.InternalId, draft.InternalId)).Value.HasNoValue);

        Assert.True((await operations.ChangeAssignment(principal, product.InternalId, draft.InternalId, assigned.Username)).IsSuccess);
        Assert.Equal("Access denied.", (await operations.FindDetail(restricted, product.InternalId, draft.InternalId)).Error);
        Assert.Equal("Access denied.", (await operations.ChangeAssignment(restricted, product.InternalId, draft.InternalId, null)).Error);
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await operations.ChangeAssignment(principal, product.InternalId, draft.InternalId, null)).Error);
        var detail = (await operations.FindDetail(principal, product.InternalId, draft.InternalId)).Value.Value;
        Assert.Equal(assigned.Username, detail.Assignee);
        Assert.Single(detail.AssignmentHistory);
        Assert.Collection(await AssignmentEvents(db), audit => Assert.Equal("success", audit.Outcome), audit =>
            Assert.Equal(new CreateEvent("Administrator", CreateTime.UtcTicks, "unknown", null, "failure", null), audit));
    }

    [PostgresFact]
    public async Task SpecificationAssignmentRejectsAnAccountThatIsInactivatedBeforeTheAssignmentCommits()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var account = await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "First.User");
        var draft = (await operations.Create(principal, product, "DEV-1", "Title", "# Description")).Value;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var identity = new NpgsqlConnection(db.ConnectionString);
        await identity.OpenAsync(deadline.Token);
        await using var inactivation = await identity.BeginTransactionAsync(deadline.Token);
        Assert.True((await new PostgresAdministratorStore(identity).ChangeAvailability(
            account, account.Inactivate("User").Value, deadline.Token)).IsSuccess);

        var assignment = operations.ChangeAssignment(principal, product, draft.InternalId, account.Username, deadline.Token);
        try
        {
            await using var observer = new NpgsqlConnection(db.ConnectionString);
            await observer.OpenAsync(deadline.Token);
            await using var blocked = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND $1 = ANY(pg_blocking_pids(pid)))
                """, observer);
            blocked.Parameters.AddWithValue(identity.ProcessID);
            while (!assignment.IsCompleted && await blocked.ExecuteScalarAsync(deadline.Token) is not true)
                await Task.Delay(20, deadline.Token);
            Assert.False(assignment.IsCompleted);
            await inactivation.CommitAsync(deadline.Token);
        }
        finally
        {
            if (inactivation.Connection is not null) await inactivation.DisposeAsync();
            await assignment;
        }

        Assert.Contains("active user", (await assignment).Error);
        var detail = (await operations.FindDetail(principal, product, draft.InternalId)).Value.Value;
        Assert.Null(detail.Assignee);
        Assert.Empty(detail.AssignmentHistory);
        Assert.Empty(await AssignmentEvents(db));
    }

    [PostgresFact]
    public async Task SpecificationAssignmentRollsBackClearingAndHistoryWhenAuditFails()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var assigned = await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "First.User");
        var draft = (await operations.Create(principal, product, "DEV-1", "Title", "# Description")).Value;
        Assert.True((await operations.ChangeAssignment(principal, product, draft.InternalId, assigned.Username)).IsSuccess);
        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        Assert.Equal("The Specification operation could not be completed.",
            (await operations.ChangeAssignment(principal, product, draft.InternalId, null)).Error);
        var detail = (await operations.FindDetail(principal, product, draft.InternalId)).Value.Value;
        Assert.Equal(assigned.Username, detail.Assignee);
        Assert.Equal(new SpecificationAssignmentEntry("User", CreateTime, null, assigned.Username), Assert.Single(detail.AssignmentHistory));
        Assert.Single(await AssignmentEvents(db));
    }

    private static async Task<Administrator> CreateAssignee(IAdministratorStore accounts, string username)
    {
        var account = Administrator.CreateAccount(AccountIdentity.Create(username, username, AccountType.Ordinary).Value,
            "unused-test-password-hash").Value;
        Assert.True((await accounts.CreateAccount(account)).IsSuccess);
        return account;
    }

    private static Task<List<CreateEvent>> AssignmentEvents(Database db) => SpecificationEvents(db, "specification.change-assignment");
}
