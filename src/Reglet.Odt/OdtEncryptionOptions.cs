namespace Reglet;

/// <summary>
/// ODF 1.3 package encryption (OF-E05). The package is encrypted per file entry with
/// AES-256-CBC and a key derived from the password (SHA-256 start key, PBKDF2-HMAC-SHA1);
/// the manifest carries the parameters and a checksum. The <c>mimetype</c> entry and the
/// manifest itself stay unencrypted.
/// </summary>
/// <remarks>
/// Encryption uses random salts and initialization vectors, so the output is intentionally not
/// deterministic. The password is never stored in the package.
/// </remarks>
public sealed record OdtEncryptionOptions
{
    private int _iterationCount = 100000;

    /// <summary>Creates the options with the password that protects the document.</summary>
    /// <exception cref="ArgumentException">The password is null or empty.</exception>
    public OdtEncryptionOptions(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        Password = password;
    }

    /// <summary>The password that opens the document.</summary>
    public string Password { get; }

    /// <summary>
    /// the number of PBKDF2 iterations. The default is 100000; a higher value makes guessing
    /// the password slower.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and 1,000,000.</exception>
    public int IterationCount
    {
        get => _iterationCount;
        init
        {
            if (value is < 1 or > OdtEncryption.MaxIterations)
            {
                throw new ArgumentOutOfRangeException(nameof(IterationCount), value, "The iteration count must be between 1 and 1,000,000.");
            }

            _iterationCount = value;
        }
    }
}

/// <summary>Options for the ODT reader.</summary>
public sealed record OdtReadOptions
{
    /// <summary>Default options.</summary>
    public static OdtReadOptions Default { get; } = new();

    /// <summary>
    /// the password of an encrypted ODT (OF-E05). A document without encryption ignores it.
    /// </summary>
    public string? Password { get; init; }
}
