using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.Product;
using Retravion.Domain.Product;
using Retravion.Web.Features.Product;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006
public sealed partial class AccountManagementTests
{
    [Fact]
    public async Task ProductNodesCollapseIndependentlyAndRetainStateAcrossNavigationAndRename()
    {
        var products = new ProductStoreDouble();
        var useCircuitNavigation = false;
        await using var baseFactory = new AdministratorWebFactory(new StartupAdministratorStore(), "admin", Initial);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IProductStore>(products);
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(NavigationManager));
            services.AddScoped<NavigationManager>(provider => useCircuitNavigation
                ? new ProductCircuitNavigation()
                : (NavigationManager)(original.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)));
        }));
        using var user = Client(factory);
        await Login(user, "admin", Initial);
        await Replace(user, Initial, Replacement);
        var actor = await ProductPrincipal(user, factory.Services, "admin", Replacement, "/home");
        useCircuitNavigation = true;
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        ((IHostEnvironmentAuthenticationStateProvider)services.GetRequiredService<AuthenticationStateProvider>())
            .SetAuthenticationState(Task.FromResult(new AuthenticationState(actor)));
        var operations = services.GetRequiredService<ProductOperations>();
        var first = await operations.Create(actor, "A / ? # & + 😀", "Same name", ProductKind.System, "");
        var second = await operations.Create(actor, "B", "Same name", ProductKind.System, "");
        Assert.True(first.IsSuccess); Assert.True(second.IsSuccess);
        var firstGroup = "product-navigation-children-" + Uri.EscapeDataString(first.Value.Id);
        var secondGroup = "product-navigation-children-" + Uri.EscapeDataString(second.Value.Id);
        Assert.NotEqual(firstGroup, secondGroup);

        await using var renderer = new ProductNavigationRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount();
            Assert.False(renderer.Attributes("div", "id", "product-navigation-list").ContainsKey("hidden"));
            Assert.Equal("/products", renderer.Attributes("a", "href", "/products")["href"]);
            renderer.AssertExpanded(firstGroup, true);
            renderer.AssertExpanded(secondGroup, true);
            Assert.Equal($"Collapse {first.Value.Id} – Same name", renderer.Attributes("button", "aria-controls", firstGroup)["aria-label"]);

            await renderer.Toggle(firstGroup);
            renderer.AssertExpanded(firstGroup, false);
            renderer.AssertExpanded(secondGroup, true);
            Assert.Equal($"Expand {first.Value.Id} – Same name", renderer.Attributes("button", "aria-controls", firstGroup)["aria-label"]);
            var firstHref = "/products/details?id=" + Uri.EscapeDataString(first.Value.Id);
            Assert.Equal(firstHref, renderer.Attributes("a", "href", firstHref)["href"]);

            await renderer.Toggle(secondGroup);
            renderer.AssertExpanded(firstGroup, false);
            renderer.AssertExpanded(secondGroup, false);
            await renderer.Toggle(firstGroup);
            renderer.AssertExpanded(firstGroup, true);
            renderer.AssertExpanded(secondGroup, false);

            var renamed = await operations.Edit(actor, second.Value, second.Value.Id, "Renamed", ProductKind.System, "", false);
            Assert.True(renamed.IsSuccess);
            services.GetRequiredService<NavigationManager>().NavigateTo("/products/details?id=B");
            renderer.AssertExpanded(firstGroup, true);
            renderer.AssertExpanded(secondGroup, false);
            Assert.Equal("Expand B – Renamed", renderer.Attributes("button", "aria-controls", secondGroup)["aria-label"]);
            Assert.Equal("page", renderer.Attributes("a", "href", "/products/details?id=B")["aria-current"]);

            var third = await operations.Create(actor, "C", "New product", ProductKind.System, "");
            Assert.True(third.IsSuccess);
            services.GetRequiredService<NavigationManager>().NavigateTo("/home");
            renderer.AssertExpanded("product-navigation-children-C", true);
            renderer.AssertExpanded(secondGroup, false);
            await renderer.Toggle(secondGroup);
            renderer.AssertExpanded(secondGroup, true);
        });
        Assert.Empty(renderer.Errors);
    }

    private sealed class ProductNavigationRenderer(IServiceProvider services, ILoggerFactory logger) : Renderer(services, logger)
    {
        private int _root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        internal List<Exception> Errors { get; } = [];
        protected override void HandleException(Exception exception) => Errors.Add(exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        internal async Task Mount()
        {
            _root = AssignRootComponentId(InstantiateComponent(typeof(ProductNavigation)));
            await RenderRootComponentAsync(_root, ParameterView.Empty);
        }
        internal Dictionary<string, object> Attributes(string element, string attribute, string value)
        {
            var frames = GetCurrentRenderTreeFrames(_root);
            var matches = Enumerable.Range(0, frames.Count)
                .Where(index => frames.Array[index].FrameType == RenderTreeFrameType.Element && frames.Array[index].ElementName == element)
                .Select(index => frames.Array.Skip(index + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                    .ToDictionary(frame => frame.AttributeName, frame => frame.AttributeValue));
            return Assert.Single(matches, attributes => attributes.TryGetValue(attribute, out var actual) && actual?.ToString() == value);
        }
        internal void AssertExpanded(string group, bool expanded)
        {
            var button = Attributes("button", "aria-controls", group);
            Assert.Equal("button", button["type"]);
            Assert.Equal(expanded ? "true" : "false", button["aria-expanded"]);
            var children = Attributes("div", "id", group);
            if (expanded) Assert.False(children.ContainsKey("hidden"));
            else Assert.Equal(true, children["hidden"]);
        }
        internal Task Toggle(string group)
        {
            var frames = GetCurrentRenderTreeFrames(_root);
            var controls = Enumerable.Range(0, frames.Count).Single(index =>
                frames.Array[index].FrameType == RenderTreeFrameType.Attribute && frames.Array[index].AttributeName == "aria-controls" &&
                frames.Array[index].AttributeValue?.ToString() == group);
            var handler = frames.Array.Skip(controls + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Single(frame => frame.AttributeName == "onclick");
            return DispatchEventAsync(handler.AttributeEventHandlerId, null, new MouseEventArgs());
        }
    }
}
#pragma warning restore BL0006
