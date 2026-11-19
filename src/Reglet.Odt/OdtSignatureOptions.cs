using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>
/// the digital signature of an ODT package (OF-E05): an XML signature (XMLDSig, RSA/SHA-256)
/// over every file of the package, stored in <c>META-INF/documentsignatures.xml</c>.
/// </summary>
public sealed record OdtSignatureOptions
{
    private IReadOnlyList<X509Certificate2> _additionalCertificates = [];

    /// <summary>Creates the options with the certificate of the signer.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is null.</exception>
    public OdtSignatureOptions(X509Certificate2 certificate)
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
}

/// <summary>the result of reading one signature of an ODT package (OF-E05).</summary>
public sealed record OdtSignatureInfo
{
    /// <summary>The certificate of the signer from the signature (null when the signature has none).</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>The subject of the certificate, for display.</summary>
    public string? Subject => Certificate?.Subject;

    /// <summary>The parts the signature covers, as they are named in the signature.</summary>
    public IReadOnlyList<string> SignedParts { get; init; } = [];

    /// <summary>Whether the signature and every digest are valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>What is wrong with the signature; empty when it is valid.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
}
