using System.Collections.Immutable;
using System.Globalization;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.TestCase;

public sealed record TestCaseStep(string Action, string ExpectedResult);

/// <summary>Incomplete Draft content is valid; submitted/review states are outside this slice.</summary>
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

    public bool SameContent(TestCaseContent other) => Title == other.Title && Description == other.Description &&
        Preconditions == other.Preconditions && Steps.SequenceEqual(other.Steps);
}

/// <summary>Only Draft v1 is implemented. Identity and ownership cannot be changed by content edits.</summary>
public sealed record TestCase
{
    public Guid InternalId { get; }
    public Guid ProductInternalId { get; }
    public Guid VersionId { get; }
    public long Number { get; }
    public string Id => "TC-" + Number.ToString("D3", CultureInfo.InvariantCulture);
    public int VersionNumber => 1;
    public string Status => "Draft";
    public TestCaseContent Content { get; }

    private TestCase(Guid internalId, Guid productInternalId, Guid versionId, long number, TestCaseContent content) =>
        (InternalId, ProductInternalId, VersionId, Number, Content) = (internalId, productInternalId, versionId, number, content);

    public static Result<TestCase> Create(Guid productInternalId, long number, TestCaseContent content) =>
        Restore(Guid.NewGuid(), productInternalId, Guid.NewGuid(), number, content);

    public static Result<TestCase> Restore(Guid internalId, Guid productInternalId, Guid versionId, long number, TestCaseContent content)
    {
        if (internalId == Guid.Empty || productInternalId == Guid.Empty || versionId == Guid.Empty || number <= 0 || content is null)
            return Result.Failure<TestCase>("The Test Case identity or content is invalid.");
        return new TestCase(internalId, productInternalId, versionId, number, content);
    }

    public TestCase Edit(TestCaseContent content) => content is null
        ? throw new ArgumentNullException(nameof(content))
        : new(InternalId, ProductInternalId, VersionId, Number, content);
}
