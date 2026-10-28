using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Identity.Domain;

public enum AccountType { Ordinary, Administrator }

/// <summary>Validated identity for an additional account. Initial provisioning retains its legacy username.</summary>
public sealed record AccountIdentity
{
    public string Username { get; }
    public string DisplayName { get; }
    public AccountType Type { get; }

    private AccountIdentity(string username, string displayName, AccountType type) =>
        (Username, DisplayName, Type) = (username, displayName, type);

    public static Result<AccountIdentity> Create(string? username, string? displayName, AccountType type)
    {
        if (username is null || username.Length is < 3 or > 50 ||
            username.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
            return Result.Failure<AccountIdentity>("Username must contain 3–50 ASCII letters, digits, '.', '_' or '-'.");
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Contains('\0') || !IsValidUnicode(name) || name.EnumerateRunes().Count() > 100)
            return Result.Failure<AccountIdentity>("Display name must contain 1–100 Unicode characters after trimming.");
        if (!Enum.IsDefined(type)) return Result.Failure<AccountIdentity>("Choose a valid account type.");
        return new AccountIdentity(username, name, type);
    }

    private static bool IsValidUnicode(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != System.Buffers.OperationStatus.Done)
                return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
