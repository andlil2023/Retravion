using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.Requirement;
using Retravion.Application.TestCase;
using Retravion.Application.UserNeed;
using Retravion.Domain.Requirement;
using Retravion.Domain.Specification;
using Retravion.Domain.TestCase;
using Retravion.Domain.UserNeed;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Specification;
using Retravion.Infrastructure.TestCase;
using Retravion.Infrastructure.UserNeed;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    private const string ReopenReason = "Correct wording";
    private const string ReopenBlocked = "This exact version is referenced by an Approved or Implemented Specification. Create a successor for changes.";

    [PostgresFact]
    public async Task SpecificationReopenProtectionBlocksApprovedAndImplementedLinksButAllowsSuccessors()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "main")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var linked = await owners.Approve(await owners.Create(product));
        await using var seed = await OpenReopenSeed(db, product);
        // A Draft reference must not hide a blocking reference from a second Specification.
        await SeedReopenLinks(seed, product, linked, "Draft");
        var specification = await SeedReopenLinks(seed, product, linked, "Approved");
        await AssertReopenBlocked(owners, linked);
        await SetReopenSeedStatus(seed, specification, "Implemented");
        await AssertReopenBlocked(owners, linked);
        AssertUnchanged(linked, await owners.Find(linked));
        AssertUnchanged(linked, await owners.Find(linked, exact: true));
        Assert.Equal(0L, await ReopenEvidenceCount(seed));

        // Even successors of the same records can be reopened once approved: the link is to v1, not v2.
        var successors = await owners.Approve(await owners.Successors(linked));
        Assert.All(successors.Links.Zip(linked.Links), pair => Assert.NotEqual(pair.First.VersionId, pair.Second.VersionId));
        AssertReopened(successors, await owners.Reopen(successors));
        Assert.Equal(6L, await ReopenEvidenceCount(seed)); // one history row and one audit event per owner
    }

    [PostgresFact]
    public async Task SpecificationReopenProtectionDoesNotBlockDraftOrInReviewReferences()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "main")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var approved = await owners.Approve(await owners.Create(product));
        await using var seed = await OpenReopenSeed(db, product);
        var specification = await SeedReopenLinks(seed, product, approved, "Draft");
        var reopened = await owners.Reopen(approved);
        AssertReopened(approved, reopened);
        approved = await owners.Approve(new(reopened.Need.Value, reopened.Requirement.Value, reopened.Case.Value));
        await SetReopenSeedStatus(seed, specification, "In Review");
        AssertReopened(approved, await owners.Reopen(approved));
        Assert.Equal(12L, await ReopenEvidenceCount(seed));
    }

    [PostgresFact]
    public async Task SpecificationReopenProtectionSeesApprovalCommittedByCompetingTransaction()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "main")).InternalId;
        var owners = new ReopenOwners(db.ConnectionString);
        var approved = await owners.Approve(await owners.Create(product));
        await using var seed = await OpenReopenSeed(db, product);
        var specification = await SeedReopenLinks(seed, product, approved, "In Review");
        // Model just the approving transaction's persisted change and exact-row lock, not a later command.
        await using var approval = await seed.BeginTransactionAsync();
        await SetReopenSeedStatus(seed, specification, "Approved", approval);
        await using (var exact = new NpgsqlCommand("SELECT 1 FROM retravion_need.need_version WHERE version_id = $1 FOR NO KEY UPDATE", seed, approval))
        {
            exact.Parameters.AddWithValue(approved.Need.Need.VersionId);
            Assert.Equal(1, await exact.ExecuteScalarAsync());
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reopening = owners.ReopenNeed(approved.Need, deadline.Token);
        await using var observer = new NpgsqlConnection(db.ConnectionString);
        await observer.OpenAsync(deadline.Token);
        await using var blocked = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND $1 = ANY(pg_blocking_pids(pid)))
            """, observer);
        blocked.Parameters.AddWithValue(seed.ProcessID);
        while (!reopening.IsCompleted && await blocked.ExecuteScalarAsync(deadline.Token) is not true)
            await Task.Delay(20, deadline.Token);
        Assert.False(reopening.IsCompleted);
        await approval.CommitAsync(deadline.Token);
        Assert.Equal(ReopenBlocked, (await reopening).Error);
        AssertUnchanged(approved, await owners.Find(approved));
        Assert.Equal(0L, await ReopenEvidenceCount(seed));
    }

    private static async Task AssertReopenBlocked(ReopenOwners owners, OwnerVersions versions)
    {
        var reopened = await owners.Reopen(versions);
        Assert.Equal(ReopenBlocked, reopened.Need.Error);
        Assert.Equal(ReopenBlocked, reopened.Requirement.Error);
        Assert.Equal(ReopenBlocked, reopened.Case.Error);
    }

    private static void AssertUnchanged(OwnerVersions before, OwnerVersions after)
    {
        Assert.Equal(before.Need, after.Need);
        Assert.Equal(before.Requirement, after.Requirement);
        Assert.True(before.Case.TestCase.SameState(after.Case.TestCase));
        Assert.Equal(before.Case.Revision, after.Case.Revision);
        Assert.Equal(before.Case.LatestApprovedVersionNumber, after.Case.LatestApprovedVersionNumber);
    }

    private static void AssertReopened(OwnerVersions before, ReopenResults after)
    {
        Assert.Equal((before.Need.Need.VersionId, UserNeedStatus.Draft), (after.Need.Value.Need.VersionId, after.Need.Value.Need.Status));
        Assert.Equal((before.Requirement.Requirement.VersionId, RequirementStatus.Draft),
            (after.Requirement.Value.Requirement.VersionId, after.Requirement.Value.Requirement.Status));
        Assert.Equal((before.Case.TestCase.VersionId, "Draft"), (after.Case.Value.TestCase.VersionId, after.Case.Value.TestCase.Status));
    }

    private static async Task<NpgsqlConnection> OpenReopenSeed(Database db, Guid product)
    {
        Assert.Empty((await new PostgresSpecificationReadStore(db.ConnectionString).List(product)).Value);
        var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        // Approval and implementation commands and storage constraints are separate tasks.
        await using var enable = new NpgsqlCommand("ALTER TABLE retravion_specification.specification DROP CONSTRAINT specification_status_check", connection);
        await enable.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<Guid> SeedReopenLinks(NpgsqlConnection connection, Guid product, OwnerVersions versions, string status)
    {
        var specification = Guid.NewGuid();
        await Seed(connection, product, specification, specification.ToString(), "Seeded description");
        foreach (var link in versions.Links)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO retravion_specification.link VALUES ($1, $2, $3)", connection);
            insert.Parameters.AddWithValue(specification);
            insert.Parameters.AddWithValue((short)link.Kind);
            insert.Parameters.AddWithValue(link.VersionId);
            await insert.ExecuteNonQueryAsync();
        }
        await SetReopenSeedStatus(connection, specification, status);
        return specification;
    }

    private static async Task SetReopenSeedStatus(NpgsqlConnection connection, Guid specification, string status, NpgsqlTransaction? transaction = null)
    {
        await using var update = new NpgsqlCommand("UPDATE retravion_specification.specification SET status = $1 WHERE internal_id = $2", connection, transaction);
        update.Parameters.AddWithValue(status);
        update.Parameters.AddWithValue(specification);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    private static async Task<long> ReopenEvidenceCount(NpgsqlConnection connection)
    {
        await using var count = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM retravion_need.review WHERE decision = 'Reopened')
                 + (SELECT count(*) FROM retravion_requirement.review WHERE decision = 'Reopened')
                 + (SELECT count(*) FROM retravion_test_case.history WHERE action = 'test-case.reopen')
                 + (SELECT count(*) FROM retravion_audit.event WHERE action IN ('need.reopen', 'requirement.reopen', 'test-case.reopen'))
            """, connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private sealed record OwnerVersions(UserNeedSnapshot Need, RequirementSnapshot Requirement, TestCaseSnapshot Case)
    {
        public SpecificationLink[] Links => [new(SpecificationLinkKind.UserNeed, Need.Need.VersionId),
            new(SpecificationLinkKind.Requirement, Requirement.Requirement.VersionId), new(SpecificationLinkKind.TestCase, Case.TestCase.VersionId)];
    }

    private sealed record ReopenResults(Result<UserNeedSnapshot> Need, Result<RequirementSnapshot> Requirement, Result<TestCaseSnapshot> Case);

    private sealed class ReopenOwners(string connectionString)
    {
        private readonly PostgresUserNeedStore needs = new(connectionString);
        private readonly PostgresRequirementStore requirements = new(connectionString);
        private readonly PostgresTestCaseStore cases = new(connectionString);

        public async Task<OwnerVersions> Create(Guid product)
        {
            var need = (await needs.Create(Need.Create(product, "N-1", "Need", "Statement").Value, CreateActor, CreateTime)).Value;
            var requirement = (await requirements.Create(Requirement.Create(product, "R-1", "Requirement", "Statement", RequirementType.System).Value,
                CreateActor, CreateTime)).Value;
            var test = (await cases.Create(product, LinkTestContent(), CreateActor, CreateTime)).Value;
            test = (await cases.ChangeLink(test, "add", new TestCaseRequirementLink(requirement.Requirement.InternalId, requirement.Requirement.VersionId),
                null, CreateActor, CreateTime)).Value;
            return new(need, requirement, test);
        }

        public async Task<OwnerVersions> Approve(OwnerVersions versions)
        {
            var need = (await needs.Update(versions.Need, versions.Need.Need.SubmitForReview().Value, CreateActor, CreateTime, "InReview")).Value;
            need = (await needs.Update(need, need.Need.Approve().Value, CreateActor, CreateTime, "Approved")).Value;
            var requirement = (await requirements.Update(versions.Requirement, versions.Requirement.Requirement.SubmitForReview().Value,
                CreateActor, CreateTime, "InReview")).Value;
            requirement = (await requirements.Update(requirement, requirement.Requirement.Approve().Value, CreateActor, CreateTime, "Approved")).Value;
            var test = (await cases.Review(versions.Case, "InReview", null, CreateActor, CreateTime)).Value;
            test = (await cases.Review(test, "Approved", null, CreateActor, CreateTime)).Value;
            return new(need, requirement, test);
        }

        public async Task<OwnerVersions> Successors(OwnerVersions versions) => new(
            (await needs.CreateSuccessor(versions.Need, "Next", CreateActor, CreateTime)).Value,
            (await requirements.CreateSuccessor(versions.Requirement, "Next", CreateActor, CreateTime)).Value,
            (await cases.CreateSuccessor(versions.Case, "Next", CreateActor, CreateTime)).Value);

        public async Task<OwnerVersions> Find(OwnerVersions versions, bool exact = false) => new(
            (await needs.Find(versions.Need.Need.InternalId, versionId: exact ? versions.Need.Need.VersionId : null)).Value.Value,
            (await requirements.Find(versions.Requirement.Requirement.InternalId, versionId: exact ? versions.Requirement.Requirement.VersionId : null)).Value.Value,
            (await cases.Find(versions.Case.TestCase.InternalId, versionId: exact ? versions.Case.TestCase.VersionId : null)).Value.Value);

        public Task<Result<UserNeedSnapshot>> ReopenNeed(UserNeedSnapshot need, CancellationToken token = default) =>
            needs.Update(need, need.Need.Reopen(ReopenReason).Value, CreateActor, CreateTime, "Reopened", ReopenReason, token);

        public async Task<ReopenResults> Reopen(OwnerVersions versions) => new(await ReopenNeed(versions.Need),
            await requirements.Update(versions.Requirement, versions.Requirement.Requirement.Reopen(ReopenReason).Value,
                CreateActor, CreateTime, "Reopened", ReopenReason),
            await cases.Reopen(versions.Case, ReopenReason, CreateActor, CreateTime));
    }
}
