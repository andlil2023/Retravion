using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AuditTrail;
using Retravion.Application.TestCase;
using Retravion.Domain.Requirement;
using Retravion.Domain.TestCase;
using RequirementState = Retravion.Domain.Requirement.Requirement;
using Case = Retravion.Domain.TestCase.TestCase;

namespace Retravion.Infrastructure.TestCase;

public sealed partial class PostgresTestCaseStore
{
    public Task<Result<TestCaseSnapshot>> ChangeLink(TestCaseSnapshot expected, string operation, TestCaseRequirementLink target,
        TestCaseRequirementLink? replacement, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default, Func<Task<bool>>? authorized = null) =>
        Mutate(expected, async (current, connection, transaction) =>
        {
            if (current.Status != "Draft") return Result.Failure<Case>("Only Draft Test Cases can change links.");
            if (target is null || target.RequirementInternalId == Guid.Empty || target.RequirementVersionId == Guid.Empty)
                return Result.Failure<Case>("An exact Requirement endpoint is required.");
            var links = current.Links.ToList();
            switch (operation)
            {
                case "add" when replacement is null:
                    if (links.Any(link => link.RequirementInternalId == target.RequirementInternalId))
                        return Result.Failure<Case>("That Requirement is already linked. Explicitly retarget its exact version instead.");
                    var added = await Target(connection, transaction, current.ProductInternalId, target, cancellationToken);
                    if (added.IsFailure) return Result.Failure<Case>(added.Error);
                    links.Add(target);
                    break;
                case "remove" when replacement is null:
                    if (!links.Remove(target)) return current;
                    break;
                case "retarget" when replacement is not null:
                    var index = links.IndexOf(target);
                    if (index < 0) return Result.Failure<Case>("The old exact Requirement endpoint is no longer linked.");
                    if (replacement.RequirementInternalId != target.RequirementInternalId)
                        return Result.Failure<Case>("Retargeting must select another version of the same Requirement.");
                    var changed = await Target(connection, transaction, current.ProductInternalId, replacement, cancellationToken);
                    if (changed.IsFailure) return Result.Failure<Case>(changed.Error);
                    links[index] = replacement;
                    break;
                default:
                    return Result.Failure<Case>("Invalid link operation.");
            }
            return current.ChangeLinks(links);
        }, actor, occurredAt, operation switch
        {
            "add" => "test-case.link.add", "remove" => "test-case.link.remove", "retarget" => "test-case.link.retarget",
            _ => "test-case.link"
        }, operation switch { "add" => "LinkAdded", "remove" => "LinkRemoved", "retarget" => "LinkRetargeted", _ => null }, null, cancellationToken, authorized: authorized);

    private static async Task<Result<RequirementState>> Target(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid product, TestCaseRequirementLink endpoint, CancellationToken token)
    {
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_requirement.requirement_version') IS NOT NULL", connection, transaction))
            if (await exists.ExecuteScalarAsync(token) is not true) return Result.Failure<RequirementState>("Requirement version not found.");
        await using var command = new NpgsqlCommand("""
            SELECT internal_id,product_internal_id,id,title,statement,type,rationale,status,version_id,version_number,ever_approved,creation_reason,source_version_id
            FROM retravion_requirement.requirement_version WHERE internal_id=$1 AND version_id=$2 FOR SHARE
            """, connection, transaction);
        command.Parameters.AddWithValue(endpoint.RequirementInternalId); command.Parameters.AddWithValue(endpoint.RequirementVersionId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return Result.Failure<RequirementState>("Requirement version not found.");
        if (reader.GetGuid(1) != product) return Result.Failure<RequirementState>("Requirement links must belong to the same Product.");
        return RequirementState.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            (RequirementType)reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetString(6), (RequirementStatus)reader.GetInt32(7),
            reader.GetGuid(8), reader.GetInt32(9), reader.GetBoolean(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetGuid(12));
    }

    private static async Task<bool> TargetsApproved(NpgsqlConnection connection, NpgsqlTransaction transaction, Case owner, CancellationToken token)
    {
        if (owner.Links.IsEmpty) return false;
        foreach (var endpoint in owner.Links)
        {
            var target = await Target(connection, transaction, owner.ProductInternalId, endpoint, token);
            if (target.IsFailure || target.Value.Status != RequirementStatus.Approved) return false;
        }
        return true;
    }
}
