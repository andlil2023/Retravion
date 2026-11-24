using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Specification;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Assert on attributes from the actual component render tree.
public sealed partial class SpecificationReadTests
{
    private const string EvidenceForm = "specification-evidence-form";
    private const string OpenEvidence = "specification-open-evidence";
    private const string ConfirmEvidence = "specification-confirm-evidence";
    private const string ReviewEvidence = "specification-evidence-reviewed";
    private const string EvidenceUrlInput = "specification-evidence-pr-url";
    private const string ReportInput = "specification-evidence-markdown";

    [PostgresFact]
    public async Task SpecificationEvidenceControlsSaveReportAndOptionalHttpsUrlAndShowValidation()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var pid = product.InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var id = (await ops.Create(actor, pid, "DEV-1", "Evidence", "Ready")).Value.InternalId;
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task<SpecificationEvidence?> Read() => (await ops.FindDetail(actor, pid, id)).Value.Value.Evidence;
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, id));
            Assert.Contains("No implementation evidence saved", renderer.Content());
            Assert.False(renderer.HasButton(OpenEvidence));
            await renderer.Click("specification-submit-review");
            Assert.False(renderer.HasButton(OpenEvidence));
            await renderer.Click("specification-approve");
            await renderer.Click(OpenEvidence);
            Assert.DoesNotContain(renderer.Element("input", "id", EvidenceUrlInput), attr => attr.AttributeName is "oninput" or "onchange" or "maxlength");

            js.UnboundedText = "";
            await renderer.Submit(EvidenceForm);
            Assert.Contains("Enter Markdown for the Specification evidence report.", renderer.Content());
            await js.InputMarkdown("**Human-verified results** 😃");
            js.UnboundedText = "http://forge.example/pr/42";
            await renderer.Submit(EvidenceForm);
            Assert.Contains("The PR URL must be an absolute HTTPS URL.", renderer.Content());
            Assert.DoesNotContain(js.UnboundedText, renderer.Links());
            Assert.Contains("<strong>Human-verified results</strong>", renderer.Content());
            js.InvalidUnboundedText = true;
            await renderer.Submit(EvidenceForm);
            Assert.Contains("The PR URL contains invalid Unicode", renderer.Content());
            Assert.Null(await Read());
            Assert.Empty(await SpecificationEvents(db, SaveEvidenceAction));

            // Correct only the URL and submit the same report. The validation failures kept the editor open.
            js.InvalidUnboundedText = false;
            js.UnboundedText = EvidenceUrl;
            await renderer.Submit(EvidenceForm);
            var evidence = (await Read())!;
            Assert.Equal((js.Markdown, EvidenceUrl), (evidence.Markdown, evidence.PullRequestUrl));
            Assert.Contains("Evidence saved.", renderer.Content());
            Assert.Contains("<strong>Human-verified results</strong>", renderer.Content());
            Assert.Contains(EvidenceUrl, renderer.Links());
            Assert.DoesNotContain(renderer.Elements("form"), attrs => attrs.Any(attr => Equals(attr.AttributeValue, EvidenceForm)));
            Assert.Contains(long.MaxValue, js.OpenedWithLimits);
            Assert.Contains(1024 * 1024, js.OpenedWithLimits);
            var saved = Assert.Single(await SpecificationEvents(db, SaveEvidenceAction));
            Assert.Equal("User", saved.Actor);
            Assert.Equal(id.ToString(), saved.Target);
            Assert.Equal(new SpecificationAuditEvidenceChange(null, new(js.Markdown, EvidenceUrl, false)), saved.Change!.Evidence);

            await renderer.Click(OpenEvidence);
            Assert.Contains(renderer.Element("textarea", "id", ReportInput), attr => attr.AttributeName == "value" && Equals(attr.AttributeValue, js.Markdown));
            Assert.Contains(renderer.Element("input", "id", EvidenceUrlInput), attr => attr.AttributeName == "value" && Equals(attr.AttributeValue, EvidenceUrl));
            js.UnboundedText = "";
            await renderer.Submit(EvidenceForm);
            Assert.Null((await Read())!.PullRequestUrl);
            Assert.Equal(js.Markdown, (await Read())!.Markdown);
            Assert.DoesNotContain(EvidenceUrl, renderer.Links());
            Assert.Contains("No PR URL provided.", renderer.Content());
            Assert.Equal(2, (await SpecificationEvents(db, SaveEvidenceAction)).Count);
        });
    }

    [PostgresFact]
    public async Task SpecificationEvidenceControlsShowRetainedOutdatedReportAndRequireConfirmationOrReplacementAfterReapproval()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var pid = product.InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var id = (await ops.Create(actor, pid, "DEV-1", "Retained evidence", "Ready")).Value.InternalId;
        Assert.True((await ops.SubmitForReview(actor, pid, id)).IsSuccess);
        Assert.True((await ops.Approve(actor, pid, id)).IsSuccess);
        const string markdown = "**Original results**\r\n\r\nKnown limitations.";
        var original = (await ops.SaveEvidence(actor, pid, id, markdown, EvidenceUrl)).Value;
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task<SpecificationEvidence> Read() => (await ops.FindDetail(actor, pid, id)).Value.Value.Evidence!;
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, id));
            Assert.False(renderer.HasButton(ConfirmEvidence));

            async Task ReopenAndReapprove()
            {
                js.UnboundedText = null;
                js.Markdown = "Revise the Specification";
                await renderer.Check("id", "specification-reopen-attestation");
                await renderer.Submit("specification-return-form");
                Assert.Equal(original.MarkOutdated(), await Read());
                Assert.Contains("Outdated:", renderer.Content());
                Assert.Contains("<strong>Original results</strong>", renderer.Content());
                Assert.Contains(EvidenceUrl, renderer.Links());
                Assert.False(renderer.HasButton(OpenEvidence));
                Assert.False(renderer.HasButton(ConfirmEvidence));
                await renderer.Click("specification-submit-review");
                Assert.False(renderer.HasButton(ConfirmEvidence));
                await renderer.Click("specification-approve");
                Assert.Contains("Outdated:", renderer.Content());
                Assert.Contains("Replace evidence", renderer.Content());
                Assert.True(renderer.ButtonDisabled(ConfirmEvidence));
            }

            await ReopenAndReapprove();
            await renderer.Click(ConfirmEvidence);
            Assert.Empty(await SpecificationEvents(db, ConfirmEvidenceAction));
            await renderer.Click(OpenEvidence);
            Assert.False(renderer.HasButton(ConfirmEvidence));
            // The textarea projects CRLF as LF. This remains a no-op, not a replacement.
            js.Markdown = markdown.Replace("\r\n", "\n");
            js.UnboundedText = EvidenceUrl;
            await renderer.Submit(EvidenceForm);
            Assert.Equal(original.MarkOutdated(), await Read());
            Assert.Single(await SpecificationEvents(db, SaveEvidenceAction));
            Assert.Contains("still outdated", renderer.Content());

            await renderer.Check("id", ReviewEvidence);
            Assert.False(renderer.ButtonDisabled(ConfirmEvidence));
            await renderer.Click(ConfirmEvidence);
            Assert.Equal(original, await Read());
            Assert.DoesNotContain("Outdated:", renderer.Content());
            Assert.False(renderer.HasButton(ConfirmEvidence));
            var confirmation = Assert.Single(await SpecificationEvents(db, ConfirmEvidenceAction));
            Assert.Equal(new SpecificationAuditEvidenceChange(new(markdown, EvidenceUrl, true), new(markdown, EvidenceUrl, false)),
                confirmation.Change!.Evidence);

            await ReopenAndReapprove();
            await renderer.Click(OpenEvidence);
            js.Markdown = "**Replacement report**";
            js.UnboundedText = "https://another-forge.example/pulls/43";
            await renderer.Submit(EvidenceForm);
            Assert.Equal(SpecificationEvidence.Prepare(js.Markdown, js.UnboundedText).Value, await Read());
            Assert.Contains("<strong>Replacement report</strong>", renderer.Content());
            Assert.Contains(js.UnboundedText, renderer.Links());
            Assert.DoesNotContain(EvidenceUrl, renderer.Links());
            Assert.DoesNotContain("Outdated:", renderer.Content());
            Assert.False(renderer.HasButton(ConfirmEvidence));
            Assert.Single(await SpecificationEvents(db, ConfirmEvidenceAction));
        });
    }

    [PostgresFact]
    public async Task SpecificationEvidenceControlsDoNotRetryUncertainSaveAndRequireReload()
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
        var id = (await ops.Create(actor, pid, "DEV-1", "Evidence", "Ready")).Value.InternalId;
        Assert.True((await ops.SubmitForReview(actor, pid, id)).IsSuccess);
        Assert.True((await ops.Approve(actor, pid, id)).IsSuccess);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, id));
            await renderer.Click(OpenEvidence);
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            js.Markdown = "**Committed report**";
            js.UnboundedText = EvidenceUrl;
            failAfterCommit = true;
            await renderer.Submit(EvidenceForm);
            Assert.Contains("The Specification operation outcome could not be confirmed.", renderer.Content());
            Assert.DoesNotContain("private provider details", renderer.Content());
            Assert.DoesNotContain("Evidence saved.", renderer.Content());
            Assert.DoesNotContain(EvidenceUrl, renderer.Links());
            Assert.False(renderer.HasButton(OpenEvidence));
            Assert.Empty(renderer.Elements("form"));

            await renderer.Click("specification-reload");
            Assert.Contains("<strong>Committed report</strong>", renderer.Content());
            Assert.Contains(EvidenceUrl, renderer.Links());
            Assert.True(renderer.HasButton(OpenEvidence));
            Assert.Equal(1, uncertainAttempts);
            Assert.Single(await SpecificationEvents(db, SaveEvidenceAction));
        });
    }

    [PostgresFact]
    public async Task SpecificationEvidenceControlsDisplayImplementedAndRetiredEvidenceWithoutMutations()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "workspace");
        var pid = product.InternalId;
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        async Task<Guid> CreateWithEvidence(string label)
        {
            var id = (await ops.Create(actor, pid, label, "Evidence", "Ready")).Value.InternalId;
            Assert.True((await ops.SubmitForReview(actor, pid, id)).IsSuccess);
            Assert.True((await ops.Approve(actor, pid, id)).IsSuccess);
            Assert.True((await ops.SaveEvidence(actor, pid, id, "**Read-only report**", EvidenceUrl)).IsSuccess);
            return id;
        }
        var implemented = await CreateWithEvidence("DEV-1");
        Assert.True((await ops.Implement(actor, pid, implemented, true)).IsSuccess);
        var retired = await CreateWithEvidence("DEV-2");
        Assert.True((await ops.Reopen(actor, pid, retired, "Revise", true)).IsSuccess);
        Assert.True((await ops.SubmitForReview(actor, pid, retired)).IsSuccess);
        Assert.True((await ops.Approve(actor, pid, retired)).IsSuccess);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, implemented));
            AssertReadOnly("Implemented");
            Assert.DoesNotContain("Outdated:", renderer.Content());

            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, retired));
            AssertReadOnly("Product retired");
            Assert.Contains("Outdated:", renderer.Content());

            void AssertReadOnly(string status)
            {
                Assert.Contains(status, renderer.Content());
                Assert.Contains("<strong>Read-only report</strong>", renderer.Content());
                Assert.Contains(EvidenceUrl, renderer.Links());
                Assert.False(renderer.HasButton(OpenEvidence));
                Assert.False(renderer.HasButton(ConfirmEvidence));
                Assert.Empty(renderer.Elements("input"));
                Assert.Empty(renderer.Elements("textarea"));
                Assert.Empty(renderer.Elements("form"));
            }
        });
    }
}
#pragma warning restore BL0006
