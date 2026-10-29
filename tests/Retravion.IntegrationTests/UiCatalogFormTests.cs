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

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/development/ui-catalog");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoOpJS : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
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
        internal Task Mount()
        {
            _root = AssignRootComponentId(InstantiateComponent(typeof(CatalogForm)));
            return RenderRootComponentAsync(_root, ParameterView.Empty);
        }
        internal MudTextField<string> NameField() => Frames(_root).Where(frame => frame.FrameType == RenderTreeFrameType.Component)
            .Select(frame => frame.Component).OfType<MudTextField<string>>().Single();
        internal string Text() => string.Join("", Frames(_root).Select(frame => frame.FrameType switch
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
