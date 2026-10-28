using Retravion.Identity.Domain;
using Xunit;

namespace Retravion.UnitTests;

public sealed class AccountAvailabilityTests
{
    [Fact]
    public void AvailabilityConfirmationHasExactlyOneDialogProviderInMainLayout()
    {
        // An idle MudDialogProvider emits no HTML. Guard its required registration in source;
        // opening, confirming and cancelling the dialog still require interactive verification.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Retravion.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var webRoot = Path.Combine(root.FullName, "src", "Retravion.Web");
        var layout = File.ReadAllText(Path.Combine(webRoot, "Components", "Layout", "MainLayout.razor"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(layout, @"<MudDialogProvider\s*/>"));
        var providers = Directory.EnumerateFiles(webRoot, "*.razor", SearchOption.AllDirectories)
            .Sum(path => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"<MudDialogProvider\b").Count);
        Assert.Equal(1, providers);
    }

    [Theory]
    [InlineData(AccountType.Ordinary)]
    [InlineData(AccountType.Administrator)]
    public void TransitionsPreserveCredentialsIdentityLockoutAndMandatoryReplacement(AccountType type)
    {
        var account = Administrator.CreateAccount(AccountIdentity.Create("target", "Target", type).Value, "hash").Value;
        for (var i = 0; i < 10; i++) account = account.RecordSignIn(false, DateTimeOffset.UnixEpoch.AddSeconds(i * 5));
        Assert.True(account.Failures.IsLocked);
        var inactive = account.Inactivate("actor").Value;
        Assert.False(inactive.IsActive);
        Assert.True(inactive.Inactivate("actor").IsFailure);
        var active = inactive.Reactivate().Value;
        Assert.True(active.IsActive);
        Assert.True(active.Reactivate().IsFailure);
        Assert.Equal(account.Username, active.Username);
        Assert.Equal(account.DisplayName, active.DisplayName);
        Assert.Equal(account.AccountType, active.AccountType);
        Assert.Equal(account.PasswordHash, active.PasswordHash);
        Assert.Equal(account.Failures, active.Failures);
        Assert.True(active.RequiresPasswordReplacement);
        Assert.False(inactive.RecordSignIn(false, DateTimeOffset.UnixEpoch).IsActive);
        Assert.False(inactive.ReplacePassword("Replacement123!", "new-hash").Value.IsActive);
        Assert.False(inactive.RecoverPassword("Recovered123!", "recovered-hash").Value.IsActive);
    }

    [Fact]
    public void SelfInactivationIsRejectedCaseInsensitively()
    {
        var account = Administrator.Create("Admin", "hash").Value;
        Assert.True(account.Inactivate("ADMIN").IsFailure);
        Assert.True(account.IsActive);
    }
}
