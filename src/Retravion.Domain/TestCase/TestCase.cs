using System.Collections.Immutable;
using System.Globalization;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.TestCase;

public sealed record TestCaseStep(string Action, string ExpectedResult);

public sealed record TestCaseContent
{
    public string Title { get; }
    public string Description { get; }
    public string Preconditions { get; }
    public ImmutableArray<TestCaseStep> Steps { get; }

    private TestCaseContent(string title, string description, string preconditions, ImmutableArray<TestCaseStep> steps) =>
        (Title, Description, Preconditions, Steps) = (title, description, preconditions, steps);

    public static Result<TestCaseContent> Create(string? title, string? description, string? preconditions,
        IEnumerable<TestCaseStep>? steps)
    {
        var values = steps?.ToImmutableArray() ?? [];
        if (values.Any(step => step is null || step.Action is null || step.ExpectedResult is null))
            return Result.Failure<TestCaseContent>("A step must contain action and expected-result text; empty text is allowed in Drafts.");
        return new TestCaseContent(title ?? "", description ?? "", preconditions ?? "", values);
    }

    public Result ValidateForReview() => string.IsNullOrWhiteSpace(Title) || Steps.IsEmpty ||
        Steps.Any(step => string.IsNullOrWhiteSpace(step.Action) || string.IsNullOrWhiteSpace(step.ExpectedResult))
        ? Result.Failure("Review requires a title, at least one step, and an action and expected result for every step.")
        : Result.Success();

    public bool SameContent(TestCaseContent other) => Title == other.Title && Description == other.Description &&
        Preconditions == other.Preconditions && Steps.SequenceEqual(other.Steps);
}

public abstract record TestCase
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public Guid VersionId { get; }
    public long Number { get; }
    public string Id => "TC-" + Number.ToString("D3", CultureInfo.InvariantCulture);
    public int VersionNumber { get; }
    public string? CreationReason { get; }
    public Guid? SourceVersionId { get; }
    public Guid? AreaId { get; }
    public abstract string Status { get; }
    public TestCaseContent Content { get; }
    public ImmutableArray<TestCaseRequirementLink> Links { get; }

    private protected TestCase(Guid internalId, Guid productInternalId, Guid versionId, long number,
        TestCaseContent content, ImmutableArray<TestCaseRequirementLink> links, int versionNumber, string? reason, Guid? source, Guid? areaId) =>
        (InternalId, ProductInternalId, VersionId, Number, Content, Links, VersionNumber, CreationReason, SourceVersionId, AreaId) =
        (internalId, productInternalId, versionId, number, content, links, versionNumber, reason, source, areaId);

    public static Result<TestCase> Create(Guid productInternalId, long number, TestCaseContent content, Guid? areaId = null) =>
        Restore(Guid.NewGuid(), productInternalId, Guid.NewGuid(), number, content, areaId: areaId);

    public static Result<TestCase> Restore(Guid internalId, Guid productInternalId, Guid versionId, long number,
        TestCaseContent content, string status = "Draft", int versionNumber = 1, string? creationReason = null,
        Guid? sourceVersionId = null, IEnumerable<TestCaseRequirementLink>? links = null, Guid? areaId = null)
    {
        var endpoints = links?.ToImmutableArray() ?? [];
        if (internalId == Guid.Empty || productInternalId == Guid.Empty || versionId == Guid.Empty || areaId == Guid.Empty || number <= 0 || content is null ||
            versionNumber < 1 || (versionNumber == 1 ? creationReason is not null || sourceVersionId is not null :
                string.IsNullOrWhiteSpace(creationReason) || sourceVersionId is null || sourceVersionId == Guid.Empty || sourceVersionId == versionId) ||
            endpoints.Any(link => link is null || link.RequirementInternalId == Guid.Empty || link.RequirementVersionId == Guid.Empty) ||
            endpoints.Select(link => link.RequirementInternalId).Distinct().Count() != endpoints.Length)
            return Result.Failure<TestCase>("The Test Case identity, version or links are invalid.");
        if (status is "InReview" or "Approved" && (content.ValidateForReview().IsFailure || endpoints.IsEmpty))
            return Result.Failure<TestCase>("Review requires complete content and at least one Requirement link.");
        return status switch
        {
            "Draft" => new Draft(internalId, productInternalId, versionId, number, content, endpoints, versionNumber, creationReason, sourceVersionId, areaId),
            "InReview" => new InReview(internalId, productInternalId, versionId, number, content, endpoints, versionNumber, creationReason, sourceVersionId, areaId),
            "Approved" => new Approved(internalId, productInternalId, versionId, number, content, endpoints, versionNumber, creationReason, sourceVersionId, areaId),
            _ => Result.Failure<TestCase>("The Test Case status is invalid.")
        };
    }

    private Result<TestCase> Change(string status, TestCaseContent content, IEnumerable<TestCaseRequirementLink> links) =>
        Restore(InternalId, ProductInternalId, VersionId, Number, content, status, VersionNumber, CreationReason, SourceVersionId, links, AreaId);

    public Result<TestCase> Classify(Guid? areaId) => Status != "Draft"
        ? Result.Failure<TestCase>("Only Draft Test Cases can be classified.")
        : Restore(InternalId, ProductInternalId, VersionId, Number, Content, Status, VersionNumber, CreationReason, SourceVersionId, Links, areaId);

    public virtual Result<TestCase> Edit(TestCaseContent content) => Result.Failure<TestCase>("Only Draft Test Cases can be edited.");
    public virtual Result<TestCase> ChangeLinks(IEnumerable<TestCaseRequirementLink> links) => Result.Failure<TestCase>("Only Draft Test Cases can change links.");
    public virtual Result<TestCase> SubmitForReview() => Result.Failure<TestCase>("Only Draft Test Cases can be submitted for review.");
    public virtual Result<TestCase> Approve(bool allTargetsApproved) => Result.Failure<TestCase>("Only Test Cases in review can be approved.");
    public virtual Result<TestCase> ReturnToDraft() => Result.Failure<TestCase>("Only Test Cases in review can be returned to Draft.");
    public virtual Result<TestCase> Reopen(string? reason) => Result.Failure<TestCase>("Only Approved Test Cases can reopen.");
    public virtual Result<TestCase> CreateSuccessor(string? reason) => Result.Failure<TestCase>("Only Approved Test Cases can have a successor.");

    public bool SameState(TestCase other) => InternalId == other.InternalId && ProductInternalId == other.ProductInternalId &&
        VersionId == other.VersionId && Number == other.Number && VersionNumber == other.VersionNumber && Status == other.Status &&
        CreationReason == other.CreationReason && SourceVersionId == other.SourceVersionId && AreaId == other.AreaId && Content.SameContent(other.Content) && Links.SequenceEqual(other.Links);

    private sealed record Draft : TestCase
    {
        public Draft(Guid id, Guid product, Guid version, long number, TestCaseContent content, ImmutableArray<TestCaseRequirementLink> links,
            int versionNumber, string? reason, Guid? source, Guid? areaId) : base(id, product, version, number, content, links, versionNumber, reason, source, areaId) { }
        public override string Status => "Draft";
        public override Result<TestCase> Edit(TestCaseContent content) => Change("Draft", content, Links);
        public override Result<TestCase> ChangeLinks(IEnumerable<TestCaseRequirementLink> links) => Change("Draft", Content, links);
        public override Result<TestCase> SubmitForReview() => Change("InReview", Content, Links);
    }

    private sealed record InReview : TestCase
    {
        public InReview(Guid id, Guid product, Guid version, long number, TestCaseContent content, ImmutableArray<TestCaseRequirementLink> links,
            int versionNumber, string? reason, Guid? source, Guid? areaId) : base(id, product, version, number, content, links, versionNumber, reason, source, areaId) { }
        public override string Status => "InReview";
        public override Result<TestCase> Approve(bool allTargetsApproved) => allTargetsApproved ? Change("Approved", Content, Links)
            : Result.Failure<TestCase>("Every linked Requirement version must be Approved.");
        public override Result<TestCase> ReturnToDraft() => Change("Draft", Content, Links);
    }

    private sealed record Approved : TestCase
    {
        public Approved(Guid id, Guid product, Guid version, long number, TestCaseContent content, ImmutableArray<TestCaseRequirementLink> links,
            int versionNumber, string? reason, Guid? source, Guid? areaId) : base(id, product, version, number, content, links, versionNumber, reason, source, areaId) { }
        public override string Status => "Approved";
        public override Result<TestCase> Reopen(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<TestCase>("A reopening reason is required.") : Change("Draft", Content, Links);
        public override Result<TestCase> CreateSuccessor(string? reason) => string.IsNullOrWhiteSpace(reason)
            ? Result.Failure<TestCase>("A change reason is required.") : VersionNumber == int.MaxValue
                ? Result.Failure<TestCase>("The version number limit has been reached.")
                : Restore(InternalId, ProductInternalId, Guid.NewGuid(), Number, Content, "Draft", VersionNumber + 1, reason, VersionId, Links, AreaId);
    }
}
