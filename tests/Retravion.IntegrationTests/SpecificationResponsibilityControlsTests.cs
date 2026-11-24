using System.Text;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Identity.Application;
using Retravion.Infrastructure.Product;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Assert on attributes from the actual component render tree.
public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task SpecificationResponsibilityControlsSelectActiveUsersAndRetainHistoryAfterClearing()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionTicketStore>();
        var actor = await Principal(sessions, "User", now: CreateTime);
        var restricted = await Principal(sessions, "Administrator", restricted: true, now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var accounts = scope.ServiceProvider.GetRequiredService<IAdministratorStore>();
        var active = await CreateAssignee(accounts, "Active.User");
        var inactive = await CreateAssignee(accounts, "Inactive.User");
        Assert.True((await accounts.ChangeAvailability(inactive, inactive.Inactivate("User").Value)).IsSuccess);
        Assert.Equal("Access denied.", (await ops.ListActiveAssignees(restricted)).Error);
        var draft = (await ops.Create(actor, product.InternalId, "DEV-1", "Assignment", "Ready")).Value;
        var path = SpecificationPresentation.DetailsPath(product.Id, draft.InternalId);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task<SpecificationDetail> Detail() => (await ops.FindDetail(actor, product.InternalId, draft.InternalId)).Value.Value;
            await renderer.Mount<SpecificationDetails>(path);
            var options = renderer.Elements("option").SelectMany(x => x).Where(x => x.AttributeName == "value").Select(x => x.AttributeValue);
            Assert.Contains(active.Username, options);
            Assert.DoesNotContain(inactive.Username, options);
            Assert.Contains("does not grant access", renderer.Content());
            Assert.True(renderer.ButtonDisabled("specification-assign"));
            await renderer.Select("specification-assignee", active.Username);
            Assert.False(renderer.ButtonDisabled("specification-assign"));
            Assert.Empty(await AssignmentEvents(db));
            await renderer.Click("specification-assign");
            Assert.Equal(active.Username, (await Detail()).Assignee);
            Assert.Contains($"Assignee: {active.Username}", renderer.Content());
            Assert.Contains($"User: Unassigned → {active.Username}", renderer.Content());

            await renderer.Click("specification-submit-review");
            Assert.True(renderer.HasButton("specification-clear-assignment"));
            await renderer.Click("specification-approve");
            Assert.True(renderer.HasButton("specification-assign"));
            await renderer.Click("specification-clear-assignment");
            var cleared = await Detail();
            Assert.Null(cleared.Assignee);
            Assert.IsType<ApprovedSpecification>(cleared.Specification);
            Assert.Equal(2, cleared.AssignmentHistory.Count);
            Assert.Equal(2, (await AssignmentEvents(db)).Count);
            Assert.Contains("Assignee: Unassigned", renderer.Content());
            Assert.Contains($"User: {active.Username} → Unassigned", renderer.Content());
            Assert.Contains($"User: Unassigned → {active.Username}", renderer.Content());
            Assert.False(renderer.HasButton("specification-clear-assignment"));

            // A formerly active option still has to pass the live transactional Identity lookup.
            await renderer.Select("specification-assignee", active.Username);
            Assert.True((await accounts.ChangeAvailability(active, active.Inactivate("User").Value)).IsSuccess);
            await renderer.Click("specification-assign");
            Assert.Contains("Select an active user for the Specification assignment.", renderer.Content());
            Assert.Null((await Detail()).Assignee);
            Assert.Equal(2, (await AssignmentEvents(db)).Count);
        });
    }

    [PostgresFact]
    public async Task SpecificationResponsibilityControlsRequireApprovalForEnteredAndUploadedMarkdownAndShowInvalidation()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var draft = (await ops.Create(actor, product.InternalId, "DEV-1", "Review", "Ready")).Value;
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task<SpecificationAiReview?> Review() => (await ops.FindDetail(actor, product.InternalId, draft.InternalId)).Value.Value.CurrentAiReview;
            const string approved = "specification-ai-review-approved";
            const string form = "specification-ai-review-form";
            const string original = "**Entered findings** 😃";
            const string replacement = "**Replacement findings** é\r\n";
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, draft.InternalId));
            Assert.Contains("No AI review submitted", renderer.Content());
            await renderer.Click("specification-open-ai-review");
            js.Markdown = original;
            await renderer.Submit(form);
            Assert.Empty(await SpecificationEvents(db, SubmitAiReviewAction));
            await renderer.Check("id", approved);
            js.InvalidMarkdown = true;
            await renderer.Submit(form);
            Assert.Contains("contains invalid Unicode", renderer.Content());
            Assert.Null(await Review());
            js.InvalidMarkdown = false;
            await renderer.Submit(form);
            Assert.Equal(original, (await Review())!.Markdown);
            Assert.Contains("<strong>Entered findings</strong>", renderer.Content());

            await renderer.Click("specification-submit-review");
            await renderer.Click("specification-open-ai-review");
            await renderer.Check("id", approved);
            await renderer.Select("specification-ai-review-method", "uploaded");
            const string fileId = "specification-submit-ai-review-file";
            var oversized = new ReviewBrowserFile(new byte[1024 * 1024 + 1]);
            await renderer.SelectFile(oversized);
            Assert.Contains("must be at most 1 MB", renderer.Content());
            Assert.True(renderer.ButtonDisabled(fileId));
            await renderer.Click(fileId);
            Assert.Equal(0, oversized.Reads);
            var invalidEncoding = new ReviewBrowserFile([0xC3, 0x28]);
            await renderer.SelectFile(invalidEncoding);
            Assert.True(renderer.ButtonDisabled(fileId));
            await renderer.Click(fileId);
            Assert.Equal(0, invalidEncoding.Reads);
            await renderer.Check("id", approved);
            await renderer.Click(fileId);
            Assert.Contains("valid UTF-8", renderer.Content());
            Assert.Equal(original, (await Review())!.Markdown);
            Assert.Single(await SpecificationEvents(db, SubmitAiReviewAction));

            var file = new ReviewBrowserFile(Encoding.UTF8.GetBytes("\uFEFF" + replacement));
            await renderer.Check("id", approved);
            await renderer.SelectFile(file);
            Assert.True(renderer.ButtonDisabled(fileId)); // Choosing a different file invalidates approval.
            Assert.Equal(0, file.Reads);
            await renderer.Check("id", approved);
            Assert.False(renderer.ButtonDisabled(fileId));
            await renderer.Click(fileId);
            Assert.Equal(1, file.Reads);
            Assert.Equal(replacement, (await Review())!.Markdown);
            Assert.Contains("<strong>Replacement findings</strong>", renderer.Content());
            Assert.DoesNotContain("Entered findings", renderer.Content());
            Assert.All(await SpecificationEvents(db, SubmitAiReviewAction), item => Assert.True(item.Change!.AiReview!.UserApproved));

            await renderer.Click("specification-approve");
            Assert.False(renderer.HasButton("specification-open-ai-review"));
            Assert.DoesNotContain("Outdated:", renderer.Content());
            await renderer.Check("id", "specification-reopen-attestation");
            js.Markdown = "Must revise the Specification";
            await renderer.Submit("specification-return-form");
            Assert.True((await Review())!.IsOutdated);
            Assert.Contains("Outdated:", renderer.Content());
            Assert.Contains("<strong>Replacement findings</strong>", renderer.Content());
            Assert.True(renderer.HasButton("specification-open-ai-review"));
        });
    }

    [PostgresFact]
    public async Task SpecificationResponsibilityControlsKeepImplementedAndRetiredDetailReadOnly()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "workspace");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var ops = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var assigned = await CreateAssignee(scope.ServiceProvider.GetRequiredService<IAdministratorStore>(), "Active.User");
        var draft = (await ops.Create(actor, product.InternalId, "DEV-1", "Completed Specification", "Ready")).Value;
        var id = draft.InternalId;
        Assert.True((await ops.ChangeAssignment(actor, product.InternalId, id, assigned.Username)).IsSuccess);
        Assert.True((await ops.SubmitAiReview(actor, product.InternalId, id, new SpecificationAiReviewInput.Entered("**Retained review**"), true)).IsSuccess);
        Assert.True((await ops.EditContent(actor, product.InternalId, id, draft.Id, draft.Title, "Changed")).IsSuccess);
        Assert.True((await ops.SubmitForReview(actor, product.InternalId, id)).IsSuccess);
        Assert.True((await ops.Approve(actor, product.InternalId, id)).IsSuccess);
        Assert.True((await ops.SaveEvidence(actor, product.InternalId, id, "Evidence", EvidenceUrl)).IsSuccess);
        Assert.True((await ops.Implement(actor, product.InternalId, id, true)).IsSuccess);
        var other = (await ops.Create(actor, product.InternalId, "DEV-2", "Retired Draft", "Ready")).Value;
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, id));
            Assert.Contains("Implemented", renderer.Content());
            Assert.Contains("Assignee: Unassigned", renderer.Content());
            Assert.Contains($"User: {assigned.Username} → Unassigned", renderer.Content());
            Assert.Contains($"User: Unassigned → {assigned.Username}", renderer.Content());
            Assert.Contains("<strong>Retained review</strong>", renderer.Content());
            Assert.Contains("Outdated:", renderer.Content());
            AssertNoMutationControls(renderer);

            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, other.InternalId));
            Assert.Contains("Product retired", renderer.Content());
            AssertNoMutationControls(renderer);
        });
    }

    private static void AssertNoMutationControls(PagesRenderer renderer)
    {
        Assert.Empty(renderer.Elements("select"));
        Assert.False(renderer.HasButton("specification-assign"));
        Assert.False(renderer.HasButton("specification-clear-assignment"));
        Assert.False(renderer.HasButton("specification-open-ai-review"));
        Assert.False(renderer.HasButton("specification-submit-ai-review-file"));
        Assert.Empty(renderer.Elements("form"));
    }

    private sealed class ReviewBrowserFile(byte[] bytes) : IBrowserFile
    {
        public string Name => "review.md";
        public DateTimeOffset LastModified => CreateTime;
        public long Size => bytes.Length;
        public string ContentType => "text/markdown";
        internal int Reads { get; private set; }

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            Assert.Equal(1024 * 1024, maxAllowedSize);
            Reads++;
            return new MemoryStream(bytes, writable: false);
        }
    }
}
#pragma warning restore BL0006
