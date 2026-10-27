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
        // Every submitted username incurs the same initial persistence read, including
        // locally delayed/locked and overflow names. Do not expose existence via a cache hit.
        var loaded = await store.Load(cancellationToken);
        var state = FindOrAdmit(username, loaded);
        if (state is null)
            return Result.Failure<string>(FailureMessage);

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!state.Failures.CanAttempt(clock.GetUtcNow()))
                return Result.Failure<string>(FailureMessage);

            return await VerifyCredentials(username, password, state, loaded, cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private Attempts? FindOrAdmit(string? username, Result<Maybe<Administrator>> loaded)
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
        return loaded.IsSuccess && loaded.Value.HasValue &&
            string.Equals(username, loaded.Value.Value.Username, StringComparison.Ordinal)
                ? reservedAdministratorAttempts
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
            var usernameMatches = string.Equals(username, administrator.Username, StringComparison.Ordinal);
            // With one stored account, reject every submitted name identically while its
            // persisted restriction applies, including on a host with no local history.
            if (!administrator.Failures.CanAttempt(clock.GetUtcNow()))
                return Result.Failure<string>(FailureMessage);

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
            loaded = await store.Load(cancellationToken);
        }
    }
}
