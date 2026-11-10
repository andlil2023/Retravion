using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.TestCase;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.Application.TestCase;

public sealed record TestCaseSnapshot(Case TestCase, long Revision, int? LatestApprovedVersionNumber = null);

public sealed record TestCaseHistory(AuditActor Actor, DateTimeOffset OccurredAt, string Action, TestCaseAuditDetails Change);

public interface ITestCaseStore
{
    Task<Result<Maybe<TestCaseAuditDetails>>> Outcome(Guid productInternalId, Guid operationId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TestCaseSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<TestCaseSnapshot>>> Find(Guid internalId, Guid? versionId = null, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TestCaseSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default);
    Task<Result<IReadOnlyList<TestCaseHistory>>> History(Guid internalId, CancellationToken cancellationToken = default);
}

/// <summary>Owns transactional numbering, Product eligibility, concurrency and atomic business/audit writes.</summary>
public interface ITestCaseChangeTransaction
{
    Task<Result<TestCaseSnapshot>> Create(Guid productInternalId, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Guid? areaId = null,
        Guid? operationId = null, Func<Task<bool>>? authorized = null);
    Task<Result<TestCaseSnapshot>> Update(TestCaseSnapshot expected, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, bool changeArea = false, Guid? areaId = null,
        Guid? operationId = null, Func<Task<bool>>? authorized = null);
    Task<Result<TestCaseSnapshot>> Review(TestCaseSnapshot expected, string decision, string? comment, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<TestCaseSnapshot>> CreateSuccessor(TestCaseSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Guid? operationId = null, Func<Task<bool>>? authorized = null);
    Task<Result<TestCaseSnapshot>> Reopen(TestCaseSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<TestCaseSnapshot>> ChangeLink(TestCaseSnapshot expected, string operation, TestCaseRequirementLink target,
        TestCaseRequirementLink? replacement, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}
