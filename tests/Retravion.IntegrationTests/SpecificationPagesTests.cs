using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Retravion.Web.Components.Markdown;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using Retravion.Infrastructure.Product;
using Retravion.Web.Features.Identity.Logout;
using Retravion.Web.Features.Product;
using Retravion.Web.Features.Specification;
using Xunit;

namespace Retravion.IntegrationTests;

#pragma warning disable BL0006 // Exercise real component render trees and interactive form callbacks.
public sealed partial class SpecificationReadTests
{
    [PostgresFact]
    public async Task SpecificationPagesListAndDetailAreProductScopedAndShowReadOnlyStates()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var first = await Product(products, "A / + & ?");
        var second = await Product(products, "second");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        var draft = (await operations.Create(actor, first.InternalId, "DEV-42", "First Specification", "**Description**")).Value;
        var reviewed = (await operations.Create(actor, first.InternalId, "DEV-43", "Reviewed", "# Ready")).Value;
        Assert.True((await operations.SubmitForReview(actor, first.InternalId, reviewed.InternalId)).IsSuccess);
        var other = (await operations.Create(actor, second.InternalId, "OTHER", "Other Specification", null)).Value;
        var navigation = new PagesNavigation();
        await using var services = PageServices(scope.ServiceProvider, actor, navigation);
        await using var renderer = new PagesRenderer(services);

        var productQuery = Uri.EscapeDataString(first.Id);
        var listPath = $"/specifications?product={productQuery}";
        var draftPath = $"/specifications/details?product={productQuery}&id={draft.InternalId}";
        var editPath = $"/specifications/edit?product={productQuery}&id={draft.InternalId}";
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.Mount<SpecificationList>(listPath);
            Assert.Contains($"/products/details?id={productQuery}", renderer.Links());
            Assert.Contains($"/specifications/create?product={productQuery}", renderer.Links());
            Assert.Contains(draftPath, renderer.Links());
            Assert.Contains("DEV-42", renderer.Content());
            Assert.Contains("In Review", renderer.Content());
            Assert.DoesNotContain("OTHER", renderer.Content());

            await renderer.Mount<SpecificationDetails>(draftPath);
            Assert.Contains(first.Id, renderer.Content());
            Assert.Contains("DEV-42", renderer.Content());
            Assert.Contains("First Specification", renderer.Content());
            Assert.Contains("Draft", renderer.Content());
            Assert.Contains("<strong>Description</strong>", renderer.Content());
            Assert.Contains(editPath, renderer.Links());
            Assert.Contains(listPath, renderer.Links());

            await renderer.Mount<SpecificationDetails>($"/specifications/details?product={productQuery}&id={other.InternalId}");
            Assert.Contains("Specification not found in this Product.", renderer.Content());
            Assert.DoesNotContain("Other Specification", renderer.Content());

            await renderer.Mount<SpecificationDetails>($"/specifications/details?product={productQuery}&id=invalid");
            Assert.Contains("Specification not found in this Product.", renderer.Content());
            Assert.DoesNotContain(renderer.Links(), link => link.StartsWith("/specifications/edit", StringComparison.Ordinal));
            await renderer.Mount<SpecificationEditor>($"/specifications/edit?product={productQuery}&id=invalid");
            Assert.Contains("Specification not found in this Product.", renderer.Content());
            Assert.Empty(renderer.Elements("form"));

            await renderer.Mount<SpecificationDetails>($"/specifications/details?product={productQuery}&id={reviewed.InternalId}");
            Assert.Contains("In Review", renderer.Content());
            Assert.DoesNotContain(renderer.Links(), link => link.StartsWith("/specifications/edit", StringComparison.Ordinal));
            await renderer.Mount<SpecificationEditor>($"/specifications/edit?product={productQuery}&id={reviewed.InternalId}");
            Assert.Contains("Only Draft Specification content can be edited.", renderer.Content());
            Assert.Empty(renderer.Elements("form"));

            Assert.True((await products.Update(first, first.Retire().Value)).IsSuccess);
            await renderer.Mount<SpecificationList>(listPath);
            Assert.Contains("Product retired", renderer.Content());
            Assert.Contains(draftPath, renderer.Links());
            Assert.DoesNotContain(renderer.Links(), link => link.StartsWith("/specifications/create", StringComparison.Ordinal));
            await renderer.Mount<SpecificationDetails>(draftPath);
            Assert.Contains("Product retired", renderer.Content());
            Assert.Contains("<strong>Description</strong>", renderer.Content());
            Assert.DoesNotContain(editPath, renderer.Links());
            await renderer.Mount<SpecificationEditor>(editPath);
            Assert.Contains("Product retired", renderer.Content());
            Assert.Empty(renderer.Elements("form"));
            await renderer.Mount<SpecificationEditor>($"/specifications/create?product={productQuery}");
            Assert.Contains("Product retired", renderer.Content());
            Assert.Empty(renderer.Elements("form"));
        });
    }

    [PostgresFact]
    public async Task SpecificationPagesEditorSubmitsFieldsAndRetainsInputsOnValidationFailure()
    {
        await using var db = await Database.Create();
        var products = new PostgresProductStore(db.ConnectionString);
        var product = await Product(products, "workspace");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        Assert.True((await operations.Create(actor, product.InternalId, "TAKEN", "Existing Specification", null)).IsSuccess);
        var navigation = new PagesNavigation();
        await using var services = PageServices(scope.ServiceProvider, actor, navigation);
        await using var renderer = new PagesRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            const string createPath = "/specifications/create?product=workspace";
            await renderer.Mount<SpecificationEditor>(createPath);
            Assert.Contains("Description (optional)", renderer.Content());
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            js.Id = " taken ";
            js.Title = "A new title";
            await js.InputMarkdown("**Keep my Markdown**");
            await renderer.Submit();
            Assert.Contains("That Specification ID is already in use in this Product.", renderer.Content());
            Assert.Contains("<strong>Keep my Markdown</strong>", renderer.Content());
            Assert.EndsWith(createPath, navigation.Uri);
            Assert.Single(await CreateEvents(db));

            js.Id = " NEW ";
            await renderer.Submit();
            var created = Assert.Single((await operations.List(actor, product.InternalId)).Value, row => row.Id == "NEW");
            Assert.Equal("A new title", created.Title);
            Assert.Equal("**Keep my Markdown**", created.Description);
            var detailsPath = $"/specifications/details?product=workspace&id={created.InternalId}";
            Assert.EndsWith(detailsPath, navigation.Uri);

            var editPath = $"/specifications/edit?product=workspace&id={created.InternalId}";
            await renderer.Mount<SpecificationEditor>(editPath);
            Assert.Contains("<strong>Keep my Markdown</strong>", renderer.Content());
            Assert.Equal(new[] { "NEW", "A new title" }, renderer.Elements("input").Select(attributes => attributes.Single(frame => frame.AttributeName == "value").AttributeValue));
            js.Id = "RENAMED";
            js.Title = " ";
            await js.InputMarkdown("");
            await renderer.Submit();
            Assert.Contains("A Specification ID and title are required.", renderer.Content());
            Assert.Empty(await ContentEvents(db));
            Assert.EndsWith(editPath, navigation.Uri);

            js.Title = "Edited title";
            await renderer.Submit();
            var edited = (await operations.Find(actor, product.InternalId, created.InternalId)).Value.Value;
            Assert.Equal("RENAMED", edited.Id);
            Assert.Equal("Edited title", edited.Title);
            Assert.Equal("", edited.Description);
            Assert.EndsWith(detailsPath, navigation.Uri);
            Assert.Single(await ContentEvents(db));

            var draftToReview = (await operations.Create(actor, product.InternalId, "REVIEW", "Ready for review", "Ready")).Value;
            await renderer.Mount<SpecificationEditor>($"/specifications/edit?product=workspace&id={draftToReview.InternalId}");
            var submitted = (await operations.SubmitForReview(actor, product.InternalId, draftToReview.InternalId)).Value;
            js.Id = "REVIEW";
            js.Title = "Change after review";
            js.Markdown = "Ready";
            await renderer.Submit();
            Assert.Contains("Only Draft Specification content can be edited.", renderer.Content());
            Assert.Empty(renderer.Elements("form"));
            Assert.Equal(submitted, (await operations.Find(actor, product.InternalId, draftToReview.InternalId)).Value.Value);
            Assert.Single(await ContentEvents(db));

            await renderer.Mount<SpecificationEditor>(editPath);
            Assert.True((await products.Update(product, product.Retire().Value)).IsSuccess);
            js.Id = "RENAMED";
            js.Title = "Change after retirement";
            js.Markdown = "";
            await renderer.Submit();
            Assert.Contains("retired or no longer exists", renderer.Content());
            Assert.Empty(renderer.Elements("form"));
            Assert.EndsWith(editPath, navigation.Uri);
            Assert.Equal(edited, (await operations.Find(actor, product.InternalId, created.InternalId)).Value.Value);
            Assert.Single(await ContentEvents(db));
        });
    }

    [PostgresFact]
    public async Task SpecificationPagesEditorPreservesUntouchedTextAcrossBrowserNormalization()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        const string title = "First\nSecond", description = "First\r\n\r\nSecond";
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            foreach (var (index, field) in new[] { "none", "id", "title", "description" }.Index())
            {
                var id = $"DEV\r\n{index}";
                var draft = (await operations.Create(actor, product.InternalId, id, title, description)).Value;
                await renderer.Mount<SpecificationEditor>($"/specifications/edit?product=workspace&id={draft.InternalId}");
                // Supply the DOM value projections that the browser streams back to the component.
                js.Id = field == "id" ? "EDITED" : $"DEV{index}";
                js.Title = field == "title" ? "Edited title" : "FirstSecond";
                js.Markdown = field == "description" ? "Edited\n\nMarkdown" : "First\n\nSecond";
                await renderer.Submit();

                var saved = (await operations.Find(actor, product.InternalId, draft.InternalId)).Value.Value;
                Assert.Equal((field == "id" ? js.Id : id, field == "title" ? js.Title : title,
                    field == "description" ? js.Markdown : description), (saved.Id, saved.Title, saved.Description));
                Assert.Equal(index, (await ContentEvents(db)).Count);
            }
        });
    }

    [PostgresFact]
    public async Task SpecificationPagesStreamTextAndKeepServerValidation()
    {
        await using var db = await Database.Create();
        var product = await Product(new PostgresProductStore(db.ConnectionString), "workspace");
        await using var factory = CreateFactory(db);
        using var scope = factory.Services.CreateScope();
        var actor = await Principal(scope.ServiceProvider.GetRequiredService<SessionTicketStore>(), "User", now: CreateTime);
        var operations = scope.ServiceProvider.GetRequiredService<SpecificationOperations>();
        await using var services = PageServices(scope.ServiceProvider, actor, new PagesNavigation());
        await using var renderer = new PagesRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            const int limit = 1024 * 1024;
            await renderer.Mount<SpecificationEditor>("/specifications/create?product=workspace");
            Assert.DoesNotContain(Assert.Single(renderer.Elements("textarea")), attribute => attribute.AttributeName == "oninput");
            Assert.All(renderer.Elements("input"), input => Assert.DoesNotContain(input, attribute =>
                attribute.AttributeName is "oninput" or "onchange" or "maxlength"));
            var js = (PagesJS)services.GetRequiredService<IJSRuntime>();
            js.Id = "LONG";
            js.Title = new string('a', 40 * 1024) + " 😃";
            await js.InputMarkdown("**Preview**\n\n" + new string('a', 40 * 1024));
            Assert.Contains("<strong>Preview</strong>", renderer.Content());

            var boundary = new string('é', limit / 2);
            js.Markdown = boundary + "a";
            await renderer.Submit();
            Assert.Contains("UTF-8 Markdown", renderer.Content());
            Assert.Empty((await operations.List(actor, product.InternalId)).Value);
            js.Markdown = "Invalid\0Markdown";
            await renderer.Submit();
            Assert.Contains("without NUL characters", renderer.Content());
            Assert.Empty((await operations.List(actor, product.InternalId)).Value);

            js.InvalidMarkdown = true;
            await renderer.Submit();
            Assert.Contains("Description contains invalid Unicode", renderer.Content());
            Assert.Single(renderer.Elements("form"));
            Assert.Empty((await operations.List(actor, product.InternalId)).Value);

            js.InvalidMarkdown = false;
            js.Markdown = "Keep me 😃";
            foreach (var (field, label) in new[] { ("id", "Specification ID"), ("title", "Title") })
            {
                js.InvalidHeadingField = field;
                await renderer.Submit();
                Assert.Contains($"{label} contains invalid Unicode", renderer.Content());
                Assert.Single(renderer.Elements("form"));
                Assert.Empty((await operations.List(actor, product.InternalId)).Value);
            }

            // Over-limit ID reaches the normal storage validation rather than blocking transport.
            js.InvalidHeadingField = null;
            var oversizedId = new byte[20 * 1024];
            new Random(123).NextBytes(oversizedId);
            js.Id = Convert.ToHexString(oversizedId);
            await renderer.Submit();
            Assert.Contains("The Specification ID is too large to store.", renderer.Content());
            Assert.Empty((await operations.List(actor, product.InternalId)).Value);

            // Correct only the ID and submit the latest Markdown, even before its debounced preview.
            js.Id = "LONG";
            js.Markdown = boundary;
            await renderer.Submit();
            Assert.Contains(limit, js.OpenedWithLimits);
            Assert.Contains(long.MaxValue, js.OpenedWithLimits);
            var created = Assert.Single((await operations.List(actor, product.InternalId)).Value);
            Assert.Equal(js.Id, created.Id);
            Assert.Equal(js.Title, created.Title);
            Assert.Equal(boundary, created.Description);
            Assert.Single(await CreateEvents(db));
        });
    }

    private static ServiceProvider PageServices(IServiceProvider operations, ClaimsPrincipal actor, PagesNavigation navigation) =>
        new ServiceCollection().AddLogging().AddMudServices()
            .AddSingleton(operations.GetRequiredService<SpecificationOperations>())
            .AddSingleton(operations.GetRequiredService<ProductOperations>())
            .AddSingleton<AuthenticationStateProvider>(new PagesAuthentication(actor))
            .AddSingleton<NavigationManager>(navigation)
            .AddSupplyValueFromQueryProvider()
            .AddSingleton<IJSRuntime, PagesJS>().BuildServiceProvider();

    private sealed class PagesAuthentication(ClaimsPrincipal actor) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(actor));
    }

    private sealed class PagesNavigation : NavigationManager
    {
        public PagesNavigation() => Initialize("https://localhost/", "https://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
            NotifyLocationChanged(false);
        }
    }

    private sealed class PagesJS : IJSRuntime, IJSObjectReference
    {
        private DescriptionEditor? _editor;
        internal string Markdown { get; set; } = "";
        internal string Id { get; set; } = "";
        internal string Title { get; set; } = "";
        internal bool InvalidMarkdown { get; set; }
        internal string? InvalidHeadingField { get; set; }
        internal HashSet<long> OpenedWithLimits { get; } = [];

        internal Task InputMarkdown(string markdown)
        {
            Markdown = markdown;
            return _editor!.RefreshMarkdownPreview();
        }

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((T)(object)this);
            if (identifier == "initializeMarkdownInput") _editor = ((DotNetObjectReference<DescriptionEditor>)args![2]!).Value;
            if (identifier == "readMarkdownInput")
            {
                var result = InvalidMarkdown ? """{"invalidUnicode":true,"content":null}"""
                    : Markdown.Length == 0 ? """{"content":null}""" : """{"content":{}}""";
                return StreamResult<T>(result, Encoding.UTF8.GetBytes(Markdown));
            }
            if (identifier == "streamSpecificationHeading")
            {
                var result = InvalidHeadingField is null ? """{"content":{}}"""
                    : JsonSerializer.Serialize(new { invalidField = InvalidHeadingField });
                return StreamResult<T>(result, JsonSerializer.SerializeToUtf8Bytes(new { id = Id, title = Title }));
            }
            return ValueTask.FromResult(default(T)!);
        }

        private ValueTask<T> StreamResult<T>(string result, byte[] bytes)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new StreamConverter(new TestStream(this, bytes)));
            return ValueTask.FromResult(JsonSerializer.Deserialize<T>(result, options)!);
        }

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class StreamConverter(IJSStreamReference stream) : JsonConverter<IJSStreamReference>
        {
            public override IJSStreamReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                reader.Skip();
                return stream;
            }

            public override void Write(Utf8JsonWriter writer, IJSStreamReference value, JsonSerializerOptions options) => throw new NotSupportedException();
        }

        private sealed class TestStream(PagesJS js, byte[] bytes) : IJSStreamReference
        {
            public long Length => bytes.Length;
            public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
            {
                Assert.True(bytes.Length <= maxAllowedSize);
                js.OpenedWithLimits.Add(maxAllowedSize);
                return ValueTask.FromResult<Stream>(new MemoryStream(bytes));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class PagesRenderer : Renderer
    {
        private readonly NavigationManager _navigation;
        private int? _page, _header;

        internal PagesRenderer(IServiceProvider services) : base(services, services.GetRequiredService<ILoggerFactory>())
        {
            _navigation = services.GetRequiredService<NavigationManager>();
            ElementReferenceContext = new WebElementReferenceContext(services.GetRequiredService<IJSRuntime>());
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception);

        internal async Task Mount<T>(string path) where T : IComponent
        {
            var route = path.Split('?')[0];
            Assert.Contains(typeof(T).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>(), attribute => attribute.Template == route);
            if (_page is { } previous) RemoveRootComponent(previous);
            if (_header is null)
            {
                _header = AssignRootComponentId(InstantiateComponent(typeof(SectionOutlet)));
                await RenderRootComponentAsync(_header.Value, ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(SectionOutlet.SectionName)] = "product-page-header"
                }));
            }
            _navigation.NavigateTo(path);
            _page = AssignRootComponentId(InstantiateComponent(typeof(T)));
            await RenderRootComponentAsync(_page.Value, ParameterView.Empty);
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

        private IEnumerable<RenderTreeFrame> PageFrames() => Frames(_page!.Value).Concat(Frames(_header!.Value));
        internal string Content() => string.Concat(PageFrames().Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Markup => frame.MarkupContent,
            RenderTreeFrameType.Text => frame.TextContent,
            _ => ""
        }));
        internal string[] Links() => Elements("a").SelectMany(attributes => attributes)
            .Where(frame => frame.AttributeName == "href").Select(frame => (string)frame.AttributeValue).ToArray();

        internal RenderTreeFrame[][] Elements(string tag)
        {
            var frames = PageFrames().ToArray();
            return Enumerable.Range(0, frames.Length)
                .Where(i => frames[i].FrameType == RenderTreeFrameType.Element && frames[i].ElementName == tag)
                .Select(i => frames.Skip(i + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute).ToArray()).ToArray();
        }

        internal Task Submit() => Event(Assert.Single(Elements("form")), "onsubmit", EventArgs.Empty);
        internal Task Submit(string id) => Event(Element("form", "id", id), "onsubmit", EventArgs.Empty);
        internal Task Select(string id, string value) => Event(Element("select", "id", id), "onchange", new ChangeEventArgs { Value = value });
        internal Task SelectFile(IBrowserFile file) => Assert.Single(PageFrames().Where(frame => frame.FrameType == RenderTreeFrameType.Component)
            .Select(frame => frame.Component).OfType<InputFile>()).OnChange.InvokeAsync(new InputFileChangeEventArgs([file]));
        internal RenderTreeFrame[] Element(string tag, string attribute, string value) =>
            Assert.Single(Elements(tag), attrs => attrs.Any(frame => frame.AttributeName == attribute && Equals(frame.AttributeValue, value)));
        internal bool HasButton(string id) => Elements("button").Any(attrs => attrs.Any(frame => frame.AttributeName == "id" && Equals(frame.AttributeValue, id)));
        internal bool ButtonDisabled(string id) => Element("button", "id", id).Any(frame => frame.AttributeName == "disabled");
        internal Task Click(string id) => Event(Element("button", "id", id), "onclick", new MouseEventArgs());
        internal Task Check(string attribute, string value, bool isChecked = true) =>
            Event(Element("input", attribute, value), "onchange", new ChangeEventArgs { Value = isChecked });

        private Task Event(RenderTreeFrame[] attributes, string name, EventArgs args) =>
            DispatchEventAsync(attributes.Single(frame => frame.AttributeName == name).AttributeEventHandlerId, null, args, waitForQuiescence: true);
    }
}
#pragma warning restore BL0006
