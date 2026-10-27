using System.Security.Cryptography;
using System.Text;
using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

/// <summary>Verifies credentials and returns only the authenticated account's username.</summary>
public sealed class PasswordSignIn(IAdministratorStore store, IPasswordHasher hasher, TimeProvider? timeProvider = null)
{
    public const string FailureMessage = "Invalid username or password.";

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private const int MaximumTrackedUsernames = 1024;
    private readonly Dictionary<string, Attempts> attempts = new(StringComparer.Ordinal);
    private readonly object admissionGate = new();
    private readonly Attempts reservedAdministratorAttempts = new();

    private sealed class Attempts
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SignInFailures Failures { get; set; } = SignInFailures.None;
    }

    public async Task<Result<string>> Authenticate(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        var state = await FindOrAdmit(username, cancellationToken);
        if (state is null)
            return Result.Failure<string>(FailureMessage);

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!state.Failures.CanAttempt(clock.GetUtcNow()))
                return Result.Failure<string>(FailureMessage);

            var result = await VerifyCredentials(username, password, cancellationToken);
            state.Failures = result.IsSuccess
                ? SignInFailures.None
                : state.Failures.RecordFailure(clock.GetUtcNow());
            return result;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<Attempts?> FindOrAdmit(string? username, CancellationToken cancellationToken)
    {
        // Fixed-size keys bound retained memory even for very long submitted usernames.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username ?? string.Empty)));
        lock (admissionGate)
        {
            if (attempts.TryGetValue(key, out var existing))
                return existing;
            if (attempts.Count < MaximumTrackedUsernames)
            {
                var admitted = new Attempts();
                attempts.Add(key, admitted);
                return admitted;
            }
        }

        // Never evict failures to admit new names. Keep one additional slot for the
        // stored administrator so an unknown-username flood cannot consume it.
        var loaded = await store.Load(cancellationToken);
        return loaded.IsSuccess && loaded.Value.HasValue &&
            string.Equals(username, loaded.Value.Value.Username, StringComparison.Ordinal)
                ? reservedAdministratorAttempts
                : null;
    }

    private async Task<Result<string>> VerifyCredentials(string? username, string? password,
        CancellationToken cancellationToken)
    {
        var loaded = await store.Load(cancellationToken);
        if (loaded.IsFailure || loaded.Value.HasNoValue)
            return Result.Failure<string>(FailureMessage);

        var administrator = loaded.Value.Value;
        // Verify even when the username differs; do not expose which credential failed.
        var passwordMatches = hasher.Verify(password ?? string.Empty, administrator.PasswordHash);
        return Result.Success(administrator.Username)
            .Ensure(_ => password is not null && passwordMatches &&
                string.Equals(username, administrator.Username, StringComparison.Ordinal), FailureMessage);
    }
}
