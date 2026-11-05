using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.TestCase;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.Application.TestCase;

public sealed record TestCaseSnapshot(Case TestCase, long Revision);

public interface ITestCaseStore
{
    Task<Result<IReadOnlyList<TestCaseSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default);
    Task<Result<Maybe<TestCaseSnapshot>>> Find(Guid internalId, Guid? versionId = null, CancellationToken cancellationToken = default);
}

/// <summary>Owns transactional numbering, Product eligibility, concurrency and atomic business/audit writes.</summary>
public interface ITestCaseChangeTransaction
{
    Task<Result<TestCaseSnapshot>> Create(Guid productInternalId, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
    Task<Result<TestCaseSnapshot>> Update(TestCaseSnapshot expected, TestCaseContent content, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default);
}
