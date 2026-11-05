using System.Security.Cryptography;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.Revision;
using Retravion.Domain.Revision;
using Retravion.Domain.TestCase;

namespace Retravion.Infrastructure.Revision;

public sealed partial class PostgresRevisionStore
{
    private static string Table(RevisionEntityKind kind) => kind switch
    {
        RevisionEntityKind.UserNeed => "retravion_need.need_version",
        RevisionEntityKind.Requirement => "retravion_requirement.requirement_version",
        RevisionEntityKind.TestCase => "retravion_test_case.test_case_version",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task<Result<RevisionBaselineMember>> LoadEndpoint(NpgsqlConnection c, NpgsqlTransaction t,
        Guid product, RevisionSelection endpoint, CancellationToken token)
    {
        if (endpoint is null || endpoint.Validate().IsFailure) return Result.Failure<RevisionBaselineMember>("Invalid exact version endpoint.");
        var loaded = await LookupEndpoint(c, t, product, endpoint.Kind, endpoint.InternalId, endpoint.VersionId, token);
        return loaded.IsFailure ? loaded : loaded.Value.Endpoint != endpoint
            ? Result.Failure<RevisionBaselineMember>("The exact version number does not match its immutable identity.") : loaded;
    }

    private static async Task<Result<RevisionBaselineMember>> LookupEndpoint(NpgsqlConnection c, NpgsqlTransaction t,
        Guid product, RevisionEntityKind kind, Guid id, Guid version, CancellationToken token)
    {
        var table = Table(kind);
        if (!await Exists(c, t, table, token)) return Result.Failure<RevisionBaselineMember>("Exact version not available in this Product.");
        var identifier = kind == RevisionEntityKind.TestCase ? "'TC-' || lpad(number::text,greatest(3,length(number::text)),'0')" : "id";
        var status = kind == RevisionEntityKind.TestCase ? "status" : "CASE status WHEN 0 THEN 'Draft' WHEN 1 THEN 'InReview' WHEN 2 THEN 'Approved' ELSE 'Invalid' END";
        await using var command = new NpgsqlCommand($"SELECT {identifier},title,{status},version_number FROM {table} WHERE product_internal_id=$1 AND internal_id=$2 AND version_id=$3", c, t);
        command.Parameters.AddWithValue(product); command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(version);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return Result.Failure<RevisionBaselineMember>("Exact version not available in this Product.");
        var endpoint = new RevisionSelection(kind, id, version, reader.GetInt32(3));
        var state = reader.GetString(2);
        if (endpoint.Validate().IsFailure || state is not ("Draft" or "InReview" or "Approved"))
            return Result.Failure<RevisionBaselineMember>("Invalid persisted exact version.");
        return new RevisionBaselineMember(endpoint, reader.GetString(0), reader.GetString(1), state, false);
    }

    public Task<Result<IReadOnlyList<RevisionBaselineMember>>> Candidates(Guid product, CancellationToken token = default) =>
        Execute<IReadOnlyList<RevisionBaselineMember>>(async (c, t) =>
        {
            var members = new List<RevisionBaselineMember>();
            foreach (var kind in Enum.GetValues<RevisionEntityKind>())
            {
                var table = Table(kind);
                if (!await Exists(c, t, table, token)) continue;
                var endpoints = new List<(Guid Id, Guid Version)>();
                await using (var command = new NpgsqlCommand($"SELECT internal_id,version_id FROM {table} WHERE product_internal_id=$1 ORDER BY internal_id,version_number", c, t))
                {
                    command.Parameters.AddWithValue(product);
                    await using var reader = await command.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) endpoints.Add((reader.GetGuid(0), reader.GetGuid(1)));
                }
                foreach (var endpoint in endpoints)
                {
                    var member = await LookupEndpoint(c, t, product, kind, endpoint.Id, endpoint.Version, token);
                    if (member.IsFailure) return Result.Failure<IReadOnlyList<RevisionBaselineMember>>(member.Error);
                    members.Add(member.Value);
                }
            }
            return members;
        }, token);

    public Task<Result<RevisionBaseline>> Baseline(Guid product, Guid id, CancellationToken token = default) =>
        Execute<RevisionBaseline>(async (c, t) =>
        {
            var found = await Find(c, t, product, id, token);
            if (found.IsFailure) return Result.Failure<RevisionBaseline>(found.Error);
            if (found.Value.HasNoValue || found.Value.Value.Deleted) return Result.Failure<RevisionBaseline>("Revision not found.");
            return await Resolve(c, t, found.Value.Value, token);
        }, token);

    private static async Task<Result<RevisionBaseline>> Resolve(NpgsqlConnection c, NpgsqlTransaction t, RevisionSnapshot row, CancellationToken token)
    {
        if (row.Value is ReleasedRevision released)
            return new RevisionBaseline(row, released.Members.Select(m => new RevisionBaselineMember(m.Endpoint, m.Identifier, m.Title, "Approved", m.Explicit)).ToArray());
        var product = row.Value.ProductInternalId;
        var roots = row.Value.Selections.ToHashSet();
        var visited = new Dictionary<(RevisionEntityKind, Guid), RevisionBaselineMember>();
        var revisions = new Dictionary<(RevisionEntityKind, Guid), long>();
        var pending = new Queue<RevisionSelection>(roots);
        while (pending.TryDequeue(out var endpoint))
        {
            if (visited.ContainsKey((endpoint.Kind, endpoint.VersionId))) continue;
            var member = await LoadEndpoint(c, t, product, endpoint, token);
            if (member.IsFailure) return Result.Failure<RevisionBaseline>(member.Error);
            visited.Add((endpoint.Kind, endpoint.VersionId), member.Value with { Explicit = roots.Contains(endpoint) });
            await using (var version = new NpgsqlCommand($"SELECT revision FROM {Table(endpoint.Kind)} WHERE version_id=$1", c, t))
            {
                version.Parameters.AddWithValue(endpoint.VersionId);
                revisions.Add((endpoint.Kind, endpoint.VersionId), (long)(await version.ExecuteScalarAsync(token))!);
            }
            var outgoing = await Outgoing(c, t, product, endpoint, token);
            if (outgoing.IsFailure) return Result.Failure<RevisionBaseline>(outgoing.Error);
            foreach (var next in outgoing.Value) pending.Enqueue(next);
        }
        var members = visited.Values.OrderBy(m => m.Endpoint.Kind).ThenBy(m => m.Endpoint.VersionId).ToArray();
        // Include every exact version's optimistic revision: reopening/reapproval or owned-link edits
        // invalidate confirmation even when the final closure identities happen to be unchanged.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { row.Value.InternalId, row.Revision,
            Members = members.Select(m => new { Member = m, Revision = revisions[(m.Endpoint.Kind, m.Endpoint.VersionId)] }) });
        return new RevisionBaseline(row, members, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task<Result<IReadOnlyList<RevisionSelection>>> Outgoing(NpgsqlConnection c, NpgsqlTransaction t,
        Guid product, RevisionSelection owner, CancellationToken token)
    {
        var targets = new List<(RevisionEntityKind Kind, Guid Id, Guid Version)>();
        if (owner.Kind == RevisionEntityKind.Requirement)
        {
            foreach (var (table, kind) in new[] { ("retravion_requirement.user_need_link", RevisionEntityKind.UserNeed), ("retravion_requirement.parent_link", RevisionEntityKind.Requirement) })
            {
                if (!await Exists(c, t, table, token))
                    return Result.Failure<IReadOnlyList<RevisionSelection>>("The complete outgoing reference storage is unavailable.");
                await using var command = new NpgsqlCommand($"SELECT target_internal_id,target_version_id FROM {table} WHERE owner_version_id=$1", c, t);
                command.Parameters.AddWithValue(owner.VersionId);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) targets.Add((kind, reader.GetGuid(0), reader.GetGuid(1)));
            }
        }
        else if (owner.Kind == RevisionEntityKind.TestCase)
        {
            await using var command = new NpgsqlCommand("SELECT links FROM retravion_test_case.test_case_version WHERE internal_id=$1 AND version_id=$2", c, t);
            command.Parameters.AddWithValue(owner.InternalId); command.Parameters.AddWithValue(owner.VersionId);
            var json = (string)(await command.ExecuteScalarAsync(token))!;
            var links = JsonSerializer.Deserialize<TestCaseRequirementLink[]>(json);
            if (links is null || links.Any(l => l is null)) return Result.Failure<IReadOnlyList<RevisionSelection>>("Invalid persisted outgoing links.");
            targets.AddRange(links.Select(l => (RevisionEntityKind.Requirement, l.RequirementInternalId, l.RequirementVersionId)));
        }
        var endpoints = new List<RevisionSelection>();
        foreach (var target in targets)
        {
            var member = await LookupEndpoint(c, t, product, target.Kind, target.Id, target.Version, token);
            if (member.IsFailure) return Result.Failure<IReadOnlyList<RevisionSelection>>(member.Error);
            endpoints.Add(member.Value.Endpoint);
        }
        return endpoints;
    }
}
