using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.UserNeed;
using Retravion.Domain.UserNeed;
using Retravion.Infrastructure.AuditTrail;
using Need = Retravion.Domain.UserNeed.UserNeed;

namespace Retravion.Infrastructure.UserNeed;

public sealed class PostgresUserNeedStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : IUserNeedStore, IUserNeedChangeTransaction
{
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620111);
        CREATE SCHEMA IF NOT EXISTS retravion_need;
        CREATE TABLE IF NOT EXISTS retravion_need.need (
            internal_id uuid PRIMARY KEY,
            product_internal_id uuid NOT NULL REFERENCES retravion_product.product(internal_id),
            id text NOT NULL CHECK (length(btrim(id)) > 0 AND char_length(id) <= 100),
            title text NOT NULL CHECK (length(btrim(title)) > 0),
            statement text NOT NULL CHECK (length(btrim(statement)) > 0),
            status integer NOT NULL CHECK (status BETWEEN 0 AND 2),
            revision bigint NOT NULL CHECK (revision > 0)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS need_product_id_unique ON retravion_need.need
            (product_internal_id, id COLLATE retravion_product.product_case_fold);
        CREATE TABLE IF NOT EXISTS retravion_need.review (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            need_internal_id uuid NOT NULL REFERENCES retravion_need.need(internal_id),
            actor_identifier text NOT NULL,
            occurred_at_utc_ticks bigint NOT NULL,
            decision text NOT NULL,
            comment text,
            title text NOT NULL,
            statement text NOT NULL
        );
        """;

    public Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT internal_id, product_internal_id, id, title, statement, status, revision FROM retravion_need.need WHERE product_internal_id = $1 ORDER BY id", connection, transaction);
            command.Parameters.AddWithValue(productInternalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<UserNeedSnapshot>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var restored = Read(reader);
                if (restored.IsFailure) return Result.Failure<IReadOnlyList<UserNeedSnapshot>>(restored.Error);
                rows.Add(restored.Value);
            }
            return Result.Success<IReadOnlyList<UserNeedSnapshot>>(rows);
        }, cancellationToken);

    public Task<Result<Maybe<UserNeedSnapshot>>> Find(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT internal_id, product_internal_id, id, title, statement, status, revision FROM retravion_need.need WHERE internal_id = $1", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<UserNeedSnapshot>.None);
            var restored = Read(reader);
            return restored.IsSuccess ? Result.Success(Maybe<UserNeedSnapshot>.From(restored.Value))
                : Result.Failure<Maybe<UserNeedSnapshot>>(restored.Error);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<UserNeedReview>>> History(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT actor_identifier, occurred_at_utc_ticks, decision, comment, title, statement FROM retravion_need.review WHERE need_internal_id = $1 ORDER BY id", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<UserNeedReview>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new(internalId, new AuditActor.Identified(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            return Result.Success<IReadOnlyList<UserNeedReview>>(rows);
        }, cancellationToken);

    public Task<Result<UserNeedSnapshot>> Create(Need need, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        if (need.Status != UserNeedStatus.Draft || actor is not AuditActor.Identified)
            return Task.FromResult(Result.Failure<UserNeedSnapshot>("Invalid User Need creation."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, need.ProductInternalId, cancellationToken))
                return Result.Failure<UserNeedSnapshot>("The responsible Product is retired or no longer exists.");
            await using var command = new NpgsqlCommand("INSERT INTO retravion_need.need (internal_id, product_internal_id, id, title, statement, status, revision) VALUES ($1,$2,$3,$4,$5,$6,1) ON CONFLICT DO NOTHING", connection, transaction);
            command.Parameters.AddWithValue(need.InternalId);
            command.Parameters.AddWithValue(need.ProductInternalId);
            command.Parameters.AddWithValue(need.Id);
            command.Parameters.AddWithValue(need.Title);
            command.Parameters.AddWithValue(need.Statement);
            command.Parameters.AddWithValue((int)need.Status);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return Result.Failure<UserNeedSnapshot>("That User Need ID is already in use for this Product.");
            await Audit(connection, transaction, actor, occurredAt, "need.create", null, need, null, null, cancellationToken);
            return Result.Success(new UserNeedSnapshot(need, 1));
        }, cancellationToken);
    }

    public Task<Result<UserNeedSnapshot>> Update(UserNeedSnapshot expected, Need replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision = null, string? comment = null, CancellationToken cancellationToken = default)
    {
        var before = expected.Need;
        if (actor is not AuditActor.Identified identified || expected.Revision <= 0 || before.InternalId != replacement.InternalId || before.ProductInternalId != replacement.ProductInternalId)
            return Task.FromResult(Result.Failure<UserNeedSnapshot>("Invalid User Need update."));
        var transition = before.Status != replacement.Status;
        var validTransition = (before.Status, replacement.Status) is (UserNeedStatus.Draft, UserNeedStatus.InReview)
            or (UserNeedStatus.InReview, UserNeedStatus.Approved) or (UserNeedStatus.InReview, UserNeedStatus.Draft)
            or (UserNeedStatus.Approved, UserNeedStatus.Draft);
        if (transition ? !validTransition || before.Id != replacement.Id || before.Title != replacement.Title || before.Statement != replacement.Statement || decision != replacement.Status.ToString()
            : before.Status != UserNeedStatus.Draft || decision is not null || comment is not null)
            return Task.FromResult(Result.Failure<UserNeedSnapshot>("Invalid User Need update."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, before.ProductInternalId, cancellationToken))
                return Result.Failure<UserNeedSnapshot>("The responsible Product is retired or no longer exists.");
            // Lock and compare the entire expected snapshot as well as its revision, including no-op saves.
            await using var command = new NpgsqlCommand("""
                UPDATE retravion_need.need SET id=$1, title=$2, statement=$3, status=$4,
                    revision=revision + CASE WHEN $5 THEN 1 ELSE 0 END
                WHERE internal_id=$6 AND product_internal_id=$7 AND revision=$8
                    AND id=$9 AND title=$10 AND statement=$11 AND status=$12
                RETURNING revision
                """, connection, transaction);
            var changed = before != replacement;
            command.Parameters.AddWithValue(replacement.Id);
            command.Parameters.AddWithValue(replacement.Title);
            command.Parameters.AddWithValue(replacement.Statement);
            command.Parameters.AddWithValue((int)replacement.Status);
            command.Parameters.AddWithValue(changed);
            command.Parameters.AddWithValue(before.InternalId);
            command.Parameters.AddWithValue(before.ProductInternalId);
            command.Parameters.AddWithValue(expected.Revision);
            command.Parameters.AddWithValue(before.Id);
            command.Parameters.AddWithValue(before.Title);
            command.Parameters.AddWithValue(before.Statement);
            command.Parameters.AddWithValue((int)before.Status);
            var version = await command.ExecuteScalarAsync(cancellationToken);
            if (version is null) return Result.Failure<UserNeedSnapshot>("The User Need changed or no longer exists. Retrieve it again.");
            if (transition)
            {
                await using var history = new NpgsqlCommand("INSERT INTO retravion_need.review (need_internal_id, actor_identifier, occurred_at_utc_ticks, decision, comment, title, statement) VALUES ($1,$2,$3,$4,$5,$6,$7)", connection, transaction);
                history.Parameters.AddWithValue(before.InternalId);
                history.Parameters.AddWithValue(identified.Identifier);
                history.Parameters.AddWithValue(occurredAt.UtcTicks);
                history.Parameters.AddWithValue(decision!);
                history.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)comment ?? DBNull.Value);
                history.Parameters.AddWithValue(before.Title);
                history.Parameters.AddWithValue(before.Statement);
                await history.ExecuteNonQueryAsync(cancellationToken);
            }
            if (changed) await Audit(connection, transaction, actor, occurredAt, transition ? "need.review." + decision : "need.edit", before, replacement, decision, comment, cancellationToken);
            return Result.Success(new UserNeedSnapshot(replacement, (long)version));
        }, cancellationToken);
    }

    private static Result<UserNeedSnapshot> Read(NpgsqlDataReader reader)
    {
        var need = Need.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), (UserNeedStatus)reader.GetInt32(5));
        return need.IsSuccess && reader.GetInt64(6) > 0 ? Result.Success(new UserNeedSnapshot(need.Value, reader.GetInt64(6)))
            : Result.Failure<UserNeedSnapshot>("The persisted User Need is invalid.");
    }

    private static async Task<bool> ProductActive(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        // This row lock serializes need writes with concurrent Product retirement.
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor actor,
        DateTimeOffset occurredAt, string action, Need? before, Need after, string? decision, string? comment, CancellationToken cancellationToken)
    {
        static UserNeedAuditValues Values(Need n) => new(n.ProductInternalId, n.Id, n.Title, n.Statement, n.Status.ToString());
        await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success,
                userNeedChange: new UserNeedAuditDetails(after.InternalId, before is null ? null : Values(before), Values(after), decision, comment)), cancellationToken);
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken cancellationToken)
    {
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var setup = new NpgsqlCommand(Setup, connection, transaction)) await setup.ExecuteNonQueryAsync(cancellationToken);
            var result = await operation(connection, transaction);
            if (result.IsSuccess)
            {
                commitStarted = true;
                if (commit is null) await transaction.CommitAsync(cancellationToken);
                else await commit(transaction, cancellationToken);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        {
            return Result.Failure<T>("The User Need operation outcome could not be confirmed. Retrieve the User Need before retrying.");
        }
        catch (PostgresException exception) when (!cancellationToken.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Result.Failure<T>("That User Need ID is already in use for this Product.");
        }
        catch (NpgsqlException exception) when (!cancellationToken.IsCancellationRequested && (exception.IsTransient || exception is PostgresException
        {
            SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction
        }))
        {
            return Result.Failure<T>("The User Need operation could not be completed.");
        }
    }
}
