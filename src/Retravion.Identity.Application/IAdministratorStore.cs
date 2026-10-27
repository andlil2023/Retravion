using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

public interface IAdministratorStore
{
    /// <summary>Loads the singleton; invalid persisted state is an explicit failure.</summary>
    Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default);

    /// <summary>Atomically inserts only when absent; never updates an existing administrator.</summary>
    Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default);
}
