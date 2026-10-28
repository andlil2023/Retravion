using CSharpFunctionalExtensions;

namespace Retravion.Identity.Application;

/// <summary>Called by the authorized administrator boundary with the authenticated actor's username.</summary>
public static class AccountAvailability
{
    public static async Task<Result> Change(IAdministratorStore store, string actorUsername, string username,
        bool active, CancellationToken cancellationToken = default)
    {
        var loaded = await store.LoadForSignIn(username, cancellationToken);
        if (loaded.IsFailure) return Result.Failure(loaded.Error);
        if (loaded.Value.HasNoValue || !string.Equals(loaded.Value.Value.Username, username, StringComparison.OrdinalIgnoreCase))
            return Result.Failure("User not found.");
        var account = loaded.Value.Value;
        return await (active ? account.Reactivate() : account.Inactivate(actorUsername))
            .Bind(replacement => store.ChangeAvailability(account, replacement, cancellationToken));
    }
}
