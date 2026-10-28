using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;
using Retravion.Web.Components;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise the actual interactive lifecycle with the existing framework renderer.
public sealed class OneTimePasswordResultTests
{
    [Theory]
    [InlineData("Account created")]
    [InlineData("Password reset")]
    public async Task ResultIsACompactModalWithReadOnlyPasswordAndExplicitDismissal(string title)
    {
        await using var services = new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton<IJSRuntime, NoOpJS>().AddSingleton<NavigationManager, TestNavigation>().BuildServiceProvider();
        await using var renderer = new DialogRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var host = await renderer.Mount(title);
            Assert.Empty(renderer.Errors);
            var frames = renderer.Frames().ToArray();
            Assert.Contains(frames, frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                frame.AttributeName == "role" && Equals(frame.AttributeValue, "dialog"));
            Assert.Contains(frames, frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                frame.AttributeName == "aria-label" && Equals(frame.AttributeValue, title));
            Assert.Contains(frames, frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                frame.AttributeName == "value" && Equals(frame.AttributeValue, Host.Secret));
            var field = Assert.Single(frames.Where(frame => frame.FrameType == RenderTreeFrameType.Component)
                .Select(frame => frame.Component).OfType<MudTextField<string>>());
            Assert.True(field.ReadOnly);
            Assert.Equal("Temporary password", field.Label);
            Assert.Equal(Variant.Outlined, field.Variant);
            var dialog = frames.Where(frame => frame.FrameType == RenderTreeFrameType.Component)
                .Select(frame => frame.Component).OfType<MudDialog>().Single(dialog => dialog.Options is not null);
            Assert.Equal(MaxWidth.Small, dialog.Options!.MaxWidth);
            Assert.True(dialog.Options.FullWidth);
            Assert.False(dialog.Options.BackdropClick);
            Assert.False(dialog.Options.CloseOnEscapeKey);
            Assert.False(dialog.Options.CloseButton);
            Assert.False(host.Dismissed);
            await renderer.Acknowledge();
            Assert.True(host.Dismissed);
            Assert.DoesNotContain(renderer.Frames(), frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                frame.AttributeName == "value" && Equals(frame.AttributeValue, Host.Secret));
            Assert.DoesNotContain(renderer.Frames(), frame => frame.FrameType == RenderTreeFrameType.Attribute &&
                frame.AttributeName == "role" && Equals(frame.AttributeValue, "dialog"));
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class Host : ComponentBase
    {
        internal const string Secret = "TemporaryPass123!";
        [Parameter] public string Title { get; set; } = "";
        internal bool Dismissed { get; private set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            if (Dismissed) return;
            builder.OpenComponent<OneTimePasswordResult>(1);
            builder.AddAttribute(2, nameof(OneTimePasswordResult.Title), Title);
            builder.AddAttribute(3, nameof(OneTimePasswordResult.Password), Secret);
            builder.AddAttribute(4, nameof(OneTimePasswordResult.OnDismiss), EventCallback.Factory.Create(this, () =>
            {
                Dismissed = true;
                StateHasChanged();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/administration/users");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class NoOpJS : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }

    private sealed class DialogRenderer : Renderer
    {
        internal DialogRenderer(IServiceProvider services, ILoggerFactory logger) : base(services, logger)
        {
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        }

        private int _root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal List<Exception> Errors { get; } = [];
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        internal async Task<Host> Mount(string title)
        {
            var host = (Host)InstantiateComponent(typeof(Host));
            _root = AssignRootComponentId(host);
            await RenderRootComponentAsync(_root, ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Host.Title)] = title }));
            return host;
        }
        internal IEnumerable<RenderTreeFrame> Frames() => Frames(_root, []);
        private IEnumerable<RenderTreeFrame> Frames(int id, HashSet<int> visited)
        {
            if (!visited.Add(id)) yield break;
            var frames = GetCurrentRenderTreeFrames(id);
            foreach (var frame in frames.Array.Take(frames.Count))
            {
                yield return frame;
                if (frame.FrameType == RenderTreeFrameType.Component && frame.Component is not null)
                    foreach (var child in Frames(frame.ComponentId, visited)) yield return child;
            }
        }
        internal Task Acknowledge()
        {
            var button = Frames().SkipWhile(frame => frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button")
                .Skip(1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Single(frame => frame.AttributeName == "onclick" && frame.AttributeEventHandlerId != 0);
            return DispatchEventAsync(button.AttributeEventHandlerId, null, new MouseEventArgs());
        }
    }
#pragma warning restore BL0006
}
