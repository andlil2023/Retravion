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

    public static Result<Administrator> Create(string? username, string? passwordHash)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Contains('\0'))
        {
            return Result.Failure<Administrator>("An administrator username without null characters is required.");
        }

        if (string.IsNullOrWhiteSpace(passwordHash) || passwordHash.Contains('\0'))
        {
            return Result.Failure<Administrator>("An administrator password hash is required.");
        }

        return Result.Success(new Administrator(username, passwordHash));
    }
}
