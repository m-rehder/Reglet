using System.Security.Cryptography;
using System.Text;

namespace Reglet;

/// <summary>
/// the password hash of a protected DOCX document (phase AD, AD03). The hash follows
/// ISO/IEC 29500-1, 17.15.1.29: the password is hashed with a random salt and
/// <c>spinCount</c> iterations; Word 2013 and newer understand it.
/// </summary>
internal static class DocxPasswordHash
{
    /// <summary>The number of iterations (the value Word 2013 writes).</summary>
    internal const int SpinCount = 100_000;

    /// <summary>The hash algorithm (as named in <c>w:algorithmName</c>).</summary>
    internal const string AlgorithmName = "SHA-512";

    /// <summary>
    /// Computes the hash value of a password. The salt is generated randomly, so the same
    /// password leads to different values.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <param name="salt">The random salt (16 bytes).</param>
    /// <returns>The hash value and the salt, both base64 encoded.</returns>
    internal static (string HashValue, string SaltValue) Compute(string password, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);

        var hash = Hash(password, salt, SpinCount);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>Computes the hash value of a password with a given salt.</summary>
    internal static byte[] Hash(string password, byte[] salt, int spinCount)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spinCount);

        // The password is hashed as UTF-16LE (as in Word).
        var passwordBytes = Encoding.Unicode.GetBytes(password);
        var buffer = new byte[salt.Length + passwordBytes.Length];
        salt.CopyTo(buffer, 0);
        passwordBytes.CopyTo(buffer, salt.Length);

        var hash = SHA512.HashData(buffer);
        var iteration = new byte[4];
        for (var round = 0; round < spinCount; round++)
        {
            iteration[0] = (byte)(round & 0xFF);
            iteration[1] = (byte)((round >> 8) & 0xFF);
            iteration[2] = (byte)((round >> 16) & 0xFF);
            iteration[3] = (byte)((round >> 24) & 0xFF);
            hash = SHA512.HashData([.. hash, .. iteration]);
        }

        return hash;
    }

    /// <summary>Generates a random salt of 16 bytes.</summary>
    internal static byte[] CreateSalt() => RandomNumberGenerator.GetBytes(16);
}
