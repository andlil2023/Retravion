using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retravion.Application.AuditTrail;
using Retravion.Web.Features.AuditTrail;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class AuditEventDetailsTests
{
    private static readonly ProductAuditValues Initial = new("historical-id", "Original name", "System", "", true);

    [Fact]
    public async Task CreationShowsEveryInitialValueAndExactMetadataInClosedNativeDisclosure()
    {
        var id = Guid.NewGuid();
        var instant = DateTimeOffset.Parse("2026-01-02T03:04:05.1234567+02:00");
        var html = await Render(new(new AuditActor.Identified("author"), instant, "product.create",
            new AuditTarget.Identified(id.ToString()), AuditActionOutcome.Success, new(id, null, Initial)));
        Assert.Contains("<details", html);
        Assert.DoesNotContain(" open", html);
        Assert.Matches("<summary[^>]*>View details</summary>", html);
        Assert.Contains("Identified: author", html);
        Assert.Contains("2026-01-02T01:04:05.1234567+00:00", WebUtility.HtmlDecode(html));
        Assert.Contains("product.create", html);
        Assert.Contains("Identified: historical-id", html);
        Assert.Contains("Success", html);
        Assert.DoesNotContain(id.ToString(), html);
        Assert.Contains("Initial Product values", html);
        foreach (var field in new[] { "Product ID", "Name", "Kind", "Description", "Active" })
            Assert.Contains($"aria-label=\"{field}\"", html);
        Assert.Contains("Original name", html);
        Assert.Contains("System", html);
        Assert.Contains("True", html);
        Assert.Contains("(empty)", html);
        Assert.DoesNotContain("Old value", html);
        Assert.DoesNotContain("No recorded field changes", html);
    }

    [Theory]
    [InlineData("product.edit")]
    [InlineData("product.retire")]
    [InlineData("product.reactivate")]
    public async Task ChangesShowOnlyDifferentFieldsWithExactEncodedOldAndNewValues(string action)
    {
        var oldText = "# Old\n<script>alert('old')</script>";
        var newText = "# New\n[link](javascript:alert(1))\n<img src=x onerror=alert(1)>\n" + new string('x', 20000) + "THE END";
        var before = Initial with { Description = oldText };
        var after = Initial with { Description = newText, IsActive = false };
        var html = await Render(Event(new(Guid.NewGuid(), before, after), action));
        Assert.Contains("Changed Product fields", html);
        Assert.Contains("Old value", html);
        Assert.Contains("New value", html);
        Assert.Contains(oldText, WebUtility.HtmlDecode(html));
        Assert.Contains(newText, WebUtility.HtmlDecode(html));
        Assert.Contains("aria-label=\"Description\"", html);
        Assert.Contains("aria-label=\"Active\"", html);
        Assert.Contains("True", html);
        Assert.Contains("False", html);
        foreach (var field in new[] { "Product ID", "Name", "Kind" })
            Assert.DoesNotContain($"aria-label=\"{field}\"", html);
        Assert.DoesNotContain("Original name", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<a ", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrIdenticalSnapshotsRemainViewable(bool hasSnapshots)
    {
        var information = new AuditInformation(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch, "product.edit",
            new AuditTarget.Unknown(), AuditActionOutcome.Failure, hasSnapshots ? new(Guid.NewGuid(), Initial, Initial) : null);
        var html = await Render(information);
        Assert.Matches("<summary[^>]*>View details</summary>", html);
        Assert.Contains("No recorded field changes.", html);
        Assert.Contains("Unknown", html);
        Assert.Contains("Failure", html);
        Assert.DoesNotContain("Old value", html);
        Assert.DoesNotContain("Initial value", html);
    }

    [Fact]
    public async Task EmptyNewValueAndUntrustedMetadataAreNotLostOrRenderedAsHtml()
    {
        var html = await Render(new(new AuditActor.Identified("<script>actor</script>"), DateTimeOffset.UnixEpoch,
            "<b>action</b>", new AuditTarget.Unknown(), AuditActionOutcome.Success,
            new(Guid.NewGuid(), Initial with { Description = "before" }, Initial)));
        Assert.Contains("before", html);
        Assert.Contains("(empty)", html);
        Assert.Contains("&lt;script&gt;actor&lt;/script&gt;", html);
        Assert.Contains("&lt;b&gt;action&lt;/b&gt;", html);
        Assert.DoesNotContain("<script", html);
        html = await Render(new(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch, "legacy",
            new AuditTarget.NotApplicable(), AuditActionOutcome.Success));
        Assert.Contains("Not applicable", html);
        html = await Render(new(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch, "legacy",
            new AuditTarget.Identified("<img src=x>"), AuditActionOutcome.Success));
        Assert.Contains("Identified: &lt;img src=x&gt;", html);
        Assert.DoesNotContain("<img", html);
    }

    private static AuditInformation Event(ProductAuditDetails? details, string action = "product.edit") =>
        new(new AuditActor.Unknown(), DateTimeOffset.UnixEpoch, action, new AuditTarget.Unknown(), AuditActionOutcome.Success, details);

    private static async Task<string> Render(AuditInformation information)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<AuditEventDetails>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(AuditEventDetails.Event)] = information }));
            return result.ToHtmlString();
        });
    }
}
