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
    private readonly Attempts reservedAccountAttempts = new();

    private sealed class Attempts
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SignInFailures Failures { get; set; } = SignInFailures.None;
    }

    public async Task<Result<string>> Authenticate(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        // Every submitted username incurs the same initial persistence read, including
        // locally delayed/locked and overflow names. Do not expose existence via a cache hit.
        var loaded = await store.LoadForSignIn(username, cancellationToken);
        var state = FindOrAdmit(username, loaded);
        if (state is null)
            return await RejectWithEquivalentWork(password, loaded, cancellationToken);

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!state.Failures.CanAttempt(clock.GetUtcNow()))
                return await RejectWithEquivalentWork(password, loaded, cancellationToken);

            return await VerifyCredentials(username, password, state, loaded, cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<Result<string>> RejectWithEquivalentWork(string? password,
        Result<Maybe<Administrator>> loaded, CancellationToken cancellationToken)
    {
        if (loaded.IsSuccess && loaded.Value.HasValue)
        {
            var snapshot = loaded.Value.Value;
            // Rejection must not reveal persisted lockout (or local admission/retry state).
            // A null username cannot match a valid account, so this performs the same
            // verification and non-mutating write roundtrip as an unknown-name failure.
            _ = hasher.Verify(password ?? string.Empty, snapshot.PasswordHash);
            await store.TryRecordSignIn(snapshot, null, false, clock.GetUtcNow(), cancellationToken);
        }
        return Result.Failure<string>(FailureMessage);
    }

    private Attempts? FindOrAdmit(string? username, Result<Maybe<Administrator>> loaded)
    {
        // Fixed-size keys bound retained memory even for very long submitted usernames.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((username ?? string.Empty).ToUpperInvariant())));
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

        // Known accounts rely on persisted restrictions. The overflow gate holds no failures,
        // so unknown-name floods cannot consume their retry capacity.
        return loaded.IsSuccess && loaded.Value.HasValue &&
            string.Equals(username, loaded.Value.Value.Username, StringComparison.OrdinalIgnoreCase)
                ? reservedAccountAttempts
                : null;
    }

    internal static Result<string> CredentialDecision(string username, bool usernameMatches, bool passwordSupplied,
        bool passwordMatches) => Result.Success(username)
        .Ensure(_ => usernameMatches && passwordSupplied && passwordMatches, FailureMessage);

    private async Task<Result<string>> VerifyCredentials(string? username, string? password, Attempts state,
        Result<Maybe<Administrator>> loaded, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded.IsFailure || loaded.Value.HasNoValue)
            {
                state.Failures = state.Failures.RecordFailure(clock.GetUtcNow());
                return Result.Failure<string>(FailureMessage);
            }

            var administrator = loaded.Value.Value;
            var usernameMatches = string.Equals(username, administrator.Username, StringComparison.OrdinalIgnoreCase);
            // Restrictions belong to the selected account, never to the dummy snapshot.
            if (usernameMatches && !administrator.Failures.CanAttempt(clock.GetUtcNow()))
                return await RejectWithEquivalentWork(password, loaded, cancellationToken);

            // Verify even when the username differs; do not expose which credential failed.
            var passwordMatches = hasher.Verify(password ?? string.Empty, administrator.PasswordHash);
            var result = CredentialDecision(administrator.Username, usernameMatches, password is not null, passwordMatches);
            // Unknown names get a non-mutating write roundtrip, not a faster credential-failure path.
            var recorded = await store.TryRecordSignIn(administrator, username, result.IsSuccess,
                clock.GetUtcNow(), cancellationToken);
            if (!usernameMatches)
                state.Failures = state.Failures.RecordFailure(clock.GetUtcNow());
            if (recorded) return result;

            // Another host changed credentials or failures. Reassess its committed state;
            // never overwrite a newly committed delay or lockout with a stale success.
            loaded = await store.LoadForSignIn(username, cancellationToken);
        }
    }
}
