using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Revision;

namespace Retravion.Application.Revision;

/// <summary>Owns active-Product checks, exact endpoint validation, stale writes and atomic evidence.
/// Authorization is rechecked after database waits and immediately before commit.</summary>
public interface IRevisionChangeTransaction
{
    Task<Result<RevisionSnapshot>> Create(Guid operation, DraftRevision draft, AuditActor actor, DateTimeOffset time,
        Func<Task<bool>> authorized, CancellationToken token = default);
    Task<Result<RevisionSnapshot>> Edit(Guid operation, RevisionSnapshot expected, string? identifier, string? description,
        AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default);
    Task<Result<RevisionSnapshot>> Select(Guid operation, RevisionSnapshot expected, string action, RevisionSelection target,
        RevisionSelection? replacement, AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default);
    Task<Result<RevisionSnapshot>> Delete(Guid operation, RevisionSnapshot expected, AuditActor actor, DateTimeOffset time,
        Func<Task<bool>> authorized, CancellationToken token = default);
}
