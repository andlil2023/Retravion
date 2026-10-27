namespace Retravion.Identity.Application;

/// <summary>Creates and verifies opaque, salted, one-way password hashes.</summary>
public interface IPasswordHasher
{
    /// <summary>Returns an encoded hash suitable for credential storage, never the password.</summary>
    /// <remarks>Password policy validation belongs to the caller. Null is a programming error.</remarks>
    string Hash(string password);

    /// <summary>Returns false for a mismatch, missing hash, or unsupported or malformed hash.</summary>
    /// <remarks>A null password is a programming error.</remarks>
    bool Verify(string password, string? encodedHash);
}
