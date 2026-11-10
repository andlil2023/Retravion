using System.Security.Claims;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Application.Product.Create;
using Retravion.Application.Requirement;
using Retravion.Application.TestCase;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Domain.Revision;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.Revision;
using Retravion.Infrastructure.TestCase;
using Retravion.Web.Features.Product;
using Xunit;
using Requirement = Retravion.Domain.Requirement.Requirement;

namespace Retravion.IntegrationTests;

public sealed partial class PostgresProductTests
{
    private static async Task<ProductAreaSnapshot> AreaCreate(PostgresProductAreaStore store, Guid product, string code = "SW", string label = "Software")
    {
        var result = await store.Save(Guid.NewGuid(), null, ProductArea.Create(product, code, label).Value, RevActor, RevTime, RevAllowed);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error : ""); return result.Value;
    }
    private static async Task<Product> AreaProduct(string connection, string id = "P") =>
        (await new CreateProduct(new PostgresProductStore(connection)).Execute(id, "Product", ProductKind.System, "")).Value;

    [PostgresFact]
    public async Task AreasHaveProductScopedImmutableNonReusableCodesAndAtomicAttributableHistory()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var q = await AreaProduct(db.ConnectionString, "Q"); var store = new PostgresProductAreaStore(db.ConnectionString);
        var races = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => store.Save(Guid.NewGuid(), null,
            ProductArea.Create(p.InternalId, i % 2 == 0 ? "sw" : "SW", "Initial").Value, RevActor, RevTime, RevAllowed)));
        var first = Assert.Single(races, r => r.IsSuccess).Value;
        await AreaCreate(store, q.InternalId, "sw");
        var renameOp = Guid.NewGuid();
        var renamed = (await store.Save(renameOp, first, first.Area.Rename("Changed").Value, RevActor, RevTime, RevAllowed)).Value;
        Assert.Equal(first.Area.InternalId, renamed.Area.InternalId); Assert.Equal("SW", renamed.Area.Code);
        Assert.True((await store.Save(Guid.NewGuid(), first, first.Area.Retire(), RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Save(Guid.NewGuid(), renamed, ProductArea.Restore(renamed.Area.InternalId, p.InternalId, "OTHER", "Changed", true).Value, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Equal(renamed.Revision, (await store.Save(Guid.NewGuid(), renamed, renamed.Area, RevActor, RevTime, RevAllowed)).Value.Revision);
        var retired = (await store.Save(Guid.NewGuid(), renamed, renamed.Area.Retire(), RevActor, RevTime, RevAllowed)).Value;
        Assert.True((await store.Save(Guid.NewGuid(), null, ProductArea.Create(p.InternalId, "sw", "Reuse").Value, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.True((await store.Save(Guid.NewGuid(), retired, ProductArea.Restore(retired.Area.InternalId, p.InternalId, "SW", "Changed", true).Value, RevActor, RevTime, RevAllowed)).IsFailure);
        var history = (await store.Retrieve(p.InternalId, renameOp)).Value;
        Assert.Equal(new[] { "product.area.create", "product.area.rename", "product.area.retire" }, history.History.Select(h => h.Action));
        Assert.Equal("Initial", history.History[0].Change.After.Label);
        Assert.Equal("Initial", history.Operation!.Change.Before!.Label); Assert.Equal("Changed", history.Operation.Change.After.Label);
        Assert.All(history.History, h => { Assert.Equal(RevActor, h.Actor); Assert.Equal(RevTime.UtcTicks, h.OccurredAt.UtcTicks); Assert.Equal(p.InternalId, h.Change.ProductInternalId); });
        Assert.Null((await store.Retrieve(q.InternalId, renameOp)).Value.Operation);
        var events = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Where(e => e.ProductAreaChange?.InternalId == first.Area.InternalId).ToArray();
        Assert.Equal(3, events.Length); Assert.All(events, e => { Assert.Equal(RevActor, e.Actor); Assert.Equal(RevTime, e.OccurredAt); Assert.Equal(AuditActionOutcome.Success, e.Outcome); Assert.Equal(new AuditTarget.Identified(first.Area.InternalId.ToString()), e.Target); });
        Assert.True((await new PostgresProductStore(db.ConnectionString).Update(p, p.Retire().Value)).IsSuccess);
        Assert.True((await store.Save(Guid.NewGuid(), retired, retired.Area.Rename("Blocked").Value, RevActor, RevTime, RevAllowed)).IsFailure);
        Assert.Single((await store.Retrieve(p.InternalId)).Value.Areas);
    }

    [PostgresFact]
    public async Task ClassificationsAreExactIndependentDraftOnlyInheritedAndNeverTransferredAcrossProducts()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString); var q = await AreaProduct(db.ConnectionString, "Q");
        var areas = new PostgresProductAreaStore(db.ConnectionString);
        var area = await AreaCreate(areas, p.InternalId); var other = await AreaCreate(areas, q.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var original = (await requirements.Create(Requirement.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime)).Value;
        Assert.Equal(area.Area.InternalId, (await requirements.Find(original.Requirement.InternalId)).Value.Value.Requirement.AreaId);
        Assert.True((await requirements.Create(Requirement.Create(p.InternalId, "Bad", "Title", "Statement", RequirementType.System, areaId: other.Area.InternalId).Value, RevActor, RevTime)).IsFailure);
        original = await ParentApprove(requirements, original);
        var tc = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: area.Area.InternalId)).Value;
        tc = (await cases.ChangeLink(tc, "add", CaseEndpoint(original), null, RevActor, RevTime)).Value;
        tc = await CaseApprove(cases, tc);
        area = (await areas.Save(Guid.NewGuid(), area, area.Area.Retire(), RevActor, RevTime, RevAllowed)).Value;
        Assert.True((await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: area.Area.InternalId)).IsFailure);
        var rOp = Guid.NewGuid(); var tOp = Guid.NewGuid();
        var successor = (await requirements.CreateSuccessor(original, "Next", RevActor, RevTime, operationId: rOp)).Value;
        var tNext = (await cases.CreateSuccessor(tc, "Next", RevActor, RevTime, operationId: tOp)).Value;
        Assert.Equal(area.Area.InternalId, successor.Requirement.AreaId); Assert.Equal(area.Area.InternalId, tNext.TestCase.AreaId);
        Assert.Equal(area.Area.InternalId, (await requirements.Outcome(p.InternalId, rOp)).Value.Value.After.Area!.Area!.InternalId);
        Assert.Equal(area.Area.InternalId, (await cases.Outcome(p.InternalId, tOp)).Value.Value.After.Area!.Area!.InternalId);
        successor = (await requirements.Update(successor, successor.Requirement.Edit("R", "Other", "Statement", RequirementType.System, null).Value, RevActor, RevTime)).Value;
        tNext = (await cases.Update(tNext, CaseContent("Other"), RevActor, RevTime)).Value;
        Assert.Equal(area.Area.InternalId, successor.Requirement.AreaId); Assert.Equal(area.Area.InternalId, tNext.TestCase.AreaId);
        var stale = successor;
        successor = (await requirements.Update(successor, successor.Requirement.Classify(null).Value, RevActor, RevTime)).Value;
        tNext = (await cases.Update(tNext, tNext.TestCase.Content, RevActor, RevTime, changeArea: true, areaId: null)).Value;
        var count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        Assert.True((await requirements.Update(successor, successor.Requirement.Classify(area.Area.InternalId).Value, RevActor, RevTime)).IsFailure);
        Assert.True((await requirements.Update(stale, stale.Requirement, RevActor, RevTime)).IsFailure);
        Assert.True((await cases.Update(tNext, tNext.TestCase.Content, RevActor, RevTime, changeArea: true, areaId: area.Area.InternalId)).IsFailure);
        Assert.True((await cases.Update(tNext, tNext.TestCase.Content, RevActor, RevTime, changeArea: true, areaId: other.Area.InternalId)).IsFailure);
        Assert.Equal(count, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
        Assert.Null(Assert.Single((await requirements.List(p.InternalId)).Value).Requirement.AreaId);
        Assert.Null(Assert.Single((await cases.List(p.InternalId)).Value).TestCase.AreaId);
        Assert.Equal(area.Area.InternalId, (await requirements.Find(original.Requirement.InternalId, versionId: original.Requirement.VersionId)).Value.Value.Requirement.AreaId);
        Assert.Equal(area.Area.InternalId, (await cases.Find(tc.TestCase.InternalId, tc.TestCase.VersionId)).Value.Value.TestCase.AreaId);
        var copy = (await requirements.Derive(original, Requirement.Create(q.InternalId, "Copy", "Title", "Statement", RequirementType.System).Value, RevActor, RevTime)).Value;
        Assert.Null(copy.Requirement.AreaId);
        Assert.True((await requirements.Derive(original, Requirement.Create(q.InternalId, "Invalid copy", "Title", "Statement", RequirementType.System, areaId: other.Area.InternalId).Value, RevActor, RevTime)).IsFailure);
        var creation = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Single(e => e.RequirementChange?.InternalId == copy.Requirement.InternalId);
        Assert.Equal(AreaEvidence.Unclassified, creation.RequirementChange!.After.Area);
        var history = (await requirements.History(successor.Requirement.InternalId)).Value;
        Assert.Equal(area.Area.Code, history.First(h => h.Decision == "requirement.create").AfterArea!.Area!.Code);
        Assert.Equal(area.Area.InternalId, history.Last().BeforeArea!.Area!.InternalId); Assert.Equal(AreaEvidence.Unclassified, history.Last().AfterArea);
        var active = await AreaCreate(areas, p.InternalId, "ACTIVE");
        Assert.True((await new PostgresProductStore(db.ConnectionString).Update(p, p.Retire().Value)).IsSuccess);
        count = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        Assert.True((await requirements.Update(successor, successor.Requirement.Classify(active.Area.InternalId).Value, RevActor, RevTime)).IsFailure);
        Assert.True((await cases.Update(tNext, tNext.TestCase.Content, RevActor, RevTime, changeArea: true, areaId: active.Area.InternalId)).IsFailure);
        Assert.True((await requirements.Create(Requirement.Create(p.InternalId, "Retired", "Title", "Statement", RequirementType.System, areaId: active.Area.InternalId).Value, RevActor, RevTime)).IsFailure);
        Assert.True((await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: active.Area.InternalId)).IsFailure);
        Assert.Equal(count, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
        Assert.Single((await requirements.List(p.InternalId)).Value); Assert.Single((await cases.List(p.InternalId)).Value);
    }

    [PostgresFact]
    public async Task ReleaseRetainsCurrentAreaLabelsForCompleteMultiVersionClosureAndLegacyEvidenceStaysUnrecorded()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var areas = new PostgresProductAreaStore(db.ConnectionString); var a = await AreaCreate(areas, p.InternalId); var b = await AreaCreate(areas, p.InternalId, "HW", "Hardware");
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var r1 = await ParentApprove(requirements, (await requirements.Create(Requirement.Create(p.InternalId, "Parent", "Parent", "Statement", RequirementType.System, areaId: a.Area.InternalId).Value, RevActor, RevTime)).Value);
        a = (await areas.Save(Guid.NewGuid(), a, a.Area.Retire(), RevActor, RevTime, RevAllowed)).Value;
        var r2 = await ParentApprove(requirements, (await requirements.CreateSuccessor(r1, "Next", RevActor, RevTime)).Value);
        var child = await ParentDraft(requirements, p.InternalId, "Child");
        child = await ParentApprove(requirements, (await requirements.AddParent(child, ParentEndpoint(r1), RevActor, RevTime)).Value);
        var tc = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: b.Area.InternalId)).Value;
        tc = await CaseApprove(cases, (await cases.ChangeLink(tc, "add", CaseEndpoint(child), null, RevActor, RevTime)).Value);
        a = (await areas.Save(Guid.NewGuid(), a, a.Area.Rename("Release-time software").Value, RevActor, RevTime, RevAllowed)).Value;
        var revisions = new PostgresRevisionStore(db.ConnectionString); var draft = await RevCreate(revisions, p.InternalId);
        foreach (var endpoint in new[] { new RevisionSelection(RevisionEntityKind.Requirement, r2.Requirement.InternalId, r2.Requirement.VersionId, 2), new RevisionSelection(RevisionEntityKind.TestCase, tc.TestCase.InternalId, tc.TestCase.VersionId, 1) })
            draft = (await revisions.Select(Guid.NewGuid(), draft, "add", endpoint, null, RevActor, RevTime, RevAllowed)).Value;
        var preview = await ReleasePreview(revisions, draft); Assert.Equal(4, preview.Members.Count);
        Assert.Equal(2, preview.Members.Count(m => m.Area?.Area?.Label == "Release-time software"));
        Assert.Equal(AreaEvidence.Unclassified, preview.Members.Single(m => m.Endpoint.InternalId == child.Requirement.InternalId).Area);
        var before = (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count;
        var released = (await ReleaseNow(revisions, preview)).Value;
        Assert.Equal(before + 1, (await new PostgresAuditQuery(db.ConnectionString).FindAsync(new())).Count);
        a = (await areas.Save(Guid.NewGuid(), a, a.Area.Rename("Later label").Value, RevActor, RevTime, RevAllowed)).Value;
        b = (await areas.Save(Guid.NewGuid(), b, b.Area.Retire(), RevActor, RevTime, RevAllowed)).Value;
        Assert.Equal(JsonSerializer.Serialize(preview.Members), JsonSerializer.Serialize((await revisions.Baseline(p.InternalId, released.Value.InternalId)).Value.Members));
        Assert.True((await requirements.Update(r2, r2.Requirement.Reopen("Blocked").Value, RevActor, RevTime, "Reopened", "Blocked")).IsFailure);
        Assert.True((await cases.Reopen(tc, "Blocked", RevActor, RevTime)).IsFailure);
        var again = await RevCreate(revisions, p.InternalId, "Later");
        foreach (var root in draft.Value.Selections) again = (await revisions.Select(Guid.NewGuid(), again, "add", root, null, RevActor, RevTime, RevAllowed)).Value;
        var next = await ReleasePreview(revisions, again); Assert.Equal(2, next.Members.Count(m => m.Area?.Area?.Label == "Later label"));
        Assert.True((await ReleaseNow(revisions, next)).IsSuccess);
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        // Emulate pre-feature JSON, which did not contain Area. Never backfill it from current records.
        await using (var legacy = new NpgsqlCommand("UPDATE retravion_revision.revision SET release_evidence=jsonb_set(release_evidence,'{Members}',(SELECT jsonb_agg(value-'Area') FROM jsonb_array_elements(release_evidence->'Members'))) WHERE internal_id=$1", c))
        { legacy.Parameters.AddWithValue(released.Value.InternalId); await legacy.ExecuteNonQueryAsync(); }
        Assert.All((await revisions.Baseline(p.InternalId, released.Value.InternalId)).Value.Members, m => Assert.Null(m.Area));
    }

    [PostgresFact]
    public async Task AreaAndClassificationWritesRollbackBusinessHistoryAndAuditOnEveryStoreFailure()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var areas = new PostgresProductAreaStore(db.ConnectionString); var area = await AreaCreate(areas, p.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var cases = new PostgresTestCaseStore(db.ConnectionString);
        var r = await ParentDraft(requirements, p.InternalId, "R"); var tc = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime)).Value;
        var approvedSource = await ParentApprove(requirements, (await requirements.Create(Requirement.Create(p.InternalId, "Approved", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime)).Value);
        var approvedCase = (await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: area.Area.InternalId)).Value;
        approvedCase = await CaseApprove(cases, (await cases.ChangeLink(approvedCase, "add", CaseEndpoint(approvedSource), null, RevActor, RevTime)).Value);
        var audit = new PostgresAuditQuery(db.ConnectionString);
        await using var c = new NpgsqlConnection(db.ConnectionString); await c.OpenAsync();
        async Task Sql(string text) { await using var cmd = new NpgsqlCommand(text, c); await cmd.ExecuteNonQueryAsync(); }
        await Sql("CREATE FUNCTION public.fail_area_write() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private' USING ERRCODE='42501'; END $$");
        foreach (var table in new[] { "retravion_audit.event", "retravion_product.area_history", "retravion_product.area" })
        {
            var count = (await audit.FindAsync(new())).Count;
            await Sql($"CREATE TRIGGER fail_area BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.fail_area_write()");
            foreach (var replacement in new[] { ProductArea.Create(p.InternalId, "New", "New").Value, area.Area.Rename("Changed").Value, area.Area.Retire() })
            {
                var outcome = await areas.Save(Guid.NewGuid(), replacement.InternalId == area.Area.InternalId ? area : null, replacement, RevActor, RevTime, RevAllowed);
                Assert.True(outcome.IsFailure); Assert.DoesNotContain("private", outcome.Error);
            }
            await Sql($"DROP TRIGGER fail_area ON {table}");
            Assert.Equal(count, (await audit.FindAsync(new())).Count);
            Assert.Equal(area, Assert.Single((await areas.Retrieve(p.InternalId)).Value.Areas));
            Assert.Single((await areas.Retrieve(p.InternalId)).Value.History);
        }
        foreach (var table in new[] { "retravion_audit.event", "retravion_requirement.operation_history", "retravion_requirement.review", "retravion_requirement.requirement_version" })
        {
            var count = (await audit.FindAsync(new())).Count; var op = Guid.NewGuid();
            await Sql($"CREATE TRIGGER fail_area BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.fail_area_write()");
            Assert.True((await requirements.Update(r, r.Requirement.Classify(area.Area.InternalId).Value, RevActor, RevTime, operationId: op)).IsFailure);
            Assert.True((await requirements.Create(Requirement.Create(p.InternalId, "New", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime)).IsFailure);
            Assert.True((await requirements.CreateSuccessor(approvedSource, "Next", RevActor, RevTime)).IsFailure);
            await Sql($"DROP TRIGGER fail_area ON {table}");
            Assert.Equal(count, (await audit.FindAsync(new())).Count); Assert.True((await requirements.Outcome(p.InternalId, op)).Value.HasNoValue);
            Assert.Equal(r, (await requirements.Find(r.Requirement.InternalId)).Value.Value); Assert.Equal(2, (await requirements.List(p.InternalId)).Value.Count);
            Assert.Single((await requirements.Versions(approvedSource.Requirement.InternalId)).Value);
        }
        foreach (var table in new[] { "retravion_audit.event", "retravion_test_case.history", "retravion_test_case.test_case_version" })
        {
            var count = (await audit.FindAsync(new())).Count; var op = Guid.NewGuid();
            await Sql($"CREATE TRIGGER fail_area BEFORE INSERT OR UPDATE ON {table} FOR EACH ROW EXECUTE FUNCTION public.fail_area_write()");
            Assert.True((await cases.Update(tc, tc.TestCase.Content, RevActor, RevTime, changeArea: true, areaId: area.Area.InternalId, operationId: op)).IsFailure);
            Assert.True((await cases.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: area.Area.InternalId)).IsFailure);
            Assert.True((await cases.CreateSuccessor(approvedCase, "Next", RevActor, RevTime)).IsFailure);
            await Sql($"DROP TRIGGER fail_area ON {table}");
            Assert.Equal(count, (await audit.FindAsync(new())).Count); Assert.True((await cases.Outcome(p.InternalId, op)).Value.HasNoValue);
            Assert.True(tc.TestCase.SameState((await cases.Find(tc.TestCase.InternalId)).Value.Value.TestCase)); Assert.Equal(2, (await cases.List(p.InternalId)).Value.Count);
            Assert.Single((await cases.Versions(approvedCase.TestCase.InternalId)).Value);
        }
    }

    [PostgresFact]
    public async Task EveryAreaWriteAndClassifiedVersionWriteDistinguishesBothUncertainCommitOutcomesWithoutRetry()
    {
        foreach (var committed in new[] { false, true })
        foreach (var cancelled in new[] { false, true })
        foreach (var path in new[] { "area-create", "area-rename", "area-retire", "requirement-create", "requirement-edit", "requirement-successor", "case-create", "case-edit", "case-successor", "release" })
        {
            await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString); var q = await AreaProduct(db.ConnectionString, "Q");
            var area = await AreaCreate(new PostgresProductAreaStore(db.ConnectionString), p.InternalId);
            var calls = 0;
            using var cancellation = new CancellationTokenSource();
            async Task LoseConfirmation(NpgsqlTransaction t, CancellationToken token)
            {
                calls++; if (committed) await t.CommitAsync(token);
                if (cancelled) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
                throw new IOException("private provider details");
            }
            var op = Guid.NewGuid();
            if (path == "release")
            {
                var requirements = new PostgresRequirementStore(db.ConnectionString);
                var r = await ParentApprove(requirements, (await requirements.Create(Requirement.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime)).Value);
                var reads = new PostgresRevisionStore(db.ConnectionString); var draft = await RevCreate(reads, p.InternalId);
                draft = (await reads.Select(Guid.NewGuid(), draft, "add", new(RevisionEntityKind.Requirement, r.Requirement.InternalId, r.Requirement.VersionId, 1), null, RevActor, RevTime, RevAllowed)).Value;
                var preview = await ReleasePreview(reads, draft);
                var result = await new PostgresRevisionStore(db.ConnectionString, LoseConfirmation).Release(op, preview.Revision, preview.Token, RevActor, RevTime, RevAllowed, cancellation.Token);
                Assert.True(result.IsFailure); Assert.Contains("outcome could not be confirmed", result.Error);
                var outcome = (await reads.Retrieve(p.InternalId, draft.Value.InternalId, op)).Value;
                Assert.Equal(committed, outcome.Operation is not null);
                Assert.Equal(committed ? "Released" : "Draft", outcome.Record!.Value.State);
                if (committed) Assert.Equal(area.Area.InternalId, outcome.Operation!.Change.After!.Release!.Members.Single().Area!.Area!.InternalId);
                Assert.Null((await reads.Retrieve(q.InternalId, draft.Value.InternalId, op)).Value.Operation);
            }
            else if (path.StartsWith("area-"))
            {
                var store = new PostgresProductAreaStore(db.ConnectionString, LoseConfirmation);
                var replacement = path == "area-create" ? ProductArea.Create(p.InternalId, "New", "New").Value : path == "area-rename" ? area.Area.Rename("Changed").Value : area.Area.Retire();
                var result = await store.Save(op, path == "area-create" ? null : area, replacement, RevActor, RevTime, RevAllowed, cancellation.Token);
                Assert.True(result.IsFailure); Assert.Contains("outcome could not be confirmed", result.Error); Assert.DoesNotContain("private", result.Error);
                var reads = new PostgresProductAreaStore(db.ConnectionString);
                Assert.Equal(committed, (await reads.Retrieve(p.InternalId, op)).Value.Operation is not null);
                Assert.Null((await reads.Retrieve(q.InternalId, op)).Value.Operation);
                Assert.Equal(committed ? 2 : 1, (await reads.Retrieve(p.InternalId)).Value.History.Count);
            }
            else
            {
                var reads = new PostgresRequirementStore(db.ConnectionString);
                var r = (await reads.Create(Requirement.Create(p.InternalId, "R", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime)).Value;
                if (path.EndsWith("successor") || path.StartsWith("case-")) r = await ParentApprove(reads, r);
                if (path.StartsWith("requirement-"))
                {
                    var writes = new PostgresRequirementStore(db.ConnectionString, LoseConfirmation);
                    var result = path == "requirement-create"
                        ? await writes.Create(Requirement.Create(p.InternalId, "New", "Title", "Statement", RequirementType.System, areaId: area.Area.InternalId).Value, RevActor, RevTime, cancellationToken: cancellation.Token, operationId: op)
                        : path == "requirement-edit" ? await writes.Update(r, r.Requirement.Classify(null).Value, RevActor, RevTime, cancellationToken: cancellation.Token, operationId: op)
                        : await writes.CreateSuccessor(r, "Next", RevActor, RevTime, cancellationToken: cancellation.Token, operationId: op);
                    Assert.True(result.IsFailure); Assert.Contains("outcome could not be confirmed", result.Error); Assert.DoesNotContain("private", result.Error);
                    Assert.Equal(committed, (await reads.Outcome(p.InternalId, op)).Value.HasValue);
                    Assert.True((await reads.Outcome(q.InternalId, op)).Value.HasNoValue);
                }
                else
                {
                    var tcReads = new PostgresTestCaseStore(db.ConnectionString);
                    var tc = (await tcReads.Create(p.InternalId, CaseContent(), RevActor, RevTime, areaId: area.Area.InternalId)).Value;
                    if (path == "case-successor") tc = await CaseApprove(tcReads, (await tcReads.ChangeLink(tc, "add", CaseEndpoint(r), null, RevActor, RevTime)).Value);
                    var writes = new PostgresTestCaseStore(db.ConnectionString, LoseConfirmation);
                    var result = path == "case-create" ? await writes.Create(p.InternalId, CaseContent(), RevActor, RevTime, cancellationToken: cancellation.Token, areaId: area.Area.InternalId, operationId: op)
                        : path == "case-edit" ? await writes.Update(tc, tc.TestCase.Content, RevActor, RevTime, cancellationToken: cancellation.Token, changeArea: true, areaId: null, operationId: op)
                        : await writes.CreateSuccessor(tc, "Next", RevActor, RevTime, cancellationToken: cancellation.Token, operationId: op);
                    Assert.True(result.IsFailure); Assert.Contains("outcome could not be confirmed", result.Error); Assert.DoesNotContain("private", result.Error);
                    Assert.Equal(committed, (await tcReads.Outcome(p.InternalId, op)).Value.HasValue);
                    Assert.True((await tcReads.Outcome(q.InternalId, op)).Value.HasNoValue);
                }
            }
            Assert.Equal(1, calls);
        }
    }

    [PostgresFact]
    public async Task AreaSelectionRetirementAndReleaseRenameRacesPreserveConsistentEvidence()
    {
        await using var db = await Database.Create(); var p = await AreaProduct(db.ConnectionString);
        var areas = new PostgresProductAreaStore(db.ConnectionString); var a = await AreaCreate(areas, p.InternalId);
        var requirements = new PostgresRequirementStore(db.ConnectionString); var r = await ParentDraft(requirements, p.InternalId, "R");
        var selection = requirements.Update(r, r.Requirement.Classify(a.Area.InternalId).Value, RevActor, RevTime);
        var retirement = areas.Save(Guid.NewGuid(), a, a.Area.Retire(), RevActor, RevTime, RevAllowed);
        await Task.WhenAll(selection, retirement); Assert.True(retirement.Result.IsSuccess);
        var current = (await requirements.Find(r.Requirement.InternalId)).Value.Value;
        Assert.Equal(selection.Result.IsSuccess ? a.Area.InternalId : (Guid?)null, current.Requirement.AreaId);
        if (current.Requirement.AreaId is null)
        {
            var b = await AreaCreate(areas, p.InternalId, "B", "Before"); a = b;
            current = (await requirements.Update(current, current.Requirement.Classify(b.Area.InternalId).Value, RevActor, RevTime)).Value;
        }
        else a = retirement.Result.Value;
        current = await ParentApprove(requirements, current);
        var revisions = new PostgresRevisionStore(db.ConnectionString); var draft = await RevCreate(revisions, p.InternalId);
        draft = (await revisions.Select(Guid.NewGuid(), draft, "add", new(RevisionEntityKind.Requirement, current.Requirement.InternalId, current.Requirement.VersionId, 1), null, RevActor, RevTime, RevAllowed)).Value;
        var preview = await ReleasePreview(revisions, draft);
        var release = ReleaseNow(revisions, preview);
        var rename = areas.Save(Guid.NewGuid(), a, a.Area.Rename("After").Value, RevActor, RevTime, RevAllowed);
        await Task.WhenAll(release, rename); Assert.True(rename.Result.IsSuccess);
        if (release.Result.IsSuccess)
            Assert.Equal(preview.Members[0].Area, (await revisions.Baseline(p.InternalId, draft.Value.InternalId)).Value.Members[0].Area);
        else
        {
            Assert.Contains("baseline changed", release.Result.Error);
            Assert.True((await ReleaseNow(revisions, await ReleasePreview(revisions, draft))).IsSuccess);
            Assert.Equal("After", (await revisions.Baseline(p.InternalId, draft.Value.InternalId)).Value.Members[0].Area!.Area!.Label);
        }
    }
}
