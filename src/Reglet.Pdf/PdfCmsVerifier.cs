using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Reglet;

/// <summary>
/// verifies a CMS SignedData of a PDF signature (RFC 5652): the message digest of the signed
/// attributes, the signature of the signer over them and an embedded RFC 3161 time-stamp token.
/// RSA (PKCS#1 v1.5 and PSS with the digest length as salt) and ECDSA with SHA-256/384/512 are
/// supported. The trust of the certificate is not judged here.
/// </summary>
internal static class PdfCmsVerifier
{
    private const string SignedDataOid = "1.2.840.113549.1.7.2";
    private const string ContentTypeOid = "1.2.840.113549.1.9.3";
    private const string MessageDigestOid = "1.2.840.113549.1.9.4";
    private const string SigningTimeOid = "1.2.840.113549.1.9.5";
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";
    private const string TstInfoOid = "1.2.840.113549.1.9.16.1.4";
    private const string RsaOid = "1.2.840.113549.1.1.1";
    private const string RsaPssOid = "1.2.840.113549.1.1.10";
    private const string EcPublicKeyOid = "1.2.840.10045.2.1";

    /// <summary>
    /// checks the SignedData. For a detached signature <paramref name="detachedDigest"/> hashes the
    /// signed bytes with the algorithm of the signer; a signature with encapsulated content (a time-stamp
    /// token) signs that content.
    /// </summary>
    public static CmsResult Verify(byte[] cms, Func<HashAlgorithmName, byte[]>? detachedDigest)
    {
        var result = new CmsResult();
        try
        {
            VerifyCore(cms, detachedDigest, result, depth: 0);
        }
        catch (DocumentValidationException exception)
        {
            result.Problems.Add(exception.Message);
        }
        catch (CryptographicException exception)
        {
            result.Problems.Add("PDF signature: the cryptographic check failed (" + exception.Message + ").");
        }

        return result;
    }

    private static void VerifyCore(byte[] cms, Func<HashAlgorithmName, byte[]>? detachedDigest, CmsResult result, int depth)
    {
        var root = DerNode.Parse(cms, 0, cms.Length);
        var rootChildren = root.Tag == 0x30 ? root.Children() : [];
        if (rootChildren.Count < 2 || rootChildren[0].OidValue() != SignedDataOid || rootChildren[1].Tag != 0xA0)
        {
            throw new DocumentValidationException("PDF signature: the contents are not a CMS SignedData.");
        }

        var signedData = rootChildren[1].Children().FirstOrDefault();
        if (signedData.Buffer is null || signedData.Tag != 0x30)
        {
            throw new DocumentValidationException("PDF signature: the SignedData is empty.");
        }

        var parts = signedData.Children();
        if (parts.Count < 4)
        {
            throw new DocumentValidationException("PDF signature: the SignedData is incomplete.");
        }

        // version, digestAlgorithms, encapContentInfo, [0] certificates, [1] crls, signerInfos.
        var encapsulated = parts[2].Children();
        result.ContentType = encapsulated.Count > 0 ? encapsulated[0].OidValue() : string.Empty;
        byte[]? content = null;
        if (encapsulated.Count > 1 && encapsulated[1].Tag == 0xA0 && encapsulated[1].Children() is { Count: > 0 } wrapped)
        {
            content = wrapped[0].OctetValue();
        }

        result.Content = content;

        var signerInfos = default(DerNode);
        foreach (var part in parts.Skip(3))
        {
            if (part.Tag == 0xA0)
            {
                foreach (var certificate in part.Children())
                {
                    if (certificate.Tag != 0x30)
                    {
                        continue;
                    }

                    try
                    {
                        result.Certificates.Add(X509CertificateLoader.LoadCertificate(certificate.Raw));
                    }
                    catch (CryptographicException)
                    {
                        result.Problems.Add("PDF signature: a certificate of the signature is not valid X.509.");
                    }
                }
            }
            else if (part.Tag == 0x31)
            {
                signerInfos = part;
            }
        }

        var signers = signerInfos.Buffer is null ? [] : signerInfos.Children();
        if (signers.Count == 0)
        {
            throw new DocumentValidationException("PDF signature: the signature has no signer.");
        }

        if (signers.Count > 1)
        {
            result.Problems.Add("PDF signature: only the first of several signers is checked.");
        }

        VerifySigner(signers[0], content, detachedDigest, result, depth);
    }

    private static void VerifySigner(DerNode signer, byte[]? content, Func<HashAlgorithmName, byte[]>? detachedDigest, CmsResult result, int depth)
    {
        var fields = signer.Children();
        var index = 0;
        if (fields.Count < 5 || fields[index++].Tag != 0x02)
        {
            throw new DocumentValidationException("PDF signature: the signer information is incomplete.");
        }

        var identifier = fields[index++];
        var digestAlgorithm = fields[index++];
        DerNode? signedAttributes = fields[index].Tag == 0xA0 ? fields[index++] : null;
        if (index + 1 >= fields.Count)
        {
            throw new DocumentValidationException("PDF signature: the signer information is incomplete.");
        }

        var signatureAlgorithm = fields[index++];
        var signature = fields[index++].OctetValue();
        DerNode? unsignedAttributes = index < fields.Count && fields[index].Tag == 0xA1 ? fields[index] : null;
        result.SignatureValue = signature;

        var digestParts = digestAlgorithm.Children();
        var hash = HashOf(digestParts.Count > 0 ? digestParts[0].OidValue() : string.Empty);
        if (hash is null)
        {
            result.Problems.Add("PDF signature: the digest algorithm is not supported (SHA-256, SHA-384 and SHA-512 are).");
            return;
        }

        var contentDigest = content is not null
            ? HashData(hash.Value, content)
            : detachedDigest?.Invoke(hash.Value);
        if (contentDigest is null)
        {
            result.Problems.Add("PDF signature: the signature has no content to check.");
            return;
        }

        var signerCertificate = FindCertificate(identifier, result.Certificates);
        result.Signer = signerCertificate;
        if (signerCertificate is null)
        {
            result.Problems.Add("PDF signature: the certificate of the signer is not part of the signature.");
        }

        byte[] signedBytes;
        if (signedAttributes is { } attributes)
        {
            byte[]? messageDigest = null;
            foreach (var attribute in attributes.Children())
            {
                var pair = attribute.Children();
                if (pair.Count != 2 || pair[1].Children().Count == 0)
                {
                    continue;
                }

                var value = pair[1].Children()[0];
                switch (pair[0].OidValue())
                {
                    case MessageDigestOid:
                        messageDigest = value.OctetValue();
                        break;
                    case SigningTimeOid:
                        result.SigningTime = value.TimeValue();
                        break;
                    case ContentTypeOid when value.OidValue() is { Length: > 0 } type && result.ContentType.Length > 0 && type != result.ContentType:
                        result.Problems.Add("PDF signature: the signed content type differs from the content type of the signature.");
                        break;
                }
            }

            if (messageDigest is null)
            {
                result.Problems.Add("PDF signature: the signed attributes carry no message digest.");
            }
            else if (!CryptographicOperations.FixedTimeEquals(messageDigest, contentDigest))
            {
                result.Problems.Add("PDF signature: the document was changed after signing (the digest does not match the signed bytes).");
            }

            // The signature covers the attributes as an explicit SET (0x31), not the [0] tag of the SignerInfo.
            signedBytes = attributes.Raw;
            signedBytes[0] = 0x31;
        }
        else if (content is not null)
        {
            signedBytes = content;
        }
        else
        {
            result.Problems.Add("PDF signature: the signature has neither signed attributes nor content.");
            return;
        }

        if (signerCertificate is not null
            && !VerifySignatureValue(signatureAlgorithm, hash.Value, signedBytes, signature, signerCertificate, result))
        {
            result.Problems.Add("PDF signature: the signature value does not match the signed data.");
        }

        if (unsignedAttributes is { } unsigned && depth == 0)
        {
            foreach (var attribute in unsigned.Children())
            {
                var pair = attribute.Children();
                if (pair.Count == 2 && pair[0].OidValue() == TimestampTokenOid && pair[1].Children().Count > 0)
                {
                    result.TimestampToken = pair[1].Children()[0].Raw;
                }
            }
        }
    }

    /// <summary>
    /// checks a time-stamp token (RFC 3161): its own signature and that its imprint is the hash of
    /// <paramref name="stamped"/>. Returns the time of the token, or null with problems added.
    /// </summary>
    public static DateTimeOffset? VerifyTimestamp(byte[] token, byte[] stamped, List<string> problems, out X509Certificate2? authority, out IReadOnlyList<X509Certificate2> certificates)
    {
        authority = null;
        certificates = [];
        var inner = new CmsResult();
        try
        {
            VerifyCore(token, null, inner, depth: 1);
        }
        catch (DocumentValidationException exception)
        {
            inner.Problems.Add(exception.Message);
        }
        catch (CryptographicException exception)
        {
            inner.Problems.Add("the cryptographic check failed (" + exception.Message + ").");
        }

        authority = inner.Signer;
        certificates = inner.Certificates;
        problems.AddRange(inner.Problems.Select(problem => "Time-stamp: " + problem));
        if (inner.ContentType != TstInfoOid || inner.Content is null)
        {
            problems.Add("Time-stamp: the token holds no TSTInfo.");
            return null;
        }

        try
        {
            // TSTInfo: version, policy, messageImprint, serialNumber, genTime, ...
            var info = DerNode.Parse(inner.Content, 0, inner.Content.Length).Children();
            var imprint = info[2].Children();
            var hash = HashOf(imprint[0].Children()[0].OidValue());
            if (hash is null)
            {
                problems.Add("Time-stamp: the imprint digest is not supported.");
                return null;
            }

            if (!CryptographicOperations.FixedTimeEquals(imprint[1].OctetValue(), HashData(hash.Value, stamped)))
            {
                problems.Add("Time-stamp: the imprint does not match the stamped data.");
                return null;
            }

            return info[4].TimeValue() ?? throw new DocumentValidationException("Time-stamp: the time is not readable.");
        }
        catch (Exception exception) when (exception is DocumentValidationException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            problems.Add("Time-stamp: the TSTInfo is malformed.");
            return null;
        }
    }

    private static bool VerifySignatureValue(DerNode algorithm, HashAlgorithmName digest, byte[] data, byte[] signature, X509Certificate2 certificate, CmsResult result)
    {
        var parts = algorithm.Children();
        var oid = parts.Count > 0 ? parts[0].OidValue() : string.Empty;

        if (oid == RsaPssOid)
        {
            var parameters = parts.Count > 1 ? parts[1].Children() : [];
            var pssHash = digest;
            long salt = 20;
            foreach (var parameter in parameters)
            {
                if (parameter.Tag == 0xA0 && parameter.Children() is { Count: > 0 } hashes && hashes[0].Children() is { Count: > 0 } hashId)
                {
                    pssHash = HashOf(hashId[0].OidValue()) ?? digest;
                }
                else if (parameter.Tag == 0xA2 && parameter.Children() is { Count: > 0 } saltValue)
                {
                    salt = saltValue[0].IntegerValue();
                }
            }

            if (salt != HashData(pssHash, []).Length)
            {
                result.Problems.Add("PDF signature: RSASSA-PSS is supported only with a salt as long as the digest.");
                return true;
            }

            using var pssKey = certificate.GetRSAPublicKey();
            return pssKey is not null && pssKey.VerifyData(data, signature, pssHash, RSASignaturePadding.Pss);
        }

        var algorithmHash = oid switch
        {
            "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            _ => digest,
        };

        if (oid == RsaOid || oid.StartsWith("1.2.840.113549.1.1.", StringComparison.Ordinal))
        {
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa is null)
            {
                result.Problems.Add("PDF signature: the certificate has no RSA public key.");
                return true;
            }

            return rsa.VerifyData(data, signature, algorithmHash, RSASignaturePadding.Pkcs1);
        }

        if (oid == EcPublicKeyOid || oid.StartsWith("1.2.840.10045.4.3.", StringComparison.Ordinal))
        {
            using var ecdsa = certificate.GetECDsaPublicKey();
            if (ecdsa is null)
            {
                result.Problems.Add("PDF signature: the certificate has no ECDSA public key.");
                return true;
            }

            return ecdsa.VerifyData(data, signature, algorithmHash, DSASignatureFormat.Rfc3279DerSequence);
        }

        result.Problems.Add($"PDF signature: the signature algorithm {oid} is not supported (RSA and ECDSA are).");
        return true;
    }

    /// <summary>Finds the signer certificate by issuer and serial number or by key identifier.</summary>
    private static X509Certificate2? FindCertificate(DerNode identifier, List<X509Certificate2> certificates)
    {
        if (identifier.Tag == 0x80)
        {
            var keyIdentifier = identifier.Content.ToArray();
            return certificates.FirstOrDefault(certificate =>
                certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault() is { } extension
                && extension.SubjectKeyIdentifierBytes.Span.SequenceEqual(keyIdentifier));
        }

        var pair = identifier.Children();
        if (pair.Count != 2)
        {
            return null;
        }

        var issuer = pair[0].Raw;
        var serial = Trim(pair[1].Content).ToArray();
        return certificates.FirstOrDefault(certificate =>
            certificate.IssuerName.RawData.AsSpan().SequenceEqual(issuer)
            && Trim(certificate.SerialNumberBytes.Span).SequenceEqual(serial.AsSpan()));
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        while (value.Length > 1 && value[0] == 0)
        {
            value = value[1..];
        }

        return value;
    }

    private static HashAlgorithmName? HashOf(string oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    internal static byte[] HashData(HashAlgorithmName name, byte[] data)
        => name == HashAlgorithmName.SHA384 ? SHA384.HashData(data)
            : name == HashAlgorithmName.SHA512 ? SHA512.HashData(data)
            : SHA256.HashData(data);

    /// <summary>What the check of one CMS SignedData found.</summary>
    internal sealed class CmsResult
    {
        public List<string> Problems { get; } = [];

        public List<X509Certificate2> Certificates { get; } = [];

        public X509Certificate2? Signer { get; set; }

        public string ContentType { get; set; } = string.Empty;

        public byte[]? Content { get; set; }

        public byte[] SignatureValue { get; set; } = [];

        public DateTimeOffset? SigningTime { get; set; }

        public byte[]? TimestampToken { get; set; }
    }
}
