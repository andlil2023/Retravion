using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.Specification;
using Retravion.Domain.Specification;
using Retravion.Infrastructure.Requirement;
using Retravion.Infrastructure.TestCase;
using Retravion.Infrastructure.UserNeed;

namespace Retravion.Infrastructure.Specification;

// SQL identifiers and expressions are supplied only by the owning stores, never by a caller.
internal sealed class PostgresSpecificationVersionSource(SpecificationLinkKind kind, string table, string identifier, string approved)
{
    internal static PostgresSpecificationVersionSource For(SpecificationLinkKind kind) => kind switch
    {
        SpecificationLinkKind.UserNeed => PostgresUserNeedStore.SpecificationVersions,
        SpecificationLinkKind.Requirement => PostgresRequirementStore.SpecificationVersions,
        SpecificationLinkKind.TestCase => PostgresTestCaseStore.SpecificationVersions,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal async Task<IReadOnlyList<SpecificationVersionChoice>> List(NpgsqlConnection connection, Guid productId,
        CancellationToken token) => await Read(connection, null, productId, null, false, token);

    internal async Task<Maybe<SpecificationVersionChoice>> Resolve(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid productId, Guid versionId, CancellationToken token, bool lockVersion = false)
    {
        if (lockVersion && transaction is null) throw new ArgumentNullException(nameof(transaction));
        var rows = await Read(connection, transaction, productId, versionId, lockVersion, token);
        return rows.Count == 0 ? Maybe<SpecificationVersionChoice>.None : Maybe<SpecificationVersionChoice>.From(rows[0]);
    }

    private async Task<List<SpecificationVersionChoice>> Read(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Guid productId, Guid? versionId, bool lockVersion, CancellationToken token)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection, transaction);
        exists.Parameters.AddWithValue(table);
        if (await exists.ExecuteScalarAsync(token) is not true) return [];

        var filter = versionId.HasValue ? " AND version_id = $2" : " ORDER BY 3, version_number, version_id";
        if (lockVersion) filter += " FOR NO KEY UPDATE";
        await using var command = new NpgsqlCommand($"""
            SELECT version_id, internal_id, {identifier}, version_number, title, {approved}
            FROM {table} WHERE product_internal_id = $1{filter}
            """, connection, transaction);
        command.Parameters.AddWithValue(productId);
        if (versionId is { } version) command.Parameters.AddWithValue(version);
        var rows = new List<SpecificationVersionChoice>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(new(new SpecificationLink(kind, reader.GetGuid(0)), reader.GetGuid(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetBoolean(5)));
        return rows;
    }
}
