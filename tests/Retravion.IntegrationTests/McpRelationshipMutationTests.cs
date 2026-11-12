using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Infrastructure.Feature;
using Retravion.Web.Features.Requirement;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task McpLinkContractRequiresExactVersionsRevisionConfirmationAndRejectsInjection()
    {
        await using var host = new McpTestHost(RegisterLinkContractStores);
        await host.Login(); await host.Replace(); await host.Authenticate();
        var tool = Assert.Single((await host.Rpc("tools/list")).GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "change_draft_link");
        Assert.True(tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.False(tool.GetProperty("annotations").GetProperty("idempotentHint").GetBoolean());
        var valid = new Dictionary<string, object?>
        {
            ["ownerKind"] = "requirement", ["ownerId"] = Guid.NewGuid(), ["ownerVersionId"] = Guid.NewGuid(),
            ["expectedRevision"] = 1L, ["relationship"] = "user-need", ["operation"] = "add",
            ["targetId"] = Guid.NewGuid(), ["targetVersionId"] = Guid.NewGuid(), ["confirmed"] = true
        };
        foreach (var name in valid.Keys)
        {
            var missing = new Dictionary<string, object?>(valid); missing.Remove(name);
            using var response = await host.Send("tools/call", new { name = "change_draft_link", arguments = missing });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        foreach (var (key, value) in new (string, object?)[]
        {
            ("ownerKind", "user-need"), ("relationship", "provenance"), ("relationship", "verifies"), ("operation", "replace"),
            ("ownerId", Guid.Empty), ("ownerVersionId", "latest"), ("targetId", Guid.Empty), ("targetVersionId", null),
            ("expectedRevision", 0), ("expectedRevision", 1.5), ("confirmed", "true"), ("actor", "spoofed"),
            ("status", "Approved"), ("replacementVersionId", Guid.NewGuid()), ("operation", "retarget")
        })
        {
            var args = new Dictionary<string, object?>(valid) { [key] = value };
            using var response = await host.Send("tools/call", new { name = "change_draft_link", arguments = args });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        valid["confirmed"] = false;
        Assert.Contains("confirmation", (await host.Call("change_draft_link", valid, true)).GetString());
    }

    [Fact]
    public async Task McpLinkDenialsAuditBeforeValidationAndSafelyHandleAuditFailure()
    {
        var audit = new LinkDenialRecorder();
        var logger = new RequirementAuditLogger();
        await using var host = new McpTestHost(services =>
        {
            RegisterLinkContractStores(services);
            services.AddSingleton<IAuditSubmission>(audit);
            services.AddSingleton<ILogger<RequirementOperations>>(logger);
        });
        async Task Denied(HttpStatusCode status, string? origin = null)
        {
            using var response = await host.Send("tools/call", new { name = "change_draft_link", arguments = new { actor = "spoofed", targetId = "secret-id" } }, origin);
            Assert.Equal(status, response.StatusCode);
        }
        await Denied(HttpStatusCode.Unauthorized);
        await host.Login(); await host.Replace(); await host.Authenticate();
        await Denied(HttpStatusCode.Forbidden, "https://evil.invalid");
        await host.Revoke();
        await Denied(HttpStatusCode.Unauthorized);
        Assert.Equal(3, audit.Events.Count);
        Assert.All(audit.Events, e =>
        {
            Assert.Equal("relationship.change", e.Action); Assert.IsType<AuditTarget.Unknown>(e.Target);
            Assert.Equal(AuditActionOutcome.Failure, e.Outcome); Assert.Null(e.RequirementChange);
            Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(e));
        });
        Assert.IsType<AuditActor.Unknown>(audit.Events[0].Actor);
        Assert.Equal(new AuditActor.Identified("admin"), audit.Events[1].Actor);
        foreach (var mode in new[] { "fail", "throw" }) { audit.Mode = mode; await Denied(HttpStatusCode.Unauthorized); }
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("secret", message); Assert.DoesNotContain("spoofed", message); Assert.DoesNotContain("private-provider", message);
        });
    }

    private static void RegisterLinkContractStores(IServiceCollection services)
    {
        // Construction only: confirmation/transport denial must never reach a store operation.
        var requirements = new RequirementStoreDouble();
        services.AddSingleton<Retravion.Application.Requirement.IRequirementStore>(requirements);
        services.AddSingleton<Retravion.Application.Requirement.IRequirementChangeTransaction>(requirements);
        services.AddSingleton<Retravion.Application.Requirement.IRequirementLinkStore>(requirements);
        var needs = new NeedStoreDouble();
        services.AddSingleton<Retravion.Application.UserNeed.IUserNeedStore>(needs);
        services.AddSingleton<Retravion.Application.UserNeed.IUserNeedChangeTransaction>(needs);
        var cases = new Retravion.Infrastructure.TestCase.PostgresTestCaseStore("Host=invalid");
        services.AddSingleton<Retravion.Application.TestCase.ITestCaseStore>(cases);
        services.AddSingleton<Retravion.Application.TestCase.ITestCaseChangeTransaction>(cases);
        var features = new PostgresFeatureStore("Host=invalid");
        services.AddSingleton<IFeatureStore>(features);
        services.AddSingleton<IFeatureChangeTransaction>(features);
    }

    private sealed class LinkDenialRecorder : IAuditSubmission
    {
        internal readonly List<AuditInformation> Events = [];
        internal string Mode = "success";
        public Task<AuditSubmissionResult> SubmitAsync(AuditInformation information, CancellationToken cancellationToken = default)
        {
            if (information.Action != "relationship.change") return Task.FromResult(AuditSubmissionResult.Success);
            if (Mode == "throw") throw new IOException("private-provider-details");
            Events.Add(information);
            return Task.FromResult(Mode == "fail" ? AuditSubmissionResult.Failure : AuditSubmissionResult.Success);
        }
    }
}

public sealed partial class PostgresProductTests
{
    private static Dictionary<string, object?> LinkArguments(string kind, Guid owner, Guid ownerVersion, long revision,
        string relationship, string operation, Guid target, Guid targetVersion, Guid? replacement = null, bool confirmed = true) => new()
    {
        ["ownerKind"] = kind, ["ownerId"] = owner, ["ownerVersionId"] = ownerVersion, ["expectedRevision"] = revision,
        ["relationship"] = relationship, ["operation"] = operation, ["targetId"] = target, ["targetVersionId"] = targetVersion,
        ["replacementVersionId"] = replacement, ["confirmed"] = confirmed
    };

    [PostgresFact]
    public async Task McpOrdinaryLinksAllKindsAddRetargetRemovePreserveEndpointsAndAuditOnce()
    {
        foreach (var postgresSettings in new[] { false, true })
        foreach (var relationship in new[] { "user-need", "parent", "verifies", "addresses" })
        {
            await using var f = await CaseRelationshipFixture.Create(postgresSettings: postgresSettings);
            var kind = relationship == "verifies" ? "test-case" : relationship == "addresses" ? "feature" : "requirement";
            var featureStore = f.Scope.ServiceProvider.GetRequiredService<IFeatureChangeTransaction>();
            var feature = (await featureStore.Create(f.Data.Product.InternalId, FeatureContent.Create("Feature", ""), FeatureActor, FeatureTime)).Value;
            var req = f.Data.Successor;
            if (relationship == "parent") req = (await f.Requirements.RemoveParent(req, RequirementActor, RequirementTime)).Value;
            var owner = kind == "requirement" ? req.Requirement.InternalId : kind == "test-case" ? f.Row.TestCase.InternalId : feature.Feature.InternalId;
            var version = kind == "requirement" ? req.Requirement.VersionId : kind == "test-case" ? f.Row.TestCase.VersionId : feature.Feature.VersionId;
            var revision = kind == "requirement" ? req.Revision : kind == "test-case" ? f.Row.Revision : feature.Revision;
            var target = relationship == "user-need" ? f.Data.Need.Need.InternalId : relationship == "parent" ? f.Data.Parent.Requirement.InternalId : f.Data.Owner.Requirement.InternalId;
            var oldVersion = relationship == "user-need" ? f.Data.Need.Need.VersionId : relationship == "parent" ? f.Data.Parent.Requirement.VersionId : f.Data.Owner.Requirement.VersionId;
            var nextVersion = relationship == "user-need" ? f.Data.NewNeed.Need.VersionId : relationship == "parent" ? f.Data.NewParent.Requirement.VersionId : f.Data.Successor.Requirement.VersionId;
            async Task<int> HistoryCount() => kind == "requirement" ? (await f.Requirements.History(owner)).Value.Count :
                kind == "test-case" ? (await f.Cases.History(owner)).Value.Count :
                (await f.Scope.ServiceProvider.GetRequiredService<IFeatureStore>().History(owner)).Value.Count;
            var before = (await f.Audit.FindAsync(new())).Count;
            var historyBefore = await HistoryCount();
            var add = LinkArguments(kind, owner, version, revision, relationship, "add", target, oldVersion);
            add["confirmed"] = false;
            await f.Host.Call("change_draft_link", add, true);
            Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
            add["confirmed"] = true;
            await f.Host.Call("change_draft_link", add);
            await f.Host.Call("change_draft_link", add, true); // stale token, no duplicate event
            await f.Host.Call("change_draft_link", LinkArguments(kind, owner, version, revision + 1, relationship, "add", target, oldVersion), true); // current duplicate
            var retarget = LinkArguments(kind, owner, version, revision + 1, relationship, "retarget", target, oldVersion, nextVersion);
            retarget["replacementVersionId"] = Guid.NewGuid();
            await f.Host.Call("change_draft_link", retarget, true);
            Assert.Equal(before + 1, (await f.Audit.FindAsync(new())).Count);
            retarget["replacementVersionId"] = nextVersion;
            await f.Host.Call("change_draft_link", retarget);
            var view = await ExploreMcp(f.Host, kind, owner, version);
            Assert.Contains(view.GetProperty("edges").EnumerateArray(), e => e.GetProperty("owner").GetProperty("versionId").GetGuid() == version && e.GetProperty("target").GetProperty("versionId").GetGuid() == nextVersion);
            Assert.DoesNotContain(view.GetProperty("edges").EnumerateArray(), e => e.GetProperty("owner").GetProperty("versionId").GetGuid() == version && e.GetProperty("target").GetProperty("versionId").GetGuid() == oldVersion);
            await f.Host.Call("change_draft_link", LinkArguments(kind, owner, version, revision + 2, relationship, "retarget", target, nextVersion, nextVersion)); // current no-op retarget
            await f.Host.Call("change_draft_link", LinkArguments(kind, owner, version, revision + 2, relationship, "remove", target, nextVersion));
            await f.Host.Call("change_draft_link", LinkArguments(kind, owner, version, revision + 3, relationship, "remove", target, nextVersion)); // current no-op
            Assert.Equal(before + 3, (await f.Audit.FindAsync(new())).Count);
            Assert.Equal(historyBefore + 3, await HistoryCount());
            var events = (await f.Audit.FindAsync(new())).Where(e => e.Actor == new AuditActor.Identified("admin") &&
                e.Target == new AuditTarget.Identified(owner.ToString()) && (e.Action.Contains(".link.", StringComparison.Ordinal) || e.Action.Contains(".parent.", StringComparison.Ordinal))).ToArray();
            Assert.Equal(3, events.Length);
            Assert.All(events, e =>
            {
                Assert.Equal(new AuditActor.Identified("admin"), e.Actor); Assert.Equal(new AuditTarget.Identified(owner.ToString()), e.Target);
                Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.True(e.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(-5));
                Assert.Contains(version.ToString(), JsonSerializer.Serialize(e)); Assert.Contains(f.Data.Product.InternalId.ToString(), JsonSerializer.Serialize(e));
            });
            var changed = Assert.Single(events, e => e.Action.EndsWith("retarget", StringComparison.Ordinal) || e.Action == "requirement.parent.replace");
            Assert.Contains(oldVersion.ToString(), JsonSerializer.Serialize(changed)); Assert.Contains(nextVersion.ToString(), JsonSerializer.Serialize(changed));
        }
    }

    private sealed class LinkClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [PostgresFact]
    public async Task McpLinkTransactionsRecheckRevokedRotatedAndExpiredCredentialsAfterWaitingOnProduct()
    {
        foreach (var relationship in new[] { "user-need", "parent", "verifies", "addresses" })
        foreach (var operation in new[] { "add", "remove", "retarget" })
        foreach (var invalidation in new[] { "revoke", "rotate", "expire" })
        {
            var clock = new LinkClock();
            await using var f = await CaseRelationshipFixture.Create(linked: operation != "add", clock: clock);
            var d = f.Data;
            var kind = relationship == "verifies" ? "test-case" : relationship == "addresses" ? "feature" : "requirement";
            var requirements = d.Successor;
            if (relationship == "user-need" && operation != "add")
                requirements = (await f.Requirements.ChangeLink(requirements, Endpoint(d.Need), true, RequirementActor, RequirementTime)).Value;
            if (relationship == "parent" && operation == "add")
                requirements = (await f.Requirements.RemoveParent(requirements, RequirementActor, RequirementTime)).Value;
            var featureStore = f.Scope.ServiceProvider.GetRequiredService<IFeatureChangeTransaction>();
            var feature = (await featureStore.Create(d.Product.InternalId, FeatureContent.Create("Feature", ""), FeatureActor, FeatureTime)).Value;
            if (relationship == "addresses" && operation != "add")
                feature = (await featureStore.ChangeLink(feature, "add", new(d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId), null, FeatureActor, FeatureTime)).Value;
            var owner = kind == "requirement" ? requirements.Requirement.InternalId : kind == "test-case" ? f.Row.TestCase.InternalId : feature.Feature.InternalId;
            var version = kind == "requirement" ? requirements.Requirement.VersionId : kind == "test-case" ? f.Row.TestCase.VersionId : feature.Feature.VersionId;
            var revision = kind == "requirement" ? requirements.Revision : kind == "test-case" ? f.Row.Revision : feature.Revision;
            var target = relationship == "user-need" ? d.Need.Need.InternalId : relationship == "parent" ? d.Parent.Requirement.InternalId : d.Owner.Requirement.InternalId;
            var oldVersion = relationship == "user-need" ? d.Need.Need.VersionId : relationship == "parent" ? d.Parent.Requirement.VersionId : d.Owner.Requirement.VersionId;
            var nextVersion = relationship == "user-need" ? d.NewNeed.Need.VersionId : relationship == "parent" ? d.NewParent.Requirement.VersionId : d.Successor.Requirement.VersionId;
            async Task<object?> ParentValue()
            {
                var parent = (await f.Requirements.Parent(owner, version)).Value;
                return parent.HasValue ? parent.Value : null;
            }
            async Task<string> StoredState() => kind == "requirement" ? JsonSerializer.Serialize(new
                { row = (await f.Requirements.Find(owner, versionId: version)).Value.Value, links = (await f.Requirements.Links(owner, version)).Value, parent = await ParentValue(),
                    history = (await f.Requirements.History(owner)).Value }) : kind == "test-case" ? JsonSerializer.Serialize(new
                { row = (await f.Cases.Find(owner, version)).Value.Value, history = (await f.Cases.History(owner)).Value }) : JsonSerializer.Serialize(new
                { row = (await f.Scope.ServiceProvider.GetRequiredService<IFeatureStore>().Find(owner, version)).Value.Value,
                    history = (await f.Scope.ServiceProvider.GetRequiredService<IFeatureStore>().History(owner)).Value });
            var before = await StoredState();
            var auditCount = (await f.Audit.FindAsync(new())).Count;
            var sessions = f.Host.Factory.Services.GetRequiredService<Retravion.Web.Features.Identity.Logout.SessionTicketStore>();
            var sessionId = f.Actor.FindFirst(Retravion.Web.Features.Identity.Logout.SessionTicketStore.SessionClaim)!.Value;
            clock.Now += TimeSpan.FromMinutes(29); Assert.True(sessions.RecordActivity(sessionId));
            var credentials = f.Host.Factory.Services.GetRequiredService<Retravion.Web.Features.Mcp.McpCredentials>();
            await using var blocker = new NpgsqlConnection(f.Database.ConnectionString); await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var rowLock = new NpgsqlCommand("SELECT internal_id FROM retravion_product.product WHERE internal_id=$1 FOR UPDATE", blocker, transaction))
            { rowLock.Parameters.AddWithValue(d.Product.InternalId); await rowLock.ExecuteScalarAsync(); }
            var mutation = f.Host.Call("change_draft_link", LinkArguments(kind, owner, version, revision, relationship, operation,
                target, oldVersion, operation == "retarget" ? nextVersion : null), true);
            try
            {
                await using var observer = new NpgsqlConnection(f.Database.ConnectionString); await observer.OpenAsync();
                var waiting = false;
                for (var attempt = 0; attempt < 200 && !waiting; attempt++)
                {
                    await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%retravion_product.product%')", observer);
                    waiting = await check.ExecuteScalarAsync() is true;
                    if (!waiting) await Task.Delay(25);
                }
                Assert.True(waiting, $"{relationship}/{operation}/{invalidation} did not reach the Product lock.");
                Assert.False(mutation.IsCompleted);
                if (invalidation == "revoke") credentials.Revoke(f.Actor);
                else if (invalidation == "rotate") await f.Host.Authenticate();
                else
                {
                    // The accepted MCP request renewed its sliding credential. Keep only the browser alive
                    // and then advance beyond that renewed credential, while the transaction stays blocked.
                    clock.Now += TimeSpan.FromMinutes(29); Assert.True(sessions.RecordActivity(sessionId));
                    clock.Now += TimeSpan.FromMinutes(2);
                }
                Assert.True(sessions.IsActive(sessionId)); Assert.False(credentials.IsActive(f.Actor));
            }
            finally { await transaction.RollbackAsync(); }
            Assert.Equal("Access denied.", (await mutation.WaitAsync(TimeSpan.FromSeconds(20))).GetString());
            Assert.Equal(before, await StoredState());
            var events = await f.Audit.FindAsync(new()); Assert.Equal(auditCount + 1, events.Count);
            var denied = Assert.Single(events, e => e.Outcome == AuditActionOutcome.Failure);
            Assert.Equal(new AuditActor.Identified("admin"), denied.Actor); Assert.IsType<AuditTarget.Unknown>(denied.Target);
            Assert.Null(denied.RequirementChange); Assert.Null(denied.TestCaseChange); Assert.Null(denied.FeatureChange);
            Assert.DoesNotContain(owner.ToString(), JsonSerializer.Serialize(denied));
        }
    }

    [PostgresFact]
    public async Task McpExactLinkReadDenialProducesOneUnknownTargetEventWithoutSubmittedValues()
    {
        await using var f = await CaseRelationshipFixture.Create();
        var before = (await f.Audit.FindAsync(new())).Count;
        var result = await f.Operations.ChangeExact(new System.Security.Claims.ClaimsPrincipal(), "test-case",
            f.Row.TestCase.InternalId, f.Row.TestCase.VersionId, f.Row.Revision,
            Retravion.Web.Features.Relationships.RelationshipOperations.TestCaseRelationship, "add",
            f.Data.Owner.Requirement.InternalId, f.Data.Owner.Requirement.VersionId, null, default);
        Assert.True(result.IsFailure); Assert.Equal("Access denied.", result.Error);
        var events = await f.Audit.FindAsync(new()); Assert.Equal(before + 1, events.Count);
        var denied = Assert.Single(events, e => e.Action == "relationship.change");
        Assert.IsType<AuditActor.Unknown>(denied.Actor); Assert.IsType<AuditTarget.Unknown>(denied.Target);
        Assert.Equal(AuditActionOutcome.Failure, denied.Outcome); Assert.Null(denied.TestCaseChange);
        Assert.DoesNotContain(f.Row.TestCase.InternalId.ToString(), JsonSerializer.Serialize(denied));
    }

    [PostgresFact]
    public async Task McpLinkFailuresRollbackAndUncertainCommitsAreNeverRetried()
    {
        foreach (var operation in new[] { "add", "remove", "retarget" })
        foreach (var failure in new[] { "audit", "business", "history", "uncommitted", "committed" })
        {
            await using var f = await CaseRelationshipFixture.Create(linked: operation != "add");
            var before = (await f.Audit.FindAsync(new())).Count;
            var history = (await f.Cases.History(f.Row.TestCase.InternalId)).Value.Count;
            var attempts = 0;
            if (failure is "committed" or "uncommitted")
                f.Writes.Store = new Retravion.Infrastructure.TestCase.PostgresTestCaseStore(f.Database.ConnectionString, async (transaction, token) =>
                {
                    attempts++;
                    if (failure == "committed") await transaction.CommitAsync(token);
                    throw new IOException("private-provider-details");
                });
            else
            {
                var table = failure == "audit" ? "retravion_audit.event" : failure == "business" ? "retravion_test_case.test_case_version" : "retravion_test_case.history";
                await using var connection = new NpgsqlConnection(f.Database.ConnectionString); await connection.OpenAsync();
                await using var trigger = new NpgsqlCommand($"""
                    CREATE FUNCTION public.reject_mcp_link() RETURNS trigger LANGUAGE plpgsql AS
                    $$ BEGIN RAISE EXCEPTION 'private-provider-details' USING ERRCODE='42501'; END $$;
                    CREATE TRIGGER reject_mcp_link BEFORE INSERT OR UPDATE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION public.reject_mcp_link();
                    """, connection);
                await trigger.ExecuteNonQueryAsync();
            }
            var result = await f.Host.Call("change_draft_link", LinkArguments("test-case", f.Row.TestCase.InternalId,
                f.Row.TestCase.VersionId, f.Row.Revision, "verifies", operation, f.Data.Owner.Requirement.InternalId,
                f.Data.Owner.Requirement.VersionId, operation == "retarget" ? f.Data.Successor.Requirement.VersionId : null), true);
            Assert.DoesNotContain("private-provider", result.GetString());
            Assert.Equal(1, f.Writes.Calls);
            if (failure is "committed" or "uncommitted")
            {
                Assert.Equal(1, attempts);
                Assert.Contains("outcome could not be confirmed", result.GetString());
                Assert.Contains("Reload before retrying; do not retry automatically", result.GetString());
            }
            var committed = failure == "committed";
            Assert.Equal(before + (committed ? 1 : 0), (await f.Audit.FindAsync(new())).Count);
            Assert.Equal(history + (committed ? 1 : 0), (await f.Cases.History(f.Row.TestCase.InternalId)).Value.Count);
            var current = (await f.Host.Call("read_test_case", new { internalId = f.Row.TestCase.InternalId, versionId = f.Row.TestCase.VersionId })).GetProperty("testCase");
            Assert.Equal(f.Row.Revision + (committed ? 1 : 0), current.GetProperty("revision").GetInt64());
            var links = (await f.Cases.Find(f.Row.TestCase.InternalId)).Value.Value.TestCase.Links;
            if (operation == "remove" && committed || operation == "add" && !committed) Assert.Empty(links);
            else Assert.Equal(CaseEndpoint(operation == "retarget" && committed ? f.Data.Successor : f.Data.Owner), Assert.Single(links));
        }
    }

    [PostgresFact]
    public async Task McpLinksRejectWrongExactOwnerHistoricalOwnerCyclesAndRetiredProductWithoutEvents()
    {
        await using var f = await CaseRelationshipFixture.Create();
        var d = f.Data;
        var before = (await f.Audit.FindAsync(new())).Count;
        foreach (var (id, version, revision) in new[]
        {
            (d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId, d.Owner.Revision),
            (d.Successor.Requirement.InternalId, Guid.NewGuid(), d.Successor.Revision),
            (d.Successor.Requirement.InternalId, d.Successor.Requirement.VersionId, d.Successor.Revision - 1)
        }) await f.Host.Call("change_draft_link", LinkArguments("requirement", id, version, revision, "user-need", "add", d.Need.Need.InternalId, d.Need.Need.VersionId), true);
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
        var foreignProduct = (await new Retravion.Application.Product.Create.CreateProduct(f.Products).Execute("Other", "Other Product", Retravion.Domain.Product.ProductKind.System, "")).Value;
        var foreign = await ParentDraft(f.Requirements, foreignProduct.InternalId, "Foreign");
        before = (await f.Audit.FindAsync(new())).Count;
        await f.Host.Call("change_draft_link", LinkArguments("test-case", f.Row.TestCase.InternalId, f.Row.TestCase.VersionId, f.Row.Revision, "verifies", "add", foreign.Requirement.InternalId, foreign.Requirement.VersionId), true);
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
        var child = (await f.Requirements.RemoveParent(d.Child, RequirementActor, RequirementTime)).Value;
        before = (await f.Audit.FindAsync(new())).Count;
        await f.Host.Call("change_draft_link", LinkArguments("requirement", child.Requirement.InternalId, child.Requirement.VersionId, child.Revision, "parent", "add", child.Requirement.InternalId, child.Requirement.VersionId), true);
        // Never remove a different parent from the one the human confirmed.
        await f.Host.Call("change_draft_link", LinkArguments("requirement", d.Successor.Requirement.InternalId, d.Successor.Requirement.VersionId, d.Successor.Revision, "parent", "remove", d.Parent.Requirement.InternalId, d.NewParent.Requirement.VersionId), true);
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
        Assert.Equal(ParentEndpoint(d.Parent), (await f.Requirements.Parent(d.Successor.Requirement.InternalId, d.Successor.Requirement.VersionId)).Value.Value);
        Assert.True((await f.Products.Update(d.Product, d.Product.Retire().Value)).IsSuccess);
        await f.Host.Call("change_draft_link", LinkArguments("test-case", f.Row.TestCase.InternalId, f.Row.TestCase.VersionId, f.Row.Revision, "verifies", "add", d.Owner.Requirement.InternalId, d.Owner.Requirement.VersionId), true);
        Assert.Equal(before, (await f.Audit.FindAsync(new())).Count);
    }
}
