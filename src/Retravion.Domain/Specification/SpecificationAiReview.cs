using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public sealed record SpecificationAiReview
{
    private const int MaximumBytes = 1024 * 1024;
    private const string InvalidSize = "A Specification AI review must be at most 1 MB of UTF-8 Markdown.";
    private const string InvalidText = "A Specification AI review must contain valid UTF-8 text without NUL characters.";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private SpecificationAiReview(string markdown, bool isOutdated) => (Markdown, IsOutdated) = (markdown, isOutdated);

    public string Markdown { get; }
    public bool IsOutdated { get; }

    public static Result<SpecificationAiReview> Submit(string? markdown, bool userApproved) => userApproved
        ? Restore(markdown, false)
        : Result.Failure<SpecificationAiReview>("Explicit user approval is required to submit a Specification AI review.");

    public static Result<SpecificationAiReview> SubmitUtf8(ReadOnlySpan<byte> content, bool userApproved)
    {
        if (!userApproved) return Submit(null, false);
        if (content.Length > MaximumBytes) return Result.Failure<SpecificationAiReview>(InvalidSize);
        if (content.StartsWith("\uFEFF"u8)) content = content[3..];
        try { return Submit(Utf8.GetString(content), true); }
        catch (DecoderFallbackException) { return Result.Failure<SpecificationAiReview>(InvalidText); }
    }

    public static Result<SpecificationAiReview> Restore(string? markdown, bool isOutdated)
    {
        if (markdown is null || markdown.Contains('\0')) return Result.Failure<SpecificationAiReview>(InvalidText);
        try
        {
            if (markdown.Length > MaximumBytes || Utf8.GetByteCount(markdown) > MaximumBytes)
                return Result.Failure<SpecificationAiReview>(InvalidSize);
        }
        catch (EncoderFallbackException) { return Result.Failure<SpecificationAiReview>(InvalidText); }
        if (string.IsNullOrWhiteSpace(markdown)) return Result.Failure<SpecificationAiReview>("Enter Markdown for the Specification AI review.");
        return Result.Success(new SpecificationAiReview(markdown, isOutdated));
    }

    public SpecificationAiReview MarkOutdated() => IsOutdated ? this : new(Markdown, true);
}
