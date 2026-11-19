using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>
/// the options of a digital signature (phase BJ, BJ02). The document is signed with the
/// private key of the certificate (RSA); the signature is a detached CMS SignedData
/// (RFC 5652) in the signature dictionary of the PDF.
///
/// <para>
/// With <see cref="Pades"/> the signature follows the PAdES baseline (ETSI EN 319 142-1, B-B):
/// the signed attributes name the certificate of the signer (RFC 5035), the subfilter is
/// <c>/ETSI.CAdES.detached</c> and the catalog holds a document security store (DSS) with the
/// certificate. A timestamp (B-T/B-LT) needs a time stamp authority and is not part of it.
/// </para>
/// </summary>
public sealed class PdfSignatureOptions
{
    private const int MaxReservedSize = 1024 * 1024;

    private int _reservedSize = 8192;
    private IReadOnlyList<X509Certificate2> _additionalCertificates = [];

    /// <summary>Creates the options with the certificate of the signer.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="certificate"/> is null.</exception>
    public PdfSignatureOptions(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        Certificate = certificate;
    }

    /// <summary>The certificate of the signer (with the private key).</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The name of the signer (the reason is free text).</summary>
    public string? Name { get; init; }

    /// <summary>The reason of the signature.</summary>
    public string? Reason { get; init; }

    /// <summary>The place of the signature.</summary>
    public string? Location { get; init; }

    /// <summary>
    /// The reserved size of the signature in bytes (default 8192, at most 1 MiB). The signature
    /// must fit into it; the rest is filled with zeros. A longer certificate chain needs more.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not between 1 and 1 MiB.</exception>
    public int ReservedSize
    {
        get => _reservedSize;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxReservedSize);
            _reservedSize = value;
        }
    }

    /// <summary>
    /// Further certificates of the chain (intermediate CAs, optionally the root) that are
    /// embedded with the signature, so a validator can build the path to a trusted root.
    /// </summary>
    public IReadOnlyList<X509Certificate2> AdditionalCertificates
    {
        get => _additionalCertificates;
        init => _additionalCertificates = value is null ? [] : [.. value];
    }

    /// <summary>
    /// Whether the signature follows the PAdES baseline (ETSI EN 319 142-1, B-B): the signed
    /// attributes carry the "signing certificate v2" (RFC 5035), the subfilter is
    /// <c>/ETSI.CAdES.detached</c> and the catalog holds a document security store with the
    /// certificate of the signer. Without a timestamp it is a B-B signature.
    /// </summary>
    public bool Pades { get; init; }

    /// <summary>
    /// optional validation material (OF-E03): certificates, OCSP responses and CRLs are written as
    /// a document security store with a VRI entry in an incremental update after the signature,
    /// which makes it a PAdES baseline B-LT signature. Requires <see cref="Pades"/>.
    /// </summary>
    public PdfValidationData? LongTermValidation { get; init; }

    /// <summary>
    /// optional time-stamp authority (OF-E03): a PAdES baseline B-T signature. The writer requests
    /// a timestamp token (RFC 3161) over the signature value and embeds it as the unsigned
    /// attribute <c>id-aa-signatureTimeStampToken</c>. The result still has the security store of
    /// B-B; the long-term validation data of B-LT is not part of it.
    /// </summary>
    public PdfTimestampOptions? Timestamp { get; init; }
}
