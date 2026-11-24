using CSharpFunctionalExtensions;

namespace Retravion.Domain.Specification;

public sealed record ImplementedSpecification : DevelopmentSpecification
{
    public SpecificationEvidence Evidence { get; }
    public string MergeAttestedBy { get; }
    public DateTimeOffset MergeAttestedAt { get; }

    private ImplementedSpecification(ApprovedSpecification approved, SpecificationEvidence evidence,
        string mergeAttestedBy, DateTimeOffset mergeAttestedAt)
        : base(approved.InternalId, approved.ProductInternalId, approved.Id, approved.Title, approved.Description, approved.Links)
    {
        Evidence = evidence;
        MergeAttestedBy = mergeAttestedBy;
        MergeAttestedAt = mergeAttestedAt.ToUniversalTime();
    }

    internal static Result<ImplementedSpecification> FromApproved(ApprovedSpecification approved, SpecificationEvidence? evidence,
        string mergeAttestedBy, DateTimeOffset mergeAttestedAt)
    {
        // SpecificationEvidence already validates Markdown and the format of any supplied URL.
        if (evidence is not { IsOutdated: false, PullRequestUrl: not null })
            return Result.Failure<ImplementedSpecification>("Current evidence with a report and an HTTPS PR URL is required to implement a Specification.");

        const string invalidActor = "A valid merge attester identity is required to implement a Specification.";
        if (string.IsNullOrWhiteSpace(mergeAttestedBy) || mergeAttestedBy.Contains('\0'))
            return Result.Failure<ImplementedSpecification>(invalidActor);
        try { _ = Utf8.GetByteCount(mergeAttestedBy); }
        catch (System.Text.EncoderFallbackException) { return Result.Failure<ImplementedSpecification>(invalidActor); }

        return Result.Success(new ImplementedSpecification(approved, evidence, mergeAttestedBy, mergeAttestedAt));
    }
}
