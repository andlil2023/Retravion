using CSharpFunctionalExtensions;

namespace Retravion.Identity.Domain;

/// <summary>The single provisioned administrator. Contains no plaintext password.</summary>
public sealed class Administrator
{
    public string Username { get; }
    public string PasswordHash { get; }

    private Administrator(string username, string passwordHash)
    {
        Username = username;
        PasswordHash = passwordHash;
    }

    public static Result<Administrator> Create(string? username, string? passwordHash) =>
        Result.Success()
            .Ensure(() => !string.IsNullOrWhiteSpace(username) && !username.Contains('\0'),
                "An administrator username without null characters is required.")
            .Ensure(() => !string.IsNullOrWhiteSpace(passwordHash) && !passwordHash.Contains('\0'),
                "An administrator password hash is required.")
            .Map(() => new Administrator(username!, passwordHash!));
}
