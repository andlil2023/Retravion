using Retravion.Identity.Domain;
using Xunit;

namespace Retravion.UnitTests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void EnforcesInclusiveScalarLengthBoundaries(int length, bool accepted)
    {
        var password = "aA1" + new string('x', length - 3);

        Assert.Equal(accepted, PasswordPolicy.Validate(password).IsSuccess);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("1")]
    [InlineData("!")]
    [InlineData("aA")]
    [InlineData("a1")]
    [InlineData("a!")]
    [InlineData("A1")]
    [InlineData("A!")]
    [InlineData("1!")]
    public void RejectsEveryOneAndTwoCategoryCombination(string categories)
    {
        var password = string.Concat(Enumerable.Repeat(categories, 10 / categories.Length));

        Assert.True(PasswordPolicy.Validate(password).IsFailure);
    }

    [Theory]
    [InlineData("aaaaaaaAA1")]
    [InlineData("aaaaaaaAA!")]
    [InlineData("aaaaaaa11!")]
    [InlineData("AAAAAAA11!")]
    [InlineData("aaaaaaAA1!")]
    public void AcceptsEveryThreeCategoryCombinationAndAllFour(string password)
    {
        Assert.True(PasswordPolicy.Validate(password).IsSuccess);
    }

    [Theory]
    [InlineData("!")]
    [InlineData("_")]
    [InlineData("—")]
    [InlineData("«")]
    [InlineData("€")]
    [InlineData("∑")]
    [InlineData("😀")]
    public void AcceptsUnicodePunctuationAndSymbolsWithoutAnAllowlist(string symbol)
    {
        Assert.True(PasswordPolicy.Validate("aaaaaaaAA" + symbol).IsSuccess);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\u00a0")]
    [InlineData("é")]
    [InlineData("É")]
    [InlineData("١")]
    [InlineData("１")]
    [InlineData("\u0301")]
    [InlineData("𐐀")]
    public void OtherCharactersCountOnlyTowardLength(string other)
    {
        Assert.True(PasswordPolicy.Validate("aaaaaaaAA" + other).IsFailure);
        Assert.True(PasswordPolicy.Validate("aaaaaaAA1" + other).IsSuccess);
    }

    [Theory]
    [InlineData("éAAAAAAA11")]
    [InlineData("Éaaaaaaa11")]
    [InlineData("١aaaaaaaAA")]
    [InlineData("１aaaaaaaAA")]
    public void NonAsciiLettersAndDigitsDoNotSupplyAsciiCategories(string password)
    {
        Assert.True(PasswordPolicy.Validate(password).IsFailure);
    }

    [Theory]
    [InlineData(" aaaaaAA1 ")]
    [InlineData("\taaaaaAA1\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaAA1℀")]
    [InlineData("e\u0301aaaaAA1!")]
    public void DoesNotTrimOrNormalizeInput(string password)
    {
        // Trimming makes whitespace cases too short; NFKC expands ℀ past 30 scalars;
        // NFC composes e + combining accent, making the final case too short.
        Assert.True(PasswordPolicy.Validate(password).IsSuccess);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(30, true)]
    [InlineData(31, false)]
    public void CountsSupplementarySymbolsAsOneScalarEach(int length, bool accepted)
    {
        var password = "aA" + string.Concat(Enumerable.Repeat("😀", length - 2));

        Assert.Equal(accepted, PasswordPolicy.Validate(password).IsSuccess);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("          ")]
    public void MissingOrCategoryFreeInputReturnsFailure(string? password)
    {
        Assert.True(PasswordPolicy.Validate(password).IsFailure);
    }

    [Fact]
    public void MalformedUtf16ReturnsFailure()
    {
        Assert.True(PasswordPolicy.Validate("aaaaaaaAA1\ud800").IsFailure);
        Assert.True(PasswordPolicy.Validate("aaaaaaaAA1\udc00").IsFailure);
    }

    [Fact]
    public void FailureDoesNotDiscloseThePassword()
    {
        const string password = "sensitive-but-only-lowercase";

        var result = PasswordPolicy.Validate(password);

        Assert.True(result.IsFailure);
        Assert.DoesNotContain(password, result.Error);
    }
}
