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
    // The entity row remains the default-version projection and the Product-scoped ID reservation.
    // Existing review snapshots are evidence, not reconstructed historical versions.
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
            actor_identifier text NOT NULL, occurred_at_utc_ticks bigint NOT NULL,
            decision text NOT NULL, comment text, title text NOT NULL, statement text NOT NULL
        );
        ALTER TABLE retravion_need.need ADD COLUMN IF NOT EXISTS version_id uuid NOT NULL DEFAULT gen_random_uuid();
        ALTER TABLE retravion_need.need ADD COLUMN IF NOT EXISTS version_number integer NOT NULL DEFAULT 1 CHECK (version_number > 0);
        ALTER TABLE retravion_need.need ADD COLUMN IF NOT EXISTS ever_approved boolean NOT NULL DEFAULT false;
        ALTER TABLE retravion_need.need ADD COLUMN IF NOT EXISTS creation_reason text;
        ALTER TABLE retravion_need.need ADD COLUMN IF NOT EXISTS source_version_id uuid;
        UPDATE retravion_need.need n SET ever_approved = true WHERE NOT ever_approved AND
            (status = 2 OR EXISTS (SELECT 1 FROM retravion_need.review r WHERE r.need_internal_id=n.internal_id AND r.decision='Approved'));
        CREATE TABLE IF NOT EXISTS retravion_need.need_version (
            internal_id uuid NOT NULL REFERENCES retravion_need.need(internal_id),
            product_internal_id uuid NOT NULL REFERENCES retravion_product.product(internal_id),
            id text NOT NULL CHECK (length(btrim(id)) > 0 AND char_length(id) <= 100),
            title text NOT NULL CHECK (length(btrim(title)) > 0),
            statement text NOT NULL CHECK (length(btrim(statement)) > 0),
            status integer NOT NULL CHECK (status BETWEEN 0 AND 2),
            revision bigint NOT NULL CHECK (revision > 0),
            version_id uuid PRIMARY KEY,
            version_number integer NOT NULL CHECK (version_number > 0),
            ever_approved boolean NOT NULL,
            creation_reason text,
            source_version_id uuid REFERENCES retravion_need.need_version(version_id),
            UNIQUE (internal_id, version_number),
            CHECK ((version_number=1 AND creation_reason IS NULL AND source_version_id IS NULL) OR
                (version_number>1 AND length(btrim(creation_reason))>0 AND source_version_id IS NOT NULL))
        );
        CREATE UNIQUE INDEX IF NOT EXISTS need_one_working_version ON retravion_need.need_version(internal_id) WHERE status <> 2;
        INSERT INTO retravion_need.need_version SELECT internal_id,product_internal_id,id,title,statement,status,revision,
            version_id,version_number,ever_approved,creation_reason,source_version_id FROM retravion_need.need
            ON CONFLICT (version_id) DO NOTHING;
        ALTER TABLE retravion_need.review ADD COLUMN IF NOT EXISTS version_id uuid;
        ALTER TABLE retravion_need.review ADD COLUMN IF NOT EXISTS version_number integer;
        ALTER TABLE retravion_need.review ADD COLUMN IF NOT EXISTS manual_id text;
        ALTER TABLE retravion_need.review ADD COLUMN IF NOT EXISTS source_version_id uuid;
        """;

    private const string Columns = "n.internal_id,n.product_internal_id,n.id,n.title,n.statement,n.status,n.revision,n.version_id,n.version_number,n.ever_approved,n.creation_reason,n.source_version_id,(SELECT max(v.version_number) FROM retravion_need.need_version v WHERE v.internal_id=n.internal_id AND v.status=2)";

    public Task<Result<IReadOnlyList<UserNeedSnapshot>>> List(Guid productInternalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_need.need n WHERE product_internal_id=$1 ORDER BY id", productInternalId, cancellationToken);

    public Task<Result<IReadOnlyList<UserNeedSnapshot>>> Versions(Guid internalId, CancellationToken cancellationToken = default) =>
        Rows($"SELECT {Columns} FROM retravion_need.need_version n WHERE internal_id=$1 ORDER BY version_number", internalId, cancellationToken);

    private Task<Result<IReadOnlyList<UserNeedSnapshot>>> Rows(string sql, Guid id, CancellationToken token) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(id);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<UserNeedSnapshot>();
            while (await reader.ReadAsync(token))
            {
                var restored = Read(reader);
                if (restored.IsFailure) return Result.Failure<IReadOnlyList<UserNeedSnapshot>>(restored.Error);
                rows.Add(restored.Value);
            }
            return Result.Success<IReadOnlyList<UserNeedSnapshot>>(rows);
        }, token);

    public Task<Result<Maybe<UserNeedSnapshot>>> Find(Guid internalId, CancellationToken cancellationToken = default, Guid? versionId = null) =>
        Execute(async (connection, transaction) =>
        {
            var sql = versionId is null ? $"SELECT {Columns} FROM retravion_need.need n WHERE internal_id=$1" :
                $"SELECT {Columns} FROM retravion_need.need_version n WHERE internal_id=$1 AND version_id=$2";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(internalId);
            if (versionId is not null) command.Parameters.AddWithValue(versionId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return Result.Success(Maybe<UserNeedSnapshot>.None);
            var restored = Read(reader);
            return restored.IsSuccess ? Result.Success(Maybe<UserNeedSnapshot>.From(restored.Value)) : Result.Failure<Maybe<UserNeedSnapshot>>(restored.Error);
        }, cancellationToken);

    public Task<Result<IReadOnlyList<UserNeedReview>>> History(Guid internalId, CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT actor_identifier,occurred_at_utc_ticks,decision,comment,title,statement,version_id,version_number,manual_id,source_version_id FROM retravion_need.review WHERE need_internal_id=$1 ORDER BY id", connection, transaction);
            command.Parameters.AddWithValue(internalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<UserNeedReview>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new(internalId, new AuditActor.Identified(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetGuid(9)));
            return Result.Success<IReadOnlyList<UserNeedReview>>(rows);
        }, cancellationToken);

    public Task<Result<UserNeedSnapshot>> Create(Need need, AuditActor actor, DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        if (need.Status != UserNeedStatus.Draft || need.VersionNumber != 1 || need.HasBeenApproved || actor is not AuditActor.Identified)
            return Task.FromResult(Result.Failure<UserNeedSnapshot>("Invalid User Need creation."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, need.ProductInternalId, cancellationToken)) return Inactive();
            await using var command = new NpgsqlCommand("INSERT INTO retravion_need.need (internal_id,product_internal_id,id,title,statement,status,revision,version_id) VALUES ($1,$2,$3,$4,$5,0,1,$6) ON CONFLICT DO NOTHING", connection, transaction);
            command.Parameters.AddWithValue(need.InternalId); command.Parameters.AddWithValue(need.ProductInternalId);
            command.Parameters.AddWithValue(need.Id); command.Parameters.AddWithValue(need.Title); command.Parameters.AddWithValue(need.Statement);
            command.Parameters.AddWithValue(need.VersionId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return Result.Failure<UserNeedSnapshot>("That User Need ID is already in use for this Product.");
            if (!await SaveVersion(connection, transaction, need.InternalId, cancellationToken))
                return Result.Failure<UserNeedSnapshot>("The User Need version identity is already in use.");
            await Audit(connection, transaction, actor, occurredAt, "need.create", null, need, null, null, cancellationToken);
            return Result.Success(new UserNeedSnapshot(need, 1));
        }, cancellationToken);
    }

    public Task<Result<UserNeedSnapshot>> CreateSuccessor(UserNeedSnapshot expected, string? reason, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        var successor = expected.Need.CreateSuccessor(reason);
        return successor.IsFailure ? Task.FromResult(Result.Failure<UserNeedSnapshot>(successor.Error)) :
            Mutate(expected, successor.Value, actor, occurredAt, "Successor", reason, cancellationToken);
    }

    public Task<Result<UserNeedSnapshot>> Update(UserNeedSnapshot expected, Need replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision = null, string? comment = null, CancellationToken cancellationToken = default)
    {
        var before = expected.Need;
        var valid = decision switch
        {
            "InReview" => before.SubmitForReview(), "Approved" => before.Approve(), "Draft" => before.ReturnToDraft(),
            "Reopened" => before.Reopen(comment),
            null when comment is null => before.Edit(replacement.Id, replacement.Title, replacement.Statement),
            _ => Result.Failure<Need>("Invalid review decision.")
        };
        if (valid.IsFailure || valid.Value != replacement) return Task.FromResult(Result.Failure<UserNeedSnapshot>(valid.IsFailure ? valid.Error : "Invalid User Need update."));
        return Mutate(expected, replacement, actor, occurredAt, decision, comment, cancellationToken);
    }

    private Task<Result<UserNeedSnapshot>> Mutate(UserNeedSnapshot expected, Need replacement, AuditActor actor,
        DateTimeOffset occurredAt, string? decision, string? comment, CancellationToken token)
    {
        var before = expected.Need;
        if (actor is not AuditActor.Identified identified || expected.Revision <= 0)
            return Task.FromResult(Result.Failure<UserNeedSnapshot>("Invalid User Need update."));
        return Execute(async (connection, transaction) =>
        {
            if (!await ProductActive(connection, transaction, before.ProductInternalId, token)) return Inactive();
            // The projection is always the newest version. This compare also blocks older-version reopening
            // and races between successor creation, reopening and edits. Revisions are entity-wide, never reset.
            await using var command = new NpgsqlCommand("""
                UPDATE retravion_need.need SET id=$1,title=$2,statement=$3,status=$4,revision=revision+CASE WHEN $5 THEN 1 ELSE 0 END,
                    version_id=$6,version_number=$7,ever_approved=$8,creation_reason=$9,source_version_id=$10
                WHERE internal_id=$11 AND product_internal_id=$12 AND revision=$13 AND version_id=$14
                    AND id=$15 AND title=$16 AND statement=$17 AND status=$18 AND version_number=$19
                    AND ever_approved=$20 AND creation_reason IS NOT DISTINCT FROM $21 AND source_version_id IS NOT DISTINCT FROM $22
                RETURNING revision
                """, connection, transaction);
            var changed = before != replacement;
            command.Parameters.AddWithValue(replacement.Id); command.Parameters.AddWithValue(replacement.Title); command.Parameters.AddWithValue(replacement.Statement);
            command.Parameters.AddWithValue((int)replacement.Status); command.Parameters.AddWithValue(changed);
            command.Parameters.AddWithValue(replacement.VersionId); command.Parameters.AddWithValue(replacement.VersionNumber); command.Parameters.AddWithValue(replacement.HasBeenApproved);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)replacement.CreationReason ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)replacement.SourceVersionId ?? DBNull.Value);
            command.Parameters.AddWithValue(before.InternalId); command.Parameters.AddWithValue(before.ProductInternalId); command.Parameters.AddWithValue(expected.Revision);
            command.Parameters.AddWithValue(before.VersionId); command.Parameters.AddWithValue(before.Id); command.Parameters.AddWithValue(before.Title); command.Parameters.AddWithValue(before.Statement);
            command.Parameters.AddWithValue((int)before.Status); command.Parameters.AddWithValue(before.VersionNumber); command.Parameters.AddWithValue(before.HasBeenApproved);
            command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)before.CreationReason ?? DBNull.Value);
            command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)before.SourceVersionId ?? DBNull.Value);
            var revision = await command.ExecuteScalarAsync(token);
            if (revision is null) return Result.Failure<UserNeedSnapshot>("The User Need changed or a newer version exists. Retrieve it again.");
            if (!await SaveVersion(connection, transaction, before.InternalId, token))
                return Result.Failure<UserNeedSnapshot>("The User Need version identity is already in use.");
            if (decision is not null)
            {
                await using var history = new NpgsqlCommand("INSERT INTO retravion_need.review (need_internal_id,actor_identifier,occurred_at_utc_ticks,decision,comment,title,statement,version_id,version_number,manual_id,source_version_id) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)", connection, transaction);
                history.Parameters.AddWithValue(before.InternalId); history.Parameters.AddWithValue(identified.Identifier); history.Parameters.AddWithValue(occurredAt.UtcTicks);
                history.Parameters.AddWithValue(decision); history.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)comment ?? DBNull.Value);
                history.Parameters.AddWithValue(replacement.Title); history.Parameters.AddWithValue(replacement.Statement);
                history.Parameters.AddWithValue(replacement.VersionId); history.Parameters.AddWithValue(replacement.VersionNumber); history.Parameters.AddWithValue(replacement.Id);
                history.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)replacement.SourceVersionId ?? DBNull.Value);
                await history.ExecuteNonQueryAsync(token);
            }
            if (changed) await Audit(connection, transaction, actor, occurredAt, decision switch
            {
                "Successor" => "need.version.create", "Reopened" => "need.reopen", null => "need.edit", _ => "need.review." + decision
            }, before, replacement, decision, comment, token);
            await using var latest = new NpgsqlCommand("SELECT max(version_number) FROM retravion_need.need_version WHERE internal_id=$1 AND status=2", connection, transaction);
            latest.Parameters.AddWithValue(before.InternalId);
            var approved = await latest.ExecuteScalarAsync(token);
            return Result.Success(new UserNeedSnapshot(replacement, (long)revision, approved is int number ? number : null));
        }, token);
    }

    private static async Task<bool> SaveVersion(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO retravion_need.need_version SELECT internal_id,product_internal_id,id,title,statement,status,revision,
                version_id,version_number,ever_approved,creation_reason,source_version_id FROM retravion_need.need WHERE internal_id=$1
            ON CONFLICT (version_id) DO UPDATE SET id=EXCLUDED.id,title=EXCLUDED.title,statement=EXCLUDED.statement,
                status=EXCLUDED.status,revision=EXCLUDED.revision,ever_approved=EXCLUDED.ever_approved
            WHERE need_version.internal_id=EXCLUDED.internal_id
                AND need_version.product_internal_id=EXCLUDED.product_internal_id
                AND need_version.version_number=EXCLUDED.version_number
                AND need_version.creation_reason IS NOT DISTINCT FROM EXCLUDED.creation_reason
                AND need_version.source_version_id IS NOT DISTINCT FROM EXCLUDED.source_version_id
            """, connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    private static Result<UserNeedSnapshot> Read(NpgsqlDataReader reader)
    {
        var need = Need.Restore(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            (UserNeedStatus)reader.GetInt32(5), reader.GetGuid(7), reader.GetInt32(8), reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetGuid(11));
        return need.IsSuccess && reader.GetInt64(6) > 0 ? Result.Success(new UserNeedSnapshot(need.Value, reader.GetInt64(6), reader.IsDBNull(12) ? null : reader.GetInt32(12)))
            : Result.Failure<UserNeedSnapshot>("The persisted User Need is invalid.");
    }

    private static Result<UserNeedSnapshot> Inactive() => Result.Failure<UserNeedSnapshot>("The responsible Product is retired or no longer exists.");

    private static async Task<bool> ProductActive(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT is_active FROM retravion_product.product WHERE internal_id=$1 FOR SHARE", connection, transaction);
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(token) is true;
    }

    private static async Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, AuditActor actor,
        DateTimeOffset occurredAt, string action, Need? before, Need after, string? decision, string? comment, CancellationToken token)
    {
        static UserNeedAuditValues Values(Need n) => new(n.ProductInternalId, n.Id, n.Title, n.Statement, n.Status.ToString());
        await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
            new AuditInformation(actor, occurredAt, action, new AuditTarget.Identified(after.InternalId.ToString()), AuditActionOutcome.Success,
                userNeedChange: new UserNeedAuditDetails(after.InternalId, before is null ? null : Values(before), Values(after), decision, comment,
                    after.VersionId, after.VersionNumber, after.SourceVersionId, decision is "Successor" or "Reopened" ? comment : null)), token);
    }

    private async Task<Result<T>> Execute<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<T>>> operation, CancellationToken token)
    {
        var commitStarted = false;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using (var setup = new NpgsqlCommand(Setup, connection, transaction)) await setup.ExecuteNonQueryAsync(token);
            var result = await operation(connection, transaction);
            if (result.IsSuccess)
            {
                commitStarted = true;
                if (commit is null) await transaction.CommitAsync(token); else await commit(transaction, token);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        { return Result.Failure<T>("The User Need operation outcome could not be confirmed. Retrieve the User Need before retrying."); }
        catch (PostgresException exception) when (!token.IsCancellationRequested && exception.SqlState == PostgresErrorCodes.UniqueViolation)
        { return Result.Failure<T>("That User Need ID or working version is already in use. Retrieve it again."); }
        catch (NpgsqlException exception) when (!token.IsCancellationRequested && (exception.IsTransient || exception is PostgresException
        { SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction }))
        { return Result.Failure<T>("The User Need operation could not be completed."); }
    }
}
