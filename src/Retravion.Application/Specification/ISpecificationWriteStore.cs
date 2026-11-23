using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Specification;

namespace Retravion.Application.Specification;

public interface ISpecificationWriteStore
{
    /// <summary>Creates a Draft and its initial-value audit event together, rechecking live write access before commit.</summary>
    Task<Result<DraftSpecification>> Create(DraftSpecification draft, AuditActor.Identified actor, DateTimeOffset occurredAt,
        Func<Task<bool>> authorized, CancellationToken cancellationToken = default);
}
