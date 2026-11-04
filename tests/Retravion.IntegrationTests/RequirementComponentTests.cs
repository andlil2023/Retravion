using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using MudBlazor.Extensions;
using Retravion.Application.Product.Create;
using Retravion.Domain.Product;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.Product;
using Retravion.Infrastructure.Requirement;
using Retravion.Web.Features.Mcp;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Requirement;
using Xunit;
using RequirementListPage = Retravion.Web.Features.Requirement.RequirementList;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Real component callbacks, following UiCatalogFormTests renderer convention.
public sealed partial class PostgresProductTests
{
    [PostgresFact]
    public async Task RequirementComponentsCreateEditFilterAndReviewThroughRealCallbacks()
    {
        await using var database = await Database.Create();
        var products = new PostgresProductStore(database.ConnectionString);
        var product = (await new CreateProduct(products).Execute("P", "Product", ProductKind.System, "")).Value;
        var store = new PostgresRequirementStore(database.ConnectionString);
        await using var host = RequirementMcpHost(products, store, store);
        await host.Login(); await host.Replace(); var token = await host.Authenticate();
        var principal = host.Factory.Services.GetRequiredService<McpCredentials>().Resolve(token);
        using var scope = host.Factory.Services.CreateScope();
        var navigation = new RequirementNavigation();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, RequirementJS>().AddSingleton<NavigationManager>(navigation)
            .AddSingleton<AuthenticationStateProvider>(new RequirementAuthentication(principal))
            .AddSingleton(scope.ServiceProvider.GetRequiredService<RequirementOperations>())
            .AddSingleton(scope.ServiceProvider.GetRequiredService<ProductOperations>()).BuildServiceProvider();
        await using var renderer = new RequirementRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(RequirementEditor), new() { [nameof(RequirementEditor.ProductId)] = "P" });
            Assert.Empty(renderer.Errors);
            await renderer.Field("Requirement ID").ValueChanged.InvokeAsync("REQ-UI");
            await renderer.Field("Title").ValueChanged.InvokeAsync("UI title");
            await renderer.Field("Rationale (optional, plain text)").ValueChanged.InvokeAsync("Why");
            await renderer.Event("oninput", new ChangeEventArgs { Value = "# UI statement" });
            await renderer.Event("onsubmit", EventArgs.Empty);
            Assert.Contains("valid Requirement type is required", renderer.Text());
            Assert.Empty((await store.List(product.InternalId)).Value);
            Assert.Equal("UI title", renderer.Field("Title").GetState(x => x.Value));
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Software);
            await renderer.Event("onsubmit", EventArgs.Empty);
            var created = Assert.Single((await store.List(product.InternalId)).Value);
            Assert.Equal("Why", created.Requirement.Rationale); Assert.Equal("# UI statement", created.Requirement.Statement);
            Assert.Contains("/requirements/details?id=", navigation.Uri);
            await renderer.Mount(typeof(RequirementDetails), new() { [nameof(RequirementDetails.Id)] = created.Requirement.InternalId });
            await renderer.Field("Review comment (optional)").ValueChanged.InvokeAsync("Ready");
            await renderer.Click("Submit for review");
            Assert.Contains("In Review", renderer.Text()); await renderer.Click("Return to Draft");
            await renderer.Click("Submit for review"); await renderer.Click("Approve");
            Assert.Contains("Approved", renderer.Text());
            await renderer.Field("Change or reopening reason").ValueChanged.InvokeAsync("Correct wording");
            await renderer.Click("Reopen to Draft");
            Assert.Equal(5, (await store.History(created.Requirement.InternalId)).Value.Count);
            navigation.Go($"requirements/edit?id={created.Requirement.InternalId}");
            await renderer.Mount(typeof(RequirementEditor), new() { [nameof(RequirementEditor.Id)] = created.Requirement.InternalId });
            Assert.Equal("REQ-UI", renderer.Field("Requirement ID").GetState(x => x.Value));
            await renderer.Field("Title").ValueChanged.InvokeAsync("Changed title");
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Hardware);
            await renderer.Event("onsubmit", EventArgs.Empty);
            var changed = (await store.Find(created.Requirement.InternalId)).Value.Value;
            Assert.Equal("Changed title", changed.Requirement.Title); Assert.Equal(RequirementType.Hardware, changed.Requirement.Type);
            Assert.All((await store.History(created.Requirement.InternalId)).Value, h => { Assert.Equal("UI title", h.Title); Assert.Equal(RequirementType.Software, h.Type); Assert.Equal("Why", h.Rationale); });
            await host.Call("create_draft_requirement", new { productId = "P", id = "REQ-OTHER", title = "Other", statement = "Statement", type = "Software" });
            await renderer.Mount(typeof(RequirementListPage), new() { [nameof(RequirementListPage.ProductId)] = "P" });
            Assert.Contains("Changed title", renderer.Text()); Assert.Contains("REQ-OTHER", renderer.Text());
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("cHaNgEd");
            Assert.Contains("REQ-UI", renderer.Text()); Assert.DoesNotContain("REQ-OTHER", renderer.Text());
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Software);
            Assert.Contains("No Requirements match", renderer.Text());
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(RequirementType.Hardware);
            await renderer.Components<MudSelect<RequirementStatus?>>().Single().ValueChanged.InvokeAsync(RequirementStatus.Approved);
            Assert.Contains("No Requirements match", renderer.Text());
            await renderer.Components<MudSelect<RequirementStatus?>>().Single().ValueChanged.InvokeAsync(RequirementStatus.Draft);
            Assert.Contains("REQ-UI", renderer.Text());
            await renderer.Field("Search ID or title").ValueChanged.InvokeAsync("");
            await renderer.Components<MudSelect<RequirementType?>>().Single().ValueChanged.InvokeAsync(null);
            await renderer.Components<MudSelect<RequirementStatus?>>().Single().ValueChanged.InvokeAsync(null);
            Assert.Contains("REQ-OTHER", renderer.Text()); Assert.Contains("REQ-UI", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class RequirementAuthentication(ClaimsPrincipal principal) : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal)); }
    private sealed class RequirementNavigation : NavigationManager
    {
        internal RequirementNavigation() => Initialize("https://localhost/", "https://localhost/requirements/create?product=P");
        internal void Go(string uri) => Uri = ToAbsoluteUri(uri).ToString();
        protected override void NavigateToCore(string uri, bool forceLoad) => Go(uri);
    }
    private sealed class RequirementJS : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(identifier == "import" ? (TValue)(object)this : default!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class RequirementRenderer : Renderer
    {
        internal RequirementRenderer(IServiceProvider services, ILoggerFactory logger) : base(services, logger) =>
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
            // The router normally supplies these query-only cascading parameters. This renderer
            // exercises the page callbacks without a router, so seed them before initialization.
            foreach (var (name, value) in parameters) type.GetProperty(name)!.SetValue(component, value);
            root = AssignRootComponentId(component);
            await RenderRootComponentAsync(root, ParameterView.Empty);
        }
        internal IEnumerable<T> Components<T>() => Frames(root).Where(f => f.FrameType == RenderTreeFrameType.Component).Select(f => f.Component).OfType<T>();
        internal MudTextField<string> Field(string label) => Components<MudTextField<string>>().Single(f => f.Label == label);
        internal Task Event(string name, EventArgs args)
        {
            var frames = Frames(root).ToArray();
            var attributes = Enumerable.Range(0, frames.Length).Where(i => frames[i].FrameType == RenderTreeFrameType.Element)
                .Select(i => frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray())
                .Single(a => a.Any(f => f.AttributeName == name && f.AttributeEventHandlerId != 0) &&
                    (name != "oninput" || a.Any(f => f.AttributeName == "id" && Equals(f.AttributeValue, "product-markdown"))));
            return DispatchEventAsync(attributes.Single(f => f.AttributeName == name).AttributeEventHandlerId, null, args);
        }
        internal Task Click(string label) => Components<MudButton>().Single(b => ButtonText(b) == label).OnClick.InvokeAsync(new MouseEventArgs());
        private string ButtonText(MudButton button)
        {
            var id = Frames(root).Single(f => f.FrameType == RenderTreeFrameType.Component && ReferenceEquals(f.Component, button)).ComponentId;
            return Text(id).Trim();
        }
        internal string Text() => Text(root);
        private string Text(int id) => string.Join("", Frames(id).Select(f => f.FrameType switch { RenderTreeFrameType.Text => f.TextContent, RenderTreeFrameType.Markup => f.MarkupContent, _ => "" }));
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
