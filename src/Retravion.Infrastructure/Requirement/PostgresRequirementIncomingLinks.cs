using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.Requirement;

namespace Retravion.Infrastructure.Requirement;

public sealed partial class PostgresRequirementStore
{
    public Task<Result<IReadOnlyList<RequirementVersionReference>>> IncomingUserNeed(Guid internalId, Guid versionId,
        CancellationToken cancellationToken = default) => Incoming("user_need_link", internalId, versionId, cancellationToken);

    public Task<Result<IReadOnlyList<RequirementVersionReference>>> Children(Guid internalId, Guid versionId,
        CancellationToken cancellationToken = default) => Incoming("parent_link", internalId, versionId, cancellationToken);

    // Table names are fixed by the two entry points, never submitted input. Join version storage, not latest state.
    private Task<Result<IReadOnlyList<RequirementVersionReference>>> Incoming(string table, Guid internalId, Guid versionId,
        CancellationToken token) => Execute(async (connection, transaction) =>
    {
        await using var command = new NpgsqlCommand($"""
            SELECT owner.internal_id, owner.version_id
            FROM retravion_requirement.{table} link
            JOIN retravion_requirement.requirement_version owner ON owner.version_id=link.owner_version_id
            WHERE link.target_internal_id=$1 AND link.target_version_id=$2
            ORDER BY owner.id, owner.internal_id, owner.version_number
            """, connection, transaction);
        command.Parameters.AddWithValue(internalId); command.Parameters.AddWithValue(versionId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<RequirementVersionReference>();
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
        return Result.Success<IReadOnlyList<RequirementVersionReference>>(rows);
    }, token, Result.Success<IReadOnlyList<RequirementVersionReference>>([]));
}
