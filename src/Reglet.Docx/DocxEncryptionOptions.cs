namespace Reglet;

/// <summary>
/// the ECMA-376 agile encryption of a DOCX (OF-E01). The package is encrypted with AES-256-CBC
/// and a password-derived key (SHA-512, iterated); the result is a compound file that Word and
/// LibreOffice open with the password.
/// </summary>
public sealed record DocxEncryptionOptions
{
    private int _spinCount = 100000;

    /// <summary>Creates the options with the password that protects the document.</summary>
    /// <exception cref="ArgumentException">The password is empty.</exception>
    public DocxEncryptionOptions(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        Password = password;
    }

    /// <summary>The password that opens the document.</summary>
    public string Password { get; }

    /// <summary>
    /// the number of hash iterations of the key derivation. Default is 100000, the value Word
    /// writes; a higher value makes guessing the password slower.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above 10,000,000.</exception>
    public int SpinCount
    {
        get => _spinCount;
        init
        {
            if (value is < 0 or > DocxAgileEncryption.MaxSpinCount)
            {
                throw new ArgumentOutOfRangeException(nameof(SpinCount), value, "The spin count must be between 0 and 10,000,000 (MS-OFFCRYPTO).");
            }

            _spinCount = value;
        }
    }
}

/// <summary>Options for the DOCX reader.</summary>
public sealed record DocxReadOptions
{
    /// <summary>Default options.</summary>
    public static DocxReadOptions Default { get; } = new();

    /// <summary>
    /// the password of an encrypted DOCX (OF-E01). A document without encryption ignores it.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Keeps the VBA project of a macro-enabled document in <see cref="DocumentMetadata.MacroProject"/> (ADR-0028).
    /// Off by default: the project is dropped and reported. It is never executed either way.
    /// </summary>
    public bool KeepMacros { get; init; }
}
