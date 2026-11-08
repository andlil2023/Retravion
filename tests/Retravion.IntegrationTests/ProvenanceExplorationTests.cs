using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Npgsql;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Application.UserNeed;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.UserNeed;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Relationships;
using Retravion.Web.Features.Requirement;
using Retravion.Web.Features.UserNeed;
using System.Security.Claims;
using Xunit;
using Need = Retravion.Domain.UserNeed.UserNeed;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task ReverseProvenanceMatchesEachSourceVersionAndAlwaysTargetsCopyV1()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("FROM", "Source Product", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("TO", "Copy Product", ProductKind.System, "")).Value;
        var r1 = await ApprovedRequirement(requirements, from.InternalId, "R");
        var r2 = (await requirements.CreateSuccessor(r1, "Next source", RequirementActor, RequirementTime)).Value;
        r1 = (await requirements.Find(r1.Requirement.InternalId, versionId: r1.Requirement.VersionId)).Value.Value;
        var rc1 = (await requirements.Derive(r1, RequirementState.Create(to.InternalId, "R-COPY-1", "Copy 1", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var rc2 = (await requirements.Derive(r2, RequirementState.Create(to.InternalId, "R-COPY-2", "Copy 2", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var rc1Review = (await requirements.Update(rc1, rc1.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var rc1Approved = (await requirements.Update(rc1Review, rc1Review.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var rc1Next = (await requirements.CreateSuccessor(rc1Approved, "Next copy", RequirementActor, RequirementTime)).Value;
        var n1 = await ApprovedNeed(needs, from.InternalId, "N");
        var n2 = (await needs.CreateSuccessor(n1, "Next source", RequirementActor, RequirementTime)).Value;
        n1 = (await needs.Find(n1.Need.InternalId, versionId: n1.Need.VersionId)).Value.Value;
        var nc1 = (await needs.Derive(n1, Need.Create(to.InternalId, "N-COPY-1", "Copy 1", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var nc2 = (await needs.Derive(n2, Need.Create(to.InternalId, "N-COPY-2", "Copy 2", "Statement").Value, RequirementActor, RequirementTime)).Value;
        var nc1Review = (await needs.Update(nc1, nc1.Need.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var nc1Approved = (await needs.Update(nc1Review, nc1Review.Need.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var nc1Next = (await needs.CreateSuccessor(nc1Approved, "Next copy", RequirementActor, RequirementTime)).Value;

        await using var host = new McpTestHost(services =>
        {
            RegisterRelationshipCases(services, database.ConnectionString);
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRequirementStore>(requirements); services.AddSingleton<IRequirementLinkStore>(requirements);
            services.AddSingleton<IRequirementChangeTransaction>(requirements);
            services.AddSingleton<IUserNeedStore>(needs); services.AddSingleton<IUserNeedChangeTransaction>(needs);
        });
        await host.Login(); await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        foreach (var (kind, id, version, expectedCopy) in new[]
        {
            ("requirement", r1.Requirement.InternalId, r1.Requirement.VersionId, rc1.Requirement.VersionId),
            ("requirement", r2.Requirement.InternalId, r2.Requirement.VersionId, rc2.Requirement.VersionId),
            ("user-need", n1.Need.InternalId, n1.Need.VersionId, nc1.Need.VersionId),
            ("user-need", n2.Need.InternalId, n2.Need.VersionId, nc2.Need.VersionId)
        })
        {
            var result = await operations.Explore(actor, kind, id, version, []);
            Assert.True(result.IsSuccess, result.IsFailure ? result.Error : "");
            var edge = Assert.Single(result.Value.Edges, e => e.IsProvenance);
            Assert.Equal(version, edge.Target.VersionId);
            Assert.Equal(expectedCopy, edge.Owner.VersionId);
            Assert.Equal(1, edge.Owner.VersionNumber);
            Assert.NotEqual(rc1Next.Requirement.VersionId, edge.Owner.VersionId);
            Assert.NotEqual(nc1Next.Need.VersionId, edge.Owner.VersionId);
        }
        var needForward = await operations.Explore(actor, "user-need", nc1Next.Need.InternalId, nc1Next.Need.VersionId, []);
        Assert.True(needForward.IsSuccess, needForward.IsFailure ? needForward.Error : "");
        Assert.Equal(n1.Need.VersionId, Assert.Single(needForward.Value.Edges, e => e.IsProvenance).Target.VersionId);
    }

    [PostgresFact]
    public async Task ProvenanceExplorationUsesExactVersionsInBothViewsAndDoesNotWrite()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var requirements = new PostgresRequirementStore(database.ConnectionString);
        var needs = new PostgresUserNeedStore(database.ConnectionString);
        var from = (await new CreateProduct(products).Execute("FROM", "Source Product", ProductKind.System, "")).Value;
        var to = (await new CreateProduct(products).Execute("TO", "Copy Product", ProductKind.System, "")).Value;
        var source = (await requirements.Create(RequirementState.Create(from.InternalId, "SRC", "Source title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var need = (await needs.Create(Need.Create(from.InternalId, "NEED", "Source need", "Need statement").Value, RequirementActor, RequirementTime)).Value;
        source = (await requirements.ChangeLink(source, Endpoint(need), true, RequirementActor, RequirementTime)).Value;
        var copy = (await requirements.Derive(source, RequirementState.Create(to.InternalId, "COPY", "Copy title", "Statement", RequirementType.System).Value, RequirementActor, RequirementTime)).Value;
        var reviewed = (await requirements.Update(copy, copy.Requirement.SubmitForReview().Value, RequirementActor, RequirementTime, "InReview")).Value;
        var approved = (await requirements.Update(reviewed, reviewed.Requirement.Approve().Value, RequirementActor, RequirementTime, "Approved")).Value;
        var successor = (await requirements.CreateSuccessor(approved, "Next", RequirementActor, RequirementTime)).Value;
        var needCopy = (await needs.Derive(need, Need.Create(to.InternalId, "NCOPY", "Need copy", "Statement").Value, RequirementActor, RequirementTime)).Value;

        await using var host = new McpTestHost(services =>
        {
            RegisterRelationshipCases(services, database.ConnectionString);
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<IRequirementStore>(requirements); services.AddSingleton<IRequirementLinkStore>(requirements);
            services.AddSingleton<IRequirementChangeTransaction>(requirements);
            services.AddSingleton<IUserNeedStore>(needs); services.AddSingleton<IUserNeedChangeTransaction>(needs);
        });
        using var login = await host.Browser.PostAsync("/identity/login", await host.Form("/", new() { ["username"] = "admin", ["password"] = "InitialPass123!" }));
        Assert.Equal("/password-replacement", login.Headers.Location?.OriginalString);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Retravion.Identity=")).Split(';')[0];
        var options = host.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        var ticket = options.TicketDataFormat.Unprotect(cookie.Split('=', 2)[1])!;
        var key = ticket.Principal.FindFirstValue("Microsoft.AspNetCore.Authentication.Cookies-SessionId")!;
        var restricted = (await options.SessionStore!.RetrieveAsync(key))!.Principal;
        using var scope = host.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<RelationshipOperations>();
        var requirementReads = scope.ServiceProvider.GetRequiredService<RequirementOperations>();
        var needReads = scope.ServiceProvider.GetRequiredService<UserNeedOperations>();
        foreach (var denied in new[] { new ClaimsPrincipal(new ClaimsIdentity()), restricted })
        {
            Assert.True((await requirementReads.Provenance(denied, copy.Requirement.InternalId)).IsFailure);
            Assert.True((await requirementReads.CopiesOf(denied, source.Requirement.VersionId)).IsFailure);
            Assert.True((await needReads.Provenance(denied, needCopy.Need.InternalId)).IsFailure);
            Assert.True((await needReads.CopiesOf(denied, need.Need.VersionId)).IsFailure);
            Assert.True((await operations.Explore(denied, "requirement", copy.Requirement.InternalId, copy.Requirement.VersionId, [source.Requirement.VersionId])).IsFailure);
            Assert.True((await operations.Load(denied, "requirement", source.Requirement.InternalId, source.Requirement.VersionId)).IsFailure);
            Assert.True((await operations.Explore(denied, "user-need", needCopy.Need.InternalId, needCopy.Need.VersionId, [need.Need.VersionId])).IsFailure);
            Assert.True((await operations.Load(denied, "user-need", need.Need.InternalId, need.Need.VersionId)).IsFailure);
        }
        await host.Replace(); var credential = await host.Authenticate();
        var actor = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(credential);
        var audit = new PostgresAuditQuery(database.ConnectionString);
        var before = (await audit.FindAsync(new())).Count;

        var reverse = await operations.Explore(actor, "requirement", source.Requirement.InternalId, source.Requirement.VersionId, []);
        Assert.True(reverse.IsSuccess, reverse.IsFailure ? reverse.Error : "");
        var edge = Assert.Single(reverse.Value.Edges, e => e.IsProvenance);
        Assert.Equal(copy.Requirement.VersionId, edge.Owner.VersionId);
        Assert.NotEqual(successor.Requirement.VersionId, edge.Owner.VersionId);
        Assert.Equal(source.Requirement.VersionId, edge.Target.VersionId);
        var forward = await operations.Explore(actor, "requirement", successor.Requirement.InternalId, successor.Requirement.VersionId, []);
        Assert.True(forward.IsSuccess, forward.IsFailure ? forward.Error : "");
        Assert.Equal(source.Requirement.VersionId, Assert.Single(forward.Value.Edges, e => e.IsProvenance).Target.VersionId);
        var needReverse = await operations.Explore(actor, "user-need", need.Need.InternalId, need.Need.VersionId, []);
        Assert.True(needReverse.IsSuccess, needReverse.IsFailure ? needReverse.Error : "");
        Assert.Equal(needCopy.Need.VersionId, Assert.Single(needReverse.Value.Edges, e => e.IsProvenance).Owner.VersionId);
        var needForward = await operations.Explore(actor, "user-need", needCopy.Need.InternalId, needCopy.Need.VersionId, []);
        Assert.True(needForward.IsSuccess, needForward.IsFailure ? needForward.Error : "");
        Assert.Equal(need.Need.VersionId, Assert.Single(needForward.Value.Edges, e => e.IsProvenance).Target.VersionId);
        Assert.True((await operations.Change(actor, edge.Owner, edge.Target, edge.Relationship, "remove")).IsFailure);
        var expanded = await operations.Explore(actor, "requirement", copy.Requirement.InternalId, copy.Requirement.VersionId,
            [source.Requirement.VersionId, need.Need.VersionId, copy.Requirement.VersionId]);
        Assert.True(expanded.IsSuccess, expanded.IsFailure ? expanded.Error : "");
        Assert.Equal(copy.Requirement.VersionId, expanded.Value.Root.Center.VersionId);
        Assert.Equal(3, expanded.Value.Neighborhoods.Count);
        Assert.Contains(expanded.Value.Edges, e => e.Relationship == RelationshipOperations.NeedRelationship && e.Target.VersionId == need.Need.VersionId);
        var collapsed = await operations.Explore(actor, "requirement", copy.Requirement.InternalId, copy.Requirement.VersionId, [need.Need.VersionId]);
        Assert.True(collapsed.IsSuccess, collapsed.IsFailure ? collapsed.Error : "");
        Assert.Single(collapsed.Value.Neighborhoods);
        Assert.DoesNotContain(collapsed.Value.Edges, e => e.Target.VersionId == need.Need.VersionId);
        var refocused = await operations.Load(actor, "requirement", source.Requirement.InternalId, source.Requirement.VersionId);
        Assert.True(refocused.IsSuccess, refocused.IsFailure ? refocused.Error : "");
        Assert.Equal(source.Requirement.VersionId, refocused.Value.Center.VersionId);

        await using var services = ExplorationServices(operations, actor);
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RelationshipLinks), Parameters(reverse.Value.Root.Center));
            Assert.Contains("Derived from (center is Source for this copy) → center", renderer.Text());
            Assert.Contains("Copy Product", renderer.Text());
            Assert.Contains(renderer.Components<MudLink>(), l => l.Href == edge.Owner.LinksPath);
            Assert.Contains(renderer.Components<MudLink>(), l => l.Href == edge.Target.LinksPath);
            Assert.DoesNotContain(renderer.Components<MudButton>(), b => b.UserAttributes?.TryGetValue("aria-label", out var label) == true && label?.ToString()?.Contains("Remove Derived from") == true);
            var graphLinks = renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray();
            await renderer.Click("Table view");
            Assert.Contains("Derived from (center is Source for this copy)", renderer.Text());
            Assert.Equal(graphLinks, renderer.Components<MudLink>().Select(l => l.Href).Distinct().Order().ToArray());
            var rows = Assert.Single(renderer.Components<MudTable<RelationshipEdge>>()).Items!.ToArray();
            var row = Assert.Single(rows, e => e.IsProvenance);
            Assert.Equal(copy.Requirement.VersionId, row.Owner.VersionId);
            Assert.Equal(source.Requirement.VersionId, row.Target.VersionId);
            await ExplorationClick(renderer, "Expand Requirement COPY · v1");
            Assert.Contains("Center: Requirement SRC · v1", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
        await using var chainRenderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await chainRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await chainRenderer.Mount(typeof(RelationshipLinks), Parameters(forward.Value.Root.Center));
            Assert.DoesNotContain("Source need", chainRenderer.Text());
            Assert.Contains(chainRenderer.Components<MudLink>(), l => l.Href == edge.Target.LinksPath);
            await ExplorationClick(chainRenderer, "Expand Requirement SRC · v1");
            Assert.Contains("Source need", chainRenderer.Text());
            Assert.Contains("Center: Requirement COPY · v2", chainRenderer.Text());
            await ExplorationClick(chainRenderer, "Expand User Need NEED · v1");
            await chainRenderer.Click("Table view");
            Assert.Contains("Source need", chainRenderer.Text());
            await ExplorationClick(chainRenderer, "Collapse Requirement SRC · v1");
            Assert.DoesNotContain("Source need", chainRenderer.Text());
            await chainRenderer.Click("Graph view");
            Assert.DoesNotContain("Source need", chainRenderer.Text());
        });
        Assert.Empty(chainRenderer.Errors);
        var sourceAfter = (await requirements.Find(source.Requirement.InternalId, versionId: source.Requirement.VersionId)).Value.Value;
        var copyAfter = (await requirements.Find(successor.Requirement.InternalId, versionId: successor.Requirement.VersionId)).Value.Value;
        var needAfter = (await needs.Find(need.Need.InternalId, versionId: need.Need.VersionId)).Value.Value;
        var needCopyAfter = (await needs.Find(needCopy.Need.InternalId, versionId: needCopy.Need.VersionId)).Value.Value;
        Assert.Equal((source.Revision, source.Requirement.Title, source.Requirement.Statement, source.Requirement.Status),
            (sourceAfter.Revision, sourceAfter.Requirement.Title, sourceAfter.Requirement.Statement, sourceAfter.Requirement.Status));
        Assert.Equal((successor.Revision, successor.Requirement.Title, successor.Requirement.Statement, successor.Requirement.Status),
            (copyAfter.Revision, copyAfter.Requirement.Title, copyAfter.Requirement.Statement, copyAfter.Requirement.Status));
        Assert.Equal((need.Revision, need.Need.Title, need.Need.Statement, need.Need.Status),
            (needAfter.Revision, needAfter.Need.Title, needAfter.Need.Statement, needAfter.Need.Status));
        Assert.Equal((needCopy.Revision, needCopy.Need.Title, needCopy.Need.Statement, needCopy.Need.Status),
            (needCopyAfter.Revision, needCopyAfter.Need.Title, needCopyAfter.Need.Statement, needCopyAfter.Need.Status));
        Assert.Equal(before, (await audit.FindAsync(new())).Count);

        // Simulate a missing exact source version without changing the copy's retained snapshot.
        var missing = Guid.NewGuid();
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var table in new[] { "retravion_requirement.provenance", "retravion_need.provenance" })
            {
                await using var command = new NpgsqlCommand($"UPDATE {table} SET source_snapshot=jsonb_set(source_snapshot, '{{SourceVersionId}}', to_jsonb($1::text))", connection);
                command.Parameters.AddWithValue(missing.ToString());
                await command.ExecuteNonQueryAsync();
            }
        }
        var absent = await operations.Explore(actor, "requirement", copy.Requirement.InternalId, copy.Requirement.VersionId, []);
        Assert.True(absent.IsSuccess, absent.IsFailure ? absent.Error : "");
        Assert.DoesNotContain(absent.Value.Edges, e => e.IsProvenance);
        Assert.Equal("Source title", Assert.Single(absent.Value.UnavailableOrigins).SourceTitle);
        var absentNeed = await operations.Explore(actor, "user-need", needCopy.Need.InternalId, needCopy.Need.VersionId, []);
        Assert.True(absentNeed.IsSuccess, absentNeed.IsFailure ? absentNeed.Error : "");
        Assert.Equal("Source need", Assert.Single(absentNeed.Value.UnavailableOrigins).SourceTitle);
        await using var missingRenderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await missingRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await missingRenderer.Mount(typeof(RelationshipLinks), Parameters(absent.Value.Root.Center));
            Assert.Contains("source unavailable", missingRenderer.Text());
            Assert.Contains("Source title", missingRenderer.Text());
            Assert.DoesNotContain(missingRenderer.Components<MudLink>(), l => l.Href?.Contains(missing.ToString()) == true);
            await missingRenderer.Click("Table view");
            Assert.Contains("source unavailable", missingRenderer.Text());
            Assert.Contains("Source title", missingRenderer.Text());
        });
        Assert.Empty(missingRenderer.Errors);
        Assert.Equal(before, (await audit.FindAsync(new())).Count);
        await host.Factory.Services.GetRequiredService<SessionTicketStore>().RemoveAsync(actor.FindFirstValue(SessionTicketStore.SessionClaim)!);
        Assert.True((await requirementReads.Provenance(actor, copy.Requirement.InternalId)).IsFailure);
        Assert.True((await requirementReads.CopiesOf(actor, source.Requirement.VersionId)).IsFailure);
        Assert.True((await needReads.Provenance(actor, needCopy.Need.InternalId)).IsFailure);
        Assert.True((await needReads.CopiesOf(actor, need.Need.VersionId)).IsFailure);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await ExplorationClick(renderer, "Collapse Requirement COPY · v1");
            Assert.Contains("Access denied", renderer.Text());
            Assert.DoesNotContain("Copy title", renderer.Text());
            Assert.Empty(renderer.Components<MudLink>());
        });
        Assert.True((await operations.Explore(actor, "requirement", copy.Requirement.InternalId, copy.Requirement.VersionId, [source.Requirement.VersionId])).IsFailure);
        Assert.True((await operations.Load(actor, "requirement", source.Requirement.InternalId, source.Requirement.VersionId)).IsFailure);
        Assert.True((await operations.Explore(actor, "user-need", needCopy.Need.InternalId, needCopy.Need.VersionId, [need.Need.VersionId])).IsFailure);
        Assert.True((await operations.Load(actor, "user-need", need.Need.InternalId, need.Need.VersionId)).IsFailure);
    }
}
