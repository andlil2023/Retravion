using System.Collections.Immutable;
using CSharpFunctionalExtensions;

namespace Retravion.Domain.Revision;

public sealed record ReleasedRevisionMember(RevisionSelection Endpoint, string Identifier, string Title, bool Explicit);

public sealed record ReleasedRevision : Revision
{
    public override string State => "Released";
    public string ReleasedBy { get; }
    public DateTimeOffset ReleasedAt { get; }
    public ImmutableArray<ReleasedRevisionMember> Members { get; }

    private ReleasedRevision(DraftRevision draft, string actor, DateTimeOffset time, ImmutableArray<ReleasedRevisionMember> members)
        : base(draft.InternalId, draft.ProductInternalId, draft.Identifier, draft.Description, draft.Selections) =>
        (ReleasedBy, ReleasedAt, Members) = (actor, time.ToUniversalTime(), members);

    // All-Approved validation and exact closure resolution are performed under the transaction's entity locks.
    // Construction validates the frozen evidence's internal invariants, including exact root membership.
    public static Result<ReleasedRevision> Restore(DraftRevision draft, string? actor, DateTimeOffset time,
        IEnumerable<ReleasedRevisionMember>? members)
    {
        var values = members?.ToImmutableArray() ?? [];
        if (draft is null || draft.Selections.IsEmpty || string.IsNullOrWhiteSpace(actor) || time == default || values.IsEmpty ||
            values.Any(m => m is null || m.Endpoint is null || m.Endpoint.Validate().IsFailure ||
                string.IsNullOrWhiteSpace(m.Identifier) || m.Title is null || m.Explicit != draft.Selections.Contains(m.Endpoint)) ||
            values.DistinctBy(m => (m.Endpoint.Kind, m.Endpoint.VersionId)).Count() != values.Length ||
            draft.Selections.Any(root => !values.Any(m => m.Endpoint == root)))
            return Result.Failure<ReleasedRevision>("Invalid frozen release evidence.");
        return new ReleasedRevision(draft, actor, time, values);
    }
}
