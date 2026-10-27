using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

public interface IAdministratorStore
{
    /// <summary>Loads the singleton; invalid persisted state is an explicit failure.</summary>
    Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts only when absent; never updates an existing administrator.</summary>
    Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default);

    /// <summary>Atomically records a permitted matching sign-in against the exact credential and failure snapshot.
    /// Nonmatching usernames incur the same persistence roundtrip without modifying the account.
    /// Returns false for a stale matching snapshot or prohibited retry. A locked account cannot be reset here.</summary>
    Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically recovers the existing account only if its entire snapshot still matches.
    /// Never inserts an account; stale snapshots and invalid transitions fail.</summary>
    Task<Result> RecoverPassword(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces credentials only if the stored account still matches the restricted snapshot.
    /// Returns failure for a stale snapshot or an invalid transition; never inserts an account.</summary>
    Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default);
}
