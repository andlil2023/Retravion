using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Requirement;
using Retravion.Domain.Specification;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task SpecificationLinksKeepHistoricalVersionsAcrossSuccessorsAndAuditEffectiveSets()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var needs = new PostgresUserNeedStore(db.ConnectionString);
        var requirements = new PostgresRequirementStore(db.ConnectionString);
        var cases = new PostgresTestCaseStore(db.ConnectionString);
        var need = (await needs.Create(Need.Create(product, "N-1", "Need", "Statement").Value, CreateActor, CreateTime)).Value;
        need = (await needs.Update(need, need.Need.SubmitForReview().Value, CreateActor, CreateTime, "InReview")).Value;
        need = (await needs.Update(need, need.Need.Approve().Value, CreateActor, CreateTime, "Approved")).Value;
        var requirement = (await requirements.Create(Requirement.Create(product, "R-1", "Requirement", "Statement",
            RequirementType.System).Value, CreateActor, CreateTime)).Value;
        requirement = (await requirements.Update(requirement, requirement.Requirement.SubmitForReview().Value,
            CreateActor, CreateTime, "InReview")).Value;
        requirement = (await requirements.Update(requirement, requirement.Requirement.Approve().Value,
            CreateActor, CreateTime, "Approved")).Value;
        var test = (await cases.Create(product, LinkTestContent(), CreateActor, CreateTime)).Value;
        test = (await cases.ChangeLink(test, "add", new TestCaseRequirementLink(requirement.Requirement.InternalId,
            requirement.Requirement.VersionId), null, CreateActor, CreateTime)).Value;
        test = (await cases.Review(test, "InReview", null, CreateActor, CreateTime)).Value;
        test = (await cases.Review(test, "Approved", null, CreateActor, CreateTime)).Value;

        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await operations.Create(principal, product, "DEV-1", "Original", null)).Value;
        var targets = new SpecificationLink[] { new(SpecificationLinkKind.UserNeed, need.Need.VersionId),
            new(SpecificationLinkKind.Requirement, requirement.Requirement.VersionId), new(SpecificationLinkKind.TestCase, test.TestCase.VersionId) };
        var ownerIds = new[] { need.Need.InternalId, requirement.Requirement.InternalId, test.TestCase.InternalId };
        var identifiers = new[] { need.Need.Id, requirement.Requirement.Id, test.TestCase.Id };
        var titles = new[] { "Need", "Requirement", "Case" };
        Assert.Equal(draft, (await operations.EditLinks(principal, product, draft.InternalId, [])).Value);
        clock.Now = CreateTime.AddMinutes(1);
        var saved = (await operations.EditLinks(principal, product, draft.InternalId, targets)).Value;
        Assert.Equal(targets, saved.Links.Targets);
        Assert.Equal(saved, (await operations.EditLinks(principal, product, draft.InternalId, targets.Reverse())).Value);

        var nextNeed = (await needs.CreateSuccessor(need, "Improve", CreateActor, CreateTime)).Value;
        var nextRequirement = (await requirements.CreateSuccessor(requirement, "Improve", CreateActor, CreateTime)).Value;
        var nextTest = (await cases.CreateSuccessor(test, "Improve", CreateActor, CreateTime)).Value;
        var nextIds = new[] { nextNeed.Need.VersionId, nextRequirement.Requirement.VersionId, nextTest.TestCase.VersionId };
        Assert.Equal(saved, (await operations.Find(principal, product, draft.InternalId)).Value.Value);
        Assert.Equal(saved, Assert.Single((await operations.List(principal, product)).Value));
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            var choices = (await operations.ListVersions(principal, product, target.Kind)).Value;
            Assert.Equal(2, choices.Count);
            var historical = (await operations.ResolveVersion(principal, product, target)).Value.Value;
            Assert.Equal(Assert.Single(choices, choice => choice.Target == target), historical);
            Assert.Equal(ownerIds[i], historical.InternalId);
            Assert.Equal(identifiers[i], historical.Identifier);
            Assert.Equal(titles[i], historical.Title);
            Assert.Equal(1, historical.VersionNumber);
            Assert.True(historical.IsApproved);
            Assert.Contains(choices, choice => choice.Target.VersionId == nextIds[i] && choice.VersionNumber == 2 && !choice.IsApproved);
        }
        var content = (await operations.EditContent(principal, product, draft, draft.Id, "Changed title", null)).Value;
        Assert.Equal(saved.Links, content.Links);
        var firstEvent = Assert.Single(await LinkEvents(db));
        Assert.Equal(("User", clock.Now.UtcTicks, "identified", draft.InternalId.ToString(), "success"),
            (firstEvent.Actor, firstEvent.UtcTicks, firstEvent.TargetKind, firstEvent.Target, firstEvent.Outcome));
        Assert.Equal(draft.InternalId, firstEvent.Change!.InternalId);
        Assert.Equal(product, firstEvent.Change.After!.ProductInternalId);
        Assert.Empty(firstEvent.Change.Before!.Links!);
        var originalAuditTargets = targets.Select(link => new SpecificationAuditLink(link.Kind.ToString(), link.VersionId)).ToArray();
        Assert.Equal(originalAuditTargets, firstEvent.Change.After.Links);

        Assert.Empty((await operations.EditLinks(principal, product, draft.InternalId, [])).Value.Links.Targets);
        Assert.Empty((await operations.Find(principal, product, draft.InternalId)).Value.Value.Links.Targets);
        var replaced = (await operations.EditLinks(principal, product, draft.InternalId, [targets[0]])).Value;
        Assert.Equal(targets[0], Assert.Single(replaced.Links.Targets));
        Assert.Equal(replaced, (await operations.Find(principal, product, draft.InternalId)).Value.Value);
        var events = await LinkEvents(db);
        Assert.Equal(3, events.Count);
        Assert.Equal(originalAuditTargets, events[1].Change!.Before!.Links);
        Assert.Empty(events[1].Change!.After!.Links!);
        Assert.Empty(events[2].Change!.Before!.Links!);
        Assert.Equal(originalAuditTargets[0], Assert.Single(events[2].Change!.After!.Links!));
    }

    [PostgresFact]
    public async Task SpecificationLinksRejectMissingWrongProductWrongKindAndNonDraftTargetsWithoutSuccessAudit()
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
        var draft = (await operations.Create(principal, product.InternalId, "DEV-1", "Original", "# Description")).Value;
        foreach (var kind in Enum.GetValues<SpecificationLinkKind>())
            Assert.Empty((await operations.ListVersions(principal, product.InternalId, kind)).Value);

        var foreign = await CreateLinkTargets(db, other.InternalId);
        foreach (var target in foreign)
        {
            Assert.Empty((await operations.ListVersions(principal, product.InternalId, target.Kind)).Value);
            Assert.True((await operations.ResolveVersion(principal, product.InternalId, target)).Value.HasNoValue);
            Assert.True((await operations.EditLinks(principal, product.InternalId, draft.InternalId, [target])).IsFailure);
            Assert.True((await operations.EditLinks(principal, product.InternalId, draft.InternalId,
                [target with { VersionId = Guid.NewGuid() }])).IsFailure);
        }
        var local = await CreateLinkTargets(db, product.InternalId);
        Assert.True((await operations.EditLinks(principal, product.InternalId, draft.InternalId,
            [local[0] with { Kind = SpecificationLinkKind.Requirement }])).IsFailure);
        Assert.True((await operations.EditLinks(principal, product.InternalId, draft.InternalId, [local[0], local[0]])).IsFailure);
        Assert.True((await operations.EditLinks(principal, product.InternalId, draft.InternalId, [local[0], foreign[1]])).IsFailure);
        Assert.True((await operations.EditLinks(principal, other.InternalId, draft.InternalId, foreign)).IsFailure);
        Assert.Equal(draft, (await operations.Find(principal, product.InternalId, draft.InternalId)).Value.Value);

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        foreach (var status in new[] { "In Review", "Approved", "Implemented" })
        {
            if (status == "Implemented") await SeedImplemented(connection, draft.InternalId);
            else await SetReopenSeedStatus(connection, draft.InternalId, status);
            Assert.Equal("Only Draft Specification links can be edited.",
                (await operations.EditLinks(principal, product.InternalId, draft.InternalId, local)).Error);
        }
        var retiring = (await operations.Create(principal, product.InternalId, "DEV-2", "Draft", null)).Value;
        Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
        Assert.Contains("retired", (await operations.EditLinks(principal, product.InternalId, retiring.InternalId, local)).Error);
        Assert.Equal(retiring, (await operations.Find(principal, product.InternalId, retiring.InternalId)).Value.Value);
        Assert.Empty(await LinkEvents(db));
    }

    [PostgresFact]
    public async Task SpecificationLinksAuditDenialAndRollBackReplacementOnAuditFailure()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var targets = await CreateLinkTargets(db, product);
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var principal = await Principal(sessions, "User", now: CreateTime);
        var revoked = await Principal(sessions, "User", now: CreateTime);
        var draft = (await operations.Create(principal, product, "DEV-1", "Original", null)).Value;
        var saved = (await operations.EditLinks(principal, product, draft.InternalId, targets.Take(2))).Value;
        await sessions.RemoveAsync(revoked.FindFirstValue(SessionTicketStore.SessionClaim)!);
        Assert.True((await operations.ListVersions(revoked, product, targets[0].Kind)).IsFailure);
        Assert.True((await operations.ResolveVersion(revoked, product, targets[0])).IsFailure);
        Assert.Equal("Access denied.", (await operations.EditLinks(revoked, product, draft.InternalId, targets)).Error);
        var denial = new CreateEvent("User", CreateTime.UtcTicks, "unknown", null, "failure", null);
        Assert.Equal(denial, (await LinkEvents(db))[1]);

        await InstallAuditFault(db, "RAISE insufficient_privilege USING MESSAGE = 'private provider details';");
        Assert.Equal("The Specification operation could not be completed.",
            (await operations.EditLinks(principal, product, draft.InternalId, targets.Skip(1))).Error);
        Assert.Equal(saved, (await operations.Find(principal, product, draft.InternalId)).Value.Value);
        Assert.Equal(2, (await LinkEvents(db)).Count);
    }

    [PostgresFact]
    public async Task SpecificationLinksUpgradeExistingDraftStorageOnRead()
    {
        await using var db = await Database.Create();
        var product = (await Product(new PostgresProductStore(db.ConnectionString), "first")).InternalId;
        var targets = await CreateLinkTargets(db, product);
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var principal = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var draft = (await operations.Create(principal, product, "DEV-1", "Original", null)).Value;
        await using (var connection = new NpgsqlConnection(db.ConnectionString))
        {
            await connection.OpenAsync();
            await using var legacySchema = new NpgsqlCommand("DROP TABLE retravion_specification.link", connection);
            await legacySchema.ExecuteNonQueryAsync();
        }

        Assert.Equal(draft, (await operations.Find(principal, product, draft.InternalId)).Value.Value);
        Assert.Empty(await LinkEvents(db));
        var linked = (await operations.EditLinks(principal, product, draft.InternalId, targets)).Value;
        Assert.Equal(linked, (await operations.Find(principal, product, draft.InternalId)).Value.Value);
        Assert.Single(await LinkEvents(db));
    }

    private static TestCaseContent LinkTestContent() => TestCaseContent.Create("Case", "", "", [new("Do", "Done")]).Value;

    private static async Task<SpecificationLink[]> CreateLinkTargets(Database db, Guid product)
    {
        var need = (await new PostgresUserNeedStore(db.ConnectionString).Create(
            Need.Create(product, "N-1", "Need", "Statement").Value, CreateActor, CreateTime)).Value;
        var requirement = (await new PostgresRequirementStore(db.ConnectionString).Create(
            Requirement.Create(product, "R-1", "Requirement", "Statement", RequirementType.System).Value, CreateActor, CreateTime)).Value;
        var test = (await new PostgresTestCaseStore(db.ConnectionString).Create(product, LinkTestContent(), CreateActor, CreateTime)).Value;
        return [new(SpecificationLinkKind.UserNeed, need.Need.VersionId), new(SpecificationLinkKind.Requirement, requirement.Requirement.VersionId),
            new(SpecificationLinkKind.TestCase, test.TestCase.VersionId)];
    }

    private static Task<List<CreateEvent>> LinkEvents(Database db) => SpecificationEvents(db, "specification.edit-links");
}
