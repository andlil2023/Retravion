using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Retravion.Web.Features.Product.Description;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise real interactive callbacks using the existing framework renderer convention.
public sealed class ProductDescriptionEditorTests
{
    [Fact]
    public async Task PreviewUpdatesOnInputInBothArrangementsAndSubmitSavesCurrentMarkdown()
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, NoOpJS>().AddSingleton<NavigationManager, TestNavigation>().BuildServiceProvider();
        await using var renderer = new EditorRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var saved = new List<string>();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(DescriptionEditor.InitialDescription)] = "Initial *description*",
                [nameof(DescriptionEditor.OnSave)] = EventCallback.Factory.Create<string>(saved, (string value) => saved.Add(value))
            };
            await renderer.Mount(parameters);
            Assert.Contains("<em>description</em>", renderer.Markup());
            renderer.AssertLayout("side-by-side");
            const string first = "# Heading\n\n**Formatted**";
            await renderer.Event("textarea", "oninput", new ChangeEventArgs { Value = first });
            Assert.Contains("<h1>Heading</h1>", renderer.Markup());
            Assert.Contains("<strong>Formatted</strong>", renderer.Markup());
            Assert.Empty(saved); // No blur or save is needed for live preview.
            await renderer.Event("button", "onclick", new MouseEventArgs(), "Stacked");
            renderer.AssertLayout("stacked");
            Assert.Contains("<h1>Heading</h1>", renderer.Markup());
            await renderer.Event("form", "onsubmit", EventArgs.Empty);
            Assert.Equal(new[] { first }, saved);
            const string second = "## Updated\n\n*Again*\n\n<script>alert(1)</script>";
            await renderer.Event("textarea", "oninput", new ChangeEventArgs { Value = second });
            Assert.Contains("<h2>Updated</h2>", renderer.Markup());
            Assert.Contains("<em>Again</em>", renderer.Markup());
            Assert.DoesNotContain("<script>", renderer.Markup());
            Assert.DoesNotContain("<h1>Heading</h1>", renderer.Markup());
            await renderer.Event("button", "onclick", new MouseEventArgs(), "Side by side");
            renderer.AssertLayout("side-by-side");
            await renderer.Event("form", "onsubmit", EventArgs.Empty);
            Assert.Equal(new[] { first, second }, saved);
            parameters[nameof(DescriptionEditor.Busy)] = true;
            await renderer.Render(parameters);
            await renderer.Event("form", "onsubmit", EventArgs.Empty);
            Assert.Equal(2, saved.Count);
        });
        Assert.Empty(renderer.Errors);
    }

    [Fact]
    public async Task FullscreenRetainsEditorDraftArrangementSaveErrorsAndBusyGuards()
    {
        var js = new RecordingJS();
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime>(js).AddSingleton<NavigationManager, TestNavigation>().BuildServiceProvider();
        await using var renderer = new EditorRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var saved = new List<string>();
        var failed = false;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = new Dictionary<string, object?>
            {
                [nameof(DescriptionEditor.InitialDescription)] = "Original",
                [nameof(DescriptionEditor.CancelHref)] = "/products/details?id=example",
                [nameof(DescriptionEditor.ChildContent)] = (RenderFragment)(builder =>
                {
                    if (failed) builder.AddMarkupContent(0, "<p role=\"alert\">Save failed; draft retained.</p>");
                }),
                [nameof(DescriptionEditor.OnSave)] = EventCallback.Factory.Create<string>(saved, (string value) =>
                {
                    saved.Add(value);
                    failed = true; // Parent failure feedback uses the existing ChildContent contract.
                })
            };
            await renderer.Mount(parameters);
            renderer.AssertToolbarOrder();
            Assert.Single(js.Calls, call => call.Name == "initializeSplitter");
            Assert.Contains("role=\"separator\"", renderer.Markup());
            Assert.Contains("aria-orientation=\"vertical\"", renderer.Markup());
            Assert.Contains("aria-valuemin=\"25\"", renderer.Markup());
            Assert.Contains("aria-valuemax=\"75\"", renderer.Markup());
            Assert.Contains("aria-valuenow=\"50\"", renderer.Markup());
            const string draft = "## Unsaved **draft**";
            await renderer.Event("textarea", "oninput", new ChangeEventArgs { Value = draft });
            await renderer.Event("button", "onclick", new MouseEventArgs(), "Stacked");
            await renderer.Event("button", "onclick", new MouseEventArgs(), "Fullscreen Markdown editor");
            renderer.AssertAttribute("aria-modal", "true");
            renderer.AssertLayout("stacked");
            Assert.Contains("<strong>draft</strong>", renderer.Markup());
            Assert.Contains(js.Calls, call => call.Name == "setFullscreen" && Equals(call.Args![1], true));
            await renderer.Event("form", "onsubmit", EventArgs.Empty);
            Assert.Equal(new[] { draft }, saved);
            Assert.Contains("Save failed; draft retained.", renderer.Markup());
            renderer.AssertAttribute("aria-modal", "true");
            parameters[nameof(DescriptionEditor.Busy)] = true;
            await renderer.Render(parameters);
            await renderer.Event("form", "onsubmit", EventArgs.Empty);
            Assert.Single(saved);
            renderer.AssertAttribute("disabled", true);
            // Escape can exit even while saving; it changes presentation, not business state.
            await renderer.Event("dialog", "oncancel", EventArgs.Empty);
            renderer.AssertAttribute("role", "group");
            Assert.Contains(js.Calls, call => call.Name == "setFullscreen" && Equals(call.Args![1], false));
            renderer.AssertLayout("stacked");
            parameters[nameof(DescriptionEditor.Busy)] = false;
            await renderer.Render(parameters);
            await renderer.Event("button", "onclick", new MouseEventArgs(), "Fullscreen Markdown editor");
            await renderer.Event("button", "onclick", new MouseEventArgs(), "Exit fullscreen Markdown editor");
            renderer.AssertAttribute("role", "group");
            renderer.AssertAttribute("href", "/products/details?id=example");
            await renderer.Event("form", "onsubmit", EventArgs.Empty);
            Assert.Equal(new[] { draft, draft }, saved);
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class RecordingJS : IJSRuntime, IJSObjectReference
    {
        internal List<(string Name, object?[]? Args)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add((identifier, args));
            return ValueTask.FromResult(identifier == "import" ? (TValue)(object)this : default!);
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/products/description?id=id");
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

    private sealed class EditorRenderer : Renderer
    {
        internal EditorRenderer(IServiceProvider services, ILoggerFactory logger) : base(services, logger) =>
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        private int _root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal List<Exception> Errors { get; } = [];
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        internal async Task Mount(Dictionary<string, object?> parameters)
        {
            _root = AssignRootComponentId(InstantiateComponent(typeof(DescriptionEditor)));
            await Render(parameters);
        }
        internal Task Render(Dictionary<string, object?> parameters) => RenderRootComponentAsync(_root, ParameterView.FromDictionary(parameters));
        internal string Markup() => string.Join("", Frames(_root).Where(frame => frame.FrameType == RenderTreeFrameType.Markup).Select(frame => frame.MarkupContent));
        internal void AssertAttribute(string name, object value) => Assert.Contains(Frames(_root), frame =>
            frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == name && Equals(frame.AttributeValue, value));
        internal void AssertToolbarOrder()
        {
            var frames = Frames(_root).ToArray();
            var labels = Enumerable.Range(0, frames.Length)
                .Where(i => frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == "button")
                .SelectMany(i => frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute))
                .Where(frame => frame.AttributeName == "aria-label")
                .Select(frame => frame.AttributeValue as string)
                .Where(label => label is "Side by side" or "Stacked" or "Fullscreen Markdown editor");
            Assert.Equal(new[] { "Side by side", "Stacked", "Fullscreen Markdown editor" }, labels);
        }
        internal void AssertLayout(string arrangement) => Assert.Contains(Frames(_root), frame => frame.FrameType == RenderTreeFrameType.Attribute &&
            frame.AttributeName == "class" && Equals(frame.AttributeValue, "description-layout " + arrangement));
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
        internal Task Event(string element, string eventName, EventArgs args, string? label = null)
        {
            var frames = Frames(_root).ToArray();
            var attributes = Enumerable.Range(0, frames.Length)
                .Where(i => frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == element)
                .Select(i => frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray())
                .Single(attributes => label is null || attributes.Any(frame => frame.AttributeName == "aria-label" && Equals(frame.AttributeValue, label)));
            var handler = attributes.Single(frame => frame.AttributeName == eventName && frame.AttributeEventHandlerId != 0);
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, args);
        }
    }
}
#pragma warning restore BL0006
