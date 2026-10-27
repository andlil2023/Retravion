using CSharpFunctionalExtensions;

namespace Retravion.Identity.Domain;

/// <summary>The single provisioned administrator. Contains no plaintext password.</summary>
public sealed class Administrator
{
    public string Username { get; }
    public string PasswordHash { get; }

    public bool RequiresPasswordReplacement { get; }
    public SignInFailures Failures { get; }

    private Administrator(string username, string passwordHash, bool requiresPasswordReplacement,
        SignInFailures failures)
    {
        Username = username;
        PasswordHash = passwordHash;
        RequiresPasswordReplacement = requiresPasswordReplacement;
        Failures = failures;
    }

    public Result<Administrator> ReplacePassword(string? password, string? passwordHash) =>
        Result.Success()
            .Ensure(() => RequiresPasswordReplacement, "Password replacement is not required.")
            .Bind(() => PasswordPolicy.Validate(password))
            .Ensure(() => !string.Equals(PasswordHash, passwordHash, StringComparison.Ordinal),
                "The replacement password hash must differ from the current hash.")
            .Bind(() => Create(Username, passwordHash, requiresPasswordReplacement: false, failures: Failures));

    public Result<Administrator> RecoverPassword(string? password, string? passwordHash) =>
        PasswordPolicy.Validate(password)
            .Ensure(() => !string.Equals(PasswordHash, passwordHash, StringComparison.Ordinal),
                "The recovery password hash must differ from the current hash.")
            .Bind(() => Create(Username, passwordHash, requiresPasswordReplacement: true, failures: SignInFailures.None));

    public static Result<Administrator> Create(string? username, string? passwordHash,
        bool requiresPasswordReplacement = true, SignInFailures? failures = null) =>
        Result.Success()
            .Ensure(() => !string.IsNullOrWhiteSpace(username) && !username.Contains('\0'),
                "An administrator username without null characters is required.")
            .Ensure(() => !string.IsNullOrWhiteSpace(passwordHash) && !passwordHash.Contains('\0'),
                "An administrator password hash is required.")
            .Map(() => new Administrator(username!, passwordHash!, requiresPasswordReplacement, failures ?? SignInFailures.None));
}
