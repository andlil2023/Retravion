using Retravion.Application.AuditTrail;
using Xunit;

namespace Retravion.UnitTests;

public sealed class AuditSubmissionContractTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 10, 27, 9, 15, 0, TimeSpan.FromHours(2));

    // TC-AUDIT-001: all supplied information is retrievable for either action outcome.
    [Theory]
    [InlineData(AuditActionOutcome.Success)]
    [InlineData(AuditActionOutcome.Failure)]
    public void RepresentsAllAuditInformation(AuditActionOutcome outcome)
    {
        var information = new AuditInformation(
            new AuditActor.Identified("user-42"), OccurredAt, "requirement.revise",
            new AuditTarget.Identified("requirement-17"), outcome);

        Assert.Equal("user-42", Assert.IsType<AuditActor.Identified>(information.Actor).Identifier);
        Assert.Equal(OccurredAt, information.OccurredAt);
        Assert.Equal(OccurredAt.Offset, information.OccurredAt.Offset);
        Assert.Equal("requirement.revise", information.Action);
        Assert.Equal("requirement-17", Assert.IsType<AuditTarget.Identified>(information.Target).Identifier);
        Assert.Equal(outcome, information.Outcome);
    }

    // TC-AUDIT-002: no invented identity is needed for an unknown actor.
    [Fact]
    public void RepresentsUnknownActor()
    {
        var information = new AuditInformation(
            new AuditActor.Unknown(), OccurredAt, "access.attempt",
            new AuditTarget.NotApplicable(), AuditActionOutcome.Failure);

        Assert.IsType<AuditActor.Unknown>(information.Actor);
    }

    // TC-AUDIT-003: the three target cases survive construction and remain distinct.
    [Fact]
    public void DistinguishesIdentifiedUnknownAndNotApplicableTargets()
    {
        AuditTarget[] targets =
        [
            new AuditTarget.Identified("requirement-17"),
            new AuditTarget.Unknown(),
            new AuditTarget.NotApplicable()
        ];
        var information = targets.Select(target => new AuditInformation(
            new AuditActor.Unknown(), OccurredAt, "activity", target, AuditActionOutcome.Success)).ToArray();

        Assert.Equal("requirement-17", Assert.IsType<AuditTarget.Identified>(information[0].Target).Identifier);
        Assert.IsType<AuditTarget.Unknown>(information[1].Target);
        Assert.IsType<AuditTarget.NotApplicable>(information[2].Target);
        Assert.Equal(3, information.Select(item => item.Target).Distinct().Count());
    }

    // TC-AUDIT-004: both submission results are possible for either audited action outcome.
    [Theory]
    [InlineData(AuditActionOutcome.Success, AuditSubmissionResult.Success)]
    [InlineData(AuditActionOutcome.Success, AuditSubmissionResult.Failure)]
    [InlineData(AuditActionOutcome.Failure, AuditSubmissionResult.Success)]
    [InlineData(AuditActionOutcome.Failure, AuditSubmissionResult.Failure)]
    public async Task ReportsSubmissionResultIndependently(
        AuditActionOutcome outcome, AuditSubmissionResult submissionResult)
    {
        var information = new AuditInformation(
            new AuditActor.Unknown(), OccurredAt, "activity", new AuditTarget.Unknown(), outcome);
        var testDouble = new SubmissionDouble(submissionResult);
        IAuditSubmission submission = testDouble;
        using var cancellation = new CancellationTokenSource();

        var result = await submission.SubmitAsync(information, cancellation.Token);

        Assert.Equal(submissionResult, result);
        Assert.Same(information, testDouble.ReceivedInformation);
        Assert.Equal(cancellation.Token, testDouble.ReceivedCancellationToken);
        Assert.Equal(outcome, information.Outcome);
    }

    [Fact]
    public async Task SubmissionAllowsOmittingCancellationToken()
    {
        var testDouble = new SubmissionDouble(AuditSubmissionResult.Success);
        IAuditSubmission submission = testDouble;
        var information = new AuditInformation(
            new AuditActor.Unknown(), OccurredAt, "activity", new AuditTarget.Unknown(), AuditActionOutcome.Success);

        Assert.Equal(AuditSubmissionResult.Success, await submission.SubmitAsync(information));
        Assert.Equal(CancellationToken.None, testDouble.ReceivedCancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void RejectsMissingIdentifiedActorIdentifier(string? identifier)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new AuditActor.Identified(identifier!));

        Assert.Equal("identifier", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void RejectsMissingIdentifiedTargetIdentifier(string? identifier)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new AuditTarget.Identified(identifier!));

        Assert.Equal("identifier", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void RejectsMissingAction(string? action)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new AuditInformation(
            new AuditActor.Unknown(), OccurredAt, action!, new AuditTarget.Unknown(), AuditActionOutcome.Success));

        Assert.Equal("action", exception.ParamName);
    }

    [Fact]
    public void RejectsNullActor()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new AuditInformation(
            null!, OccurredAt, "activity", new AuditTarget.Unknown(), AuditActionOutcome.Success));

        Assert.Equal("actor", exception.ParamName);
    }

    [Fact]
    public void RejectsNullTarget()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new AuditInformation(
            new AuditActor.Unknown(), OccurredAt, "activity", null!, AuditActionOutcome.Success));

        Assert.Equal("target", exception.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void RejectsUndefinedActionOutcome(int outcome)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new AuditInformation(
            new AuditActor.Unknown(), OccurredAt, "activity", new AuditTarget.Unknown(), (AuditActionOutcome)outcome));

        Assert.Equal("outcome", exception.ParamName);
    }

    [Fact]
    public void ProductDetailsRetainInitialAndChangedValuesWithoutRequiringDetailsOnExistingEvents()
    {
        var id = Guid.NewGuid();
        var initial = new ProductAuditValues("original", "Name", "System", "# Initial 😀", true);
        var changed = new ProductAuditValues("renamed", "New name", "Firmware", "", false);
        var created = new AuditInformation(new AuditActor.Identified("author"), OccurredAt, "product.create",
            new AuditTarget.Identified(id.ToString()), AuditActionOutcome.Success, new ProductAuditDetails(id, null, initial));
        var edited = new AuditInformation(created.Actor, OccurredAt, "product.retire", created.Target,
            AuditActionOutcome.Success, new ProductAuditDetails(id, initial, changed));
        var legacy = new AuditInformation(created.Actor, OccurredAt, "login", new AuditTarget.NotApplicable(), AuditActionOutcome.Success);
        Assert.Equal(id, created.ProductChange!.InternalId);
        Assert.Null(created.ProductChange.Before);
        Assert.Equal(initial, created.ProductChange.After);
        Assert.Equal(initial, edited.ProductChange!.Before);
        Assert.Equal(changed, edited.ProductChange.After);
        Assert.Null(legacy.ProductChange);
    }

    private sealed class SubmissionDouble(AuditSubmissionResult result) : IAuditSubmission
    {
        public AuditInformation? ReceivedInformation { get; private set; }
        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task<AuditSubmissionResult> SubmitAsync(
            AuditInformation information, CancellationToken cancellationToken = default)
        {
            ReceivedInformation = information;
            ReceivedCancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }
}
