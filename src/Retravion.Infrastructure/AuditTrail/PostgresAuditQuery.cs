using Npgsql;
using NpgsqlTypes;
using Retravion.Application.AuditTrail;

namespace Retravion.Infrastructure.AuditTrail;

public sealed class PostgresAuditQuery(string connectionString) : IAuditQuery
{
    public async Task<IReadOnlyList<AuditInformation>> FindAsync(AuditFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // Storage is created on first submission, so an uninitialized database has no events.
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('retravion_audit.event') IS NOT NULL", connection))
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!) return [];

        await using var command = new NpgsqlCommand("""
            SELECT actor_kind, actor_identifier, occurred_at_utc_ticks, action, target_kind, target_identifier, outcome
            FROM retravion_audit.event
            WHERE ($1::bigint IS NULL OR occurred_at_utc_ticks >= $1)
              AND ($2::bigint IS NULL OR occurred_at_utc_ticks <= $2)
              AND ($3::text IS NULL OR actor_kind = $3)
              AND ($4::text IS NULL OR actor_identifier = $4)
              AND ($5::text IS NULL OR action = $5)
              AND ($6::text IS NULL OR outcome = $6)
              AND ($7::text IS NULL OR target_kind = $7)
              AND ($8::text IS NULL OR target_identifier = $8)
            ORDER BY occurred_at_utc_ticks DESC, id DESC
            """, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, (object?)filter.From?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, (object?)filter.To?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, filter.Actor switch { AuditActor.Identified => "identified", AuditActor.Unknown => "unknown", null => DBNull.Value, _ => throw new ArgumentException("Invalid actor filter.") });
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)(filter.Actor as AuditActor.Identified)?.Identifier ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)filter.Action ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, filter.Outcome switch { AuditActionOutcome.Success => "success", AuditActionOutcome.Failure => "failure", null => DBNull.Value, _ => throw new ArgumentException("Invalid outcome filter.") });
        command.Parameters.AddWithValue(NpgsqlDbType.Text, filter.Target switch { AuditTarget.Identified => "identified", AuditTarget.Unknown => "unknown", AuditTarget.NotApplicable => "not_applicable", null => DBNull.Value, _ => throw new ArgumentException("Invalid target filter.") });
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)(filter.Target as AuditTarget.Identified)?.Identifier ?? DBNull.Value);
        var events = new List<AuditInformation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            AuditActor actor = reader.GetString(0) switch
            {
                "identified" => new AuditActor.Identified(reader.GetString(1)),
                "unknown" => new AuditActor.Unknown(),
                _ => throw new InvalidOperationException("Invalid stored actor.")
            };
            AuditTarget target = reader.GetString(4) switch
            {
                "identified" => new AuditTarget.Identified(reader.GetString(5)),
                "unknown" => new AuditTarget.Unknown(),
                "not_applicable" => new AuditTarget.NotApplicable(),
                _ => throw new InvalidOperationException("Invalid stored target.")
            };
            var outcome = reader.GetString(6) switch
            {
                "success" => AuditActionOutcome.Success,
                "failure" => AuditActionOutcome.Failure,
                _ => throw new InvalidOperationException("Invalid stored outcome.")
            };
            events.Add(new AuditInformation(actor, new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero), reader.GetString(3), target, outcome));
        }
        return events;
    }
}
