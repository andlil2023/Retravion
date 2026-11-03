using CSharpFunctionalExtensions;
using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;
using Retravion.Application.Requirement;
using Retravion.Domain.Requirement;
using Retravion.Infrastructure.AuditTrail;
using RequirementState = Retravion.Domain.Requirement.Requirement;

namespace Retravion.Infrastructure.Requirement;

public sealed class PostgresRequirementStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : IRequirementStore, IRequirementChangeTransaction
{
    private const string Columns = "internal_id, product_internal_id, id, title, statement, type, rationale, status, revision";
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620112);
        CREATE SCHEMA IF NOT EXISTS retravion_requirement;
        CREATE TABLE IF NOT EXISTS retravion_requirement.requirement (
            internal_id uuid PRIMARY KEY,
            product_internal_id uuid NOT NULL REFERENCES retravion_product.product(internal_id),
            id text NOT NULL CHECK (length(btrim(id)) > 0 AND char_length(id) <= 100),
            title text NOT NULL CHECK (length(btrim(title)) > 0),
            statement text NOT NULL CHECK (length(btrim(statement)) > 0),
            type integer NOT NULL CHECK (type BETWEEN 0 AND 5),
            rationale text,
            status integer NOT NULL CHECK (status BETWEEN 0 AND 2),
            revision bigint NOT NULL CHECK (revision > 0)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS requirement_product_id_unique ON retravion_requirement.requirement
            (product_internal_id, id COLLATE retravion_product.product_case_fold);
        CREATE TABLE IF NOT EXISTS retravion_requirement.review (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            requirement_internal_id uuid NOT NULL REFERENCES retravion_requirement.requirement(internal_id),
            actor_identifier text NOT NULL,
            occurred_at_utc_ticks bigint NOT NULL,
            decision text NOT NULL,
            comment text,
            manual_id text NOT NULL,
            title text NOT NULL,
            statement text NOT NULL,
            type integer NOT NULL CHECK (type BETWEEN 0 AND 5),
            rationale text
        );
        """;

    public Task<Result<IReadOnlyList<RequirementSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_requirement.requirement WHERE product_internal_id=$1 ORDER BY id", connection, transaction);
            command.Parameters.AddWithValue(productInternalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<RequirementSnapshot>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var restored = Read(reader);
                if (restored.IsFailure) return Result.Failure<IReadOnlyList<RequirementSnapshot>>(restored.Error);
                rows.Add(restored.Value);
            }
            return Result.Success<IReadOnlyList<RequirementSnapshot>>(rows);
        }, cancellationToken, Result.Success<IReadOnlyList<RequirementSnapshot>>([]));

    public Task<Result<Maybe<RequirementSnapshot>>> Find(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_requirement.requirement WHERE internal_id=$1", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<RequirementSnapshot>.None);
            var restored = Read(reader);
            return restored.IsSuccess ? Result.Success(Maybe<RequirementSnapshot>.From(restored.Value))
                : Result.Failure<Maybe<RequirementSnapshot>>(restored.Error);
        }, cancellationToken, Result.Success(Maybe<RequirementSnapshot>.None));

    public Task<Result<IReadOnlyList<RequirementReview>>> History(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT actor_identifier, occurred_at_utc_ticks, decision, comment, manual_id, title, statement, type, rationale FROM retravion_requirement.review WHERE requirement_internal_id=$1 ORDER BY id", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<RequirementReview>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var type = (RequirementType)reader.GetInt32(7);
                if (!Enum.IsDefined(type)) return Result.Failure<IReadOnlyList<RequirementReview>>("The persisted Requirement review is invalid.");
                rows.Add(new(internalId, new AuditActor.Identified(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    reader.GetString(2), Text(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetString(6), type, Text(reader, 8)));
            }
            return Result.Success<IReadOnlyList<RequirementReview>>(rows);
        }, cancellationToken, Result.Success<IReadOnlyList<RequirementReview>>([]));

    public Task<Result<RequirementSnapshot>> Create(RequirementState requirement, AuditActor actor, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        if (requirement.Status != RequirementStatus.Draft || actor is not AuditActor.Identified)
            return Task.FromResult(Result.Failure<RequirementSnapshot>("Invalid Requirement creation."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, requirement.ProductInternalId, cancellationToken))
                return Result.Failure<RequirementSnapshot>("The responsible Product is retired or no longer exists.");
            await using var command = new NpgsqlCommand($"INSERT INTO retravion_requirement.requirement ({Columns}) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,1) ON CONFLICT DO NOTHING", connection, transaction);
            command.Parameters.AddWithValue(requirement.InternalId);
            command.Parameters.AddWithValue(requirement.ProductInternalId);
            command.Parameters.AddWithValue(requirement.Id);
            command.Parameters.AddWithValue(requirement.Title);
            command.Parameters.AddWithValue(requirement.Statement);
            command.Parameters.AddWithValue((int)requirement.Type);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)requirement.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue((int)requirement.Status);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                return Result.Failure<RequirementSnapshot>("That Requirement ID is already in use for this Product.");
            await Audit(connection, transaction, actor, occurredAt, "requirement.create", null, requirement, null, null, cancellationToken);
            return Result.Success(new RequirementSnapshot(requirement, 1));
        }, cancellationToken);
    }

    public Task<Result<RequirementSnapshot>> Update(RequirementSnapshot expected, RequirementState replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision = null, string? comment = null, CancellationToken cancellationToken = default)
    {
        var before = expected.Requirement;
        if (actor is not AuditActor.Identified identified || expected.Revision <= 0 ||
            before.InternalId != replacement.InternalId || before.ProductInternalId != replacement.ProductInternalId)
            return Task.FromResult(Result.Failure<RequirementSnapshot>("Invalid Requirement update."));
        var transition = before.Status != replacement.Status;
        var validTransition = (before.Status, replacement.Status) is (RequirementStatus.Draft, RequirementStatus.InReview)
            or (RequirementStatus.InReview, RequirementStatus.Approved) or (RequirementStatus.InReview, RequirementStatus.Draft)
            or (RequirementStatus.Approved, RequirementStatus.Draft);
        if (transition ? !validTransition || before.Id != replacement.Id || before.Title != replacement.Title ||
                before.Statement != replacement.Statement || before.Type != replacement.Type || before.Rationale != replacement.Rationale ||
                decision != replacement.Status.ToString()
            : before.Status != RequirementStatus.Draft || decision is not null || comment is not null)
            return Task.FromResult(Result.Failure<RequirementSnapshot>("Invalid Requirement update."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, before.ProductInternalId, cancellationToken))
                return Result.Failure<RequirementSnapshot>("The responsible Product is retired or no longer exists.");
            // Compare the complete expected snapshot, even for a no-op save; never trust a token alone.
            await using var command = new NpgsqlCommand("""
                UPDATE retravion_requirement.requirement SET id=$1, title=$2, statement=$3, type=$4, rationale=$5, status=$6,
                    revision=revision + CASE WHEN $7 THEN 1 ELSE 0 END
                WHERE internal_id=$8 AND product_internal_id=$9 AND revision=$10
                    AND id=$11 AND title=$12 AND statement=$13 AND type=$14
                    AND rationale IS NOT DISTINCT FROM $15 AND status=$16
                RETURNING revision
                """, connection, transaction);
            var changed = before != replacement;
            command.Parameters.AddWithValue(replacement.Id);
            command.Parameters.AddWithValue(replacement.Title);
            command.Parameters.AddWithValue(replacement.Statement);
            command.Parameters.AddWithValue((int)replacement.Type);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)replacement.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue((int)replacement.Status);
            command.Parameters.AddWithValue(changed);
            command.Parameters.AddWithValue(before.InternalId);
            command.Parameters.AddWithValue(before.ProductInternalId);
            command.Parameters.AddWithValue(expected.Revision);
            command.Parameters.AddWithValue(before.Id);
            command.Parameters.AddWithValue(before.Title);
            command.Parameters.AddWithValue(before.Statement);
            command.Parameters.AddWithValue((int)before.Type);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)before.Rationale ?? DBNull.Value);
            command.Parameters.AddWithValue((int)before.Status);
            var revision = await command.ExecuteScalarAsync(cancellationToken);
            if (revision is null) return Result.Failure<RequirementSnapshot>("The Requirement changed or no longer exists. Retrieve it again.");
            if (transition)
            {
                await using var history = new NpgsqlCommand("INSERT INTO retravion_requirement.review (requirement_internal_id, actor_identifier, occurred_at_utc_ticks, decision, comment, manual_id, title, statement, type, rationale) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)", connection, transaction);
                history.Parameters.AddWithValue(before.InternalId);
                history.Parameters.AddWithValue(identified.Identifier);
                history.Parameters.AddWithValue(occurredAt.UtcTicks);
                history.Parameters.AddWithValue(decision!);
                history.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)comment ?? DBNull.Value);
                history.Parameters.AddWithValue(before.Id);
                history.Parameters.AddWithValue(before.Title);
                history.Parameters.AddWithValue(before.Statement);
                history.Parameters.AddWithValue((int)before.Type);
                history.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)before.Rationale ?? DBNull.Value);
                await history.ExecuteNonQueryAsync(cancellationToken);
            }
            if (changed) await Audit(connection, transaction, actor, occurredAt,
                transition ? "requirement.review." + decision : "requirement.edit", before, replacement, decision, comment, cancellationToken);
            return Result.Success(new RequirementSnapshot(replacement, (long)revision));
        }, cancellationToken);
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Result<RequirementSnapshot> Read(NpgsqlDataReader reader)
    {
        var requirement = RequirementState.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), (RequirementType)reader.GetInt32(5), Text(reader, 6), (RequirementStatus)reader.GetInt32(7));
        return requirement.IsSuccess && reader.GetInt64(8) > 0 ? Result.Success(new RequirementSnapshot(requirement.Value, reader.GetInt64(8)))
            : Result.Failure<RequirementSnapshot>("The persisted Requirement is invalid.");
    }

    private static async Task<bool> ProductActive(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        // Serialize writes with Product retirement, using the existing Product eligibility contract.
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static async Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor actor,
        DateTimeOffset occurredAt, string action, RequirementState? before, RequirementState after, string? decision, string? comment, CancellationToken token)
    {
        static RequirementAuditValues Values(RequirementState r) => new(r.ProductInternalId, r.Id, r.Title, r.Statement, r.Type.ToString(), r.Rationale, r.Status.ToString());
        await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success,
                requirementChange: new RequirementAuditDetails(after.InternalId, before is null ? null : Values(before), Values(after), decision, comment)), token);
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token,
        Result<T>? whenProductStorageMissing = null)
    {
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            // Product storage is lazy-initialized by its owning feature. A fresh installation
            // has no Requirements to read, and no eligible Product for writes; do not create
            // Product tables here or rely on users visiting Product navigation first.
            await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_product.product') IS NOT NULL", connection, transaction))
                if (await exists.ExecuteScalarAsync(token) is not true)
                    return whenProductStorageMissing ?? Result.Failure<T>("The responsible Product is retired or no longer exists.");
            await using (var setup = new NpgsqlCommand(Setup, connection, transaction)) await setup.ExecuteNonQueryAsync(token);
            var result = await operation(connection, transaction);
            if (result.IsSuccess)
            {
                commitStarted = true;
                if (commit is null) await transaction.CommitAsync(token);
                else await commit(transaction, token);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        {
            return Result.Failure<T>("The Requirement operation outcome could not be confirmed. Retrieve the Requirement before retrying.");
        }
        catch (PostgresException exception) when (!token.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Result.Failure<T>("That Requirement ID is already in use for this Product.");
        }
        catch (NpgsqlException exception) when (!token.IsCancellationRequested && (exception.IsTransient || exception is PostgresException
        { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction }))
        {
            return Result.Failure<T>("The Requirement operation could not be completed.");
        }
    }
}
