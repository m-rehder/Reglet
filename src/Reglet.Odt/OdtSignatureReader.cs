using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// reads and verifies the digital signatures of an ODT package (OF-E05): the XML signature in
/// <c>META-INF/documentsignatures.xml</c> over every file of the package. Binary and encrypted
/// parts are verified as raw octets; XML parts with a C14N transform are canonicalized first.
/// Certificate chain and trust are the caller's responsibility.
/// </summary>
public static class OdtSignatureReader
{
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Dsig = "urn:oasis:names:tc:opendocument:xmlns:digitalsignature:1.0";
    private const string Canonicalization = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";

    /// <summary>
    /// reads the signatures of a package. A package without a signature yields an empty list; a
    /// signature that does not verify is returned with <see cref="OdtSignatureInfo.IsValid"/>
    /// false and the reasons in <see cref="OdtSignatureInfo.Problems"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public static IReadOnlyList<OdtSignatureInfo> Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ReadCore(ReaderGuard.ReadAllBounded(input, "ODT signature"));
    }

    /// <summary>Reads the signatures of a package from its bytes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.</exception>
    public static IReadOnlyList<OdtSignatureInfo> Read(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return ReadCore(package);
    }

    // A damaged package fails like in the document readers, not with a ZIP exception.
    private static IReadOnlyList<OdtSignatureInfo> ReadCore(byte[] package)
        => ReaderGuard.Run("ODT", () => ReadPackage(package));

    private static IReadOnlyList<OdtSignatureInfo> ReadPackage(byte[] package)
    {
        if (package.Length > ReaderGuard.MaxInputBytes)
        {
            throw new DocumentValidationException("ODT signature: the file exceeds the input size limit.");
        }

        using var archive = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long remaining = ReaderGuard.MaxDecodedBytes;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length > remaining)
            {
                throw new DocumentValidationException("ODT signature: the package exceeds the decoded size limit.");
            }

            remaining -= entry.Length;
        }

        remaining = ReaderGuard.MaxDecodedBytes;
        foreach (var entry in archive.Entries)
        {
            var data = ReaderGuard.ReadBoundedBytes(entry, remaining);
            remaining -= data.Length;
            parts[entry.FullName] = data;
        }

        if (!parts.TryGetValue(OdtSignatureWriter.SignaturePartName, out var signatureFile))
        {
            return [];
        }

        XDocument document;
        try
        {
            document = ReaderGuard.ParseXml(Encoding.UTF8.GetString(signatureFile));
        }
        catch (System.Xml.XmlException exception)
        {
            return [new OdtSignatureInfo { Problems = ["The signature part is not well-formed XML: " + exception.Message] }];
        }

        var result = new List<OdtSignatureInfo>();
        foreach (var signature in document.Descendants(Ds + "Signature"))
        {
            result.Add(Verify(signature, parts));
        }

        return result;
    }

    private static OdtSignatureInfo Verify(XElement signature, IReadOnlyDictionary<string, byte[]> parts)
    {
        var problems = new List<string>();
        var signedParts = new List<string>();
        var certificate = ReadCertificate(signature, problems);
        var signedInfo = signature.Element(Ds + "SignedInfo");
        var signatureValue = signature.Element(Ds + "SignatureValue")?.Value;
        if (signedInfo is null || string.IsNullOrEmpty(signatureValue))
        {
            problems.Add("The signature has no SignedInfo or SignatureValue.");
            return new OdtSignatureInfo { Certificate = certificate, Problems = problems };
        }

        if (certificate?.GetRSAPublicKey() is not { } publicKey)
        {
            problems.Add("The signature has no RSA certificate.");
        }
        else
        {
            try
            {
                var canonical = XmlCanonicalizer.Canonicalize(signedInfo);
                if (!publicKey.VerifyData(
                        canonical,
                        Convert.FromBase64String(signatureValue),
                        HashAlgorithmName.SHA256,
                        RSASignaturePadding.Pkcs1))
                {
                    problems.Add("The SignedInfo signature is not valid.");
                }
            }
            catch (FormatException)
            {
                problems.Add("The SignatureValue is not valid base64.");
            }
            catch (CryptographicException exception)
            {
                problems.Add("The signature could not be verified: " + exception.Message);
            }
        }

        foreach (var reference in signedInfo.Elements(Ds + "Reference"))
        {
            var uri = (string?)reference.Attribute("URI");
            if (string.IsNullOrEmpty(uri) || uri.StartsWith('#'))
            {
                problems.Add($"The reference '{uri}' is not supported.");
                continue;
            }

            signedParts.Add(uri);
            if (!parts.TryGetValue(uri, out var data))
            {
                problems.Add($"The signed part '{uri}' is missing.");
                continue;
            }

            var canonical = reference.Elements(Ds + "Transforms")
                .SelectMany(transforms => transforms.Elements(Ds + "Transform"))
                .Any(transform => (string?)transform.Attribute("Algorithm") == Canonicalization);
            if (canonical)
            {
                try
                {
                    var root = ReaderGuard.ParseXml(Encoding.UTF8.GetString(data)).Root;
                    data = root is null ? data : XmlCanonicalizer.Canonicalize(root);
                }
                catch (System.Xml.XmlException)
                {
                    problems.Add($"The signed part '{uri}' is not well-formed XML.");
                    continue;
                }
            }

            var expected = reference.Element(Ds + "DigestValue")?.Value;
            if (expected is null
                || !string.Equals(
                    expected,
                    Convert.ToBase64String(SHA256.HashData(data)),
                    StringComparison.Ordinal))
            {
                problems.Add($"The digest of '{uri}' is not valid.");
            }
        }

        return new OdtSignatureInfo
        {
            Certificate = certificate,
            SignedParts = signedParts,
            IsValid = problems.Count == 0,
            Problems = problems,
        };
    }

    private static X509Certificate2? ReadCertificate(XElement signature, List<string> problems)
    {
        var value = signature.Descendants(Ds + "X509Certificate").FirstOrDefault()?.Value;
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(value));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            problems.Add("The X509 certificate is not valid: " + exception.Message);
            return null;
        }
    }
}
