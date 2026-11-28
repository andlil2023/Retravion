using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Identity.Application;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed partial class SpecificationReadTests
{
    private const string MarkImplemented = "specification-implement";
    private const string MergeAttestation = "specification-merge-attestation";

    [PostgresFact]
    public async Task SpecificationFinalizeControlsRequireEvidenceAndExplicitAttestationThenDisplayCommittedDetail()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var pid = product.InternalId;
        var clock = new CreateClock();
        await using var factory = CreateFactory(db, clock);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var assignee = await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "Active.User");
        var id = (await ops.Create(actor, pid, "DEV-1", "Finalization", "Ready")).Value.InternalId;
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task<SpecificationDetail> Read() => (await ops.FindDetail(actor, pid, id)).Value.Value;
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, id));
            Assert.False(renderer.HasButton(MarkImplemented));
            await renderer.Select("specification-assignee", assignee.Username);
            await renderer.Click("specification-assign");
            await renderer.Click("specification-open-ai-review");
            js.Markdown = "**Retained review**";
            await renderer.Check("id", "specification-ai-review-approved");
            await renderer.Submit("specification-ai-review-form");
            await renderer.Click("specification-submit-review");
            Assert.False(renderer.HasButton(MarkImplemented));
            await renderer.Click("specification-approve");

            Assert.Contains("Save a final evidence report and an HTTPS PR URL", renderer.Content());
            Assert.True(renderer.ButtonDisabled(MarkImplemented));
            await renderer.Click(MarkImplemented);
            await renderer.Click(OpenEvidence);
            js.Markdown = "**Final evidence**";
            js.UnboundedText = "";
            await renderer.Submit(EvidenceForm);
            Assert.Contains("Add an HTTPS PR URL", renderer.Content());
            Assert.True(renderer.ButtonDisabled(MarkImplemented));
            await renderer.Click(MarkImplemented);
            Assert.DoesNotContain("Implemented", renderer.Content());

            await renderer.Click(OpenEvidence);
            js.UnboundedText = EvidenceUrl;
            await renderer.Submit(EvidenceForm);
            Assert.Contains("Retravion does not verify the merge.", renderer.Content());
            Assert.Contains("I attest that the implementation PR was merged.", renderer.Content());
            Assert.True(renderer.ButtonDisabled(MarkImplemented));
            await renderer.Click(MarkImplemented);
            await renderer.Check("id", MergeAttestation);
            Assert.False(renderer.ButtonDisabled(MarkImplemented));
            await renderer.Check("id", MergeAttestation, isChecked: false);
            await renderer.Click(MarkImplemented);
            Assert.True(renderer.ButtonDisabled(MarkImplemented));
            Assert.DoesNotContain("Explicitly attest", renderer.Content());
            Assert.DoesNotContain("Implemented", renderer.Content());
            Assert.Empty(await SpecificationEvents(db, ImplementAction));
            var before = await Read();
            Assert.IsType<ApprovedSpecification>(before.Specification);

            clock.Now = CreateTime.AddTicks(10);
            await renderer.Check("id", MergeAttestation);
            await renderer.Click(MarkImplemented);

            Assert.Contains("Implemented", renderer.Content());
            Assert.Contains("<strong>Final evidence</strong>", renderer.Content());
            Assert.Contains(EvidenceUrl, renderer.Links());
            Assert.Contains("<strong>Retained review</strong>", renderer.Content());
            Assert.Contains("Assignee: Unassigned", renderer.Content());
            Assert.Contains("Assignment history", renderer.Content());
            Assert.Contains($"User: Unassigned → {assignee.Username}", renderer.Content());
            Assert.Contains($"User: {assignee.Username} → Unassigned", renderer.Content());
            Assert.False(renderer.HasButton(MarkImplemented));
            Assert.False(renderer.HasButton(OpenEvidence));
            Assert.Empty(renderer.Elements("input"));
            AssertNoMutationControls(renderer);
            var after = await Read();
            var implemented = Assert.IsType<ImplementedSpecification>(after.Specification);
            Assert.Equal(("User", clock.Now), (implemented.MergeAttestedBy, implemented.MergeAttestedAt));
            Assert.Equal(before.Evidence, after.Evidence);
            Assert.Equal(before.CurrentAiReview, after.CurrentAiReview);
            Assert.Null(after.Assignee);
            var audit = Assert.Single(await SpecificationEvents(db, ImplementAction));
            Assert.Equal("success", audit.Outcome);
            Assert.Equal(id.ToString(), audit.Target);
            Assert.True(audit.Change!.MergeAttested);
        });
    }

    [PostgresFact]
    public async Task SpecificationFinalizeControlsSurfaceEvidenceThatBecameOutdatedAndRequireNewAttestation()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var pid = product.InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var id = (await ops.Create(actor, pid, "DEV-1", "Finalization", "Ready")).Value.InternalId;
        Assert.True((await ops.SubmitForReview(actor, pid, id)).IsSuccess);
        Assert.True((await ops.Approve(actor, pid, id)).IsSuccess);
        Assert.True((await ops.SaveEvidence(actor, pid, id, null, "**Retained evidence**", EvidenceUrl)).IsSuccess);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, id));
            await renderer.Check("id", MergeAttestation);
            Assert.False(renderer.ButtonDisabled(MarkImplemented));

            // Another session reopens and reapproves while the page still displays current evidence.
            Assert.True((await ops.Reopen(actor, pid, id, "Revise", true)).IsSuccess);
            Assert.True((await ops.SubmitForReview(actor, pid, id)).IsSuccess);
            Assert.True((await ops.Approve(actor, pid, id)).IsSuccess);
            await renderer.Click(MarkImplemented);

            Assert.Contains("Current evidence with a report and an HTTPS PR URL is required", renderer.Content());
            Assert.Contains("Confirm or replace the outdated evidence", renderer.Content());
            Assert.Contains("<strong>Retained evidence</strong>", renderer.Content());
            Assert.Contains("Outdated:", renderer.Content());
            Assert.Contains("Approved", renderer.Content());
            Assert.DoesNotContain("Implemented", renderer.Content());
            Assert.True(renderer.ButtonDisabled(MarkImplemented));
            Assert.Empty(await SpecificationEvents(db, ImplementAction));

            await renderer.Check("id", ReviewEvidence);
            await renderer.Click(ConfirmEvidence);
            Assert.DoesNotContain("Outdated:", renderer.Content());
            Assert.True(renderer.ButtonDisabled(MarkImplemented));
            await renderer.Click(MarkImplemented);
            Assert.DoesNotContain("Implemented", renderer.Content());
            Assert.Empty(await SpecificationEvents(db, ImplementAction));
        });
    }

    [PostgresFact]
    public async Task SpecificationFinalizeControlsRejectAttestationForAPrReplacedInAnotherSession()
    {
        const string replacementUrl = "https://forge.example/project/pull-requests/43";
        const string report = "**Final evidence**";
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var pid = product.InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var assignee = await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "Active.User");
        var id = (await ops.Create(actor, pid, "DEV-1", "Finalization", "Ready")).Value.InternalId;
        Assert.True((await ops.ChangeAssignment(actor, pid, id, assignee.Username)).IsSuccess);
        Assert.True((await ops.SubmitForReview(actor, pid, id)).IsSuccess);
        Assert.True((await ops.Approve(actor, pid, id)).IsSuccess);
        Assert.True((await ops.SaveEvidence(actor, pid, id, null, report, EvidenceUrl)).IsSuccess);
        var before = (await ops.FindDetail(actor, pid, id)).Value.Value;
        var path = SpecificationPresentation.DetailsPath(product.Id, id);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        using var editorScope = factory.Services.CreateScope();
        var editor = await Principal(editorScope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "Administrator", now: CreateTime);
        await using var editorServices = PageServices(editorScope.ServiceProvider, editor, new PagesNavigation());
        await using var editorRenderer = new PagesRenderer(editorServices);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SpecificationDetails>(path);
            Assert.Contains(EvidenceUrl, renderer.Links());
            await renderer.Check("id", MergeAttestation);
            Assert.False(renderer.ButtonDisabled(MarkImplemented));
        });
        await editorRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await editorRenderer.Mount<SpecificationDetails>(path);
            await editorRenderer.Click(OpenEvidence);
            var js = (PagesJS)editorServices.GetRequiredService<IJSRuntime>();
            js.Markdown = report;
            js.UnboundedText = replacementUrl;
            await editorRenderer.Submit(EvidenceForm);
            Assert.Contains(replacementUrl, editorRenderer.Links());
            Assert.DoesNotContain("Outdated:", editorRenderer.Content());
        });
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Assert.Contains(EvidenceUrl, renderer.Links());
            Assert.False(renderer.ButtonDisabled(MarkImplemented));
            await renderer.Click(MarkImplemented);

            Assert.Contains("The implementation PR URL has changed. Review the current PR URL and attest again.", renderer.Content());
            Assert.Contains(replacementUrl, renderer.Links());
            Assert.DoesNotContain(EvidenceUrl, renderer.Links());
            Assert.Contains("Approved", renderer.Content());
            Assert.DoesNotContain("Implemented", renderer.Content());
            Assert.Contains($"Assignee: {assignee.Username}", renderer.Content());
            Assert.True(renderer.ButtonDisabled(MarkImplemented));
            await renderer.Click(MarkImplemented);
            var after = (await ops.FindDetail(actor, pid, id)).Value.Value;
            Assert.IsType<ApprovedSpecification>(after.Specification);
            Assert.Equal(assignee.Username, after.Assignee);
            Assert.Equal(before.AssignmentHistory, after.AssignmentHistory);
            Assert.Equal(replacementUrl, after.Evidence?.PullRequestUrl);
            Assert.Empty(await SpecificationEvents(db, ImplementAction));

            await renderer.Check("id", MergeAttestation);
            await renderer.Click(MarkImplemented);
            Assert.Contains("Implemented", renderer.Content());
            Assert.Contains(replacementUrl, renderer.Links());
            Assert.Contains($"User: {assignee.Username} → Unassigned", renderer.Content());
            Assert.Single(await SpecificationEvents(db, ImplementAction));
        });
    }

    [PostgresFact]
    public async Task SpecificationFinalizeControlsDoNotRetryOrShowSuccessForUncertainCommitUntilReload()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var pid = product.InternalId;
        var failAfterCommit = false;
        var uncertainAttempts = 0;
        var writes = new PostgresSpecificationWriteStore(db.ConnectionString, async (transaction, token) =>
        {
            await transaction.CommitAsync(token);
            if (!failAfterCommit) return;
            uncertainAttempts++;
            throw new IOException("private provider details");
        });
        await using var factory = CreateFactory(db, writes: writes);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var id = (await ops.Create(actor, pid, "DEV-1", "Finalization", "Ready")).Value.InternalId;
        Assert.True((await ops.SubmitForReview(actor, pid, id)).IsSuccess);
        Assert.True((await ops.Approve(actor, pid, id)).IsSuccess);
        Assert.True((await ops.SaveEvidence(actor, pid, id, null, "**Final evidence**", EvidenceUrl)).IsSuccess);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, id));
            await renderer.Check("id", MergeAttestation);
            failAfterCommit = true;
            await renderer.Click(MarkImplemented);
            Assert.Contains("The Specification operation outcome could not be confirmed.", renderer.Content());
            Assert.DoesNotContain("Implemented", renderer.Content());
            Assert.DoesNotContain("private provider details", renderer.Content());
            Assert.False(renderer.HasButton(MarkImplemented));

            await renderer.Click("specification-reload");
            Assert.Contains("Implemented", renderer.Content());
            Assert.Contains(EvidenceUrl, renderer.Links());
            Assert.False(renderer.HasButton(MarkImplemented));
            Assert.Equal(1, uncertainAttempts);
            Assert.Single(await SpecificationEvents(db, ImplementAction));
        });
    }
}
