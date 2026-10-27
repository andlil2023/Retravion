using CSharpFunctionalExtensions;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.UnitTests;

public sealed class PasswordReplacementTests
{
    private const string Initial = "InitialPass123!";
    private const string Replacement = "Replacement123!";
    private static readonly IPasswordHasher Hasher = new Pbkdf2PasswordHasher();

    [Fact]
    public async Task ReplacementClearsRestrictionAndSubsequentSignInsAndInitializationPreserveIt()
    {
        var store = new Store();
        var original = store.Account.Value;
        var result = await new PasswordReplacement(store, Hasher).Replace("admin", Initial, Replacement);

        Assert.True(result.IsSuccess);
        Assert.True(original.RequiresPasswordReplacement);
        Assert.False(store.Account.Value.RequiresPasswordReplacement);
        Assert.True(Hasher.Verify(Replacement, store.Account.Value.PasswordHash));
        Assert.False(Hasher.Verify(Initial, store.Account.Value.PasswordHash));
        Assert.DoesNotContain(Replacement, store.Account.Value.PasswordHash);
        Assert.Equal(1, store.Writes);
        var signIn = new PasswordSignIn(store, Hasher);
        Assert.True((await signIn.Authenticate("admin", Initial)).IsFailure);
        Assert.True((await signIn.Authenticate("admin", Replacement)).IsSuccess);
        Assert.True((await signIn.Authenticate("admin", Replacement)).IsSuccess);
        Assert.True((await new AdministratorInitializer(store, Hasher).Initialize("other", Initial)).IsSuccess);
        Assert.False(store.Account.Value.RequiresPasswordReplacement);
        Assert.True((await new PasswordReplacement(store, Hasher).Replace("admin", Replacement, Initial)).IsFailure);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abcdefghijk")]
    [InlineData("InitialPass123!")]
    [InlineData("WayTooLongPassword123456789012345!")]
    public async Task InvalidOrReusedPasswordNeverHashesOrWrites(string? password)
    {
        var store = new Store();
        var original = store.Account.Value;
        var hasher = new TrackingHasher();
        var result = await new PasswordReplacement(store, hasher).Replace("admin", Initial, password);

        Assert.True(result.IsFailure);
        if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, result.Error);
        Assert.Same(original, store.Account.Value);
        Assert.Equal(0, hasher.HashCalls);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task MalformedUnicodeNeverHashesOrWrites() =>
        await InvalidOrReusedPasswordNeverHashesOrWrites("InvalidPass123!" + (char)0xD800);

    [Theory]
    [InlineData("other", "InitialPass123!")]
    [InlineData("Admin", "InitialPass123!")]
    [InlineData("admin", "wrong")]
    [InlineData(null, "InitialPass123!")]
    [InlineData("admin", null)]
    public async Task IncorrectCredentialsFailGenericallyWithoutHashingOrWriting(string? username, string? password)
    {
        var store = new Store();
        var hasher = new TrackingHasher();
        var result = await new PasswordReplacement(store, hasher).Replace(username, password, Replacement);
        Assert.True(result.IsFailure);
        Assert.Equal(PasswordSignIn.FailureMessage, result.Error);
        Assert.Equal(0, hasher.HashCalls);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidAccountFailsClosed(bool invalid)
    {
        var store = new Store { Account = Maybe<Administrator>.None, Invalid = invalid };
        var hasher = new TrackingHasher();
        var result = await new PasswordReplacement(store, hasher).Replace("admin", Initial, Replacement);
        Assert.True(result.IsFailure);
        Assert.Equal(PasswordSignIn.FailureMessage, result.Error);
        Assert.Equal(0, hasher.HashCalls);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task StaleUpdateFailsWithoutChangingAccount()
    {
        var store = new Store { Stale = true };
        var original = store.Account.Value;
        var result = await new PasswordReplacement(store, Hasher).Replace("admin", Initial, Replacement);
        Assert.True(result.IsFailure);
        Assert.Same(original, store.Account.Value);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task UnexpectedPersistenceFaultRemainsAnException()
    {
        var store = new Store { Fault = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PasswordReplacement(store, Hasher).Replace("admin", Initial, Replacement));
        Assert.True(store.Account.Value.RequiresPasswordReplacement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\0hash")]
    public void DomainRejectsInvalidReplacementHash(string? hash)
    {
        var original = Administrator.Create("admin", "initial-hash").Value;
        Assert.True(original.ReplacePassword(Replacement, hash).IsFailure);
        Assert.True(original.RequiresPasswordReplacement);
    }

    [Fact]
    public void DomainEnforcesPasswordRulesAndOnlyPermitsTheMandatoryTransition()
    {
        var original = Administrator.Create("admin", "initial-hash").Value;
        Assert.True(original.ReplacePassword("short", "new-hash").IsFailure);
        Assert.True(original.ReplacePassword(Replacement, original.PasswordHash).IsFailure);
        var changed = original.ReplacePassword(Replacement, "new-hash");
        Assert.True(changed.IsSuccess);
        Assert.False(changed.Value.RequiresPasswordReplacement);
        Assert.True(changed.Value.ReplacePassword(Initial, "third-hash").IsFailure);
    }

    [Fact]
    public async Task PasswordReplacementDoesNotUnlockAnAccount()
    {
        var failures = SignInFailures.Restore(10, DateTimeOffset.MinValue).Value;
        var store = new Store
        {
            Account = Administrator.Create("admin", Hasher.Hash(Initial), failures: failures).Value
        };
        Assert.True((await new PasswordReplacement(store, Hasher).Replace("admin", Initial, Replacement)).IsSuccess);
        Assert.True(store.Account.Value.Failures.IsLocked);
        Assert.True((await new PasswordSignIn(store, Hasher).Authenticate("admin", Replacement)).IsFailure);
    }

    private sealed class TrackingHasher : IPasswordHasher
    {
        public int HashCalls { get; private set; }
        public string Hash(string password) { HashCalls++; return Hasher.Hash(password); }
        public bool Verify(string password, string? encodedHash) => Hasher.Verify(password, encodedHash);
    }

    private sealed class Store : IAdministratorStore
    {
        public Maybe<Administrator> Account { get; set; } = Administrator.Create("admin", Hasher.Hash(Initial)).Value;
        public bool Invalid { get; init; }
        public bool Stale { get; init; }
        public bool Fault { get; init; }
        public int Writes { get; private set; }
        public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default) =>
            Task.FromResult(Invalid ? Result.Failure<Maybe<Administrator>>("Invalid persisted state") : Result.Success(Account));
        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Existing account must not be provisioned again.");
        public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(username, expected.Username, StringComparison.Ordinal)) return Task.FromResult(true);
            if (Account.HasNoValue || !ReferenceEquals(Account.Value, expected) || !expected.Failures.CanAttempt(now))
                return Task.FromResult(false);
            Account = Administrator.Create(expected.Username, expected.PasswordHash, expected.RequiresPasswordReplacement,
                succeeded ? SignInFailures.None : expected.Failures.RecordFailure(now)).Value;
            return Task.FromResult(true);
        }

        public Task<Result> RecoverPassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default)
        {
            if (Fault) throw new InvalidOperationException("Database unavailable");
            if (Stale || Account.HasNoValue || !ReferenceEquals(Account.Value, expected))
                return Task.FromResult(Result.Failure("Stale account"));
            Account = replacement;
            Writes++;
            return Task.FromResult(Result.Success());
        }
    }
}
