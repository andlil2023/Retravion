using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;

namespace Retravion.Infrastructure.AuditTrail;

/// <summary>Persists audit facts without exposing a read, update, or delete API.</summary>
public sealed class PostgresAuditSubmission(string connectionString) : IAuditSubmission
{
    public async Task<AuditSubmissionResult> SubmitAsync(AuditInformation information,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(information);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Serialize first-use DDL across instances. This lock is distinct from Identity's.
            // Force durable commit even if the connection/database defaults disable it.
            await using (var setup = new NpgsqlCommand("""
                SET LOCAL synchronous_commit = on;
                SELECT pg_advisory_xact_lock(821749620109);
                CREATE SCHEMA IF NOT EXISTS retravion_audit;
                CREATE TABLE IF NOT EXISTS retravion_audit.event (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    actor_kind text NOT NULL CHECK (actor_kind IN ('identified', 'unknown')),
                    actor_identifier text,
                    occurred_at_utc_ticks bigint NOT NULL
                        CHECK (occurred_at_utc_ticks BETWEEN 0 AND 3155378975999999999),
                    action text NOT NULL CHECK (length(btrim(action)) > 0),
                    target_kind text NOT NULL CHECK (target_kind IN ('identified', 'unknown', 'not_applicable')),
                    target_identifier text,
                    outcome text NOT NULL CHECK (outcome IN ('success', 'failure')),
                    CHECK ((actor_kind = 'identified' AND actor_identifier IS NOT NULL
                                AND length(btrim(actor_identifier)) > 0)
                        OR (actor_kind = 'unknown' AND actor_identifier IS NULL)),
                    CHECK ((target_kind = 'identified' AND target_identifier IS NOT NULL
                                AND length(btrim(target_identifier)) > 0)
                        OR (target_kind IN ('unknown', 'not_applicable') AND target_identifier IS NULL))
                );
                """, connection, transaction))
                await setup.ExecuteNonQueryAsync(cancellationToken);

            await using var command = new NpgsqlCommand("""
                INSERT INTO retravion_audit.event
                    (actor_kind, actor_identifier, occurred_at_utc_ticks, action,
                        target_kind, target_identifier, outcome)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """, connection, transaction);
            command.Parameters.AddWithValue(information.Actor is AuditActor.Identified ? "identified" : "unknown");
            command.Parameters.AddWithValue(NpgsqlDbType.Text,
                information.Actor is AuditActor.Identified actor ? actor.Identifier : DBNull.Value);
            // PostgreSQL timestamps truncate sub-microsecond ticks; retain the exact UTC instant instead.
            command.Parameters.AddWithValue(information.OccurredAt.UtcTicks);
            command.Parameters.AddWithValue(information.Action);
            command.Parameters.AddWithValue(information.Target switch
            {
                AuditTarget.Identified => "identified",
                AuditTarget.Unknown => "unknown",
                AuditTarget.NotApplicable => "not_applicable",
                _ => throw new ArgumentOutOfRangeException(nameof(information))
            });
            command.Parameters.AddWithValue(NpgsqlDbType.Text,
                information.Target is AuditTarget.Identified target ? target.Identifier : DBNull.Value);
            command.Parameters.AddWithValue(information.Outcome == AuditActionOutcome.Success ? "success" : "failure");
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AuditSubmissionResult.Success;
        }
        // Availability/resource/concurrency failures and denied writes are expected storage failures.
        // Programming/schema/configuration faults are not silently turned into an expected result.
        catch (NpgsqlException exception) when (!cancellationToken.IsCancellationRequested &&
            (exception.IsTransient || exception is PostgresException
            {
                SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ReadOnlySqlTransaction
            }))
        {
            // The result intentionally contains no database/connection details. A lost commit
            // acknowledgement can leave a committed record; this implementation never retries.
            return AuditSubmissionResult.Failure;
        }
    }
}
