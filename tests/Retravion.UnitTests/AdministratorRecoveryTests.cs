using CSharpFunctionalExtensions;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.UnitTests;

public sealed class AdministratorRecoveryTests
{
    private const string Password = "RecoveryPass123!";
    private static readonly IPasswordHasher Hasher = new Pbkdf2PasswordHasher();

    [Fact]
    public async Task RecoveryPreservesUsernameResetsLockoutAndRequiresReplacementWithSaltedHash()
    {
        var original = Administrator.Create(" admin ", Hasher.Hash("OldPassword123!"), false,
            SignInFailures.Restore(10, DateTimeOffset.MinValue).Value).Value;
        var store = new Store { Account = original };
        Assert.True((await new AdministratorRecovery(store, Hasher).Recover(Password)).IsSuccess);
        var recovered = store.Account.Value;
        Assert.Equal(original.Username, recovered.Username);
        Assert.True(recovered.RequiresPasswordReplacement);
        Assert.Equal(SignInFailures.None, recovered.Failures);
        Assert.True(Hasher.Verify(Password, recovered.PasswordHash));
        Assert.False(Hasher.Verify("OldPassword123!", recovered.PasswordHash));
        Assert.DoesNotContain(Password, recovered.PasswordHash);
        Assert.NotEqual(Hasher.Hash(Password), recovered.PasswordHash);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abcdefghijk")]
    [InlineData("WayTooLongPassword123456789012345!")]
    public async Task InvalidPasswordDoesNotHashOrChangeAccount(string? password)
    {
        var original = Administrator.Create("admin", "old-hash", false,
            SignInFailures.Restore(10, DateTimeOffset.MinValue).Value).Value;
        var store = new Store { Account = original };
        var hasher = new CountingHasher();
        var result = await new AdministratorRecovery(store, hasher).Recover(password);
        Assert.True(result.IsFailure);
        if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, result.Error);
        Assert.Same(original, store.Account.Value);
        Assert.Equal(0, store.Loads);
        Assert.Equal(0, store.Writes);
        Assert.Equal(0, hasher.Calls);
    }

    [Fact]
    public async Task MalformedUnicodeIsRejected() => await InvalidPasswordDoesNotHashOrChangeAccount("InvalidPass123!" + (char)0xD800);

    [Fact]
    public async Task MissingOrInvalidAccountNeverCreatesOrHashes()
    {
        foreach (var invalid in new[] { false, true })
        {
            var store = new Store { Invalid = invalid };
            var hasher = new CountingHasher();
            var result = await new AdministratorRecovery(store, hasher).Recover(Password);
            Assert.True(result.IsFailure);
            Assert.Equal(invalid ? "Invalid persisted state" : "The administrator does not exist.", result.Error);
            Assert.Equal(1, store.Loads);
            Assert.True(store.Account.HasNoValue);
            Assert.Equal(0, store.Writes);
            Assert.Equal(0, hasher.Calls);
        }
    }

    [Fact]
    public async Task StaleSnapshotFailsWithoutOverwritingConcurrentChange()
    {
        var original = Administrator.Create("admin", "old-hash").Value;
        var concurrent = Administrator.Create("admin", "other-hash", false).Value;
        var store = new Store { Account = original, Concurrent = concurrent };
        Assert.True((await new AdministratorRecovery(store, Hasher).Recover(Password)).IsFailure);
        Assert.Same(concurrent, store.Account.Value);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task CancellationAndUnexpectedLoadFaultRemainExceptions()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new Store();
        var hasher = new CountingHasher();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AdministratorRecovery(cancelled, hasher).Recover(Password, cancellation.Token));
        Assert.Equal(cancellation.Token, cancelled.LoadToken);
        Assert.Equal(0, hasher.Calls);
        Assert.Equal(0, cancelled.Writes);

        var faulty = new Store { Fault = true };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AdministratorRecovery(faulty, hasher).Recover(Password));
        Assert.Equal("Unexpected load failure", exception.Message);
        Assert.Equal(0, hasher.Calls);
        Assert.Equal(0, faulty.Writes);
    }

    [Fact]
    public async Task SuccessfulRecoveryPassesCancellationTokenToLoadAndWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store { Account = Administrator.Create("admin", "old-hash").Value };

        Assert.True((await new AdministratorRecovery(store, Hasher).Recover(Password, cancellation.Token)).IsSuccess);
        Assert.Equal(cancellation.Token, store.LoadToken);
        Assert.Equal(cancellation.Token, store.WriteToken);
    }

    [Fact]
    public void DomainRejectsInvalidRecoveryInput()
    {
        var account = Administrator.Create("admin", "old-hash").Value;
        Assert.True(account.RecoverPassword("short", "new-hash").IsFailure);
        Assert.True(account.RecoverPassword(Password, null).IsFailure);
        Assert.True(account.RecoverPassword(Password, "old-hash").IsFailure);
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        public int Calls { get; private set; }
        public string Hash(string password) { Calls++; return Hasher.Hash(password); }
        public bool Verify(string password, string? encodedHash) => Hasher.Verify(password, encodedHash);
    }

    private sealed class Store : ITestAdministratorStore
    {
        public Maybe<Administrator> Account { get; set; }
        public Administrator? Concurrent { get; init; }
        public bool Invalid { get; init; }
        public bool Fault { get; init; }
        public int Loads { get; private set; }
        public int Writes { get; private set; }
        public CancellationToken LoadToken { get; private set; }
        public CancellationToken WriteToken { get; private set; }
        public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
        {
            Loads++;
            LoadToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (Fault) throw new InvalidOperationException("Unexpected load failure");
            return Task.FromResult(Invalid ? Result.Failure<Maybe<Administrator>>("Invalid persisted state") : Result.Success(Account));
        }
        public Task<Result> RecoverPassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default)
        {
            WriteToken = cancellationToken;
            if (Concurrent is not null) Account = Concurrent;
            if (Account.HasNoValue || !ReferenceEquals(Account.Value, expected)) return Task.FromResult(Result.Failure("Stale account"));
            Account = replacement;
            Writes++;
            return Task.FromResult(Result.Success());
        }
        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
