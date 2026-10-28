using CSharpFunctionalExtensions;

namespace Retravion.Identity.Domain;

/// <summary>Account credential state (legacy name retained for provisioning/recovery compatibility).
/// Contains no plaintext password; identity metadata survives credential transitions.</summary>
public sealed class Administrator
{
    public string Username { get; }
    public string PasswordHash { get; }
    public string DisplayName { get; }
    public AccountType AccountType { get; }
    public bool IsInitialAdministrator { get; }
    public bool IsActive { get; }

    public bool RequiresPasswordReplacement { get; }
    public SignInFailures Failures { get; }

    private Administrator(string username, string passwordHash, bool requiresPasswordReplacement,
        SignInFailures failures, string displayName, AccountType accountType, bool isInitialAdministrator, bool isActive = true)
    {
        Username = username;
        PasswordHash = passwordHash;
        RequiresPasswordReplacement = requiresPasswordReplacement;
        Failures = failures;
        DisplayName = displayName;
        AccountType = accountType;
        IsInitialAdministrator = isInitialAdministrator;
        IsActive = isActive;
    }

    public Result<Administrator> ReplacePassword(string? password, string? passwordHash) =>
        Result.Success()
            .Ensure(() => RequiresPasswordReplacement, "Password replacement is not required.")
            .Bind(() => PasswordPolicy.Validate(password))
            .Ensure(() => !string.Equals(PasswordHash, passwordHash, StringComparison.Ordinal),
                "The replacement password hash must differ from the current hash.")
            .Bind(() => Restore(Username, DisplayName, AccountType, IsInitialAdministrator, passwordHash, false, Failures, IsActive));

    public Result<Administrator> RecoverPassword(string? password, string? passwordHash) =>
        PasswordPolicy.Validate(password)
            .Ensure(() => !string.Equals(PasswordHash, passwordHash, StringComparison.Ordinal),
                "The recovery password hash must differ from the current hash.")
            .Bind(() => Restore(Username, DisplayName, AccountType, IsInitialAdministrator, passwordHash, true, SignInFailures.None, IsActive));

    public static Result<Administrator> Create(string? username, string? passwordHash,
        bool requiresPasswordReplacement = true, SignInFailures? failures = null) =>
        Result.Success()
            .Ensure(() => !string.IsNullOrWhiteSpace(username) && !username.Contains('\0'),
                "An administrator username without null characters is required.")
            .Ensure(() => !string.IsNullOrWhiteSpace(passwordHash) && !passwordHash.Contains('\0'),
                "An administrator password hash is required.")
            .Map(() => new Administrator(username!, passwordHash!, requiresPasswordReplacement, failures ?? SignInFailures.None,
                username!, AccountType.Administrator, true));

    public static Result<Administrator> CreateAccount(AccountIdentity identity, string passwordHash) =>
        Restore(identity.Username, identity.DisplayName, identity.Type, false, passwordHash, true, SignInFailures.None);

    public static Result<Administrator> Restore(string username, string displayName, AccountType accountType,
        bool isInitialAdministrator, string? passwordHash, bool requiresPasswordReplacement, SignInFailures failures, bool isActive = true)
    {
        var identity = isInitialAdministrator
            ? Result.Success().Ensure(() => accountType == AccountType.Administrator && displayName == username,
                "Invalid initial administrator identity.")
            : AccountIdentity.Create(username, displayName, accountType)
                .Ensure(value => value.DisplayName == displayName, "Invalid persisted display name.")
                .Map(_ => true).Bind(_ => Result.Success());
        return identity.Bind(() => Create(username, passwordHash, requiresPasswordReplacement, failures))
            .Map(_ => new Administrator(username, passwordHash!, requiresPasswordReplacement, failures,
                displayName, accountType, isInitialAdministrator, isActive));
    }

    public Result<Administrator> Inactivate(string actorUsername) =>
        Result.Success()
            .Ensure(() => !string.Equals(Username, actorUsername, StringComparison.OrdinalIgnoreCase),
                "You cannot inactivate your own account.")
            .Ensure(() => IsActive, "The account is already inactive.")
            .Map(() => new Administrator(Username, PasswordHash, RequiresPasswordReplacement, Failures,
                DisplayName, AccountType, IsInitialAdministrator, false));

    public Result<Administrator> Reactivate() =>
        Result.Success()
            .Ensure(() => !IsActive, "The account is already active.")
            .Map(() => new Administrator(Username, PasswordHash, RequiresPasswordReplacement, Failures,
                DisplayName, AccountType, IsInitialAdministrator, true));

    public Administrator RecordSignIn(bool succeeded, DateTimeOffset now) =>
        new(Username, PasswordHash, RequiresPasswordReplacement,
            succeeded ? SignInFailures.None : Failures.RecordFailure(now), DisplayName, AccountType, IsInitialAdministrator, IsActive);
}
