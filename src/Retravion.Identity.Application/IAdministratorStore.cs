using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

public interface IAdministratorStore
{
    /// <summary>Loads the singleton; invalid persisted state is an explicit failure.</summary>
    Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts only when absent; never updates an existing administrator.</summary>
    Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces credentials only if the stored account still matches the restricted snapshot.
    /// Returns failure for a stale snapshot or an invalid transition; never inserts an account.</summary>
    Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
        CancellationToken cancellationToken = default);
}
