using CSharpFunctionalExtensions;
using Npgsql;

namespace Retravion.Infrastructure.Specification;

// Called inside the owner's Read Committed reopen transaction, before changing the exact version.
internal static class PostgresSpecificationReopenProtection
{
    internal static async Task<Result> Check(NpgsqlConnection connection, NpgsqlTransaction transaction,
        PostgresSpecificationVersionSource versions, Guid productId, Guid ownerId, Guid versionId, CancellationToken token)
    {
        var version = await versions.Resolve(connection, transaction, productId, versionId, token, lockVersion: true);
        if (version.HasNoValue || version.Value.InternalId != ownerId || !version.Value.IsApproved)
            return Result.Failure("The exact approved version could not be found. Retrieve it again.");

        // Older installations may not have Specification links yet. Do not initialize another slice here.
        await using var exists = new NpgsqlCommand("SELECT to_regclass('retravion_specification.link') IS NOT NULL", connection, transaction);
        if (await exists.ExecuteScalarAsync(token) is not true) return Result.Success();

        // A separate statement after the lock observes an approval that committed while we waited.
        await using var references = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM retravion_specification.link l
                JOIN retravion_specification.specification s ON s.internal_id = l.specification_internal_id
                WHERE l.kind = $1 AND l.version_id = $2 AND s.product_internal_id = $3
                    AND s.status IN ('Approved', 'Implemented'))
            """, connection, transaction);
        references.Parameters.AddWithValue((short)version.Value.Target.Kind);
        references.Parameters.AddWithValue(versionId);
        references.Parameters.AddWithValue(productId);
        return await references.ExecuteScalarAsync(token) is true
            ? Result.Failure("This exact version is referenced by an Approved or Implemented Specification. Create a successor for changes.")
            : Result.Success();
    }
}
