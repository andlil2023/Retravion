using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public sealed record SpecificationEvidence
{
    private const int MaximumReportBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private SpecificationEvidence(string markdown, string? pullRequestUrl, bool isOutdated) =>
        (Markdown, PullRequestUrl, IsOutdated) = (markdown, pullRequestUrl, isOutdated);

    public string Markdown { get; }
    public string? PullRequestUrl { get; }
    public bool IsOutdated { get; }

    public static Result<SpecificationEvidence> Prepare(string? markdown, string? pullRequestUrl) =>
        Restore(markdown, pullRequestUrl, false);

    public static Result<SpecificationEvidence> Restore(string? markdown, string? pullRequestUrl, bool isOutdated)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Result.Failure<SpecificationEvidence>("Enter Markdown for the Specification evidence report.");
        if (markdown.Contains('\0') || !TryCountUtf8Bytes(markdown, out var byteCount))
            return Result.Failure<SpecificationEvidence>("The Specification evidence report must contain valid UTF-8 text without NUL characters.");
        if (byteCount > MaximumReportBytes)
            return Result.Failure<SpecificationEvidence>("The Specification evidence report must be at most 1 MB of UTF-8 Markdown.");

        if (pullRequestUrl is not null &&
            (pullRequestUrl.Any(char.IsControl) || !TryCountUtf8Bytes(pullRequestUrl, out _) ||
             !Uri.TryCreate(pullRequestUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
             url.Host.Length == 0 || !url.IsWellFormedOriginalString()))
            return Result.Failure<SpecificationEvidence>("The PR URL must be an absolute HTTPS URL.");

        return Result.Success(new SpecificationEvidence(markdown, pullRequestUrl, isOutdated));
    }

    public SpecificationEvidence MarkOutdated() => IsOutdated ? this : new(Markdown, PullRequestUrl, true);

    public Result<SpecificationEvidence> ConfirmCurrent(bool reviewedEvidence) => reviewedEvidence
        ? Result.Success(IsOutdated ? new SpecificationEvidence(Markdown, PullRequestUrl, false) : this)
        : Result.Failure<SpecificationEvidence>("Explicitly confirm that you reviewed the retained Specification evidence.");

    private static bool TryCountUtf8Bytes(string value, out int byteCount)
    {
        try { byteCount = Utf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { byteCount = 0; return false; }
    }
}
