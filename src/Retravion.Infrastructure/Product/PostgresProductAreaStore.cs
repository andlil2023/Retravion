using System.Text.Json;
using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Product;
using Retravion.Domain.Product;
using Retravion.Infrastructure.AuditTrail;

namespace Retravion.Infrastructure.Product;

public sealed class PostgresProductAreaStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : IProductAreaStore
{
    // Same first lock as Requirement/Test Case/release writes. Area changes cannot race classification or release.
    internal const string Setup = """
        SELECT pg_advisory_xact_lock(821749620111);
        CREATE SCHEMA IF NOT EXISTS retravion_product;
        CREATE TABLE IF NOT EXISTS retravion_product.area (
            internal_id uuid PRIMARY KEY, product_internal_id uuid NOT NULL,
            code text NOT NULL CHECK (code ~ '^[A-Z0-9-]+$' AND char_length(code)<=100),
            label text NOT NULL CHECK (length(btrim(label))>0),
            is_active boolean NOT NULL, revision bigint NOT NULL CHECK (revision>0),
            UNIQUE(product_internal_id,code)
        );
        CREATE TABLE IF NOT EXISTS retravion_product.area_history (
            sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            product_internal_id uuid NOT NULL, internal_id uuid NOT NULL REFERENCES retravion_product.area(internal_id),
            operation_id uuid NOT NULL UNIQUE, actor_identifier text NOT NULL,
            occurred_at_utc_ticks bigint NOT NULL, action text NOT NULL, change jsonb NOT NULL
        );
        """;

    public Task<Result<ProductAreaRetrieval>> Retrieve(Guid product, Guid? operation = null, CancellationToken token = default) =>
        Execute(async (c, t) =>
        {
            var areas = new List<ProductAreaSnapshot>();
            await using (var command = new NpgsqlCommand("SELECT internal_id,code,label,is_active,revision FROM retravion_product.area WHERE product_internal_id=$1 ORDER BY code", c, t))
            {
                command.Parameters.AddWithValue(product);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var area = ProductArea.Restore(reader.GetGuid(0), product, reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
                    if (area.IsFailure || reader.GetInt64(4) <= 0) return Result.Failure<ProductAreaRetrieval>("Invalid persisted Product area.");
                    areas.Add(new(area.Value, reader.GetInt64(4)));
                }
            }
            var history = new List<ProductAreaHistory>();
            await using (var command = new NpgsqlCommand("SELECT actor_identifier,occurred_at_utc_ticks,action,change FROM retravion_product.area_history WHERE product_internal_id=$1 ORDER BY sequence", c, t))
            {
                command.Parameters.AddWithValue(product);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var change = JsonSerializer.Deserialize<ProductAreaAuditDetails>(reader.GetString(3));
                    if (change is null) return Result.Failure<ProductAreaRetrieval>("Invalid persisted Product area history.");
                    history.Add(new(new AuditActor.Identified(reader.GetString(0)), new(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), change));
                }
            }
            return Result.Success(new ProductAreaRetrieval(areas, history, operation is null ? null : history.SingleOrDefault(h => h.Change.OperationId == operation)));
        }, token);

    public Task<Result<ProductAreaSnapshot>> Save(Guid operation, ProductAreaSnapshot? expected, ProductArea replacement,
        AuditActor actor, DateTimeOffset time, Func<Task<bool>> authorized, CancellationToken token = default) =>
        Execute(async (c, t) =>
        {
            if (!await authorized() || actor is not AuditActor.Identified identified) return Result.Failure<ProductAreaSnapshot>("Access denied.");
            if (operation == Guid.Empty) return Result.Failure<ProductAreaSnapshot>("An immutable operation identity is required.");
            await using (var previous = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM retravion_product.area_history WHERE operation_id=$1)", c, t))
            {
                previous.Parameters.AddWithValue(operation);
                if (await previous.ExecuteScalarAsync(token) is true) return Result.Failure<ProductAreaSnapshot>("This operation already committed. Retrieve its outcome before another change.");
            }
            await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", c, t))
                if (await exists.ExecuteScalarAsync(token) is not true) return Inactive();
            await using (var product = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", c, t))
            {
                product.Parameters.AddWithValue(replacement.ProductInternalId);
                if (await product.ExecuteScalarAsync(token) is not true) return Inactive();
            }
            if (expected is null)
            {
                if (!replacement.IsActive) return Result.Failure<ProductAreaSnapshot>("New areas must be active.");
                await using var insert = new NpgsqlCommand("INSERT INTO retravion_product.area(internal_id,product_internal_id,code,label,is_active,revision) VALUES($1,$2,$3,$4,true,1)", c, t);
                insert.Parameters.AddWithValue(replacement.InternalId); insert.Parameters.AddWithValue(replacement.ProductInternalId);
                insert.Parameters.AddWithValue(replacement.Code); insert.Parameters.AddWithValue(replacement.Label);
                await insert.ExecuteNonQueryAsync(token);
            }
            else
            {
                var before = expected.Area;
                if (before.InternalId != replacement.InternalId || before.ProductInternalId != replacement.ProductInternalId ||
                    before.Code != replacement.Code || !before.IsActive && replacement.IsActive ||
                    before.IsActive != replacement.IsActive && before.Label != replacement.Label)
                    return Result.Failure<ProductAreaSnapshot>("Area codes are immutable; retirement cannot rename or reactivate an area.");
                await using var update = new NpgsqlCommand("UPDATE retravion_product.area SET label=$1,is_active=$2,revision=revision+$3 WHERE internal_id=$4 AND product_internal_id=$5 AND revision=$6 AND code=$7 AND label=$8 AND is_active=$9 RETURNING revision", c, t);
                update.Parameters.AddWithValue(replacement.Label); update.Parameters.AddWithValue(replacement.IsActive);
                update.Parameters.AddWithValue(before == replacement ? 0 : 1); update.Parameters.AddWithValue(before.InternalId);
                update.Parameters.AddWithValue(before.ProductInternalId); update.Parameters.AddWithValue(expected.Revision);
                update.Parameters.AddWithValue(before.Code); update.Parameters.AddWithValue(before.Label); update.Parameters.AddWithValue(before.IsActive);
                if (await update.ExecuteScalarAsync(token) is null) return Result.Failure<ProductAreaSnapshot>("The area changed. Retrieve it before editing.");
                if (before == replacement) return expected;
            }
            static ProductAreaAuditValues Values(ProductArea area) => new(area.Code, area.Label, area.IsActive);
            var action = expected is null ? "product.area.create" : expected.Area.IsActive != replacement.IsActive ? "product.area.retire" : "product.area.rename";
            var change = new ProductAreaAuditDetails(replacement.ProductInternalId, replacement.InternalId, operation,
                expected is null ? null : Values(expected.Area), Values(replacement));
            await using var history = new NpgsqlCommand("INSERT INTO retravion_product.area_history(product_internal_id,internal_id,operation_id,actor_identifier,occurred_at_utc_ticks,action,change) VALUES($1,$2,$3,$4,$5,$6,$7)", c, t);
            history.Parameters.AddWithValue(replacement.ProductInternalId); history.Parameters.AddWithValue(replacement.InternalId);
            history.Parameters.AddWithValue(operation); history.Parameters.AddWithValue(identified.Identifier); history.Parameters.AddWithValue(time.UtcTicks);
            history.Parameters.AddWithValue(action); history.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(change));
            await history.ExecuteNonQueryAsync(token);
            await PostgresAuditSubmission.SubmitInTransactionAsync(c, t, new AuditInformation(actor, time, action,
                new AuditTarget.Identified(replacement.InternalId.ToString()), AuditActionOutcome.Success, productAreaChange: change), token);
            if (!await authorized()) return Result.Failure<ProductAreaSnapshot>("Access denied.");
            return Result.Success(new ProductAreaSnapshot(replacement, (expected?.Revision ?? 0) + 1));
        }, token);

    internal static async Task<bool> CanSelect(NpgsqlConnection c, NpgsqlTransaction t, Guid product, Guid? before, Guid? after, CancellationToken token)
    {
        if (after is null) return true;
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.area WHERE product_internal_id=$1 AND internal_id=$2", c, t);
        command.Parameters.AddWithValue(product); command.Parameters.AddWithValue(after.Value);
        return await command.ExecuteScalarAsync(token) is bool active && (active || before == after);
    }

    internal static async Task<AreaEvidence> Evidence(NpgsqlConnection c, NpgsqlTransaction t, Guid product, Guid? id, CancellationToken token)
    {
        if (id is null) return AreaEvidence.Unclassified;
        await using var command = new NpgsqlCommand("SELECT code,label FROM retravion_product.area WHERE product_internal_id=$1 AND internal_id=$2", c, t);
        command.Parameters.AddWithValue(product); command.Parameters.AddWithValue(id.Value);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Persisted classification is unavailable.");
        return new(new AreaValue(id.Value, reader.GetString(0), reader.GetString(1)));
    }

    private static Result<ProductAreaSnapshot> Inactive() => Result.Failure<ProductAreaSnapshot>("The responsible Product is retired or no longer exists.");

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token)
    {
        var commitStarted = false;
        try
        {
            await using var c = new NpgsqlConnection(connectionString);
            await c.OpenAsync(token);
            await using var t = await c.BeginTransactionAsync(token);
            await using (var setup = new NpgsqlCommand(Setup, c, t)) await setup.ExecuteNonQueryAsync(token);
            var result = await operation(c, t);
            if (result.IsSuccess)
            {
                commitStarted = true;
                if (commit is null) await t.CommitAsync(token); else await commit(t, token);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        { return Result.Failure<T>("The area operation outcome could not be confirmed. Retrieve its outcome before retrying; do not retry automatically."); }
        catch (PostgresException e) when (!token.IsCancellationRequested && e.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Result.Failure<T>("That area code or identity is already in use, including retired areas. Retrieve before editing."); }
        catch (NpgsqlException e) when (!token.IsCancellationRequested && (e.IsTransient || e is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction }))
        { return Result.Failure<T>("The area operation could not be completed."); }
    }
}
