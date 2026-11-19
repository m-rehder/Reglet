using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// reads and verifies the digital signatures of a DOCX package (OF-E02): the signature parts
/// (<c>_xmlsignatures/sig*.xml</c>), their XML signature over the parts of the package and the
/// signed properties of the signature.
/// </summary>
public static class DocxSignatureReader
{
    private static readonly XNamespace Ds = DocxSignatureWriter.Ds;

    /// <summary>
    /// reads the signatures of a package. A package without a signature yields an empty list; a
    /// signature that does not verify is returned with <see cref="DocxSignatureInfo.IsValid"/>
    /// false and the reasons in <see cref="DocxSignatureInfo.Problems"/>.
    /// Input and total decompressed package data are subject to the document reader size limits.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public static IReadOnlyList<DocxSignatureInfo> Read(Stream input)
        => Read(input, DocxSignatureReadOptions.Default);

    /// <summary>
    /// reads the signatures and also evaluates the certificate chain against the trusted roots of
    /// <paramref name="options"/> (OF-E02).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="options"/> is null.</exception>
    public static IReadOnlyList<DocxSignatureInfo> Read(Stream input, DocxSignatureReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        return ReadCore(ReaderGuard.ReadAllBounded(input, "DOCX signature"), options);
    }

    /// <summary>Reads the signatures of a package from its bytes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.</exception>
    public static IReadOnlyList<DocxSignatureInfo> Read(byte[] package)
        => ReadCore(package, DocxSignatureReadOptions.Default);

    /// <summary>Reads the signatures and evaluates the certificate chain.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> or <paramref name="options"/> is null.</exception>
    public static IReadOnlyList<DocxSignatureInfo> Read(byte[] package, DocxSignatureReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ReadCore(package, options);
    }

    private static IReadOnlyList<DocxSignatureInfo> ReadCore(byte[] package, DocxSignatureReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Length > ReaderGuard.MaxInputBytes)
        {
            throw new DocumentValidationException("DOCX signature: the file exceeds the input size limit.");
        }

        using var zip = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long remaining = ReaderGuard.MaxDecodedBytes;
        foreach (var entry in zip.Entries)
        {
            if (entry.Length > remaining)
            {
                throw new DocumentValidationException("DOCX signature: the package exceeds the decoded size limit.");
            }
            remaining -= entry.Length;
        }
        remaining = ReaderGuard.MaxDecodedBytes;
        foreach (var entry in zip.Entries)
        {
            var data = ReaderGuard.ReadBoundedBytes(entry, remaining);
            remaining -= data.Length;
            parts.Add(entry.FullName, data);
        }

        var result = new List<DocxSignatureInfo>();
        foreach (var (name, data) in parts)
        {
            if (!name.StartsWith(DocxSignatureWriter.Folder, StringComparison.Ordinal)
                || !name.EndsWith(".xml", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(Verify(name, data, parts, options));
        }

        return result;
    }

    private static DocxSignatureInfo Verify(
        string partName,
        byte[] data,
        Dictionary<string, byte[]> parts,
        DocxSignatureReadOptions options)
    {
        var problems = new List<string>();
        var signedParts = new List<string>();

        XDocument document;
        try
        {
            document = ReaderGuard.ParseXml(Encoding.UTF8.GetString(data));
        }
        catch (System.Xml.XmlException exception)
        {
            return new DocxSignatureInfo { Problems = [$"{partName}: not well-formed XML ({exception.Message})."] };
        }

        var signature = document.Root;
        if (signature is null || signature.Name != Ds + "Signature")
        {
            return new DocxSignatureInfo { Problems = [$"{partName}: the part holds no ds:Signature."] };
        }

        var signedInfo = signature.Element(Ds + "SignedInfo");
        var signatureValue = signature.Element(Ds + "SignatureValue")?.Value;
        if (signedInfo is null || string.IsNullOrEmpty(signatureValue))
        {
            return new DocxSignatureInfo { Problems = [$"{partName}: SignedInfo or SignatureValue is missing."] };
        }

        var certificate = ReadCertificate(signature, problems);

        // The signature over the canonical form of SignedInfo.
        if (certificate?.GetRSAPublicKey() is { } publicKey)
        {
            using var rsa = publicKey;
            try
            {
                var signedBytes = XmlCanonicalizer.Canonicalize(signedInfo);
                var valid = rsa.VerifyData(
                    signedBytes,
                    Convert.FromBase64String(signatureValue),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                if (!valid)
                {
                    problems.Add($"{partName}: the signature value does not match SignedInfo.");
                }
            }
            catch (FormatException)
            {
                problems.Add($"{partName}: the signature value is not Base64.");
            }
        }
        else if (certificate is not null)
        {
            problems.Add($"{partName}: the certificate does not have an RSA public key.");
        }

        // The digests of the references: a part of the package or the signed properties.
        foreach (var reference in signedInfo.Elements(Ds + "Reference"))
        {
            var uri = (string?)reference.Attribute("URI");
            var digest = reference.Element(Ds + "DigestValue")?.Value;
            if (uri is null || string.IsNullOrEmpty(digest))
            {
                problems.Add($"{partName}: a reference has no URI or no digest.");
                continue;
            }

            if (uri.StartsWith('#'))
            {
                VerifySignedProperties(signature, uri, digest, problems, partName);
                continue;
            }

            var name = uri.StartsWith('/') ? uri[1..] : uri;
            if (!parts.TryGetValue(name, out var content))
            {
                problems.Add($"{partName}: the signed part '{uri}' does not exist.");
                continue;
            }

            signedParts.Add(name);
            if (!DigestMatches(content, digest))
            {
                problems.Add($"{partName}: the digest of '{uri}' does not match the content.");
            }
        }

        // The chain: only with trusted roots; the certificates of KeyInfo build the path.
        bool? trusted = null;
        var chainProblems = new List<string>();
        if (certificate is not null && options.TrustedRoots.Count > 0)
        {
            trusted = CheckChain(certificate, signature, options, chainProblems);
        }

        return new DocxSignatureInfo
        {
            Certificate = certificate,
            SignedParts = signedParts,
            IsValid = problems.Count == 0,
            Problems = problems,
            IsTrusted = trusted,
            ChainProblems = chainProblems,
        };
    }

    /// <summary>builds the certificate chain against the trusted roots of the read options.</summary>
    private static bool CheckChain(
        X509Certificate2 certificate,
        XElement signature,
        DocxSignatureReadOptions options,
        List<string> problems)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = options.RevocationMode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(options.TrustedRoots.ToArray());

        // The certificates of KeyInfo are the intermediate CAs the signature carries.
        foreach (var encoded in signature
            .Element(Ds + "KeyInfo")
            ?.Element(Ds + "X509Data")
            ?.Elements(Ds + "X509Certificate")
            .Select(element => element.Value)
            .Where(value => !string.IsNullOrEmpty(value))
            ?? Enumerable.Empty<string>())
        {
            try
            {
                var extra = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded));
                if (!chain.ChainPolicy.ExtraStore.Contains(extra))
                {
                    chain.ChainPolicy.ExtraStore.Add(extra);
                }
            }
            catch (CryptographicException)
            {
                // Already reported by the certificate reading.
            }
        }

        try
        {
            if (chain.Build(certificate))
            {
                return true;
            }
        }
        catch (CryptographicException exception)
        {
            problems.Add("The certificate chain could not be built: " + exception.Message);
            return false;
        }

        foreach (var status in chain.ChainStatus)
        {
            problems.Add($"{status.Status}: {status.StatusInformation.Trim()}");
        }

        return false;
    }

    /// <summary>the signed properties of the signature object (#idSignedProperties).</summary>
    private static void VerifySignedProperties(
        XElement signature,
        string uri,
        string digest,
        List<string> problems,
        string partName)
    {
        var id = uri[1..];
        var target = signature.Descendants()
            .FirstOrDefault(element => (string?)element.Attribute("Id") == id);
        if (target is null)
        {
            problems.Add($"{partName}: the signed properties '{uri}' are missing.");
            return;
        }

        if (!DigestMatches(XmlCanonicalizer.Canonicalize(target), digest))
        {
            problems.Add($"{partName}: the digest of the signed properties does not match.");
        }
    }

    private static bool DigestMatches(byte[] content, string digest)
        => string.Equals(
            Convert.ToBase64String(SHA256.HashData(content)),
            digest.Trim(),
            StringComparison.Ordinal);

    private static X509Certificate2? ReadCertificate(XElement signature, List<string> problems)
    {
        var encoded = signature
            .Element(Ds + "KeyInfo")
            ?.Element(Ds + "X509Data")
            ?.Element(Ds + "X509Certificate")
            ?.Value;
        if (string.IsNullOrEmpty(encoded))
        {
            problems.Add("The signature carries no certificate.");
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded));
        }
        catch (CryptographicException)
        {
            problems.Add("The certificate of the signature is not valid X.509.");
            return null;
        }
        catch (FormatException)
        {
            problems.Add("The certificate of the signature is not Base64.");
            return null;
        }
    }
}
