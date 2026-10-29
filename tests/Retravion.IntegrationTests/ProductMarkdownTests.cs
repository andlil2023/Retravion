using System.Net;
using System.Text.RegularExpressions;
using Retravion.Web.Features.Product.Description;
using Xunit;

namespace Retravion.IntegrationTests;

public sealed class ProductMarkdownTests
{
    [Fact]
    public void CommonMarkFormattingAndApprovedLinksRender()
    {
        var html = ProductMarkdown.Render("# Heading\n\n**Bold** and *emphasis* with `code`.\n\n- One\n- Two\n\n[Site](https://example.com) [Mail](mailto:team@example.com) [Local](/products) [Section](#heading)");
        Assert.Contains("<h1>Heading</h1>", html);
        Assert.Contains("<strong>Bold</strong>", html);
        Assert.Contains("<em>emphasis</em>", html);
        Assert.Contains("<code>code</code>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("href=\"mailto:team@example.com\"", html);
        Assert.Contains("href=\"/products\"", html);
        Assert.Contains("href=\"#heading\"", html);
        Assert.Equal("", ProductMarkdown.Render(""));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<svg onload=alert(1)></svg>")]
    [InlineData("<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>")]
    public void RawHtmlCannotBecomeExecutableMarkup(string markdown)
    {
        var html = ProductMarkdown.Render(markdown);
        Assert.Contains("&lt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<svg", html);
        Assert.DoesNotContain("<iframe", html);
    }

    [Theory]
    [InlineData("[Click](javascript:alert%281%29)")]
    [InlineData("[Click](JaVaScRiPt:alert%281%29)")]
    [InlineData("[Click](javascript&#58;alert%281%29)")]
    [InlineData("[Click](jav&#x61;script:alert%281%29)")]
    [InlineData("[Click](java&#x09;script:alert%281%29)")]
    [InlineData("![Image](data:image/svg+xml;base64,PHN2Zz4=)")]
    [InlineData("[Click](vbscript:msgbox%281%29)")]
    [InlineData("[Click](file:///etc/passwd)")]
    [InlineData("[Click](//example.com)")]
    [InlineData("[Click][ref]\n\n[ref]: javascript:alert%281%29")]
    [InlineData("<javascript:alert%281%29>")]
    public void UnsafeDestinationsAreRemoved(string markdown)
    {
        var html = ProductMarkdown.Render(markdown);
        var destinations = Regex.Matches(html, "(?:href|src)=\"([^\"]*)\"");
        Assert.NotEmpty(destinations);
        foreach (Match destination in destinations)
            Assert.Equal("", WebUtility.HtmlDecode(destination.Groups[1].Value));
    }

    [Fact]
    public void LinkTitlesAndCodeCannotInjectAttributesOrElements()
    {
        var html = ProductMarkdown.Render("[Click](https://example.com \"\\\" onmouseover=\\\"alert(1)\")\n\n`<script>alert(1)</script>`");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("\" onmouseover=\"", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}
