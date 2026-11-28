namespace Retravion.Application.Specification;

public abstract record SpecificationAiReviewInput
{
    private SpecificationAiReviewInput() { }

    public sealed record Entered(string? Markdown) : SpecificationAiReviewInput;
    public sealed record Uploaded(ReadOnlyMemory<byte> Utf8Content) : SpecificationAiReviewInput;
}
