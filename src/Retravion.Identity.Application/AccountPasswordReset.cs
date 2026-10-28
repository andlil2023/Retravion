using CSharpFunctionalExtensions;

namespace Retravion.Identity.Application;

/// <summary>Called by the authorized administrator boundary with the authenticated actor's username.</summary>
public static class AccountPasswordReset
{
    public static async Task<Result<CreatedAccount>> Reset(IAdministratorStore store, IPasswordHasher hasher,
        string actorUsername, string username, CancellationToken cancellationToken = default)
    {
        var loaded = await store.LoadForSignIn(username, cancellationToken);
        if (loaded.IsFailure) return Result.Failure<CreatedAccount>(loaded.Error);
        if (loaded.Value.HasNoValue || !string.Equals(loaded.Value.Value.Username, username, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<CreatedAccount>("User not found.");
        var account = loaded.Value.Value;
        var password = AccountCreation.GeneratePassword();
        return await account.ResetPassword(actorUsername, password, hasher.Hash(password))
            .Bind(replacement => store.ResetPassword(account, replacement, cancellationToken)
                .Map(() => new CreatedAccount(AccountSummary.From(replacement), password)));
    }
}
