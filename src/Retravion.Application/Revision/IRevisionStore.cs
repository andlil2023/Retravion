using CSharpFunctionalExtensions;
using Retravion.Application.AuditTrail;
using Retravion.Domain.Revision;

namespace Retravion.Application.Revision;

public sealed record RevisionSnapshot(Retravion.Domain.Revision.Revision Value, long Revision, bool Deleted = false);
public sealed record RevisionHistory(AuditActor Actor, DateTimeOffset OccurredAt, string Action, RevisionAuditDetails Change);
public sealed record RevisionRetrieval(RevisionSnapshot? Record, RevisionHistory? Operation);
public sealed record RevisionBaselineMember(RevisionSelection Endpoint, string Identifier, string Title, string Status, bool Explicit);
public sealed record RevisionBaseline(RevisionSnapshot Revision, IReadOnlyList<RevisionBaselineMember> Members, string Token = "");

/// <summary>All exact-version reads and outcome retrieval coordinate with in-flight writes.</summary>
public interface IRevisionStore
{
    Task<Result<IReadOnlyList<RevisionSnapshot>>> List(Guid product, CancellationToken token = default);
    Task<Result<RevisionRetrieval>> Retrieve(Guid product, Guid id, Guid? operation = null, CancellationToken token = default);
    Task<Result<IReadOnlyList<RevisionHistory>>> History(Guid product, Guid id, CancellationToken token = default);
    Task<Result<IReadOnlyList<RevisionBaselineMember>>> Candidates(Guid product, CancellationToken token = default);
    Task<Result<RevisionBaseline>> Baseline(Guid product, Guid id, CancellationToken token = default);
}
