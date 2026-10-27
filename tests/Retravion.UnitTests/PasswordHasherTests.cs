using System.Security.Cryptography;
using Retravion.Identity.Application;
using Retravion.Identity.Infrastructure;
using Xunit;

namespace Retravion.UnitTests;

public sealed class PasswordHasherTests
{
    private readonly IPasswordHasher _hasher = new Pbkdf2PasswordHasher();

    [Theory]
    [InlineData("Correct-Horse7!")]
    [InlineData(" Åä漢字🔑 Password7! ")]
    [InlineData("")]
    public void MatchingPasswordVerifiesAcrossInstancesWithoutApplyingPasswordPolicy(string password)
    {
        var encodedHash = _hasher.Hash(password);

        Assert.True(new Pbkdf2PasswordHasher().Verify(password, encodedHash));
        Assert.False(_hasher.Verify(password + "x", encodedHash));
    }

    [Fact]
    public void PasswordComparisonIsCaseAndWhitespaceSensitive()
    {
        var encodedHash = _hasher.Hash("Password-123!");

        Assert.False(_hasher.Verify("password-123!", encodedHash));
        Assert.False(_hasher.Verify("Password-123! ", encodedHash));
    }

    [Fact]
    public void IdenticalPasswordsHaveDistinctSaltsAndHashes()
    {
        const string password = "Correct-Horse7!";
        var first = _hasher.Hash(password);
        var second = _hasher.Hash(password);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.Split('$')[1], second.Split('$')[1]);
        Assert.NotEqual(first.Split('$')[2], second.Split('$')[2]);
        Assert.True(_hasher.Verify(password, first));
        Assert.True(_hasher.Verify(password, second));
    }

    [Fact]
    public void StoredFormatContainsOnlyVersionSaltAndOneWayDerivedKey()
    {
        const string password = "Never-Store-This-Password7!";
        var encodedHash = _hasher.Hash(password);
        var parts = encodedHash.Split('$');

        Assert.Equal(3, parts.Length);
        Assert.Equal("pbkdf2-sha256-v1", parts[0]);
        var salt = Convert.FromBase64String(parts[1]);
        var key = Convert.FromBase64String(parts[2]);
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, key.Length);
        Assert.DoesNotContain(password, encodedHash);
        Assert.Equal(
            Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32),
            key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext-password")]
    [InlineData("pbkdf2-sha256-v1$")]
    [InlineData("pbkdf2-sha256-v2$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256-v1$!AAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256-v1$AAAAAAAAAAAAAAAAAAAAAA==$!AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256-v1$AAAAAAAAAAAAAAAAAAAAAA==:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256-v1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=$extra")]
    [InlineData("pbkdf2-sha256-v1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    public void MissingMalformedAndUnsupportedHashesFailClosed(string? encodedHash)
    {
        Assert.False(_hasher.Verify("Password-123!", encodedHash));
    }

    [Fact]
    public void OversizedHashFailsClosed()
    {
        Assert.False(_hasher.Verify("Password-123!", new string('A', 100_000)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TamperedSaltOrKeyDoesNotVerify(int partIndex)
    {
        const string password = "Password-123!";
        var parts = _hasher.Hash(password).Split('$');
        var bytes = Convert.FromBase64String(parts[partIndex]);
        bytes[0] ^= 1;
        parts[partIndex] = Convert.ToBase64String(bytes);

        Assert.False(_hasher.Verify(password, string.Join('$', parts)));
    }

    [Fact]
    public void NullPasswordIsAProgrammingError()
    {
        Assert.Throws<ArgumentNullException>(() => _hasher.Hash(null!));
        Assert.Throws<ArgumentNullException>(() => _hasher.Verify(null!, null));
    }
}
