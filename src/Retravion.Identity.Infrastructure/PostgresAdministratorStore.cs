using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Infrastructure;

/// <summary>Identity-owned singleton storage. Schema initialization never modifies existing credentials.</summary>
public sealed class PostgresAdministratorStore(string connectionString) : IAdministratorStore
{
    public async Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Serialize first-time DDL across application instances (CREATE IF NOT EXISTS alone can race).
        await using var setup = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(821749620108);
            CREATE SCHEMA IF NOT EXISTS retravion_identity;
            CREATE TABLE IF NOT EXISTS retravion_identity.administrator (
                singleton boolean PRIMARY KEY DEFAULT TRUE CHECK (singleton),
                username text NOT NULL CHECK (length(btrim(username)) > 0),
                password_hash text NOT NULL CHECK (length(btrim(password_hash)) > 0)
            );
            """, connection, transaction);
        await setup.ExecuteNonQueryAsync(cancellationToken);

        Result<Maybe<Administrator>> result;
        await using (var command = new NpgsqlCommand(
            "SELECT username, password_hash FROM retravion_identity.administrator", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                result = Result.Success(Maybe<Administrator>.None);
            }
            else
            {
                result = Administrator.Create(reader.GetString(0), reader.GetString(1))
                    .Match(
                        administrator => Result.Success(Maybe<Administrator>.From(administrator)),
                        _ => Result.Failure<Maybe<Administrator>>("The persisted administrator is invalid."));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO retravion_identity.administrator (singleton, username, password_hash)
            VALUES (TRUE, $1, $2)
            ON CONFLICT (singleton) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue(administrator.Username);
        command.Parameters.AddWithValue(administrator.PasswordHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
