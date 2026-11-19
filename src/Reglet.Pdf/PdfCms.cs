using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>
/// builds a detached CMS SignedData (phase BJ, BJ02) as in RFC 5652: the signature is
/// computed over the SHA-256 digest of the content, the signed attributes carry the
/// content type, the signing time and the message digest. The signature algorithm is
/// RSA with SHA-256 (PKCS#1 v1.5).
/// </summary>
internal static class PdfCms
{
    /// <summary>The object identifier of the signed data (RFC 5652).</summary>
    private const string SignedDataOid = "1.2.840.113549.1.7.2";

    /// <summary>The object identifier of the encapsulated content "data".</summary>
    private const string DataOid = "1.2.840.113549.1.7.1";

    /// <summary>The object identifier of SHA-256.</summary>
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    /// <summary>The object identifier of RSA encryption (the signature algorithm).</summary>
    private const string RsaOid = "1.2.840.113549.1.1.1";

    /// <summary>The object identifier of the attribute "content type".</summary>
    private const string ContentTypeOid = "1.2.840.113549.1.9.3";

    /// <summary>The object identifier of the attribute "signing time".</summary>
    private const string SigningTimeOid = "1.2.840.113549.1.9.5";

    /// <summary>The object identifier of the attribute "message digest".</summary>
    private const string MessageDigestOid = "1.2.840.113549.1.9.4";

    /// <summary>The object identifier of the attribute "signing certificate v2" (RFC 5035).</summary>
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";

    /// <summary>
    /// signs the content and returns the DER of the ContentInfo with the SignedData.
    /// </summary>
    /// <param name="content">The signed data.</param>
    /// <param name="certificate">The certificate of the signer (with the private key).</param>
    /// <param name="signingTime">The time of the signature.</param>
    /// <param name="pades">
    /// Whether the signature follows the PAdES baseline (ETSI EN 319 142-1, B-B): the signed
    /// attributes carry the "signing certificate v2" (RFC 5035) with the hash of the certificate.
    /// </param>
    /// <param name="additionalCertificates">Further certificates of the chain that are embedded.</param>
    /// <param name="timestamp">
    /// optional time-stamp authority (OF-E03): its token over the signature value is embedded as
    /// an unsigned attribute (PAdES baseline B-T).
    /// </param>
    /// <exception cref="ArgumentNullException">Content or certificate is null.</exception>
    /// <exception cref="InvalidOperationException">The certificate has no usable private key.</exception>
    public static byte[] SignDetached(
        byte[] content,
        X509Certificate2 certificate,
        DateTimeOffset signingTime,
        bool pades = false,
        IReadOnlyList<X509Certificate2>? additionalCertificates = null,
        PdfTimestampOptions? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(certificate);

        using var rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The certificate has no RSA private key for the signature.");

        var digest = SHA256.HashData(content);

        // The signed attributes: the content type, the signing time and the message digest.
        var contentTypeAttribute = PdfDer.Sequence(
            PdfDer.Oid(ContentTypeOid),
            PdfDer.SetOf([PdfDer.Oid(DataOid)]));
        var signingTimeAttribute = PdfDer.Sequence(
            PdfDer.Oid(SigningTimeOid),
            PdfDer.SetOf([PdfDer.UtcTime(signingTime)]));
        var messageDigestAttribute = PdfDer.Sequence(
            PdfDer.Oid(MessageDigestOid),
            PdfDer.SetOf([PdfDer.OctetString(digest)]));

        // PAdES (ETSI EN 319 142-1, 5.2.1): the signing-time attribute shall not be present; the
        // claimed time is the /M entry of the signature dictionary.
        var attributes = pades
            ? new List<byte[]> { contentTypeAttribute, messageDigestAttribute }
            : new List<byte[]> { contentTypeAttribute, signingTimeAttribute, messageDigestAttribute };

        if (pades)
        {
            // PAdES B-B: the "signing certificate v2" attribute (RFC 5035) names the
            // certificate of the signer by its hash.
            attributes.Add(SigningCertificateV2Attribute(certificate));
        }

        // The signature is computed over the DER of the attributes as a SET OF (the tag 0x31),
        // not over the implicit context tag of the SignerInfo.
        var signedAttributes = PdfDer.SetOf(attributes);
        var signature = rsa.SignData(signedAttributes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var issuerAndSerial = PdfDer.Sequence(
            certificate.IssuerName.RawData,
            SerialInteger(certificate));

        // OF-E03: the timestamp token covers the signature value and becomes an unsigned
        // attribute, so it is requested between computing the signature and writing the SignerInfo.
        var unsignedAttributes = new List<byte[]>();
        if (timestamp is { } authority)
        {
            var request = PdfTimestamp.BuildRequest(signature, authority.Policy);
            var token = PdfTimestamp.ReadToken(authority.Request(request));
            unsignedAttributes.Add(PdfTimestamp.TimestampAttribute(token));
        }

        var signerInfo = PdfDer.Sequence(
            [
                PdfDer.Integer(1),
                issuerAndSerial,
                PdfDer.Sequence(PdfDer.Oid(Sha256Oid), PdfDer.Null()),
                PdfDer.Context(0, constructed: true, PdfDer.ContentOf(signedAttributes)),
                PdfDer.Sequence(PdfDer.Oid(RsaOid), PdfDer.Null()),
                PdfDer.OctetString(signature),
                .. unsignedAttributes.Count == 0
                    ? Array.Empty<byte[]>()
                    : [PdfDer.Context(1, constructed: true, PdfDer.Concat(unsignedAttributes))],
            ]);

        var signedData = PdfDer.Sequence(
            PdfDer.Integer(1),
            PdfDer.SetOf([PdfDer.Sequence(PdfDer.Oid(Sha256Oid), PdfDer.Null())]),
            // Detached: the encapsulated content carries only the type.
            PdfDer.Sequence(PdfDer.Oid(DataOid)),
            // The certificates: the signer and the chain up to the root, so a validator can build the path.
            PdfDer.Context(0, constructed: true, PdfDer.Concat([certificate.RawData, .. (additionalCertificates ?? []).Select(extra => extra.RawData)])),
            PdfDer.SetOf([signerInfo]));

        return PdfDer.Sequence(
            PdfDer.Oid(SignedDataOid),
            PdfDer.Context(0, constructed: true, signedData));
    }

    /// <summary>
    /// The "signing certificate v2" attribute (RFC 5035): the certificates of the signers with
    /// the hash of the certificate (the issuer and the serial number are optional and omitted).
    /// </summary>
    /// <param name="certificate">The certificate of the signer.</param>
    private static byte[] SigningCertificateV2Attribute(X509Certificate2 certificate)
    {
        var certHash = SHA256.HashData(certificate.RawData);

        // ESSCertIDv2: the hash algorithm (SHA-256 without parameters) and the hash.
        var certId = PdfDer.Sequence(
            PdfDer.Sequence(PdfDer.Oid(Sha256Oid)),
            PdfDer.OctetString(certHash));

        // SigningCertificateV2: a SEQUENCE with the SEQUENCE OF the certificates.
        var signingCertificate = PdfDer.Sequence(PdfDer.Sequence(certId));

        return PdfDer.Sequence(
            PdfDer.Oid(SigningCertificateV2Oid),
            PdfDer.SetOf([signingCertificate]));
    }

    /// <summary>
    /// the serial number of the certificate as a DER INTEGER (the serial number is a hex
    /// string in the .NET API).
    /// </summary>
    private static byte[] SerialInteger(X509Certificate2 certificate)
    {
        var hex = certificate.SerialNumber.Trim();
        if (hex.StartsWith('-'))
        {
            throw new InvalidOperationException("A negative serial number cannot be signed.");
        }

        if (hex.Length % 2 == 1)
        {
            hex = "0" + hex;
        }

        var bytes = new List<byte>();
        for (var index = 0; index < hex.Length; index += 2)
        {
            bytes.Add(byte.Parse(hex.Substring(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        while (bytes.Count > 1 && bytes[0] == 0)
        {
            bytes.RemoveAt(0);
        }

        if (bytes.Count > 0 && (bytes[0] & 0x80) != 0)
        {
            bytes.Insert(0, 0);
        }

        return PdfDer.Tlv(0x02, [.. bytes]);
    }
}
