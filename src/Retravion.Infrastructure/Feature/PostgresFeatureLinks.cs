using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.Feature;
using Retravion.Domain.Feature;
using Retravion.Domain.Requirement;
using FeatureRecord = Retravion.Domain.Feature.Feature;

namespace Retravion.Infrastructure.Feature;

public sealed partial class PostgresFeatureStore
{
    public Task<Result<FeatureSnapshot>> ChangeLink(FeatureSnapshot expected, string operation, FeatureRequirementLink target,
        FeatureRequirementLink? replacement, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, async (current, connection, transaction) =>
        {
            if (current.Status != "Draft") return Result.Failure<FeatureRecord>("Only Draft Features can change links.");
            if (target is null || target.RequirementInternalId == Guid.Empty || target.RequirementVersionId == Guid.Empty)
                return Result.Failure<FeatureRecord>("An exact Requirement endpoint is required.");
            var links = current.Links.ToList();
            switch (operation)
            {
                case "add" when replacement is null:
                    if (links.Any(link => link.RequirementInternalId == target.RequirementInternalId))
                        return Result.Failure<FeatureRecord>("That Requirement is already linked. Explicitly retarget its exact version instead.");
                    var added = await Target(connection, transaction, current.ProductInternalId, target, cancellationToken);
                    if (added.IsFailure) return Result.Failure<FeatureRecord>(added.Error);
                    links.Add(target);
                    break;
                case "remove" when replacement is null:
                    if (!links.Remove(target)) return current;
                    break;
                case "retarget" when replacement is not null:
                    var index = links.IndexOf(target);
                    if (index < 0) return Result.Failure<FeatureRecord>("The old exact Requirement endpoint is no longer linked.");
                    if (replacement.RequirementInternalId != target.RequirementInternalId)
                        return Result.Failure<FeatureRecord>("Retargeting must select another version of the same Requirement.");
                    var changed = await Target(connection, transaction, current.ProductInternalId, replacement, cancellationToken);
                    if (changed.IsFailure) return Result.Failure<FeatureRecord>(changed.Error);
                    links[index] = replacement;
                    break;
                default: return Result.Failure<FeatureRecord>("Invalid link operation.");
            }
            return current.ChangeLinks(links);
        }, actor, occurredAt, operation switch { "add" => "feature.link.add", "remove" => "feature.link.remove", "retarget" => "feature.link.retarget", _ => "feature.link" },
        operation switch { "add" => "LinkAdded", "remove" => "LinkRemoved", "retarget" => "LinkRetargeted", _ => null }, null, cancellationToken, authorized: authorized);

    private static async Task<Result<RequirementStatus>> Target(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid product, FeatureRequirementLink endpoint, CancellationToken token)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_requirement.requirement_version') IS NOT NULL", connection, transaction))
            if (await exists.ExecuteScalarAsync(token) is not true) return Result.Failure<RequirementStatus>("Requirement version not found.");
        await using var command = new NpgsqlCommand("SELECT product_internal_id,status FROM retravion_requirement.requirement_version WHERE internal_id=$1 AND version_id=$2 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(endpoint.RequirementInternalId); command.Parameters.AddWithValue(endpoint.RequirementVersionId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return Result.Failure<RequirementStatus>("Requirement version not found.");
        if (reader.GetGuid(0) != product) return Result.Failure<RequirementStatus>("Requirement links must belong to the same Product.");
        return (RequirementStatus)reader.GetInt32(1);
    }

    private static async Task<bool> TargetsApproved(NpgsqlConnection connection, NpgsqlTransaction transaction, FeatureRecord owner, CancellationToken token)
    {
        foreach (var endpoint in owner.Links)
        {
            var target = await Target(connection, transaction, owner.ProductInternalId, endpoint, token);
            if (target.IsFailure || target.Value != RequirementStatus.Approved) return false;
        }
        return true;
    }
}
