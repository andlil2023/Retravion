using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

public sealed class AdministratorInitializer(IAdministratorStore store, IPasswordHasher hasher)
{
    public Task<Result> Initialize(string? username, string? password, CancellationToken cancellationToken = default) =>
        store.Load(cancellationToken)
            .Bind(existing => existing.HasValue
                // Provisioning configuration is irrelevant after the first successful initialization.
                ? Task.FromResult(Result.Success())
                : Result.Success()
                    .Ensure(() => !string.IsNullOrWhiteSpace(username) && !username.Contains('\0'),
                        "InitialAdmin:Username is required and must not contain null characters.")
                    .Bind(() => PasswordPolicy.Validate(password)
                        .MapError(error => $"InitialAdmin:Password is invalid. {error}"))
                    .Map(() => hasher.Hash(password!))
                    .Bind(hash => Administrator.Create(username, hash))
                    .Tap(administrator => store.CreateIfAbsent(administrator, cancellationToken))
                    .Bind(_ => Result.Success()));
}
