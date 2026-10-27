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
            ALTER TABLE retravion_identity.administrator
                ADD COLUMN IF NOT EXISTS failed_sign_ins integer NOT NULL DEFAULT 0
                    CHECK (failed_sign_ins BETWEEN 0 AND 10),
                ADD COLUMN IF NOT EXISTS retry_at timestamp with time zone NOT NULL DEFAULT '-infinity';
            """, connection, transaction);
        await setup.ExecuteNonQueryAsync(cancellationToken);

        Result<Maybe<Administrator>> result;
        await using (var command = new NpgsqlCommand(
            "SELECT username, password_hash, requires_password_replacement, failed_sign_ins, retry_at FROM retravion_identity.administrator", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                result = Result.Success(Maybe<Administrator>.None);
            }
            else
            {
                var failures = SignInFailures.Restore(reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4));
                result = failures.Bind(state => Administrator.Create(reader.GetString(0), reader.GetString(1),
                        reader.GetBoolean(2), state))
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
            INSERT INTO retravion_identity.administrator
                (singleton, username, password_hash, requires_password_replacement, failed_sign_ins, retry_at)
            VALUES (TRUE, $1, $2, $3, $4, $5)
            ON CONFLICT (singleton) DO NOTHING
            """, connection);
        command.Parameters.AddWithValue(administrator.Username);
        command.Parameters.AddWithValue(administrator.PasswordHash);
        command.Parameters.AddWithValue(administrator.RequiresPasswordReplacement);
        command.Parameters.AddWithValue(administrator.Failures.Count);
        command.Parameters.AddWithValue(administrator.Failures.RetryAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var usernameMatches = string.Equals(username, expected.Username, StringComparison.Ordinal);
        if (usernameMatches && !expected.Failures.CanAttempt(now)) return false;
        var failures = !usernameMatches ? expected.Failures
            : succeeded ? SignInFailures.None : expected.Failures.RecordFailure(now);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE retravion_identity.administrator
            SET failed_sign_ins = $1, retry_at = $2
            WHERE singleton AND username = $3 AND password_hash = $4
                AND requires_password_replacement = $5 AND failed_sign_ins = $6 AND retry_at = $7 AND $8
            """, connection);
        command.Parameters.AddWithValue(failures.Count);
        command.Parameters.AddWithValue(failures.RetryAt);
        command.Parameters.AddWithValue(expected.Username);
        command.Parameters.AddWithValue(expected.PasswordHash);
        command.Parameters.AddWithValue(expected.RequiresPasswordReplacement);
        command.Parameters.AddWithValue(expected.Failures.Count);
        command.Parameters.AddWithValue(expected.Failures.RetryAt);
        command.Parameters.AddWithValue(usernameMatches);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return !usernameMatches || affected == 1;
    }

    public async Task<Result> RecoverPassword(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default)
    {
        if (!replacement.RequiresPasswordReplacement || replacement.Failures != SignInFailures.None ||
            !string.Equals(expected.Username, replacement.Username, StringComparison.Ordinal) ||
            string.Equals(expected.PasswordHash, replacement.PasswordHash, StringComparison.Ordinal))
            return Result.Failure("Invalid administrator recovery transition.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE retravion_identity.administrator
            SET password_hash = $1, requires_password_replacement = TRUE, failed_sign_ins = 0, retry_at = '-infinity'
            WHERE singleton AND username = $2 AND password_hash = $3 AND requires_password_replacement = $4
                AND failed_sign_ins = $5 AND retry_at = $6
            """, connection);
        command.Parameters.AddWithValue(replacement.PasswordHash);
        command.Parameters.AddWithValue(expected.Username);
        command.Parameters.AddWithValue(expected.PasswordHash);
        command.Parameters.AddWithValue(expected.RequiresPasswordReplacement);
        command.Parameters.AddWithValue(expected.Failures.Count);
        command.Parameters.AddWithValue(expected.Failures.RetryAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? Result.Success()
            : Result.Failure("The administrator changed; retry recovery.");
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
