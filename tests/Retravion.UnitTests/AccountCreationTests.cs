using System.Text;
using CSharpFunctionalExtensions;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.UnitTests;

public sealed class AccountCreationTests
{
    public static TheoryData<string?, bool> Usernames => new()
    {
        { null, false }, { "", false }, { "ab", false }, { "abc", true },
        { new string('A', 50), true }, { new string('A', 51), false },
        { "AbZ-._09", true }, { " abc", false }, { "ab c", false },
        { "abé", false }, { "ab!", false }, { "ab\0", false }, { "用户a", false }
    };

    [Theory]
    [MemberData(nameof(Usernames))]
    public async Task UsernameRulesAreEnforcedBeforeHashingOrWriting(string? username, bool valid)
    {
        var store = new Store();
        var hasher = new Hasher();
        var result = await new AccountCreation(store, hasher).Create(username, "Name", AccountType.Ordinary);
        Assert.Equal(valid, result.IsSuccess);
        Assert.Equal(valid ? 1 : 0, store.Creations);
        Assert.Equal(valid ? 1 : 0, hasher.Hashes);
    }

    public static TheoryData<string?, string?> DisplayNames => new()
    {
        { null, null }, { "", null }, { " \t\r\n", null }, { " a ", "a" },
        { "  李  😀  ", "李  😀" }, { new string('a', 100), new string('a', 100) },
        { new string('a', 101), null },
        { string.Concat(Enumerable.Repeat("😀", 100)), string.Concat(Enumerable.Repeat("😀", 100)) },
        { string.Concat(Enumerable.Repeat("😀", 101)), null }
    };

    [Theory]
    [MemberData(nameof(DisplayNames))]
    public async Task DisplayNameRulesUseTrimmedUnicodeScalarLengthAndPreserveInternalWhitespace(string? name, string? expected)
    {
        var store = new Store();
        var hasher = new Hasher();
        var result = await new AccountCreation(store, hasher).Create("user", name, AccountType.Ordinary);
        Assert.Equal(expected is not null, result.IsSuccess);
        Assert.Equal(expected is null ? 0 : 1, hasher.Hashes);
        Assert.Equal(expected is null ? 0 : 1, store.Creations);
        if (expected is not null) Assert.Equal(expected, store.Account!.DisplayName);
    }

    [Theory]
    [InlineData(AccountType.Ordinary)]
    [InlineData(AccountType.Administrator)]
    public async Task CreationHashesPolicyValidTemporaryPasswordAndCredentialTransitionsRetainAccountIdentity(AccountType type)
    {
        var store = new Store();
        var hasher = new Hasher();
        var result = await new AccountCreation(store, hasher).Create("User-1", " Shared  Name ", type);
        Assert.True(result.IsSuccess);
        var password = result.Value.TemporaryPassword;
        Assert.True(PasswordPolicy.Validate(password).IsSuccess);
        Assert.Equal(20, password.EnumerateRunes().Count());
        var account = store.Account!;
        Assert.True(hasher.Verify(password, account.PasswordHash));
        Assert.DoesNotContain(password, account.PasswordHash);
        Assert.True(account.RequiresPasswordReplacement);
        Assert.True(result.Value.Account.RequiresPasswordReplacement);
        Assert.Equal(AccountSummary.From(account), result.Value.Account);
        Assert.False(account.IsInitialAdministrator);
        Assert.Equal(type, account.AccountType);
        Assert.Equal("Shared  Name", account.DisplayName);
        var failed = account.RecordSignIn(false, DateTimeOffset.UnixEpoch);
        Assert.Equal(1, failed.Failures.Count);
        var replaced = failed.ReplacePassword("Replacement123!", hasher.Hash("Replacement123!")).Value;
        Assert.False(replaced.RequiresPasswordReplacement);
        Assert.False(AccountSummary.From(replaced).RequiresPasswordReplacement);
        Assert.Equal(account.Username, replaced.Username);
        Assert.Equal(account.DisplayName, replaced.DisplayName);
        Assert.Equal(type, replaced.AccountType);
        Assert.False(replaced.IsInitialAdministrator);
        Assert.Equal(failed.Failures, replaced.Failures);
    }

    [Fact]
    public async Task InvalidAccountTypeAndDuplicateFailuresDoNotReturnPasswords()
    {
        var store = new Store();
        var hasher = new Hasher();
        var creation = new AccountCreation(store, hasher);
        Assert.True((await creation.Create("user", "Name", (AccountType)99)).IsFailure);
        Assert.Equal(0, hasher.Hashes);
        Assert.True((await creation.Create("user", "Name", AccountType.Ordinary)).IsSuccess);
        var original = store.Account;
        Assert.True((await creation.Create("USER", "Name", AccountType.Administrator)).IsFailure);
        Assert.Same(original, store.Account);
    }

    [Fact]
    public async Task MalformedUnicodeDisplayNamesAreRejectedWithoutHashingOrWriting() =>
        await DisplayNameRulesUseTrimmedUnicodeScalarLengthAndPreserveInternalWhitespace("bad" + (char)0xD800, null);

    [Fact]
    public void PersistedAccountMetadataCannotBypassValidation()
    {
        Assert.True(Administrator.Restore("ab", "Name", AccountType.Ordinary, false, "hash", true, SignInFailures.None).IsFailure);
        Assert.True(Administrator.Restore("user", " Name ", AccountType.Ordinary, false, "hash", true, SignInFailures.None).IsFailure);
        Assert.True(Administrator.Restore("user", "Name", (AccountType)7, false, "hash", true, SignInFailures.None).IsFailure);
        Assert.True(Administrator.Restore("admin", "admin", AccountType.Ordinary, true, "hash", true, SignInFailures.None).IsFailure);
    }

    private sealed class Hasher : IPasswordHasher
    {
        private readonly Pbkdf2PasswordHasher real = new();
        public int Hashes { get; private set; }
        public string Hash(string password) { Hashes++; return real.Hash(password); }
        public bool Verify(string password, string? hash) => real.Verify(password, hash);
    }

    private sealed class Store : ITestAdministratorStore
    {
        public Administrator? Account { get; private set; }
        public int Creations { get; private set; }
        public Task<Result> CreateAccount(Administrator account, CancellationToken cancellationToken = default)
        {
            if (Account is not null) return Task.FromResult(Result.Failure("Duplicate username."));
            Account = account;
            Creations++;
            return Task.FromResult(Result.Success());
        }
        public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> RecoverPassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
