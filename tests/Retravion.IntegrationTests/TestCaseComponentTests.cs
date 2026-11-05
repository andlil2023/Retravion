using System.Security.Claims;
using CSharpFunctionalExtensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product.Create;
using Retravion.Application.TestCase;
using Retravion.Domain.Product;
using Retravion.Domain.TestCase;
using Retravion.Infrastructure.AuditTrail;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.TestCase;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.TestCase;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise real Blazor callbacks, following the existing component-test convention.
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task TestCasePagesCreateIncompleteDraftEditMarkdownStepsFilterAndPreserveFailedDrafts()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var other = (await new CreateProduct(products).Execute("Q", "Other", ProductKind.System, "")).Value;
        var store = new PostgresTestCaseStore(database.ConnectionString);
        var writes = new CaseWriteHook(store);
        await using var host = new McpTestHost(services =>
        {
            services.AddSingleton<Retravion.Application.Product.IProductStore>(products);
            services.AddSingleton<ITestCaseStore>(store); services.AddSingleton<ITestCaseChangeTransaction>(writes);
        });
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var navigation = new CaseNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, CaseJS>().AddSingleton<NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new CaseAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<TestCaseOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new CaseRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(TestCaseEditor), new() { [nameof(TestCaseEditor.ProductId)] = "P" });
            Assert.Empty(renderer.Errors);
            Assert.Single(renderer.Elements("form"));
            Assert.DoesNotContain(renderer.Elements("input"), attrs => attrs.Any(f => f.AttributeName == "required" && Equals(f.AttributeValue, true)));
            var save = renderer.Button("Save Draft");
            Assert.Equal(Variant.Filled, save.Variant); Assert.Equal(Color.Primary, save.Color);
            await renderer.Event("onsubmit", EventArgs.Empty);
            var created = Assert.Single((await store.List(product.InternalId)).Value);
            Assert.Equal("", created.TestCase.Content.Title); Assert.Empty(created.TestCase.Content.Steps);
            Assert.Contains($"version={created.TestCase.VersionId}", navigation.Uri);
            navigation.Go($"test-cases/edit?id={created.TestCase.InternalId}&version={created.TestCase.VersionId}");
            await renderer.Mount(typeof(TestCaseEditor), new() { [nameof(TestCaseEditor.Id)] = created.TestCase.InternalId, [nameof(TestCaseEditor.VersionId)] = created.TestCase.VersionId });
            await renderer.Field("Title").ValueChanged.InvokeAsync(" <b>UI title</b> ");
            await renderer.Input("test-case-description", "# Description\n\n| A |\n| --- |\n| B |\n\n<script>bad()</script>");
            await renderer.Input("test-case-preconditions", "* Prepare");
            await renderer.Click("Add step");
            await renderer.Input(renderer.Editor("Step 1 action").InputId, "First action");
            await renderer.Input(renderer.Editor("Step 1 expected result").InputId, "First result");
            await renderer.Click("Add step");
            await renderer.Input(renderer.Editor("Step 2 action").InputId, "**Second** action");
            await renderer.Input(renderer.Editor("Step 2 expected result").InputId, "Second result");
            await renderer.Event("onclick", new MouseEventArgs(), "aria-label", "Move step 2 up");
            await renderer.Event("onclick", new MouseEventArgs(), "aria-label", "Remove step 2");
            await renderer.Click("Add step");
            await renderer.Input(renderer.Editor("Step 2 action").InputId, "Third action");
            await renderer.Input(renderer.Editor("Step 2 expected result").InputId, "");
            var ids = renderer.Elements("textarea").Select(attrs => attrs.Single(f => f.AttributeName == "id").AttributeValue).ToArray();
            Assert.Equal(6, ids.Length); Assert.Equal(ids.Length, ids.Distinct().Count());
            Assert.Single(renderer.Elements("form"));
            await renderer.Event("onsubmit", EventArgs.Empty);
            var changed = (await store.Find(created.TestCase.InternalId)).Value.Value;
            Assert.Equal(" <b>UI title</b> ", changed.TestCase.Content.Title);
            Assert.Equal("* Prepare", changed.TestCase.Content.Preconditions);
            Assert.Equal(new[] { "**Second** action", "Third action" }, changed.TestCase.Content.Steps.Select(step => step.Action));
            Assert.Equal(new[] { "Second result", "" }, changed.TestCase.Content.Steps.Select(step => step.ExpectedResult));
            var detail = await host.Browser.GetStringAsync($"/test-cases/details?id={created.TestCase.InternalId}&version={created.TestCase.VersionId}");
            Assert.Contains("<table>", detail); Assert.Contains("<strong>Second</strong>", detail); Assert.Contains("&lt;b&gt;UI title&lt;/b&gt;", detail);
            Assert.DoesNotContain("<script>bad", detail); Assert.Contains("Requirement links, review, and successor versions are not available", detail);
            Assert.DoesNotContain("Submit for review", detail);
            var wrong = await host.Browser.GetStringAsync($"/test-cases/details?id={created.TestCase.InternalId}&version={Guid.NewGuid()}");
            Assert.Contains("Test Case version not found", wrong); Assert.DoesNotContain("UI title", wrong);
            await store.Create(product.InternalId, CaseContent("Other match"), CaseActor, CaseTime);
            await store.Create(other.InternalId, CaseContent("Private other Product"), CaseActor, CaseTime);
            await renderer.Mount(typeof(TestCaseList), new() { [nameof(TestCaseList.ProductId)] = "P" });
            Assert.Contains("UI title", renderer.Text()); Assert.Contains("Other match", renderer.Text()); Assert.DoesNotContain("Private other Product", renderer.Text());
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("uI tItLe");
            Assert.Contains("UI title", renderer.Text()); Assert.DoesNotContain("Other match", renderer.Text());
            await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync("Approved");
            Assert.Contains("No Test Cases match", renderer.Text());
            await renderer.Components<MudSelect<string>>().Single().ValueChanged.InvokeAsync("Draft");
            Assert.Contains("UI title", renderer.Text());
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("TC-002");
            Assert.DoesNotContain("UI title", renderer.Text()); Assert.Contains("Other match", renderer.Text());
            var navigationHtml = await host.Browser.GetStringAsync("/test-cases?product=P");
            Assert.Contains("Test Cases for P", navigationHtml); Assert.Contains("/test-cases/create?product=P", navigationHtml);
            navigation.Go($"test-cases/edit?id={created.TestCase.InternalId}");
            await renderer.Mount(typeof(TestCaseEditor), new() { [nameof(TestCaseEditor.Id)] = created.TestCase.InternalId });
            await renderer.Field("Title").ValueChanged.InvokeAsync("Unsaved retained title");
            await renderer.Input("test-case-description", "Unsaved retained description");
            foreach (var committed in new[] { false, true })
            {
                writes.Store = new PostgresTestCaseStore(database.ConnectionString, async (transaction, cancellationToken) =>
                { if (committed) await transaction.CommitAsync(cancellationToken); throw new IOException("lost confirmation"); });
                var calls = writes.Calls;
                await renderer.Event("onsubmit", EventArgs.Empty);
                Assert.Equal(calls + 1, writes.Calls);
                Assert.Contains("could not be confirmed", renderer.Text()); Assert.Contains("Reload", renderer.Text());
                Assert.Equal("Unsaved retained title", renderer.Field("Title").GetState(field => field.Value));
                Assert.Contains("Unsaved retained description", renderer.Text());
            }
            writes.Store = store;
            await renderer.Event("onsubmit", EventArgs.Empty);
            Assert.Contains("changed", renderer.Text()); // Committed uncertainty left this editor snapshot stale.
            Assert.Equal("Unsaved retained title", renderer.Field("Title").GetState(field => field.Value));
            var operations = scope.ServiceProvider.GetRequiredService<TestCaseOperations>();
            var count = (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count;
            Assert.True((await operations.Create(principal, product.InternalId, "invalid", "", "", [null!])).IsFailure);
            Assert.Equal(count, (await new PostgresAuditQuery(database.ConnectionString).FindAsync(new())).Count);
            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            var retiredList = await host.Browser.GetStringAsync("/test-cases?product=P");
            Assert.Contains("read-only", retiredList); Assert.DoesNotContain("/test-cases/create?product=P", retiredList);
            var retiredDetails = await host.Browser.GetStringAsync($"/test-cases/details?id={created.TestCase.InternalId}");
            Assert.Contains("retired", retiredDetails); Assert.DoesNotContain("Edit Draft", retiredDetails);
            var retiredForm = await host.Browser.GetStringAsync($"/test-cases/edit?id={created.TestCase.InternalId}");
            Assert.Contains("retired", retiredForm); Assert.DoesNotContain("test-case-form", retiredForm);
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class CaseWriteHook(PostgresTestCaseStore store) : ITestCaseChangeTransaction
    {
        internal PostgresTestCaseStore Store = store;
        internal int Calls;
        public Task<Result<TestCaseSnapshot>> Create(Guid productInternalId, TestCaseContent content, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        { Calls++; return Store.Create(productInternalId, content, actor, occurredAt, cancellationToken); }
        public Task<Result<TestCaseSnapshot>> Update(TestCaseSnapshot expected, TestCaseContent content, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
        { Calls++; return Store.Update(expected, content, actor, occurredAt, cancellationToken); }
    }
    private sealed class CaseAuthentication(ClaimsPrincipal principal) : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal)); }
    private sealed class CaseNavigation : NavigationManager
    {
        internal CaseNavigation() => Initialize("https://localhost/", "https://localhost/test-cases/create?product=P");
        internal void Go(string uri) => Uri = ToAbsoluteUri(uri).ToString();
        protected override void NavigateToCore(string uri, bool forceLoad) => Go(uri);
    }
    private sealed class CaseJS : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(identifier == "import" ? (TValue)(object)this : default!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CaseRenderer : Renderer
    {
        internal CaseRenderer(IServiceProvider services, ILoggerFactory logger) : base(services, logger) =>
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        private int root = -1;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal List<Exception> Errors { get; } = [];
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        internal async Task Mount(Type type, Dictionary<string, object?> parameters)
        {
            if (root >= 0) RemoveRootComponent(root);
            var component = InstantiateComponent(type);
            foreach (var (name, value) in parameters) type.GetProperty(name)!.SetValue(component, value);
            root = AssignRootComponentId(component);
            await RenderRootComponentAsync(root, ParameterView.Empty);
        }
        internal IEnumerable<T> Components<T>() => Frames(root).Where(frame => frame.FrameType == RenderTreeFrameType.Component).Select(frame => frame.Component).OfType<T>();
        internal MudTextField<string> Field(string label) => Components<MudTextField<string>>().Single(field => field.Label == label);
        internal Retravion.Web.Components.Markdown.DescriptionEditor Editor(string label) => Components<Retravion.Web.Components.Markdown.DescriptionEditor>().Single(editor => editor.Label == label);
        internal IEnumerable<RenderTreeFrame[]> Elements(string element)
        {
            var frames = Frames(root).ToArray();
            return Enumerable.Range(0, frames.Length).Where(i => frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == element)
                .Select(i => frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray());
        }
        internal Task Input(string id, string value) => Event("oninput", new ChangeEventArgs { Value = value }, "id", id);
        internal Task Event(string name, EventArgs args, string? attribute = null, string? value = null)
        {
            var frames = Frames(root).ToArray();
            var attributes = Enumerable.Range(0, frames.Length).Where(i => frames[i].FrameType == RenderTreeFrameType.Element)
                .Select(i => frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray())
                .Single(attrs => attrs.Any(frame => frame.AttributeName == name && frame.AttributeEventHandlerId != 0) &&
                    (attribute is null || attrs.Any(frame => frame.AttributeName == attribute && Equals(frame.AttributeValue, value))));
            return DispatchEventAsync(attributes.Single(frame => frame.AttributeName == name).AttributeEventHandlerId, null, args);
        }
        internal MudButton Button(string label) => Components<MudButton>().Single(button => ButtonText(button) == label);
        internal Task Click(string label) => Button(label).OnClick.InvokeAsync(new MouseEventArgs());
        private string ButtonText(MudButton button) => Text(Frames(root).Single(frame => frame.FrameType == RenderTreeFrameType.Component && ReferenceEquals(frame.Component, button)).ComponentId).Trim();
        internal string Text() => Text(root);
        private string Text(int id) => string.Join("", Frames(id).Select(frame => frame.FrameType switch { RenderTreeFrameType.Text => frame.TextContent, RenderTreeFrameType.Markup => frame.MarkupContent, _ => "" }));
        private IEnumerable<RenderTreeFrame> Frames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            foreach (var frame in frames.Array.Take(frames.Count))
            {
                yield return frame;
                if (frame.FrameType == RenderTreeFrameType.Component && frame.Component is not null)
                    foreach (var child in Frames(frame.ComponentId)) yield return child;
            }
        }
    }
}
#pragma warning restore BL0006
