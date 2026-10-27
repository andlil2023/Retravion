using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

public sealed class AdministratorInitializer(IAdministratorStore store, IPasswordHasher hasher)
{
    public async Task<Result> Initialize(string? username, string? password, CancellationToken cancellationToken = default)
    {
        var existing = await store.Load(cancellationToken);
        if (existing.IsFailure)
        {
            return Result.Failure(existing.Error);
        }

        // Provisioning configuration is irrelevant after the first successful initialization.
        if (existing.Value.HasValue)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(username) || username.Contains('\0'))
        {
            return Result.Failure("InitialAdmin:Username is required and must not contain null characters.");
        }

        var validation = PasswordPolicy.Validate(password);
        if (validation.IsFailure)
        {
            return Result.Failure($"InitialAdmin:Password is invalid. {validation.Error}");
        }

        var administrator = Administrator.Create(username, hasher.Hash(password!));
        if (administrator.IsFailure)
        {
            return Result.Failure(administrator.Error);
        }

        await store.CreateIfAbsent(administrator.Value, cancellationToken);
        return Result.Success();
    }
}
