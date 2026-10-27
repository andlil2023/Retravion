using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

/// <summary>Prepares recovery without establishing or invalidating sessions. Do not expose until session invalidation exists.</summary>
public sealed class AdministratorRecovery(IAdministratorStore store, IPasswordHasher hasher)
{
    public async Task<Result> Recover(string? password, CancellationToken cancellationToken = default)
    {
        var validation = PasswordPolicy.Validate(password);
        if (validation.IsFailure) return validation;

        var loaded = await store.Load(cancellationToken);
        if (loaded.IsFailure) return Result.Failure(loaded.Error);
        if (loaded.Value.HasNoValue) return Result.Failure("The administrator does not exist.");

        var administrator = loaded.Value.Value;
        return await administrator.RecoverPassword(password, hasher.Hash(password!))
            .Bind(replacement => store.RecoverPassword(administrator, replacement, cancellationToken));
    }
}
