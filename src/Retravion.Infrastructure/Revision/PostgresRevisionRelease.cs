using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;

namespace Retravion.Infrastructure.Revision;

public sealed partial class PostgresRevisionStore
{
    public Task<Result<RevisionSnapshot>> Release(Guid operation, RevisionSnapshot expected, string baselineToken,
        AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) =>
        Write(operation, actor, authorized, async (c, t) =>
        {
            if (expected?.Value is not DraftRevision draft || expected.Deleted || expected.Revision <= 0) return Stale();
            if (!await ProductActive(c, t, draft.ProductInternalId, token)) return Inactive();
            var found = await Find(c, t, draft.ProductInternalId, draft.InternalId, token);
            if (found.IsFailure) return Result.Failure<RevisionSnapshot>(found.Error);
            if (found.Value.HasNoValue || found.Value.Value.Deleted || found.Value.Value.Revision != expected.Revision ||
                found.Value.Value.Value is not DraftRevision current || !current.SameValues(draft)) return Stale();
            var baseline = await Resolve(c, t, found.Value.Value, token);
            if (baseline.IsFailure) return Result.Failure<RevisionSnapshot>(baseline.Error);
            if (string.IsNullOrEmpty(baselineToken) || baseline.Value.Token != baselineToken)
                return Result.Failure<RevisionSnapshot>("The baseline changed. Retrieve it and confirm release again.");
            if (baseline.Value.Members.Count == 0 || baseline.Value.Members.Any(m => m.Status != "Approved"))
                return Result.Failure<RevisionSnapshot>("Release requires nonempty selections and an entirely Approved outgoing baseline.");
            var released = ReleasedRevision.Restore(current, ((AuditActor.Identified)actor).Identifier, time,
                baseline.Value.Members.Select(m => new ReleasedRevisionMember(m.Endpoint, m.Identifier, m.Title, m.Explicit)));
            if (released.IsFailure) return Result.Failure<RevisionSnapshot>(released.Error);
            // Each member is protected in the same transaction as frozen evidence/history/audit. All
            // participating entity mutations take their existing locks before checking protection.
            foreach (var member in released.Value.Members)
            {
                await using var protect = new NpgsqlCommand($"UPDATE {Table(member.Endpoint.Kind)} SET release_protected=true WHERE internal_id=$1 AND version_id=$2", c, t);
                protect.Parameters.AddWithValue(member.Endpoint.InternalId); protect.Parameters.AddWithValue(member.Endpoint.VersionId);
                if (await protect.ExecuteNonQueryAsync(token) != 1)
                    return Result.Failure<RevisionSnapshot>("The complete baseline could not be protected.");
            }
            await using var update = new NpgsqlCommand("UPDATE retravion_revision.revision SET state='Released',release_evidence=$1,revision=revision+1 WHERE internal_id=$2 RETURNING revision", c, t);
            update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new RevisionReleaseEvidence(released.Value.ReleasedBy, released.Value.ReleasedAt, released.Value.Members)));
            update.Parameters.AddWithValue(current.InternalId);
            var revision = (long)(await update.ExecuteScalarAsync(token))!;
            await Evidence(c, t, operation, actor, time, "revision.release", current, released.Value, false, token);
            return new RevisionSnapshot(released.Value, revision);
        }, token);
}
