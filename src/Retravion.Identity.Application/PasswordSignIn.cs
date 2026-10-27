using CSharpFunctionalExtensions;

namespace Retravion.Identity.Application;

/// <summary>Verifies credentials and returns only the authenticated account's username.</summary>
public sealed class PasswordSignIn(IAdministratorStore store, IPasswordHasher hasher)
{
    public const string FailureMessage = "Invalid username or password.";

    public async Task<Result<string>> Authenticate(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        var loaded = await store.Load(cancellationToken);
        if (loaded.IsFailure || loaded.Value.HasNoValue)
            return Result.Failure<string>(FailureMessage);

        var administrator = loaded.Value.Value;
        // Verify even when the username differs; do not expose which credential failed.
        var passwordMatches = hasher.Verify(password ?? string.Empty, administrator.PasswordHash);
        return Result.Success(administrator.Username)
            .Ensure(_ => password is not null && passwordMatches &&
                string.Equals(username, administrator.Username, StringComparison.Ordinal), FailureMessage);
    }
}
