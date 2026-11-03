using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Application.AiWorkflow;
using Retravion.Application.AuditTrail;
using Retravion.Infrastructure.AuditTrail;

namespace Retravion.Infrastructure.AiWorkflow;

public sealed class PostgresAiWorkflowSettingsStore(string connectionString,
    Func<NpgsqlTransaction, CancellationToken, Task>? commit = null) : IAiWorkflowSettingsStore
{
    private const string Setup = """
        SELECT pg_advisory_xact_lock(821749620112);
        CREATE SCHEMA IF NOT EXISTS retravion_ai;
        CREATE TABLE IF NOT EXISTS retravion_ai.settings (
            id boolean PRIMARY KEY CHECK (id),
            enabled boolean NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0)
        );
        INSERT INTO retravion_ai.settings VALUES (true, false, 1) ON CONFLICT DO NOTHING;
        """;

    public Task<Result<AiWorkflowSettings>> Load(CancellationToken cancellationToken = default) =>
        Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT enabled, revision FROM retravion_ai.settings WHERE id", connection, transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt64(1) <= 0)
                return Result.Failure<AiWorkflowSettings>("AI Workflow settings could not be loaded.");
            return Result.Success(new AiWorkflowSettings(reader.GetBoolean(0), reader.GetInt64(1)));
        }, false, cancellationToken);

    public Task<Result<AiWorkflowSettings>> Save(AiWorkflowSettings expected, bool enabled, AuditActor actor,
        DateTimeOffset occurredAt, CancellationToken cancellationToken = default)
    {
        if (actor is not AuditActor.Identified || expected.Revision <= 0)
            return Task.FromResult(Result.Failure<AiWorkflowSettings>("Invalid AI Workflow settings change."));
        return Execute(async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE retravion_ai.settings
                SET enabled=$1, revision=revision + CASE WHEN enabled <> $1 THEN 1 ELSE 0 END
                WHERE id AND revision=$2 AND enabled=$3 RETURNING revision
                """, connection, transaction);
            command.Parameters.AddWithValue(enabled);
            command.Parameters.AddWithValue(expected.Revision);
            command.Parameters.AddWithValue(expected.Enabled);
            if (await command.ExecuteScalarAsync(cancellationToken) is not long revision)
                return Result.Failure<AiWorkflowSettings>("AI Workflow settings changed. Reload before saving again.");
            if (enabled != expected.Enabled)
                await PostgresAuditSubmission.SubmitInTransactionAsync(connection, transaction,
                    new AuditInformation(actor, occurredAt, "ai-workflow.configure", new AuditTarget.Identified("ai-workflow"),
                        AuditActionOutcome.Success, aiWorkflowChange: new(expected.Enabled, enabled)), cancellationToken);
            return Result.Success(new AiWorkflowSettings(enabled, revision));
        }, true, cancellationToken);
    }

    private async Task<Result<AiWorkflowSettings>> Execute(
        Func<NpgsqlConnection, NpgsqlTransaction, Task<Result<AiWorkflowSettings>>> operation, bool mutation,
        CancellationToken cancellationToken)
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
                if (commit is null || !mutation) await transaction.CommitAsync(cancellationToken);
                else await commit(transaction, cancellationToken);
            }
            return result;
        }
        catch (Exception) when (commitStarted)
        {
            return Result.Failure<AiWorkflowSettings>(mutation
                ? "The settings change outcome could not be confirmed. Reload AI Workflow settings before retrying manually."
                : "AI Workflow settings could not be loaded.");
        }
        catch (NpgsqlException) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail closed without logging connection strings or provider details. No automatic retries.
            return Result.Failure<AiWorkflowSettings>("AI Workflow settings are unavailable. No success could be confirmed.");
        }
        catch (ArgumentException)
        {
            return Result.Failure<AiWorkflowSettings>("AI Workflow settings are unavailable. Check server configuration.");
        }
    }
}
