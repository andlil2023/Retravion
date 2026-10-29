using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Retravion.Web.Features.Product.Description;

internal static class ProductMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static string Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!SafeUrl(link.Url)) link.Url = "";
        }
        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!SafeUrl(link.IsEmail ? "mailto:" + link.Url : link.Url)) link.Url = "";
        }
        return document.ToHtml(Pipeline);
    }

    private static bool SafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;
        if (url.Any(character => char.IsControl(character) || character == '\\')) return false;
        if (url.StartsWith('#')) return true;
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "https" or "http" or "mailto";
    }
}
