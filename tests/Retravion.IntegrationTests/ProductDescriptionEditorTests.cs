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
            await renderer.Event("select", "onchange", new ChangeEventArgs { Value = "stacked" });
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
            await renderer.Event("select", "onchange", new ChangeEventArgs { Value = "side-by-side" });
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

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/products/description?id=id");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoOpJS : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
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
        internal Task Event(string element, string eventName, EventArgs args)
        {
            var handler = Frames(_root).SkipWhile(frame => frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != element)
                .Skip(1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Single(frame => frame.AttributeName == eventName && frame.AttributeEventHandlerId != 0);
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, args);
        }
    }
}
#pragma warning restore BL0006
