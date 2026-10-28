using CSharpFunctionalExtensions;
using Npgsql;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Infrastructure;

/// <summary>Identity account storage. The original table name and initial-account marker are retained.</summary>
public sealed class PostgresAdministratorStore(string connectionString) : IAdministratorStore
{
    private const string Columns = "username, password_hash, requires_password_replacement, failed_sign_ins, retry_at, display_name, account_type, singleton IS TRUE, is_active";

    public async Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
    {
        await InitializeSchema(cancellationToken);
        return await ReadOne(null, cancellationToken);
    }

    public Task<Result<Maybe<Administrator>>> LoadForSignIn(string? username, CancellationToken cancellationToken = default) =>
        ReadOne(username, cancellationToken);

    private async Task<Result<Maybe<Administrator>>> ReadOne(string? username, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM retravion_identity.administrator
            WHERE normalized_username = $1 OR singleton IS TRUE
            ORDER BY (normalized_username = $1) DESC NULLS LAST LIMIT 1
            """, connection);
        command.Parameters.AddWithValue((username ?? "").ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Maybe<Administrator>.None;
        return Map(reader).Map(value => Maybe<Administrator>.From(value));
    }

    public async Task<Result<IReadOnlyList<Administrator>>> List(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM retravion_identity.administrator ORDER BY normalized_username", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accounts = new List<Administrator>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var mapped = Map(reader);
            if (mapped.IsFailure) return Result.Failure<IReadOnlyList<Administrator>>(mapped.Error);
            accounts.Add(mapped.Value);
        }
        return Result.Success<IReadOnlyList<Administrator>>(accounts);
    }

    private static Result<Administrator> Map(NpgsqlDataReader reader) =>
        SignInFailures.Restore(reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4))
            .Bind(failures => Administrator.Restore(reader.GetString(0), reader.GetString(5),
                (AccountType)reader.GetInt32(6), reader.GetBoolean(7), reader.GetString(1), reader.GetBoolean(2), failures, reader.GetBoolean(8)))
            .MapError(_ => "The persisted administrator is invalid.");

    private async Task InitializeSchema(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setup = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(821749620108);
            CREATE SCHEMA IF NOT EXISTS retravion_identity;
            CREATE TABLE IF NOT EXISTS retravion_identity.administrator (
                singleton boolean DEFAULT TRUE CHECK (singleton),
                username text NOT NULL CHECK (length(btrim(username)) > 0),
                password_hash text NOT NULL CHECK (length(btrim(password_hash)) > 0)
            );
            ALTER TABLE retravion_identity.administrator
                ADD COLUMN IF NOT EXISTS is_active boolean NOT NULL DEFAULT TRUE,
                ADD COLUMN IF NOT EXISTS requires_password_replacement boolean NOT NULL DEFAULT TRUE,
                ADD COLUMN IF NOT EXISTS failed_sign_ins integer NOT NULL DEFAULT 0 CHECK (failed_sign_ins BETWEEN 0 AND 10),
                ADD COLUMN IF NOT EXISTS retry_at timestamp with time zone NOT NULL DEFAULT '-infinity',
                ADD COLUMN IF NOT EXISTS display_name text,
                ADD COLUMN IF NOT EXISTS normalized_username text,
                ADD COLUMN IF NOT EXISTS account_type integer NOT NULL DEFAULT 1 CHECK (account_type IN (0, 1));
            ALTER TABLE retravion_identity.administrator DROP CONSTRAINT IF EXISTS administrator_pkey;
            ALTER TABLE retravion_identity.administrator ALTER COLUMN singleton DROP NOT NULL;
            UPDATE retravion_identity.administrator SET display_name = username WHERE display_name IS NULL;
            ALTER TABLE retravion_identity.administrator ALTER COLUMN display_name SET NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS administrator_initial ON retravion_identity.administrator (singleton);

            """, connection, transaction);
        await setup.ExecuteNonQueryAsync(cancellationToken);
        // Use the same invariant normalization as credential comparisons, including legacy Unicode usernames.
        var legacyNames = new List<string>();
        await using (var select = new NpgsqlCommand("SELECT username FROM retravion_identity.administrator WHERE normalized_username IS NULL", connection, transaction))
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) legacyNames.Add(reader.GetString(0));
        foreach (var username in legacyNames)
        {
            await using var normalize = new NpgsqlCommand("UPDATE retravion_identity.administrator SET normalized_username = $1 WHERE username = $2", connection, transaction);
            normalize.Parameters.AddWithValue(username.ToUpperInvariant());
            normalize.Parameters.AddWithValue(username);
            await normalize.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var finish = new NpgsqlCommand("""
            ALTER TABLE retravion_identity.administrator ALTER COLUMN normalized_username SET NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS account_username ON retravion_identity.administrator (normalized_username);
            """, connection, transaction);
        await finish.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default)
    {
        if (!administrator.IsInitialAdministrator) throw new ArgumentException("Initial administrator required.");
        await Insert(administrator, cancellationToken);
    }

    public async Task<Result> CreateAccount(Administrator account, CancellationToken cancellationToken = default)
    {
        if (!account.IsActive || account.IsInitialAdministrator || !account.RequiresPasswordReplacement || account.Failures != SignInFailures.None)
            return Result.Failure("Invalid new account state.");
        return await Insert(account, cancellationToken) == 1
            ? Result.Success() : Result.Failure("That username is already in use.");
    }

    private async Task<int> Insert(Administrator account, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO retravion_identity.administrator
                (singleton, username, password_hash, requires_password_replacement, failed_sign_ins, retry_at, display_name, account_type, normalized_username, is_active)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT DO NOTHING
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Boolean,
            Value = account.IsInitialAdministrator ? true : DBNull.Value });
        command.Parameters.AddWithValue(account.Username);
        command.Parameters.AddWithValue(account.PasswordHash);
        command.Parameters.AddWithValue(account.RequiresPasswordReplacement);
        command.Parameters.AddWithValue(account.Failures.Count);
        command.Parameters.AddWithValue(account.Failures.RetryAt);
        command.Parameters.AddWithValue(account.DisplayName);
        command.Parameters.AddWithValue((int)account.AccountType);
        command.Parameters.AddWithValue(account.Username.ToUpperInvariant());
        command.Parameters.AddWithValue(account.IsActive);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var matches = string.Equals(username, expected.Username, StringComparison.OrdinalIgnoreCase);
        if (matches && (!expected.IsActive || !expected.Failures.CanAttempt(now))) return false;
        var failures = !matches ? expected.Failures : succeeded ? SignInFailures.None : expected.Failures.RecordFailure(now);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE retravion_identity.administrator SET failed_sign_ins = $1, retry_at = $2
            WHERE username = $3 AND password_hash = $4 AND requires_password_replacement = $5
                AND failed_sign_ins = $6 AND retry_at = $7 AND $8 AND is_active
            """, connection);
        command.Parameters.AddWithValue(failures.Count);
        command.Parameters.AddWithValue(failures.RetryAt);
        command.Parameters.AddWithValue(expected.Username);
        command.Parameters.AddWithValue(expected.PasswordHash);
        command.Parameters.AddWithValue(expected.RequiresPasswordReplacement);
        command.Parameters.AddWithValue(expected.Failures.Count);
        command.Parameters.AddWithValue(expected.Failures.RetryAt);
        command.Parameters.AddWithValue(matches);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return !matches || affected == 1;
    }

    public async Task<Result> ChangeAvailability(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default)
    {
        if (expected.Username != replacement.Username || expected.IsActive == replacement.IsActive ||
            expected.PasswordHash != replacement.PasswordHash || expected.DisplayName != replacement.DisplayName ||
            expected.AccountType != replacement.AccountType || expected.IsInitialAdministrator != replacement.IsInitialAdministrator ||
            expected.RequiresPasswordReplacement != replacement.RequiresPasswordReplacement || expected.Failures != replacement.Failures)
            return Result.Failure("Invalid availability transition.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE retravion_identity.administrator SET is_active = $1
            WHERE username = $2 AND is_active = $3
            """, connection);
        command.Parameters.AddWithValue(replacement.IsActive);
        command.Parameters.AddWithValue(expected.Username);
        command.Parameters.AddWithValue(expected.IsActive);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? Result.Success() : Result.Failure("The account changed; refresh and try again.");
    }

    public Task<Result> RecoverPassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) =>
        ChangePassword(expected, replacement, true, cancellationToken);

    public Task<Result> ReplacePassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) =>
        ChangePassword(expected, replacement, false, cancellationToken);

    private async Task<Result> ChangePassword(Administrator expected, Administrator replacement, bool recovery, CancellationToken cancellationToken)
    {
        if (expected.Username != replacement.Username || expected.DisplayName != replacement.DisplayName ||
            expected.AccountType != replacement.AccountType || expected.IsInitialAdministrator != replacement.IsInitialAdministrator ||
            expected.IsActive != replacement.IsActive || expected.PasswordHash == replacement.PasswordHash ||
            (recovery ? !expected.IsInitialAdministrator || !replacement.RequiresPasswordReplacement || replacement.Failures != SignInFailures.None
                : !expected.RequiresPasswordReplacement || replacement.RequiresPasswordReplacement || expected.Failures != replacement.Failures))
            return Result.Failure("Invalid password transition.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE retravion_identity.administrator
            SET password_hash = $1, requires_password_replacement = $2,
                failed_sign_ins = CASE WHEN $3 THEN 0 ELSE failed_sign_ins END,
                retry_at = CASE WHEN $3 THEN '-infinity'::timestamptz ELSE retry_at END
            WHERE username = $4 AND password_hash = $5 AND requires_password_replacement = $6
                AND is_active = $9
                AND (NOT $3 OR (singleton IS TRUE AND failed_sign_ins = $7 AND retry_at = $8))
            """, connection);
        command.Parameters.AddWithValue(replacement.PasswordHash);
        command.Parameters.AddWithValue(replacement.RequiresPasswordReplacement);
        command.Parameters.AddWithValue(recovery);
        command.Parameters.AddWithValue(expected.Username);
        command.Parameters.AddWithValue(expected.PasswordHash);
        command.Parameters.AddWithValue(expected.RequiresPasswordReplacement);
        command.Parameters.AddWithValue(expected.Failures.Count);
        command.Parameters.AddWithValue(expected.Failures.RetryAt);
        command.Parameters.AddWithValue(expected.IsActive);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? Result.Success() : Result.Failure("The account changed; authenticate again.");
    }
}
