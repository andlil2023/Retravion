using System.Buffers;
using System.Text;
using CSharpFunctionalExtensions;

namespace Retravion.Identity.Domain;

public static class PasswordPolicy
{
    public static Result Validate(string? password)
    {
        if (password is null)
        {
            return Result.Failure("A password is required.");
        }

        var remaining = password.AsSpan();
        var length = 0;
        var lowercase = false;
        var uppercase = false;
        var digit = false;
        var symbol = false;

        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var character, out var consumed) != OperationStatus.Done)
            {
                return Result.Failure("The password must contain valid Unicode scalar values.");
            }

            remaining = remaining[consumed..];
            if (++length > 30)
            {
                return Result.Failure("The password must contain 10–30 Unicode scalar values.");
            }

            lowercase |= character.Value is >= 'a' and <= 'z';
            uppercase |= character.Value is >= 'A' and <= 'Z';
            digit |= character.Value is >= '0' and <= '9';
            symbol |= Rune.IsPunctuation(character) || Rune.IsSymbol(character);
        }

        if (length < 10)
        {
            return Result.Failure("The password must contain 10–30 Unicode scalar values.");
        }

        var categories = (lowercase ? 1 : 0) + (uppercase ? 1 : 0)
            + (digit ? 1 : 0) + (symbol ? 1 : 0);

        return categories >= 3
            ? Result.Success()
            : Result.Failure("The password must include at least three character categories: a–z, A–Z, 0–9, and Unicode punctuation or symbols.");
    }
}
