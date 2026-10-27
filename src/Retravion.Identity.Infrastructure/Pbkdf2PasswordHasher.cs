using System.Security.Cryptography;
using Retravion.Identity.Application;

namespace Retravion.Identity.Infrastructure;

/// <summary>PBKDF2-SHA256 password hashing using a fresh cryptographic salt for every hash.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    // Version 1 fixes the algorithm, work factor and sizes. Changes require a new version.
    private const string Prefix = "pbkdf2-sha256-v1$";
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int EncodedSaltSize = 24;
    private const int EncodedKeySize = 44;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Prefix}{Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string? encodedHash)
    {
        ArgumentNullException.ThrowIfNull(password);

        // Bound parsing and never take a work factor or allocation size from stored data.
        if (encodedHash is null ||
            encodedHash.Length != Prefix.Length + EncodedSaltSize + 1 + EncodedKeySize ||
            !encodedHash.StartsWith(Prefix, StringComparison.Ordinal) ||
            encodedHash[Prefix.Length + EncodedSaltSize] != '$')
        {
            return false;
        }

        Span<byte> salt = stackalloc byte[SaltSize];
        Span<byte> expectedKey = stackalloc byte[KeySize];
        if (!Convert.TryFromBase64Chars(encodedHash.AsSpan(Prefix.Length, EncodedSaltSize), salt, out var saltLength) ||
            saltLength != SaltSize ||
            !Convert.TryFromBase64Chars(encodedHash.AsSpan(Prefix.Length + EncodedSaltSize + 1), expectedKey, out var keyLength) ||
            keyLength != KeySize)
        {
            return false;
        }

        Span<byte> actualKey = stackalloc byte[KeySize];
        Rfc2898DeriveBytes.Pbkdf2(password, salt, actualKey, Iterations, HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }
}
