using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.Infrastructure.Requirement;

public sealed partial class PostgresRequirementStore
{
    private const string ParentRelationship = "Child–parent Requirement";

    public Task<Result<Maybe<RequirementParentLink>>> Parent(Guid requirementInternalId, Guid versionId,
        CancellationToken cancellationToken = default) => Execute(async (connection, transaction) =>
    {
        await using var owner = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM retravion_requirement.requirement_version WHERE internal_id=$1 AND version_id=$2)", connection, transaction);
        owner.Parameters.AddWithValue(requirementInternalId); owner.Parameters.AddWithValue(versionId);
        return await owner.ExecuteScalarAsync(cancellationToken) is true
            ? Result.Success(await LoadParent(connection, transaction, versionId, cancellationToken))
            : Result.Failure<Maybe<RequirementParentLink>>("Requirement version not found.");
    }, cancellationToken, Result.Success(Maybe<RequirementParentLink>.None));

    public Task<Result<RequirementSnapshot>> AddParent(RequirementSnapshot expected, RequirementParentLink target,
        AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default) =>
        ChangeParent(expected, target, "add", actor, occurredAt, cancellationToken);

    public Task<Result<RequirementSnapshot>> RemoveParent(RequirementSnapshot expected, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default) =>
        ChangeParent(expected, Maybe<RequirementParentLink>.None, "remove", actor, occurredAt, cancellationToken);

    public Task<Result<RequirementSnapshot>> ReplaceParent(RequirementSnapshot expected, RequirementParentLink target,
        AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default) =>
        ChangeParent(expected, target, "replace", actor, occurredAt, cancellationToken);

    private Task<Result<RequirementSnapshot>> ChangeParent(RequirementSnapshot expected, Maybe<RequirementParentLink> target,
        string action, AuditActor actor, DateTimeOffset occurredAt, CancellationToken token)
    {
        var editable = RequirementParentLinks.CanChange(expected.Requirement);
        if (editable.IsFailure || actor is not AuditActor.Identified || expected.Revision <= 0)
            return Task.FromResult(Result.Failure<RequirementSnapshot>(editable.IsFailure ? editable.Error : "Invalid Requirement parent change."));
        return Execute(async (connection, transaction) =>
        {
            var child = expected.Requirement;
            if (!await ProductActive(connection, transaction, child.ProductInternalId, token)) return Inactive();
            await using (var current = new NpgsqlCommand($"SELECT {Columns} FROM retravion_requirement.requirement r WHERE internal_id=$1 FOR UPDATE", connection, transaction))
            {
                current.Parameters.AddWithValue(child.InternalId);
                await using var reader = await current.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) return Result.Failure<RequirementSnapshot>("Requirement not found.");
                var stored = Read(reader);
                if (stored.IsFailure) return Result.Failure<RequirementSnapshot>(stored.Error);
                if (stored.Value.Revision != expected.Revision || stored.Value.Requirement != child)
                    return Result.Failure<RequirementSnapshot>("The Requirement changed or a newer version exists. Retrieve it again.");
            }
            var before = await LoadParent(connection, transaction, child.VersionId, token);
            if (action == "add" && before.HasValue)
                return Result.Failure<RequirementSnapshot>("The Requirement already has a parent. Replace it explicitly.");
            if (action == "replace" && before.HasNoValue)
                return Result.Failure<RequirementSnapshot>("The Requirement has no parent to replace.");
            if (target.HasValue)
            {
                var parent = await LoadParentTarget(connection, transaction, target.Value, token);
                if (parent.IsFailure) return Result.Failure<RequirementSnapshot>(parent.Error);
                var edges = await LoadParentEdges(connection, transaction, child.ProductInternalId, token);
                var valid = RequirementParentLinks.Set(child, parent.Value, edges);
                if (valid.IsFailure) return Result.Failure<RequirementSnapshot>(valid.Error);
            }
            if (before == target) return Result.Success(expected); // No-op, only after persisted concurrency validation.
            await using (var change = new NpgsqlCommand(target.HasValue
                ? "INSERT INTO retravion_requirement.parent_link VALUES ($1,$2,$3) ON CONFLICT (owner_version_id) DO UPDATE SET target_internal_id=EXCLUDED.target_internal_id,target_version_id=EXCLUDED.target_version_id"
                : "DELETE FROM retravion_requirement.parent_link WHERE owner_version_id=$1", connection, transaction))
            {
                change.Parameters.AddWithValue(child.VersionId);
                if (target.HasValue)
                { change.Parameters.AddWithValue(target.Value.RequirementInternalId); change.Parameters.AddWithValue(target.Value.VersionId); }
                await change.ExecuteNonQueryAsync(token);
            }
            await using (var bump = new NpgsqlCommand("UPDATE retravion_requirement.requirement SET revision=revision+1 WHERE internal_id=$1", connection, transaction))
            { bump.Parameters.AddWithValue(child.InternalId); await bump.ExecuteNonQueryAsync(token); }
            if (!await SaveVersion(connection, transaction, child.InternalId, token))
                return Result.Failure<RequirementSnapshot>("The Requirement version identity is already in use.");
            var decision = action switch { "add" => "ParentAdded", "remove" => "ParentRemoved", _ => "ParentReplaced" };
            await using (var history = new NpgsqlCommand("INSERT INTO retravion_requirement.review (requirement_internal_id,actor_identifier,occurred_at_utc_ticks,decision,manual_id,title,statement,type,rationale,version_id,version_number,source_version_id) SELECT internal_id,$2,$3,$4,id,title,statement,type,rationale,version_id,version_number,source_version_id FROM retravion_requirement.requirement WHERE internal_id=$1", connection, transaction))
            {
                history.Parameters.AddWithValue(child.InternalId); history.Parameters.AddWithValue(((AuditActor.Identified)actor).Identifier);
                history.Parameters.AddWithValue(occurredAt.UtcTicks); history.Parameters.AddWithValue(decision);
                await history.ExecuteNonQueryAsync(token);
            }
            await SetHistoryParent(connection, transaction, child, before, target, token);
            var values = new RequirementAuditValues(child.ProductInternalId, child.Id, child.Title, child.Statement, child.Type.ToString(), child.Rationale, child.Status.ToString());
            await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
                new AuditInformation(actor, occurredAt, "requirement.parent." + action, new AuditTarget.Identified(child.InternalId.ToString()),
                    AuditActionOutcome.Success, requirementChange: new RequirementAuditDetails(child.InternalId, values, values, decision, null,
                        child.VersionId, child.VersionNumber, child.SourceVersionId, ParentRelationshipType: ParentRelationship,
                        BeforeParent: before.HasValue ? before.Value : null, AfterParent: target.HasValue ? target.Value : null)), token);
            return Result.Success(expected with { Revision = expected.Revision + 1 });
        }, token);
    }

    private static async Task<Maybe<RequirementParentLink>> LoadParent(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid ownerVersion, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT target_internal_id,target_version_id FROM retravion_requirement.parent_link WHERE owner_version_id=$1", connection, transaction);
        command.Parameters.AddWithValue(ownerVersion);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new RequirementParentLink(reader.GetGuid(0), reader.GetGuid(1)) : Maybe<RequirementParentLink>.None;
    }

    private static async Task<Result<RequirementState>> LoadParentTarget(NpgsqlConnection connection, NpgsqlTransaction transaction,
        RequirementParentLink target, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_requirement.requirement_version r WHERE internal_id=$1 AND version_id=$2 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(target.RequirementInternalId); command.Parameters.AddWithValue(target.VersionId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return Result.Failure<RequirementState>("Parent Requirement version not found.");
        return Read(reader).Map(r => r.Requirement);
    }

    private static async Task<IReadOnlyList<RequirementParentEdge>> LoadParentEdges(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid product, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT p.owner_version_id,v.internal_id,p.target_internal_id FROM retravion_requirement.parent_link p JOIN retravion_requirement.requirement_version v ON v.version_id=p.owner_version_id WHERE v.product_internal_id=$1", connection, transaction);
        command.Parameters.AddWithValue(product);
        await using var reader = await command.ExecuteReaderAsync(token);
        var edges = new List<RequirementParentEdge>();
        while (await reader.ReadAsync(token)) edges.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
        return edges;
    }

    private static async Task<Result> ValidateParent(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid ownerVersion, CancellationToken token)
    {
        var link = await LoadParent(connection, transaction, ownerVersion, token);
        if (link.HasNoValue) return Result.Success();
        var parent = await LoadParentTarget(connection, transaction, link.Value, token);
        return parent.IsFailure ? Result.Failure(parent.Error) : RequirementParentLinks.CanApprove(parent.Value);
    }

    private static RequirementParentLink? ReadParent(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : JsonSerializer.Deserialize<RequirementParentLink>(reader.GetString(ordinal));

    private static async Task SetHistoryParent(NpgsqlConnection connection, NpgsqlTransaction transaction, RequirementState owner,
        Maybe<RequirementParentLink> before, Maybe<RequirementParentLink> after, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("UPDATE retravion_requirement.review SET parent_relationship_type=$1,before_parent=$2,after_parent=$3 WHERE id=(SELECT max(id) FROM retravion_requirement.review WHERE requirement_internal_id=$4 AND version_id=$5)", connection, transaction);
        command.Parameters.AddWithValue(ParentRelationship);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, before.HasValue ? JsonSerializer.Serialize(before.Value) : DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, after.HasValue ? JsonSerializer.Serialize(after.Value) : DBNull.Value);
        command.Parameters.AddWithValue(owner.InternalId); command.Parameters.AddWithValue(owner.VersionId);
        await command.ExecuteNonQueryAsync(token);
    }
}
