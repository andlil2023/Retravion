using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Domain.UserNeed;
using Retravion.Infrastructure.AuditTrail;
using Need = Retravion.Domain.UserNeed.UserNeed;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.Infrastructure.Requirement;

public sealed partial class PostgresRequirementStore
{
    private const string RelationshipType = "Requirement–User Need";

    public Task<Result<IReadOnlyList<RequirementUserNeedLink>>> Links(Guid requirementInternalId, Guid versionId,
        CancellationToken cancellationToken = default) => Execute(async (connection, transaction) =>
    {
        await using var owner = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM retravion_requirement.requirement_version WHERE internal_id=$1 AND version_id=$2)", connection, transaction);
        owner.Parameters.AddWithValue(requirementInternalId); owner.Parameters.AddWithValue(versionId);
        return await owner.ExecuteScalarAsync(cancellationToken) is true
            ? Result.Success(await LoadLinks(connection, transaction, versionId, cancellationToken))
            : Result.Failure<IReadOnlyList<RequirementUserNeedLink>>("Requirement version not found.");
    }, cancellationToken, Result.Success<IReadOnlyList<RequirementUserNeedLink>>([]));

    public Task<Result<RequirementSnapshot>> ChangeLink(RequirementSnapshot expected, RequirementUserNeedLink target, bool add,
        AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        var editable = RequirementUserNeedLinks.CanChange(expected.Requirement);
        if (editable.IsFailure || actor is not AuditActor.Identified || expected.Revision <= 0)
            return Task.FromResult(Result.Failure<RequirementSnapshot>(editable.IsFailure ? editable.Error : "Invalid Requirement link change."));
        return Execute(async (connection, transaction) =>
        {
            var r = expected.Requirement;
            if (!await ProductActive(connection, transaction, r.ProductInternalId, cancellationToken)) return Inactive();
            await using (var current = new NpgsqlCommand($"SELECT {Columns} FROM retravion_requirement.requirement r WHERE internal_id=$1 FOR UPDATE", connection, transaction))
            {
                current.Parameters.AddWithValue(r.InternalId);
                await using var reader = await current.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) return Result.Failure<RequirementSnapshot>("Requirement not found.");
                var stored = Read(reader);
                if (stored.IsFailure) return Result.Failure<RequirementSnapshot>(stored.Error);
                if (stored.Value.Revision != expected.Revision || stored.Value.Requirement != r)
                    return Result.Failure<RequirementSnapshot>("The Requirement changed or a newer version exists. Retrieve it again.");
            }
            var before = await LoadLinks(connection, transaction, r.VersionId, cancellationToken);
            if (add)
            {
                var need = await LoadTarget(connection, transaction, target, cancellationToken);
                if (need.IsFailure) return Result.Failure<RequirementSnapshot>(need.Error);
                var link = RequirementUserNeedLinks.Add(r, need.Value);
                if (link.IsFailure) return Result.Failure<RequirementSnapshot>(link.Error);
                if (before.Contains(target)) return Result.Failure<RequirementSnapshot>("That exact User Need version is already linked.");
            }
            else if (!before.Contains(target)) return Result.Success(expected); // Unchanged removal, after concurrency validation.
            await using (var change = new NpgsqlCommand(add
                ? "INSERT INTO retravion_requirement.user_need_link VALUES ($1,$2,$3)"
                : "DELETE FROM retravion_requirement.user_need_link WHERE owner_version_id=$1 AND target_internal_id=$2 AND target_version_id=$3", connection, transaction))
            {
                change.Parameters.AddWithValue(r.VersionId); change.Parameters.AddWithValue(target.UserNeedInternalId); change.Parameters.AddWithValue(target.UserNeedVersionId);
                await change.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var bump = new NpgsqlCommand("UPDATE retravion_requirement.requirement SET revision=revision+1 WHERE internal_id=$1", connection, transaction))
            { bump.Parameters.AddWithValue(r.InternalId); await bump.ExecuteNonQueryAsync(cancellationToken); }
            if (!await SaveVersion(connection, transaction, r.InternalId, cancellationToken))
                return Result.Failure<RequirementSnapshot>("The Requirement version identity is already in use.");
            var after = await LoadLinks(connection, transaction, r.VersionId, cancellationToken);
            var decision = add ? "LinkAdded" : "LinkRemoved";
            await using (var history = new NpgsqlCommand("INSERT INTO retravion_requirement.review (requirement_internal_id,actor_identifier,occurred_at_utc_ticks,decision,manual_id,title,statement,type,rationale,version_id,version_number,source_version_id) SELECT internal_id,$2,$3,$4,id,title,statement,type,rationale,version_id,version_number,source_version_id FROM retravion_requirement.requirement WHERE internal_id=$1", connection, transaction))
            {
                history.Parameters.AddWithValue(r.InternalId); history.Parameters.AddWithValue(((AuditActor.Identified)actor).Identifier);
                history.Parameters.AddWithValue(occurredAt.UtcTicks); history.Parameters.AddWithValue(decision);
                await history.ExecuteNonQueryAsync(cancellationToken);
            }
            await SetHistoryLinks(connection, transaction, r, before, after, cancellationToken);
            var values = new RequirementAuditValues(r.ProductInternalId, r.Id, r.Title, r.Statement, r.Type.ToString(), r.Rationale, r.Status.ToString());
            await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
                new AuditInformation(actor, occurredAt, add ? "requirement.link.add" : "requirement.link.remove",
                    new AuditTarget.Identified(r.InternalId.ToString()), AuditActionOutcome.Success,
                    requirementChange: new RequirementAuditDetails(r.InternalId, values, values, decision, null,
                        r.VersionId, r.VersionNumber, r.SourceVersionId, RelationshipType: RelationshipType, BeforeLinks: before, AfterLinks: after)), cancellationToken);
            return Result.Success(expected with { Revision = expected.Revision + 1 });
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<RequirementUserNeedLink>> LoadLinks(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid version, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT target_internal_id,target_version_id FROM retravion_requirement.user_need_link WHERE owner_version_id=$1 ORDER BY target_version_id", connection, transaction);
        command.Parameters.AddWithValue(version);
        await using var reader = await command.ExecuteReaderAsync(token);
        var links = new List<RequirementUserNeedLink>();
        while (await reader.ReadAsync(token)) links.Add(new(reader.GetGuid(0), reader.GetGuid(1)));
        return links;
    }

    private static async Task<Result<Need>> LoadTarget(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RequirementUserNeedLink target, CancellationToken token)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_need.need_version') IS NOT NULL", connection, transaction))
            if (await exists.ExecuteScalarAsync(token) is not true) return Result.Failure<Need>("User Need version not found.");
        await using var command = new NpgsqlCommand("SELECT internal_id,product_internal_id,id,title,statement,status,version_id,version_number,ever_approved,creation_reason,source_version_id FROM retravion_need.need_version WHERE internal_id=$1 AND version_id=$2 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(target.UserNeedInternalId); command.Parameters.AddWithValue(target.UserNeedVersionId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return Result.Failure<Need>("User Need version not found.");
        return Need.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            (UserNeedStatus)reader.GetInt32(5), reader.GetGuid(6), reader.GetInt32(7), reader.GetBoolean(8), Text(reader, 9), reader.IsDBNull(10) ? null : reader.GetGuid(10));
    }

    private static async Task<Result> ValidateTargets(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid owner, CancellationToken token)
    {
        var targets = new List<Need>();
        foreach (var link in await LoadLinks(connection, transaction, owner, token))
        {
            var target = await LoadTarget(connection, transaction, link, token);
            if (target.IsFailure) return Result.Failure(target.Error);
            targets.Add(target.Value);
        }
        return RequirementUserNeedLinks.CanApprove(targets);
    }

    private static IReadOnlyList<RequirementUserNeedLink>? ReadLinks(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : JsonSerializer.Deserialize<RequirementUserNeedLink[]>(reader.GetString(ordinal));

    private static async Task SetHistoryLinks(NpgsqlConnection connection, NpgsqlTransaction transaction, RequirementState owner,
        IReadOnlyList<RequirementUserNeedLink>? before, IReadOnlyList<RequirementUserNeedLink> after, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("UPDATE retravion_requirement.review SET before_links=$1,after_links=$2 WHERE id=(SELECT max(id) FROM retravion_requirement.review WHERE requirement_internal_id=$3 AND version_id=$4)", connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, before is null ? DBNull.Value : JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(after));
        command.Parameters.AddWithValue(owner.InternalId); command.Parameters.AddWithValue(owner.VersionId);
        await command.ExecuteNonQueryAsync(token);
    }
}
