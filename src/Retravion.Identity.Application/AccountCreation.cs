using System.Security.Cryptography;
using CSharpFunctionalExtensions;
using Retravion.Identity.Domain;

namespace Retravion.Identity.Application;

public sealed record AccountSummary(string Username, string DisplayName, AccountType AccountType,
    bool RequiresPasswordReplacement, bool IsActive = true)
{
    public static AccountSummary From(Administrator account) =>
        new(account.Username, account.DisplayName, account.AccountType, account.RequiresPasswordReplacement, account.IsActive);
}

public sealed record CreatedAccount(AccountSummary Account, string TemporaryPassword);

public sealed class AccountCreation(IAdministratorStore store, IPasswordHasher hasher)
{
    public async Task<Result<CreatedAccount>> Create(string? username, string? displayName, AccountType type,
        CancellationToken cancellationToken = default)
    {
        var identity = AccountIdentity.Create(username, displayName, type);
        if (identity.IsFailure) return Result.Failure<CreatedAccount>(identity.Error);
        var password = GeneratePassword();
        return await PasswordPolicy.Validate(password)
            .Bind(() => Administrator.CreateAccount(identity.Value, hasher.Hash(password)))
            .Bind(account => store.CreateAccount(account, cancellationToken)
                .Map(() => new CreatedAccount(AccountSummary.From(account), password)));
    }

    private static string GeneratePassword()
    {
        const string lower = "abcdefghijklmnopqrstuvwxyz";
        const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        const string digits = "0123456789";
        const string symbols = "!#$%&*+-=?@_";
        const string all = lower + upper + digits + symbols;
        var characters = new char[20];
        var categories = new[] { lower, upper, digits, symbols };
        for (var i = 0; i < characters.Length; i++)
        {
            var alphabet = i < categories.Length ? categories[i] : all;
            characters[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        RandomNumberGenerator.Shuffle(characters.AsSpan());
        return new string(characters);
    }
}
