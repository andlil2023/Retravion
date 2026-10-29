using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using MudBlazor.Extensions;
using Retravion.Web.Features.UiCatalog;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise real callbacks with the existing framework renderer convention.
public sealed class UiCatalogFormTests
{
    [Fact]
    public async Task SampleFormValidatesAndRetainsInputWithoutPersistence()
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, NoOpJS>().AddSingleton<NavigationManager, TestNavigation>().BuildServiceProvider();
        await using var renderer = new FormRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount();
            Assert.DoesNotContain("An example name is required.", renderer.Text());
            await renderer.Submit();
            Assert.Contains("An example name is required.", renderer.Text());
            Assert.DoesNotContain("Example validated.", renderer.Text());
            var field = renderer.NameField();
            await field.ValueChanged.InvokeAsync("Example input");
            await renderer.Submit();
            Assert.DoesNotContain("An example name is required.", renderer.Text());
            Assert.Contains("Example validated. Nothing was saved.", renderer.Text());
            Assert.Equal("Example input", renderer.NameField().GetState(x => x.Value));
            await renderer.NameField().ValueChanged.InvokeAsync("   ");
            await renderer.Submit();
            Assert.Contains("An example name is required.", renderer.Text());
            Assert.DoesNotContain("Example validated.", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
    }

    [Fact]
    public async Task ListTemplateExposesLoadedLoadingEmptyErrorAndRetryStates()
    {
        await using var services = Services();
        await using var renderer = new FormRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(CatalogListTemplate));
            Assert.Contains("Another example", renderer.Text());
            var selector = renderer.Components<MudSelect<string>>().Single();
            await selector.ValueChanged.InvokeAsync("Loading");
            Assert.Contains("Loading example items", renderer.Text());
            Assert.DoesNotContain("Another example", renderer.Text());
            await selector.ValueChanged.InvokeAsync("Empty");
            Assert.Contains("No example items yet", renderer.Text());
            await selector.ValueChanged.InvokeAsync("Error");
            Assert.Contains("Example items could not be loaded", renderer.Text());
            await renderer.Click("Retry example list");
            Assert.Contains("Another example", renderer.Text());
            Assert.DoesNotContain("could not be loaded", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndEditTemplatesRetainInputOnFailureGuardBusyAndCancel(bool editMode)
    {
        await using var services = Services();
        await using var renderer = new FormRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(CatalogPageForm), new() { [nameof(CatalogPageForm.EditMode)] = editMode });
            var field = renderer.Components<MudTextField<string>>().First();
            Assert.Equal(editMode ? "Existing example item" : "", field.GetState(x => x.Value));
            await field.ValueChanged.InvokeAsync(" ");
            await renderer.Submit();
            Assert.Contains("An example name is required.", renderer.Text());
            await field.ValueChanged.InvokeAsync("Changed example");
            var outcome = renderer.Components<MudSelect<string>>().Single();
            await outcome.ValueChanged.InvokeAsync("Failure");
            await renderer.Submit();
            Assert.Contains("Simulated save failed.", renderer.Text());
            Assert.Equal("Changed example", renderer.Components<MudTextField<string>>().First().GetState(x => x.Value));
            await outcome.ValueChanged.InvokeAsync("Hold busy");
            await renderer.Submit();
            Assert.Contains("Simulated request in progress", renderer.Text());
            Assert.True(renderer.HasAttribute("disabled", true));
            Assert.True(renderer.HasAttribute("aria-busy", "true"));
            await renderer.Submit(); // A queued/double submit cannot complete a held request.
            Assert.DoesNotContain("Example saved", renderer.Text());
            await renderer.Click(editMode ? "Complete edit simulation" : "Complete create simulation");
            Assert.Contains("Nothing was persisted", renderer.Text());
            Assert.DoesNotContain("Simulated request in progress", renderer.Text());
            await renderer.Click(editMode ? "Cancel edit example" : "Cancel create example");
            Assert.Equal(editMode ? "Existing example item" : "", renderer.Components<MudTextField<string>>().First().GetState(x => x.Value));
            Assert.Contains(editMode ? "original example values restored" : "example fields cleared", renderer.Text());
            await outcome.ValueChanged.InvokeAsync("Success");
            await renderer.Components<MudTextField<string>>().First().ValueChanged.InvokeAsync("Retry succeeds");
            await renderer.Submit();
            Assert.Contains("Example saved", renderer.Text());
            Assert.DoesNotContain("Simulated save failed", renderer.Text());
        });
        Assert.Empty(renderer.Errors);
    }

    [Fact]
    public async Task MarkdownTemplateUsesActualEditorForLivePreviewLayoutsAndLocalSave()
    {
        await using var services = Services();
        await using var renderer = new FormRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(CatalogMarkdownExample));
            Assert.Single(renderer.Components<Retravion.Web.Features.Product.Description.DescriptionEditor>());
            Assert.Contains("<strong>Markdown</strong>", renderer.Text());
            await renderer.Event("oninput", new ChangeEventArgs { Value = "## Changed\n\n**Live**\n\n<script>alert(1)</script>" });
            Assert.Contains("<strong>Live</strong>", renderer.Text());
            Assert.DoesNotContain("<script>", renderer.Text());
            await renderer.Event("onclick", new MouseEventArgs(), "aria-label", "Stacked");
            Assert.True(renderer.HasAttribute("class", "description-layout stacked"));
            Assert.True(renderer.ElementHasAttribute("aria-label", "Stacked", "aria-pressed", true));
            await renderer.Submit();
            Assert.Contains("Markdown captured for this demonstration only", renderer.Text());
            Assert.Contains("Last saved Markdown source", renderer.Text());
            await renderer.Event("onclick", new MouseEventArgs(), "aria-label", "Side by side");
            Assert.True(renderer.HasAttribute("class", "description-layout side-by-side"));
            Assert.True(renderer.ElementHasAttribute("aria-label", "Side by side", "aria-pressed", true));
        });
        Assert.Empty(renderer.Errors);
    }

    [Fact]
    public async Task ButtonGuideReportsRealInteractionStatesWithoutChangingBusinessData()
    {
        await using var services = Services();
        await using var renderer = new FormRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount(typeof(CatalogButtonGuide));
            Assert.Contains("None yet; button ready", renderer.Text());
            await renderer.Event("onfocus", new FocusEventArgs(), "id", "catalog-state-button");
            Assert.Contains("Focus received by pointer or keyboard", renderer.Text());
            await renderer.Event("onmouseenter", new MouseEventArgs(), "id", "catalog-state-button");
            Assert.Contains("Pointer entered. Focus may also be present.", renderer.Text());
            await renderer.Event("onmouseleave", new MouseEventArgs(), "id", "catalog-state-button");
            Assert.Contains("Pointer left. Focus, if present, is unchanged.", renderer.Text());
            Assert.DoesNotContain("Default: pointer left", renderer.Text());
            // Pointer-acquired focus must not be mislabeled as keyboard-only focus.
            await renderer.Event("onmouseenter", new MouseEventArgs(), "id", "catalog-state-button");
            await renderer.Event("onfocus", new FocusEventArgs(), "id", "catalog-state-button");
            Assert.Contains("Focus received by pointer or keyboard", renderer.Text());
            Assert.DoesNotContain("Keyboard focus:", renderer.Text());
            await renderer.Event("onblur", new FocusEventArgs(), "id", "catalog-state-button");
            Assert.Contains("Focus left. Pointer hover may still be present.", renderer.Text());
            await renderer.Event("onpointerdown", new PointerEventArgs(), "id", "catalog-state-button");
            Assert.Contains("Pointer pressed. This is not a persistent selection.", renderer.Text());
            await renderer.Event("onpointerup", new PointerEventArgs(), "id", "catalog-state-button");
            Assert.Contains("Pointer released. Focus and hover may still be present.", renderer.Text());
            await renderer.Click("Try button states");
            Assert.Contains("Activated: example only", renderer.Text());
            Assert.True(renderer.HasAttribute("disabled", true));
            Assert.True(renderer.HasAttribute("aria-busy", "true"));
        });
        Assert.Empty(renderer.Errors);
    }

    private static ServiceProvider Services() => new ServiceCollection().AddLogging().AddMudServices()
        .AddSingleton<IJSRuntime, NoOpJS>().AddSingleton<NavigationManager, TestNavigation>().BuildServiceProvider();

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/development/ui-catalog");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoOpJS : IJSRuntime, IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(identifier == "import" ? (TValue)(object)this : default!);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class FormRenderer : Renderer
    {
        internal FormRenderer(IServiceProvider services, ILoggerFactory logger) : base(services, logger) =>
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        private int _root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal List<Exception> Errors { get; } = [];
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        internal Task Mount(Type? type = null, Dictionary<string, object?>? parameters = null)
        {
            _root = AssignRootComponentId(InstantiateComponent(type ?? typeof(CatalogForm)));
            return RenderRootComponentAsync(_root, parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters));
        }
        internal IEnumerable<T> Components<T>() => Frames(_root).Where(frame => frame.FrameType == RenderTreeFrameType.Component)
            .Select(frame => frame.Component).OfType<T>();
        internal bool HasAttribute(string name, object value) => Frames(_root).Any(frame =>
            frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name && Equals(frame.AttributeValue, value));
        internal bool ElementHasAttribute(string matchName, string matchValue, string name, object value) =>
            Attributes().Any(attributes => attributes.Any(frame => frame.AttributeName == matchName && Equals(frame.AttributeValue, matchValue)) &&
                attributes.Any(frame => frame.AttributeName == name && Equals(frame.AttributeValue, value)));
        private IEnumerable<RenderTreeFrame[]> Attributes()
        {
            var frames = Frames(_root).ToArray();
            return Enumerable.Range(0, frames.Length).Where(i => frames[i].FrameType == RenderTreeFrameType.Element)
                .Select(i => frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray());
        }
        internal Task Event(string name, EventArgs args, string? matchName = null, string? matchValue = null)
        {
            var attributes = Attributes().Single(attributes => attributes.Any(frame => frame.AttributeName == name && frame.AttributeEventHandlerId != 0) &&
                (matchName is null || attributes.Any(frame => frame.AttributeName == matchName && Equals(frame.AttributeValue, matchValue))));
            var handler = attributes.Single(frame => frame.AttributeName == name);
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, args);
        }
        internal Task Click(string text)
        {
            var button = Frames(_root).Where(frame => frame.FrameType == RenderTreeFrameType.Component && frame.Component is MudButton)
                .Single(frame => Text(frame.ComponentId).Trim() == text);
            return ((MudButton)button.Component).OnClick.InvokeAsync(new MouseEventArgs());
        }
        internal MudTextField<string> NameField() => Frames(_root).Where(frame => frame.FrameType == RenderTreeFrameType.Component)
            .Select(frame => frame.Component).OfType<MudTextField<string>>().Single();
        internal string Text() => Text(_root);
        private string Text(int id) => string.Join("", Frames(id).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => ""
        }));
        internal Task Submit()
        {
            var handler = Frames(_root).Single(frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                frame.AttributeName == "onsubmit" && frame.AttributeEventHandlerId != 0);
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, EventArgs.Empty);
        }
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
