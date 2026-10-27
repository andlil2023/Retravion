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
            ALTER TABLE retravion_identity.administrator
                ADD COLUMN IF NOT EXISTS requires_password_replacement boolean NOT NULL DEFAULT TRUE;
            """, connection, transaction);
        await setup.ExecuteNonQueryAsync(cancellationToken);

        Result<Maybe<Administrator>> result;
        await using (var command = new NpgsqlCommand(
            "SELECT username, password_hash, requires_password_replacement FROM retravion_identity.administrator", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                result = Result.Success(Maybe<Administrator>.None);
            }
            else
            {
                result = Administrator.Create(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))
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
            INSERT INTO retravion_identity.administrator (singleton, username, password_hash, requires_password_replacement)
            VALUES (TRUE, $1, $2, $3)
            ON CONFLICT (singleton) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue(administrator.Username);
        command.Parameters.AddWithValue(administrator.PasswordHash);
        command.Parameters.AddWithValue(administrator.RequiresPasswordReplacement);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default)
    {
        if (!expected.RequiresPasswordReplacement || replacement.RequiresPasswordReplacement ||
            !string.Equals(expected.Username, replacement.Username, StringComparison.Ordinal) ||
            string.Equals(expected.PasswordHash, replacement.PasswordHash, StringComparison.Ordinal))
            return Result.Failure("Invalid password replacement transition.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE retravion_identity.administrator
            SET password_hash = $1, requires_password_replacement = FALSE
            WHERE singleton AND username = $2 AND password_hash = $3 AND requires_password_replacement
            """, connection);
        command.Parameters.AddWithValue(replacement.PasswordHash);
        command.Parameters.AddWithValue(expected.Username);
        command.Parameters.AddWithValue(expected.PasswordHash);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? Result.Success()
            : Result.Failure("The administrator changed; authenticate again before replacing the password.");
    }
}
