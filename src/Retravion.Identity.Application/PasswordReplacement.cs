using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

/// <summary>Completes mandatory replacement after verifying the current account credentials.
/// Does not establish a session or grant access to protected functionality.</summary>
public sealed class PasswordReplacement(IAdministratorStore store, IPasswordHasher hasher)
{
    public async Task<Result> Replace(string? username, string? currentPassword, string? newPassword,
        CancellationToken cancellationToken = default)
    {
        var loaded = await store.LoadForSignIn(username, cancellationToken);
        if (loaded.IsFailure || loaded.Value.HasNoValue)
            return Result.Failure(PasswordSignIn.FailureMessage);

        var administrator = loaded.Value.Value;
        var matches = hasher.Verify(currentPassword ?? string.Empty, administrator.PasswordHash);
        if (currentPassword is null || !matches ||
            !string.Equals(username, administrator.Username, StringComparison.OrdinalIgnoreCase))
            return Result.Failure(PasswordSignIn.FailureMessage);

        return await Result.Success()
            .Ensure(() => administrator.RequiresPasswordReplacement, "Password replacement is not required.")
            .Bind(() => PasswordPolicy.Validate(newPassword))
            .Ensure(() => !hasher.Verify(newPassword!, administrator.PasswordHash),
                "The replacement password must differ from the initial password.")
            .Map(() => hasher.Hash(newPassword!))
            .Bind(hash => administrator.ReplacePassword(newPassword, hash))
            .Bind(replacement => store.ReplacePassword(administrator, replacement, cancellationToken));
    }
}
