using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>
/// the digital signature of a DOCX package (OF-E02): an OPC signature part with an XML signature
/// (XMLDSig) over the parts of the package.
/// </summary>
public sealed record DocxSignatureOptions
{
    private IReadOnlyList<X509Certificate2> _additionalCertificates = [];

    /// <summary>Creates the options with the certificate of the signer.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is null.</exception>
    public DocxSignatureOptions(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        Certificate = certificate;
    }

    /// <summary>The certificate of the signer (it must have an RSA private key).</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Further certificates of the chain that are embedded in the signature.</summary>
    public IReadOnlyList<X509Certificate2> AdditionalCertificates
    {
        get => _additionalCertificates;
        init => _additionalCertificates = value is null ? [] : [.. value];
    }

    /// <summary>
    /// the parts to sign; null signs every part of the package except the signature parts. A part
    /// name is written with a leading slash (<c>/word/document.xml</c>).
    /// </summary>
    public IReadOnlyList<string>? Parts { get; init; }
}

/// <summary>the result of reading one signature of a DOCX package (OF-E02).</summary>
public sealed record DocxSignatureInfo
{
    /// <summary>The certificate of the signer from the signature (null when the signature has none).</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>The subject of the certificate, for display.</summary>
    public string? Subject => Certificate?.Subject;

    /// <summary>The parts the signature covers, as they are named in the signature.</summary>
    public IReadOnlyList<string> SignedParts { get; init; } = [];

    /// <summary>Whether the signature and every digest are valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Whether the certificate chains to a trusted root of the read options and is valid at the
    /// signing time; null when no trusted roots were given.
    /// </summary>
    public bool? IsTrusted { get; init; }

    /// <summary>The problems of the chain check (expired, untrusted root, revocation, …).</summary>
    public IReadOnlyList<string> ChainProblems { get; init; } = [];

    /// <summary>What is wrong with the signature; empty when it is valid.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}

/// <summary>the trust settings for reading a DOCX signature (OF-E02).</summary>
public sealed record DocxSignatureReadOptions
{
    private IReadOnlyList<X509Certificate2> _trustedRoots = [];

    /// <summary>Default options: the signature is verified, the certificate chain is not evaluated.</summary>
    public static DocxSignatureReadOptions Default { get; } = new();

    /// <summary>
    /// the trusted root certificates. Without them the cryptographic signature is checked, but a
    /// certificate is not judged trusted; with them the chain is built against exactly these roots.
    /// </summary>
    public IReadOnlyList<X509Certificate2> TrustedRoots
    {
        get => _trustedRoots;
        init => _trustedRoots = value is null ? [] : [.. value];
    }

    /// <summary>
    /// how the chain checks revocation. Default is <see cref="X509RevocationMode.NoCheck"/>, because
    /// the library never calls the network; a caller that wants revocation sets
    /// <see cref="X509RevocationMode.Online"/> and accepts the network access.
    /// </summary>
    public X509RevocationMode RevocationMode { get; init; } = X509RevocationMode.NoCheck;
}
