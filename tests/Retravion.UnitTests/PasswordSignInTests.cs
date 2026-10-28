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

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void CredentialDecisionRequiresAllCredentialChecks(bool usernameMatches, bool passwordSupplied,
        bool passwordMatches, bool succeeds)
    {
        var result = PasswordSignIn.CredentialDecision("administrator", usernameMatches, passwordSupplied, passwordMatches);

        Assert.Equal(succeeds, result.IsSuccess);
        if (succeeds) Assert.Equal("administrator", result.Value);
        else Assert.Equal(PasswordSignIn.FailureMessage, result.Error);
    }

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
        var hasher = new CountingHasher();
        var signIn = new PasswordSignIn(store, hasher, clock);
        for (var i = 0; i < 3; i++)
            Assert.Equal(PasswordSignIn.FailureMessage, (await signIn.Authenticate(username, "wrong")).Error);

        Assert.Equal(3, hasher.Verifications);
        Assert.Equal(PasswordSignIn.FailureMessage,
            (await signIn.Authenticate(username, " ValidPass123! ")).Error);
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(5, hasher.Verifications);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(6, hasher.Verifications);
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(7, hasher.Verifications);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await signIn.Authenticate(username, "wrong")).IsFailure);
        Assert.Equal(8, hasher.Verifications);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
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
        var hasher = new CountingHasher();
        var signIn = new PasswordSignIn(store, hasher, new Clock());
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => signIn.Authenticate("unknown", "wrong")));
        Assert.All(results, result => Assert.Equal(PasswordSignIn.FailureMessage, result.Error));
        Assert.Equal(20, store.Loads);
        Assert.Equal(20, hasher.Verifications);
        Assert.Equal(20, store.Recordings);
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        Assert.Equal(21, store.Loads);
        Assert.Equal(21, hasher.Verifications);
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
        // Rejections still perform equivalent credential work, including overflow names.
        Assert.Equal(1503, hasher.Verifications);
        Assert.Equal(PasswordSignIn.FailureMessage,
            (await signIn.Authenticate("another-untracked-name", "wrong")).Error);
        Assert.True((await signIn.Authenticate("restricted-unknown", "wrong")).IsFailure);
        Assert.Equal(1505, hasher.Verifications);

        // The administrator still gets the full retry policy after unknown capacity is exhausted.
        for (var i = 0; i < 3; i++)
            Assert.True((await signIn.Authenticate(Account.Username, "wrong")).IsFailure);
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsFailure);
        Assert.Equal(1509, hasher.Verifications);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        Assert.Equal(1511, hasher.Verifications);
        Assert.True((await signIn.Authenticate("restricted-unknown", "wrong")).IsFailure);
        Assert.Equal(1512, hasher.Verifications);
        Assert.True((await signIn.Authenticate("restricted-unknown", "wrong")).IsFailure);
        Assert.Equal(1513, hasher.Verifications);
    }

    [Theory]
    [InlineData(" Admin ")]
    [InlineData("unknown")]
    public async Task TenthFailurePermanentlyBlocksBothPasswordsWhileKeepingRejectionWorkEquivalent(string username)
    {
        var clock = new Clock();
        var hasher = new CountingHasher();
        var store = new Store(Account);
        var signIn = new PasswordSignIn(store, hasher, clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(PasswordSignIn.FailureMessage, (await signIn.Authenticate(username, "wrong")).Error);
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromDays(3650) })
        {
            clock.Advance(wait);
            Assert.Equal(PasswordSignIn.FailureMessage,
                (await signIn.Authenticate(username, " ValidPass123! ")).Error);
            Assert.Equal(PasswordSignIn.FailureMessage, (await signIn.Authenticate(username, "wrong")).Error);
        }
        Assert.Equal(16, hasher.Verifications);
        if (username == Account.Username)
        {
            Assert.True((await store.Load()).Value.Value.Failures.IsLocked);
            Assert.Equal(PasswordSignIn.FailureMessage,
                (await new PasswordSignIn(store, hasher, clock).Authenticate(username, " ValidPass123! ")).Error);
            Assert.Equal(17, hasher.Verifications);
        }
        else
        {
            Assert.True((await signIn.Authenticate(Account.Username, " ValidPass123! ")).IsSuccess);
        }
    }

    [Fact]
    public async Task FailureCountAndDelaySurviveNewSignInInstancesAndConcurrentTenthAttempts()
    {
        var clock = new Clock();
        var hasher = new CountingHasher();
        var store = new Store(Account);
        for (var i = 0; i < 9; i++)
        {
            Assert.True((await new PasswordSignIn(store, hasher, clock).Authenticate(Account.Username, "wrong")).IsFailure);
            if (i >= 2)
            {
                Assert.True((await new PasswordSignIn(store, hasher, clock)
                    .Authenticate(Account.Username, " ValidPass123! ")).IsFailure);
                Assert.Equal(2 * i, hasher.Verifications);
            }
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            new PasswordSignIn(store, hasher, clock).Authenticate(Account.Username, "wrong")));
        Assert.All(results, result => Assert.Equal(PasswordSignIn.FailureMessage, result.Error));
        Assert.Equal(10, (await store.Load()).Value.Value.Failures.Count);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True((await new PasswordSignIn(store, hasher, clock)
            .Authenticate(Account.Username, " ValidPass123! ")).IsFailure);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task KnownAndUnknownNamesUseEquivalentPersistenceAndVerificationWork(int failures)
    {
        var clock = new Clock();
        var knownStore = new Store(Account);
        var unknownStore = new Store(Account);
        var knownHasher = new CountingHasher();
        var unknownHasher = new CountingHasher();
        var known = new PasswordSignIn(knownStore, knownHasher, clock);
        var unknown = new PasswordSignIn(unknownStore, unknownHasher, clock);
        for (var i = 0; i < failures; i++)
        {
            Assert.Equal(PasswordSignIn.FailureMessage, (await known.Authenticate(Account.Username, "wrong")).Error);
            Assert.Equal(PasswordSignIn.FailureMessage, (await unknown.Authenticate("unknown", "wrong")).Error);
            Assert.Equal(i + 1, knownStore.Loads);
            Assert.Equal(knownStore.Loads, unknownStore.Loads);
            Assert.Equal(i + 1, knownStore.Recordings);
            Assert.Equal(knownStore.Recordings, unknownStore.Recordings);
            Assert.Equal(i + 1, knownHasher.Verifications);
            Assert.Equal(knownHasher.Verifications, unknownHasher.Verifications);
            if (i < failures - 1) clock.Advance(TimeSpan.FromSeconds(5));
        }
        var rejected = 0;
        foreach (var password in new[] { "wrong", " ValidPass123! " })
        {
            rejected++;
            Assert.Equal(PasswordSignIn.FailureMessage, (await known.Authenticate(Account.Username, password)).Error);
            Assert.Equal(PasswordSignIn.FailureMessage, (await unknown.Authenticate("unknown", password)).Error);
            Assert.Equal(knownStore.Loads, unknownStore.Loads);
            Assert.Equal(failures + rejected, knownStore.Recordings);
            Assert.Equal(knownStore.Recordings, unknownStore.Recordings);
            Assert.Equal(failures + rejected, knownHasher.Verifications);
            Assert.Equal(knownHasher.Verifications, unknownHasher.Verifications);
        }
        Assert.Equal(failures + 2, knownStore.Loads);
        Assert.Equal(SignInFailures.None, (await unknownStore.Load()).Value.Value.Failures);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task FreshHostsApplyPersistedRestrictionsOnlyToTheMatchingUsername(int count)
    {
        var clock = new Clock();
        var failures = SignInFailures.Restore(count,
            count == 10 ? DateTimeOffset.MinValue : clock.GetUtcNow().AddSeconds(5)).Value;
        var account = Administrator.Create(Account.Username, Account.PasswordHash, failures: failures).Value;
        foreach (var username in new[] { Account.Username, "unknown" })
        foreach (var password in new[] { "wrong", " ValidPass123! " })
        {
            var store = new Store(account);
            var hasher = new CountingHasher();
            // Each request starts with an entirely empty local tracker, as on another host.
            var signIn = new PasswordSignIn(store, hasher, clock);
            Assert.Equal(PasswordSignIn.FailureMessage, (await signIn.Authenticate(username, password)).Error);
            Assert.Equal(1, store.Loads);
            Assert.Equal(1, hasher.Verifications);
            Assert.Equal(1, store.Recordings);
            Assert.Equal(failures, (await store.Load()).Value.Value.Failures);
            clock.Advance(count == 10 ? TimeSpan.FromDays(365) : TimeSpan.Zero);
        }
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

    [Fact]
    public void FailureStateLocksAtTenAndCannotAdvanceOrExpire()
    {
        var state = SignInFailures.None;
        for (var i = 1; i <= 10; i++)
        {
            state = state.RecordFailure(DateTimeOffset.UnixEpoch);
            Assert.Equal(i, state.Count);
            Assert.Equal(i == 10, state.IsLocked);
        }
        Assert.False(state.CanAttempt(DateTimeOffset.MaxValue));
        Assert.Same(state, state.RecordFailure(DateTimeOffset.UnixEpoch));
        Assert.Equal(state, SignInFailures.Restore(10, DateTimeOffset.MinValue).Value);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public void InvalidPersistedFailureStatesAreRejected(int count, bool hasRetryTime)
    {
        var retryAt = hasRetryTime ? DateTimeOffset.UnixEpoch : DateTimeOffset.MinValue;
        Assert.True(SignInFailures.Restore(count, retryAt).IsFailure);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Store(Maybe<Administrator> account, bool invalid = false, bool fault = false) : ITestAdministratorStore
    {
        private int loads;
        private int recordings;
        public int Loads => loads;
        public int Recordings => recordings;

        public async Task<Result<Maybe<Administrator>>> Load(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref loads);
            await Task.Yield();
            if (fault) throw new InvalidOperationException("Database unavailable");
            return invalid
                ? Result.Failure<Maybe<Administrator>>("Invalid persisted state")
                : Result.Success(account);
        }

        public Task<bool> TryRecordSignIn(Administrator expected, string? username, bool succeeded, DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref recordings);
            if (!string.Equals(username, expected.Username, StringComparison.Ordinal)) return Task.FromResult(true);
            lock (this)
            {
                if (account.HasNoValue || !ReferenceEquals(account.Value, expected) || !expected.Failures.CanAttempt(now))
                    return Task.FromResult(false);
                var failures = succeeded ? SignInFailures.None : expected.Failures.RecordFailure(now);
                account = Administrator.Create(expected.Username, expected.PasswordHash,
                    expected.RequiresPasswordReplacement, failures).Value;
                return Task.FromResult(true);
            }
        }

        public Task<Result> RecoverPassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result> ReplacePassword(Administrator expected, Administrator replacement,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Sign-in must not write credentials.");

        public Task CreateIfAbsent(Administrator administrator, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Sign-in must not write credentials.");
    }
}
