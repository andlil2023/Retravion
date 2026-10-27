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

    [Theory]
    [InlineData(" Admin ")]
    [InlineData("unknown")]
    public async Task ThirdFailureRestrictsEverySubsequentAttemptForExactlyFiveSeconds(string username)
    {
        var clock = new Clock();
        var store = new Store(Account);
        var signIn = new PasswordSignIn(store, Hasher, clock);
        for (var i = 0; i < 3; i++)
            Assert.Equal(PasswordSignIn.FailureMessage, (await signIn.Authenticate(username, "wrong")).Error);

        Assert.Equal(3, store.Loads);
        Assert.Equal(PasswordSignIn.FailureMessage,
            (await signIn.Authenticate(username, " ValidPass123! ")).Error);
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(3, store.Loads);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(4, store.Loads);
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(4, store.Loads);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(5, store.Loads);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task SuccessResetsTheConsecutiveFailures(int failures)
    {
        var clock = new Clock();
        var signIn = new PasswordSignIn(new Store(Account), Hasher, clock);
        for (var i = 0; i < failures; i++)
        {
            Assert.True((await signIn.Authenticate(Account.Username, "wrong")).IsFailure);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        for (var i = 0; i < 2; i++)
            Assert.True((await signIn.Authenticate(Account.Username, "wrong")).IsFailure);
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentAttemptsCannotBypassTheThresholdAndOtherUsernamesRemainIndependent()
    {
        var store = new Store(Account);
        var signIn = new PasswordSignIn(store, Hasher, new Clock());
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => signIn.Authenticate("unknown", "wrong")));
        Assert.All(results, result => Assert.Equal(PasswordSignIn.FailureMessage, result.Error));
        Assert.Equal(3, store.Loads);
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        Assert.Equal(4, store.Loads);
    }

    [Fact]
    public async Task DistinctUsernameFloodIsBoundedWithoutEvictingRestrictionsOrConsumingAdministratorCapacity()
    {
        var clock = new Clock();
        var hasher = new CountingHasher();
        var signIn = new PasswordSignIn(new Store(Account), hasher, clock);
        for (var i = 0; i < 3; i++)
            Assert.True((await signIn.Authenticate("restricted-unknown", "wrong")).IsFailure);

        var results = await Task.WhenAll(Enumerable.Range(0, 1500)
            .Select(i => signIn.Authenticate($"unknown-{i}-" + new string('x', 2000), "wrong")));
        Assert.All(results, result => Assert.Equal(PasswordSignIn.FailureMessage, result.Error));
        // Three initial checks plus one per remaining admitted username, not per submission.
        Assert.Equal(1026, hasher.Verifications);
        Assert.Equal(PasswordSignIn.FailureMessage,
            (await signIn.Authenticate("another-untracked-name", "wrong")).Error);
        Assert.True((await signIn.Authenticate("restricted-unknown", "wrong")).IsFailure);
        Assert.Equal(1026, hasher.Verifications);

        // The administrator still gets the full retry policy after unknown capacity is exhausted.
        for (var i = 0; i < 3; i++)
            Assert.True((await signIn.Authenticate(Account.Username, "wrong")).IsFailure);
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsFailure);
        Assert.Equal(1029, hasher.Verifications);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        Assert.Equal(1031, hasher.Verifications);
        Assert.True((await signIn.Authenticate("restricted-unknown", "wrong")).IsFailure);
        Assert.Equal(1032, hasher.Verifications);
        Assert.True((await signIn.Authenticate("restricted-unknown", "wrong")).IsFailure);
        Assert.Equal(1032, hasher.Verifications);
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        private int verifications;
        public int Verifications => verifications;
        public string Hash(string password) => throw new NotSupportedException();
        public bool Verify(string password, string? passwordHash)
        {
            Interlocked.Increment(ref verifications);
            return password == " ValidPass123! ";
        }
    }

    [Fact]
    public void FailureStateEnforcesThresholdAndInclusiveRetryBoundary()
    {
        var now = DateTimeOffset.UnixEpoch;
        var state = SignInFailures.None;
        for (var count = 1; count <= 4; count++)
        {
            state = state.RecordFailure(now);
            Assert.Equal(count, state.Count);
            Assert.Equal(count < 3, state.CanAttempt(now));
            if (count >= 3)
            {
                Assert.False(state.CanAttempt(now.AddSeconds(5).AddTicks(-1)));
                Assert.True(state.CanAttempt(now.AddSeconds(5)));
                now = now.AddSeconds(5);
            }
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Store(Maybe<Administrator> account, bool invalid = false, bool fault = false) : IAdministratorStore
    {
        private int loads;
        public int Loads => loads;

        public async Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref loads);
            await Task.Yield();
            if (fault) throw new InvalidOperationException("Database unavailable");
            return invalid
                ? Result.Failure<Maybe<Administrator>>("Invalid persisted state")
                : Result.Success(account);
        }

        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Sign-in must not write credentials.");

        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Sign-in must not write credentials.");
    }
}
