using CSharpFunctionalExtensions;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.UnitTests;

public sealed class AdministratorInitializationTests
{
    [Theory]
    [InlineData(null, "ValidPass123!")]
    [InlineData("", "ValidPass123!")]
    [InlineData(" \t", "ValidPass123!")]
    [InlineData("bad\0name", "ValidPass123!")]
    [InlineData("private-user", null)]
    [InlineData("private-user", "")]
    [InlineData("private-user", "short")]
    [InlineData("private-user", "abcdefghijk")]
    [InlineData("private-user", "WayTooLongPassword123456789012345!")]
    public async Task InvalidConfigurationNeverHashesOrWrites(string? username, string? password)
    {
        var store = new MemoryStore();
        var hasher = new CountingHasher();
        var result = await new AdministratorInitializer(store, hasher).Initialize(username, password);

        Assert.True(result.IsFailure);
        Assert.Contains(username is null or "" or " \t" or "bad\0name" ? "Username" : "Password", result.Error);
        if (!string.IsNullOrEmpty(username)) Assert.DoesNotContain(username, result.Error);
        if (!string.IsNullOrEmpty(password)) Assert.DoesNotContain(password, result.Error);
        Assert.Equal(0, hasher.Calls);
        Assert.Equal(0, store.Writes);
        Assert.True(store.Account.HasNoValue);
    }

    [Fact]
    public async Task InitializesWithOriginalValuesAndOnlyASaltedHash()
    {
        var store = new MemoryStore();
        var hasher = new Pbkdf2PasswordHasher();
        const string password = " ValidPass123! ";
        var result = await new AdministratorInitializer(store, hasher).Initialize(" admin ", password);

        Assert.True(result.IsSuccess);
        Assert.Equal(" admin ", store.Account.Value.Username);
        Assert.True(store.Account.Value.RequiresPasswordReplacement);
        Assert.NotEqual(password, store.Account.Value.PasswordHash);
        Assert.True(hasher.Verify(password, store.Account.Value.PasswordHash));
        Assert.False(hasher.Verify(password.Trim(), store.Account.Value.PasswordHash));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task ExistingAdministratorBypassesAllProvisioningAndNeverResets()
    {
        var account = Administrator.Create("original", "opaque-hash").Value;
        var store = new MemoryStore { Account = account };
        var hasher = new CountingHasher();
        var initializer = new AdministratorInitializer(store, hasher);

        Assert.True((await initializer.Initialize(null, null)).IsSuccess);
        Assert.True((await initializer.Initialize("different", "Replacement123!")).IsSuccess);
        Assert.Same(account, store.Account.Value);
        Assert.Equal(0, hasher.Calls);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task InvalidPersistedStatePreventsCreation()
    {
        var store = new MemoryStore { Invalid = true };
        var hasher = new CountingHasher();
        var result = await new AdministratorInitializer(store, hasher).Initialize("admin", "ValidPass123!");
        Assert.True(result.IsFailure);
        Assert.Equal("The persisted administrator is invalid.", result.Error);
        Assert.Equal(0, hasher.Calls);
        Assert.Equal(0, store.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0hash")]
    public async Task InvalidHashedAccountShortCircuitsPersistence(string hash)
    {
        var store = new MemoryStore();
        var hasher = new CountingHasher(hash);

        var result = await new AdministratorInitializer(store, hasher).Initialize("admin", "ValidPass123!");

        Assert.True(result.IsFailure);
        Assert.Equal("An administrator password hash is required.", result.Error);
        Assert.Equal(1, hasher.Calls);
        Assert.Equal(0, store.Writes);
        Assert.True(store.Account.HasNoValue);
    }

    [Theory]
    [InlineData(null, "hash")]
    [InlineData(" ", "hash")]
    [InlineData("bad\0name", "hash")]
    [InlineData("admin", null)]
    [InlineData("admin", "")]
    [InlineData("admin", "bad\0hash")]
    public void DomainRejectsInvalidRequiredState(string? username, string? hash) =>
        Assert.True(Administrator.Create(username, hash).IsFailure);

    [Fact]
    public void DomainValidationReportsFirstFailureWithoutDisclosingInput()
    {
        var result = Administrator.Create("private\0user", "private\0hash");

        Assert.True(result.IsFailure);
        Assert.Equal("An administrator username without null characters is required.", result.Error);
    }

    [Fact]
    public void DomainConstructionPreservesValidatedValues()
    {
        var result = Administrator.Create(" admin ", " opaque-hash ");

        Assert.True(result.IsSuccess);
        Assert.Equal(" admin ", result.Value.Username);
        Assert.Equal(" opaque-hash ", result.Value.PasswordHash);
    }

    private sealed class CountingHasher(string hash = "opaque-hash") : IPasswordHasher
    {
        public int Calls { get; private set; }
        public string Hash(string password) { Calls++; return hash; }
        public bool Verify(string password, string? encodedHash) => throw new NotSupportedException();
    }

    private sealed class MemoryStore : IAdministratorStore
    {
        public Maybe<Administrator> Account { get; set; }
        public bool Invalid { get; init; }
        public int Writes { get; private set; }
        public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default) =>
            Task.FromResult(Invalid
                ? Result.Failure<Maybe<Administrator>>("The persisted administrator is invalid.")
                : Result.Success(Account));
        public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (Account.HasNoValue) Account = administrator;
            return Task.CompletedTask;
        }
    }
}
