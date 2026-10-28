using CSharpFunctionalExtensions;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;

namespace Retravion.UnitTests;

// Existing single-account fixtures retain their deliberately narrow responsibilities.
internal interface ITestAdministratorStore : IAdministratorStore
{
    Task<Result<Maybe<Administrator>>> IAdministratorStore.LoadForSignIn(string? username, CancellationToken cancellationToken) => Load(cancellationToken);
    Task<Result<IReadOnlyList<Administrator>>> IAdministratorStore.List(CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<Result> IAdministratorStore.CreateAccount(Administrator account, CancellationToken cancellationToken) => throw new NotSupportedException();
}
