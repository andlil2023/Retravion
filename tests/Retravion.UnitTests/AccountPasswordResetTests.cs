using Retravion.Identity.Domain;
using Xunit;

namespace Retravion.UnitTests;

public sealed class AccountPasswordResetTests
{
    [Theory]
    [InlineData(AccountType.Ordinary, false)]
    [InlineData(AccountType.Ordinary, true)]
    [InlineData(AccountType.Administrator, false)]
    [InlineData(AccountType.Administrator, true)]
    public void ResetPreservesIdentityAndAvailabilityClearsLockoutAndRequiresReplacement(AccountType type, bool active)
    {
        var account = Administrator.CreateAccount(AccountIdentity.Create("target", "Target 😀", type).Value, "old-hash").Value;
        if (!active) account = account.Inactivate("actor").Value;
        for (var i = 0; i < 10; i++) account = account.RecordSignIn(false, DateTimeOffset.UnixEpoch.AddSeconds(i * 5));
        Assert.True(account.Failures.IsLocked);
        var reset = account.ResetPassword("actor", "TemporaryPass123!", "new-hash").Value;
        Assert.Equal(account.Username, reset.Username);
        Assert.Equal(account.DisplayName, reset.DisplayName);
        Assert.Equal(type, reset.AccountType);
        Assert.Equal(account.IsInitialAdministrator, reset.IsInitialAdministrator);
        Assert.Equal(active, reset.IsActive);
        Assert.Equal("new-hash", reset.PasswordHash);
        Assert.Equal(SignInFailures.None, reset.Failures);
        Assert.True(reset.RequiresPasswordReplacement);
        Assert.True(account.Failures.IsLocked);
        Assert.Equal("old-hash", account.PasswordHash);
        Assert.True(account.ResetPassword("TARGET", "TemporaryPass123!", "other-hash").IsFailure);
        Assert.True(account.ResetPassword("actor", "short", "other-hash").IsFailure);
        Assert.True(account.ResetPassword("actor", "TemporaryPass123!", "old-hash").IsFailure);
    }
}
