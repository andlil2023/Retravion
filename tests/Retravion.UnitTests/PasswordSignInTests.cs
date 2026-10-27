using CSharpFunctionalExtensions;
using Retravion.Identity.Application;
using Retravion.Identity.Domain;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.UnitTests;

public sealed class PasswordSignInTests
{
    private static readonly IPasswordHasher Hasher = new Pbkdf2PasswordHasher();
    private static readonly Administrator Account = Administrator.Create(" Admin ", Hasher.Hash(" ValidPass123! ")).Value;

    [Fact]
    public async Task MatchingCredentialsReturnOnlyTheStoredUsername()
    {
        var result = await new PasswordSignIn(new Store(Account), Hasher).Authenticate(" Admin ", " ValidPass123! ");
        Assert.True(result.IsSuccess);
        Assert.Equal(Account.Username, result.Value);
    }

    [Theory]
    [InlineData("other", " ValidPass123! ")]
    [InlineData(" Admin ", "wrong")]
    [InlineData(" Admin ", "ValidPass123!")]
    [InlineData("Admin", " ValidPass123! ")]
    [InlineData(" admin ", " ValidPass123! ")]
    [InlineData(null, " ValidPass123! ")]
    [InlineData(" Admin ", null)]
    [InlineData("", "")]
    public async Task IncorrectCredentialsReturnTheSameGenericFailure(string? username, string? password)
    {
        var result = await new PasswordSignIn(new Store(Account), Hasher).Authenticate(username, password);
        Assert.True(result.IsFailure);
        Assert.Equal(PasswordSignIn.FailureMessage, result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidPersistedAccountFailsClosed(bool invalid)
    {
        var result = await new PasswordSignIn(new Store(Maybe<Administrator>.None, invalid), Hasher)
            .Authenticate(" Admin ", " ValidPass123! ");
        Assert.True(result.IsFailure);
        Assert.Equal(PasswordSignIn.FailureMessage, result.Error);
    }

    [Fact]
    public async Task UnexpectedStoreFaultIsNotConvertedToCredentialFailure()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PasswordSignIn(new Store(Account, fault: true), Hasher).Authenticate(" Admin ", " ValidPass123! "));
    }

    private sealed class Store(Maybe<Administrator> account, bool invalid = false, bool fault = false) : IAdministratorStore
    {
        public Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
        {
            if (fault) throw new InvalidOperationException("Database unavailable");
            return Task.FromResult(invalid
                ? Result.Failure<Maybe<Administrator>>("Invalid persisted state")
                : Result.Success(account));
        }

        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Sign-in must not write credentials.");

        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Sign-in must not write credentials.");
    }
}
