using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
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
    [PostgresFact]
    public async Task SpecificationLifecycleControlsSelectAndNavigateSameProductExactVersions()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "workspace");
        var foreign = await Product(products, "foreign");
        var owners = new ReopenOwners(db.ConnectionString);
        var versions = await owners.Approve(await owners.Create(product.InternalId));
        var successors = await owners.Successors(versions);
        var foreignVersions = await owners.Create(foreign.InternalId);
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var draft = (await operations.Create(actor, product.InternalId, "DEV-1", "Traceability", "Ready")).Value;
        var path = SpecificationPresentation.DetailsPath(product.Id, draft.InternalId);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task<DevelopmentSpecification> Current() => (await operations.Find(actor, product.InternalId, draft.InternalId)).Value.Value;
            await renderer.Mount<SpecificationDetails>(path);
            await renderer.Click("specification-edit-links");
            var offered = renderer.Elements("input").SelectMany(x => x).Where(x => x.AttributeName == "value")
                .Select(x => x.AttributeValue?.ToString()).ToArray();
            Assert.All(versions.Links.Concat(successors.Links), target => Assert.Contains(target.VersionId.ToString(), offered));
            Assert.All(foreignVersions.Links, target => Assert.DoesNotContain(target.VersionId.ToString(), offered));
            Assert.True(renderer.ButtonDisabled("specification-save-links"));
            foreach (var target in versions.Links) await renderer.Check("value", target.VersionId.ToString());
            Assert.Empty((await Current()).Links.Targets);
            await renderer.Click("specification-save-links");
            Assert.Equal(versions.Links, (await Current()).Links.Targets);
            Assert.Single(await LinkEvents(db));

            string[] paths = [
                $"/user-needs/details?id={versions.Need.Need.InternalId}&version={versions.Need.Need.VersionId}",
                $"/requirements/details?id={versions.Requirement.Requirement.InternalId}&version={versions.Requirement.Requirement.VersionId}",
                $"/test-cases/details?id={versions.Case.TestCase.InternalId}&version={versions.Case.TestCase.VersionId}"
            ];
            Assert.All(paths, link => Assert.Contains(link, renderer.Links()));
            Assert.All(successors.Links, target => Assert.DoesNotContain(renderer.Links(), link => link.EndsWith(target.VersionId.ToString(), StringComparison.Ordinal)));
            await renderer.Click("specification-submit-review");
            Assert.False(renderer.HasButton("specification-edit-links"));
            Assert.All(paths, link => Assert.Contains(link, renderer.Links()));
            await renderer.Click("specification-approve");
            Assert.IsType<ApprovedSpecification>(await Current());
            Assert.DoesNotContain("No exact-version links for", renderer.Content());

            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            await renderer.Mount<SpecificationDetails>(path);
            Assert.Contains("Product retired", renderer.Content());
            Assert.False(renderer.HasButton("specification-open-delete"));
            Assert.False(renderer.HasButton("specification-return"));
            Assert.False(renderer.HasButton("specification-edit-links"));
            Assert.All(paths, link => Assert.Contains(link, renderer.Links()));
        });
    }

    [PostgresFact]
    public async Task SpecificationLifecycleControlsSubmitApproveAndReturnWithStateSpecificRequirements()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var targets = await CreateLinkTargets(db, product.InternalId);
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var blank = (await operations.Create(actor, product.InternalId, "EMPTY", "Blank Draft", null)).Value;
        var draft = (await operations.Create(actor, product.InternalId, "DEV-1", "Ready Draft", "Ready")).Value;
        Assert.True((await operations.EditLinks(actor, product.InternalId, draft.InternalId, targets.Take(1))).IsSuccess);
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            async Task<DevelopmentSpecification> Current() => (await operations.Find(actor, product.InternalId, draft.InternalId)).Value.Value;
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, blank.InternalId));
            Assert.True(renderer.ButtonDisabled("specification-submit-review"));
            Assert.Contains("Add a description", renderer.Content());
            Assert.Empty(await SpecificationEvents(db, "specification.submit-for-review"));

            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, draft.InternalId));
            Assert.False(renderer.ButtonDisabled("specification-submit-review"));
            await renderer.Click("specification-submit-review");
            Assert.IsType<InReviewSpecification>(await Current());
            Assert.Contains("Return reason (optional)", renderer.Content());
            Assert.False(renderer.ButtonDisabled("specification-return"));
            await renderer.Click("specification-approve");
            Assert.Contains("Every linked UserNeed version must be Approved", renderer.Content());
            Assert.IsType<InReviewSpecification>(await Current());
            Assert.Empty(await SpecificationEvents(db, "specification.approve"));

            await renderer.Submit(); // No reason is needed from In Review.
            Assert.IsType<DraftSpecification>(await Current());
            Assert.Null(Assert.Single(await SpecificationEvents(db, "specification.return-to-draft")).Change!.Reason);
            await renderer.Click("specification-edit-links");
            await renderer.Check("value", targets[0].VersionId.ToString(), false);
            await renderer.Click("specification-save-links");
            Assert.Empty((await Current()).Links.Targets);
            await renderer.Click("specification-submit-review");
            await renderer.Click("specification-approve");
            Assert.IsType<ApprovedSpecification>(await Current());
            Assert.Contains("No exact-version links for: User Need, Requirement, Test Case.", renderer.Content());
            Assert.Contains("Return reason (required)", renderer.Content());
            Assert.Contains("Retravion does not verify factory run state.", renderer.Content());
            Assert.True(renderer.ButtonDisabled("specification-return"));
            js.Markdown = "Needs revision";
            await renderer.Submit(); // Submitting the form directly must not bypass the attestation.
            Assert.Empty(await SpecificationEvents(db, "specification.reopen"));
            await renderer.Check("id", "specification-reopen-attestation");
            js.InvalidMarkdown = true;
            await renderer.Submit();
            Assert.Contains("The return reason contains invalid Unicode", renderer.Content());
            Assert.Empty(await SpecificationEvents(db, "specification.reopen"));
            js.InvalidMarkdown = false;
            js.Markdown = " ";
            await renderer.Submit();
            Assert.Contains("A reason is required to reopen", renderer.Content());
            Assert.IsType<ApprovedSpecification>(await Current());
            Assert.Empty(await SpecificationEvents(db, "specification.reopen"));
            js.Markdown = new string('r', 40 * 1024) + " 😃";
            await renderer.Submit();
            Assert.IsType<DraftSpecification>(await Current());
            var reopen = Assert.Single(await SpecificationEvents(db, "specification.reopen")).Change!;
            Assert.Equal(js.Markdown, reopen.Reason);
            Assert.True(reopen.NoActiveOrUncertainRunAttested);
        });
    }

    [PostgresFact]
    public async Task SpecificationLifecycleControlsConfirmIdentifiedDeletionSeparatelyFromAttestation()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "Space & / workspace");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var draft = (await operations.Create(actor, product.InternalId, "DEV-1", "To remove", "Ready")).Value;
        var completed = (await operations.Create(actor, product.InternalId, "DEV-2", "Completed", "Ready")).Value;
        foreach (var id in new[] { draft.InternalId, completed.InternalId })
        {
            Assert.True((await operations.SubmitForReview(actor, product.InternalId, id)).IsSuccess);
            Assert.True((await operations.Approve(actor, product.InternalId, id)).IsSuccess);
            Assert.True((await operations.SaveEvidence(actor, product.InternalId, id, "Evidence", EvidenceUrl)).IsSuccess);
        }
        Assert.True((await operations.Implement(actor, product.InternalId, completed.InternalId, true, EvidenceUrl)).IsSuccess);
        var navigation = new PagesNavigation();
        await using var services = PageServices(scope.ServiceProvider, actor, navigation);
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var approvedPath = SpecificationPresentation.DetailsPath(product.Id, draft.InternalId);
            await renderer.Mount<SpecificationDetails>(approvedPath);
            await renderer.Check("id", "specification-reopen-attestation");
            await renderer.Click("specification-open-delete");
            Assert.Contains("DEV-1 · To remove", renderer.Content());
            Assert.Contains("current review, submitted evidence and any associated factory activity history", renderer.Content());
            Assert.Contains("Retravion does not verify factory run state.", renderer.Content());
            Assert.True(renderer.ButtonDisabled("specification-confirm-delete"));
            await renderer.Check("id", "specification-delete-attestation");
            Assert.False(renderer.ButtonDisabled("specification-confirm-delete"));
            Assert.Empty(await SpecificationEvents(db, DeleteAction));
            await renderer.Click("specification-cancel-delete");
            await renderer.Click("specification-open-delete");
            Assert.True(renderer.ButtonDisabled("specification-confirm-delete"));

            await renderer.Mount<SpecificationDetails>(SpecificationPresentation.DetailsPath(product.Id, completed.InternalId));
            Assert.Contains("Implemented", renderer.Content());
            Assert.False(renderer.HasButton("specification-open-delete"));
            Assert.False(renderer.HasButton("specification-confirm-delete"));
            Assert.False(renderer.HasButton("specification-return"));
            Assert.Empty(await SpecificationEvents(db, DeleteAction));

            await renderer.Mount<SpecificationDetails>(approvedPath);
            await renderer.Click("specification-open-delete");
            Assert.True(renderer.ButtonDisabled("specification-confirm-delete"));
            await renderer.Check("id", "specification-delete-attestation");
            await renderer.Click("specification-confirm-delete");
            Assert.EndsWith(SpecificationPresentation.ListPath(product.Id), navigation.Uri);
            Assert.True((await operations.Find(actor, product.InternalId, draft.InternalId)).Value.HasNoValue);
            Assert.IsType<ImplementedSpecification>((await operations.Find(actor, product.InternalId, completed.InternalId)).Value.Value);
            var deletion = Assert.Single(await SpecificationEvents(db, DeleteAction));
            Assert.Equal(draft.InternalId.ToString(), deletion.Target);
            Assert.True(deletion.Change!.NoActiveOrUncertainRunAttested);
        });
    }

    [PostgresFact]
    public async Task SpecificationLifecycleControlsReportUncertainDeletionWithoutAnotherWriteOrSuccessNavigation()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        var attempts = 0;
        var writes = new PostgresSpecificationWriteStore(db.ConnectionString, async (transaction, token) =>
        {
            attempts++;
            await transaction.CommitAsync(token);
            throw new IOException("private provider details");
        });
        var draft = DraftSpecification.Create(product.InternalId, "DEV-1", "Uncertain deletion", "Ready").Value;
        var setup = new PostgresSpecificationWriteStore(db.ConnectionString);
        Assert.True((await setup.Create(draft, CreateActor, CreateTime, () => Task.FromResult(true))).IsSuccess);
        await using var factory = CreateFactory(db, writes: writes);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var navigation = new PagesNavigation();
        await using var services = PageServices(scope.ServiceProvider, actor, navigation);
        await using var renderer = new PagesRenderer(services);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var path = SpecificationPresentation.DetailsPath(product.Id, draft.InternalId);
            await renderer.Mount<SpecificationDetails>(path);
            await renderer.Click("specification-open-delete");
            await renderer.Check("id", "specification-delete-attestation");
            await renderer.Click("specification-confirm-delete");
            Assert.Equal(1, attempts);
            Assert.EndsWith(path, navigation.Uri);
            Assert.Contains("outcome could not be confirmed", renderer.Content());
            Assert.DoesNotContain("private provider details", renderer.Content());
            Assert.False(renderer.HasButton("specification-confirm-delete"));
            Assert.False(renderer.HasButton("specification-submit-review"));
            await renderer.Click("specification-reload");
            Assert.Contains("Specification not found in this Product.", renderer.Content());
            Assert.Equal(1, attempts);
            Assert.Single(await SpecificationEvents(db, DeleteAction));
        });
    }
}
#pragma warning restore BL0006
