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

    public bool RequiresPasswordReplacement { get; }
    public SignInFailures Failures { get; }

    private Administrator(string username, string passwordHash, bool requiresPasswordReplacement,
        SignInFailures failures, string displayName, AccountType accountType, bool isInitialAdministrator)
    {
        Username = username;
        PasswordHash = passwordHash;
        RequiresPasswordReplacement = requiresPasswordReplacement;
        Failures = failures;
        DisplayName = displayName;
        AccountType = accountType;
        IsInitialAdministrator = isInitialAdministrator;
    }

    public Result<Administrator> ReplacePassword(string? password, string? passwordHash) =>
        Result.Success()
            .Ensure(() => RequiresPasswordReplacement, "Password replacement is not required.")
            .Bind(() => PasswordPolicy.Validate(password))
            .Ensure(() => !string.Equals(PasswordHash, passwordHash, StringComparison.Ordinal),
                "The replacement password hash must differ from the current hash.")
            .Bind(() => Restore(Username, DisplayName, AccountType, IsInitialAdministrator, passwordHash, false, Failures));

    public Result<Administrator> RecoverPassword(string? password, string? passwordHash) =>
        PasswordPolicy.Validate(password)
            .Ensure(() => !string.Equals(PasswordHash, passwordHash, StringComparison.Ordinal),
                "The recovery password hash must differ from the current hash.")
            .Bind(() => Restore(Username, DisplayName, AccountType, IsInitialAdministrator, passwordHash, true, SignInFailures.None));

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
        bool isInitialAdministrator, string? passwordHash, bool requiresPasswordReplacement, SignInFailures failures)
    {
        var identity = isInitialAdministrator
            ? Result.Success().Ensure(() => accountType == AccountType.Administrator && displayName == username,
                "Invalid initial administrator identity.")
            : AccountIdentity.Create(username, displayName, accountType)
                .Ensure(value => value.DisplayName == displayName, "Invalid persisted display name.")
                .Map(_ => true).Bind(_ => Result.Success());
        return identity.Bind(() => Create(username, passwordHash, requiresPasswordReplacement, failures))
            .Map(_ => new Administrator(username, passwordHash!, requiresPasswordReplacement, failures,
                displayName, accountType, isInitialAdministrator));
    }

    public Administrator RecordSignIn(bool succeeded, DateTimeOffset now) =>
        new(Username, PasswordHash, RequiresPasswordReplacement,
            succeeded ? SignInFailures.None : Failures.RecordFailure(now), DisplayName, AccountType, IsInitialAdministrator);
}
