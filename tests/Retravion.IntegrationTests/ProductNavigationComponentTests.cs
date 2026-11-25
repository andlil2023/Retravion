using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor;
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

    [Fact]
    public async Task SpecificationPagesProductDevelopmentDisclosureLinksToEachProduct()
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
        var first = await operations.Create(actor, "A / ? # & + 😀", "First <product>", ProductKind.System, "");
        var second = await operations.Create(actor, "B", "Second product", ProductKind.System, "");
        Assert.True(first.IsSuccess); Assert.True(second.IsSuccess);
        var firstParent = "product-navigation-children-" + Uri.EscapeDataString(first.Value.Id);
        var secondParent = "product-navigation-children-" + Uri.EscapeDataString(second.Value.Id);
        var navigation = services.GetRequiredService<NavigationManager>();
        const string selectedHref = "/products/details?id=B";
        navigation.NavigateTo(selectedHref);

        await using var renderer = new ProductNavigationRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount();
            var firstGroup = renderer.FindDevelopmentGroup(firstParent, $"Development for {first.Value.Id} – {first.Value.Name}");
            var secondGroup = renderer.FindDevelopmentGroup(secondParent, $"Development for {second.Value.Id} – {second.Value.Name}");
            Assert.NotEqual(firstGroup, secondGroup);
            var links = renderer.ProductLinks();
            Assert.Equal(17, links.Length);
            Assert.Equal((selectedHref, "page"), Assert.Single(links, link => link.Current is not null));
            var location = navigation.Uri;

            void AssertState(bool firstExpanded, bool secondExpanded, bool secondParentExpanded = true)
            {
                renderer.AssertExpanded(firstGroup, firstExpanded);
                renderer.AssertExpanded(secondGroup, secondExpanded);
                renderer.AssertDevelopmentIcons(firstParent, firstGroup, firstExpanded);
                renderer.AssertDevelopmentIcons(secondParent, secondGroup, secondExpanded);
                renderer.AssertExpanded(firstParent, true);
                renderer.AssertExpanded(secondParent, secondParentExpanded);
                Assert.Equal(links, renderer.ProductLinks());
                Assert.Equal(location, navigation.Uri);
            }

            AssertState(false, false);
            await renderer.Toggle(firstGroup);
            AssertState(true, false);
            renderer.AssertSpecifications(firstGroup, first.Value.Id, first.Value.Name);
            await renderer.Toggle(secondGroup);
            AssertState(true, true);
            renderer.AssertSpecifications(secondGroup, second.Value.Id, second.Value.Name);
            await renderer.Toggle(secondParent);
            AssertState(true, true, secondParentExpanded: false);
            await renderer.Toggle(firstGroup);
            AssertState(false, true, secondParentExpanded: false);
            await renderer.Toggle(secondParent);
            AssertState(false, true);
            await renderer.Toggle(secondGroup);
            AssertState(false, false);
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
        internal (string Href, string? Current)[] ProductLinks()
        {
            var frames = RootFrames();
            return Enumerable.Range(0, frames.Length)
                .Where(index => frames[index].FrameType == RenderTreeFrameType.Element && frames[index].ElementName == "a")
                .Select(index => ElementAttributes(frames, index).ToDictionary(frame => frame.AttributeName, frame => frame.AttributeValue))
                .Select(attributes => ((string)attributes["href"], (string?)attributes.GetValueOrDefault("aria-current")))
                .ToArray();
        }

        internal string FindDevelopmentGroup(string parentId, string accessibleLabel)
        {
            var parent = GroupFrames(parentId);
            var button = Assert.Single(Enumerable.Range(0, parent.Length), index =>
                parent[index].FrameType == RenderTreeFrameType.Element && parent[index].ElementName == "button");
            Assert.Equal("/revisions?product=" + parentId["product-navigation-children-".Length..],
                parent.Take(button).Last(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "href").AttributeValue);
            var attributes = ElementAttributes(parent, button);
            Assert.Equal(accessibleLabel, Assert.Single(attributes, frame => frame.AttributeName == "aria-label").AttributeValue);
            var handler = Assert.Single(attributes, frame => frame.AttributeEventHandlerId != 0);
            Assert.Equal("onclick", handler.AttributeName);
            var groupId = (string)Assert.Single(attributes, frame => frame.AttributeName == "aria-controls").AttributeValue;
            var group = SkipWhitespace(parent, button + parent[button].ElementSubtreeLength);
            Assert.Equal(RenderTreeFrameType.Element, parent[group].FrameType);
            Assert.Equal("div", parent[group].ElementName);
            Assert.Equal(groupId, Assert.Single(ElementAttributes(parent, group), frame => frame.AttributeName == "id").AttributeValue);
            return groupId;
        }

        internal void AssertDevelopmentIcons(string parentId, string groupId, bool expanded)
        {
            var parent = GroupFrames(parentId);
            var button = Assert.Single(Enumerable.Range(0, parent.Length), index =>
                parent[index].FrameType == RenderTreeFrameType.Element && parent[index].ElementName == "button");
            var attributes = ElementAttributes(parent, button);
            Assert.Equal(groupId, Assert.Single(attributes, frame => frame.AttributeName == "aria-controls").AttributeValue);
            var icons = parent.Skip(button + 1).Take(parent[button].ElementSubtreeLength - 1)
                .Where(frame => frame.FrameType == RenderTreeFrameType.Component).ToArray();
            Assert.Collection(icons,
                indicator => AssertRenderedSvg(indicator, "product-navigation-development-indicator",
                    expanded ? Icons.Material.Outlined.ExpandMore : Icons.Material.Outlined.ChevronRight, decorative: true),
                code => AssertRenderedSvg(code, "product-navigation-development-code", Icons.Material.Outlined.Code));
        }

        private void AssertRenderedSvg(RenderTreeFrame component, string cssClass, string glyph, bool decorative = false)
        {
            Assert.Equal(typeof(MudIcon), component.ComponentType);
            var rendered = GetCurrentRenderTreeFrames(component.ComponentId);
            var frames = rendered.Array[..rendered.Count];
            var svgIndex = Assert.Single(Enumerable.Range(0, frames.Length), index =>
                frames[index].FrameType == RenderTreeFrameType.Element && frames[index].ElementName == "svg");
            var attributes = ElementAttributes(frames, svgIndex).ToDictionary(frame => frame.AttributeName, frame => frame.AttributeValue);
            Assert.Contains(cssClass, Assert.IsType<string>(attributes["class"]).Split(' '));
            Assert.Equal(glyph, Assert.Single(frames.Skip(svgIndex + 1).Take(frames[svgIndex].ElementSubtreeLength - 1),
                frame => frame.FrameType == RenderTreeFrameType.Markup).MarkupContent);
            if (decorative)
            {
                Assert.Equal("true", attributes["aria-hidden"]);
                Assert.Equal("false", attributes["focusable"]);
                Assert.False(attributes.ContainsKey("tabindex"));
            }
        }

        internal void AssertSpecifications(string groupId, string productId, string productName)
        {
            var group = GroupFrames(groupId);
            var label = SkipWhitespace(group, 1 + ElementAttributes(group, 0).Length);
            Assert.Equal(RenderTreeFrameType.Element, group[label].FrameType);
            Assert.Equal("a", group[label].ElementName);
            var labelEnd = label + group[label].ElementSubtreeLength;
            Assert.Equal(group.Length, SkipWhitespace(group, labelEnd));
            var attributes = ElementAttributes(group, label);
            Assert.Equal($"/specifications?product={Uri.EscapeDataString(productId)}", Assert.Single(attributes, frame => frame.AttributeName == "href").AttributeValue);
            Assert.Equal($"Specifications for {productId} – {productName}", Assert.Single(attributes, frame => frame.AttributeName == "aria-label").AttributeValue);
            Assert.DoesNotContain(attributes, frame => frame.AttributeName == "aria-disabled");
            var icon = label + 1 + attributes.Length;
            Assert.Equal(RenderTreeFrameType.Component, group[icon].FrameType);
            AssertIcon(group[icon..labelEnd], Icons.Material.Outlined.Description);
            var text = Assert.Single(group[(icon + group[icon].ComponentSubtreeLength)..labelEnd]);
            Assert.Equal(RenderTreeFrameType.Markup, text.FrameType);
            Assert.Equal("<span>Specifications</span>", text.MarkupContent);
        }

        private RenderTreeFrame[] RootFrames()
        {
            var frames = GetCurrentRenderTreeFrames(_root);
            return frames.Array[..frames.Count];
        }

        private RenderTreeFrame[] GroupFrames(string groupId)
        {
            var frames = RootFrames();
            var start = Assert.Single(Enumerable.Range(0, frames.Length), index =>
                frames[index].FrameType == RenderTreeFrameType.Element && frames[index].ElementName == "div" &&
                ElementAttributes(frames, index).Any(frame => frame.AttributeName == "id" && Equals(frame.AttributeValue, groupId)));
            return frames[start..(start + frames[start].ElementSubtreeLength)];
        }

        private static int SkipWhitespace(RenderTreeFrame[] frames, int start)
        {
            while (start < frames.Length && (frames[start].FrameType switch
            {
                RenderTreeFrameType.Markup => string.IsNullOrWhiteSpace(frames[start].MarkupContent),
                RenderTreeFrameType.Text => string.IsNullOrWhiteSpace(frames[start].TextContent),
                _ => false
            })) start++;
            return start;
        }

        private static RenderTreeFrame[] ElementAttributes(RenderTreeFrame[] frames, int element) => frames
            .Skip(element + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray();

        private static void AssertIcon(IEnumerable<RenderTreeFrame> frames, string expectedIcon)
        {
            Assert.Equal(typeof(MudIcon), Assert.Single(frames, frame => frame.FrameType == RenderTreeFrameType.Component).ComponentType);
            Assert.Equal(expectedIcon, Assert.Single(frames, frame =>
                frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == nameof(MudIcon.Icon)).AttributeValue);
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
