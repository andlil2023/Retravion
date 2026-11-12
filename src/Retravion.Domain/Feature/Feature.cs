using System.Collections.Immutable;
using System.Globalization;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Feature;

public sealed record FeatureContent(string Title, string Description)
{
    public static FeatureContent Create(string? title, string? description) => new(title ?? "", description ?? "");
    public Result ValidateForReview() => string.IsNullOrWhiteSpace(Title)
        ? Result.Failure("A title is required for review.") : Result.Success();
}

public sealed record FeatureRequirementLink(Guid RequirementInternalId, Guid RequirementVersionId);

public abstract record Feature
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public Guid VersionId { get; }
    public long Number { get; }
    public string Id => "F-" + Number.ToString("D3", CultureInfo.InvariantCulture);
    public int VersionNumber { get; }
    public string? CreationReason { get; }
    public Guid? SourceVersionId { get; }
    public FeatureContent Content { get; }
    public ImmutableArray<FeatureRequirementLink> Links { get; }
    public abstract string Status { get; }

    private protected Feature(Guid id, Guid product, Guid version, long number, int versionNumber,
        string? reason, Guid? source, FeatureContent content, ImmutableArray<FeatureRequirementLink> links) =>
        (InternalId, ProductInternalId, VersionId, Number, VersionNumber, CreationReason, SourceVersionId, Content, Links) =
        (id, product, version, number, versionNumber, reason, source, content, links);

    public static Result<Feature> Create(Guid product, long number, FeatureContent content) =>
        Restore(Guid.NewGuid(), product, Guid.NewGuid(), number, 1, null, null, content, "Draft");

    public static Result<Feature> Restore(Guid id, Guid product, Guid version, long number, int versionNumber,
        string? reason, Guid? source, FeatureContent content, string status, IEnumerable<FeatureRequirementLink>? links = null)
    {
        var endpoints = links?.ToImmutableArray() ?? [];
        if (id == Guid.Empty || product == Guid.Empty || version == Guid.Empty || number <= 0 || versionNumber <= 0 ||
            content is null || content.Title is null || content.Description is null ||
            (versionNumber == 1 ? reason is not null || source is not null :
                string.IsNullOrWhiteSpace(reason) || source is null || source == Guid.Empty || source == version) ||
            endpoints.Any(l => l is null || l.RequirementInternalId == Guid.Empty || l.RequirementVersionId == Guid.Empty) ||
            endpoints.Select(l => l.RequirementInternalId).Distinct().Count() != endpoints.Length)
            return Result.Failure<Feature>("The Feature identity, content, version or links are invalid.");
        if (status is "InReview" or "Approved" && content.ValidateForReview().IsFailure)
            return Result.Failure<Feature>("A title is required for review.");
        return status switch
        {
            "Draft" => new Draft(id, product, version, number, versionNumber, reason, source, content, endpoints),
            "InReview" => new InReview(id, product, version, number, versionNumber, reason, source, content, endpoints),
            "Approved" => new Approved(id, product, version, number, versionNumber, reason, source, content, endpoints),
            _ => Result.Failure<Feature>("The Feature status is invalid.")
        };
    }

    private Result<Feature> Change(string status, FeatureContent content, IEnumerable<FeatureRequirementLink> links) =>
        Restore(InternalId, ProductInternalId, VersionId, Number, VersionNumber, CreationReason, SourceVersionId, content, status, links);
    public bool SameState(Feature other) => InternalId == other.InternalId && ProductInternalId == other.ProductInternalId &&
        VersionId == other.VersionId && Number == other.Number && VersionNumber == other.VersionNumber && Status == other.Status &&
        CreationReason == other.CreationReason && SourceVersionId == other.SourceVersionId && Content == other.Content && Links.SequenceEqual(other.Links);
    public virtual Result<Feature> Edit(FeatureContent content) => Result.Failure<Feature>("Only Draft Features can be edited.");
    public virtual Result<Feature> ChangeLinks(IEnumerable<FeatureRequirementLink> links) => Result.Failure<Feature>("Only Draft Features can change links.");
    public virtual Result<Feature> SubmitForReview() => Result.Failure<Feature>("Only Draft Features can be submitted for review.");
    public virtual Result<Feature> Approve() => Result.Failure<Feature>("Only Features in review can be approved.");
    public virtual Result<Feature> ReturnToDraft() => Result.Failure<Feature>("Only Features in review can be returned to Draft.");
    public virtual Result<Feature> Reopen(string? reason) => Result.Failure<Feature>("Only Approved Features can be reopened.");
    public virtual Result<Feature> CreateSuccessor(string? reason) => Result.Failure<Feature>("Only Approved Features can have a successor.");

    private sealed record Draft(Guid IdValue, Guid Product, Guid Version, long NumberValue, int VersionNo,
        string? Reason, Guid? Source, FeatureContent Value, ImmutableArray<FeatureRequirementLink> Endpoints) : Feature(IdValue, Product, Version, NumberValue, VersionNo, Reason, Source, Value, Endpoints)
    {
        public override string Status => "Draft";
        public override Result<Feature> Edit(FeatureContent content) => Change("Draft", content, Links);
        public override Result<Feature> ChangeLinks(IEnumerable<FeatureRequirementLink> links) => Change("Draft", Content, links);
        public override Result<Feature> SubmitForReview() => Change("InReview", Content, Links);
    }
    private sealed record InReview(Guid IdValue, Guid Product, Guid Version, long NumberValue, int VersionNo,
        string? Reason, Guid? Source, FeatureContent Value, ImmutableArray<FeatureRequirementLink> Endpoints) : Feature(IdValue, Product, Version, NumberValue, VersionNo, Reason, Source, Value, Endpoints)
    {
        public override string Status => "InReview";
        public override Result<Feature> Approve() => Change("Approved", Content, Links);
        public override Result<Feature> ReturnToDraft() => Change("Draft", Content, Links);
    }
    private sealed record Approved(Guid IdValue, Guid Product, Guid Version, long NumberValue, int VersionNo,
        string? Reason, Guid? Source, FeatureContent Value, ImmutableArray<FeatureRequirementLink> Endpoints) : Feature(IdValue, Product, Version, NumberValue, VersionNo, Reason, Source, Value, Endpoints)
    {
        public override string Status => "Approved";
        public override Result<Feature> Reopen(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<Feature>("A reopening reason is required.") : Change("Draft", Content, Links);
        public override Result<Feature> CreateSuccessor(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<Feature>("A change reason is required.") : VersionNumber == int.MaxValue
                ? Result.Failure<Feature>("The version number limit has been reached.")
                : Restore(InternalId, ProductInternalId, Guid.NewGuid(), Number, VersionNumber + 1, reason, VersionId, Content, "Draft", Links);
    }
}
