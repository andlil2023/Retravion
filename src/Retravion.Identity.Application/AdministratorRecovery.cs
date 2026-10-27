using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

/// <summary>Prepares recovery without establishing or invalidating sessions. Do not expose until session invalidation exists.</summary>
public sealed class AdministratorRecovery(IAdministratorStore store, IPasswordHasher hasher)
{
    public async Task<Result> Recover(string? password, CancellationToken cancellationToken = default)
    {
        return await PasswordPolicy.Validate(password)
            .Bind(() => store.Load(cancellationToken))
            .Bind(account => account.ToResult("The administrator does not exist."))
            .Bind(administrator => administrator.RecoverPassword(password, hasher.Hash(password!))
                .Bind(replacement => store.RecoverPassword(administrator, replacement, cancellationToken)));
    }
}
