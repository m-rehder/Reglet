using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// adds an ODF 1.3 digital signature to a finished ODT package (OF-E05): one XML signature over
/// every file in the package, stored in <c>META-INF/documentsignatures.xml</c>. XML parts are
/// canonicalized (C14N) and SHA-256 digested; binary and encrypted parts are digested as raw
/// octets. The signature file itself is not signed and stays unencrypted.
/// </summary>
internal static class OdtSignatureWriter
{
    public const string SignaturePartName = "META-INF/documentsignatures.xml";

    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Dsig = "urn:oasis:names:tc:opendocument:xmlns:digitalsignature:1.0";
    private static readonly XNamespace Manifest = OdtNamespaces.Manifest;

    private const string Canonicalization = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
    private const string SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    private const string DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256";

    /// <summary>Signs the package and returns the signed package; the input is not modified.</summary>
    /// <exception cref="InvalidOperationException">The certificate has no RSA private key.</exception>
    public static byte[] Sign(byte[] package, OdtSignatureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        using var rsa = options.Certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The signature certificate has no RSA private key.");

        using var archive = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        var entries = ReadEntries(archive);
        var encrypted = EncryptedParts(entries);
        var existingSignature = entries.FirstOrDefault(entry => entry.Name == SignaturePartName);
        var signatures = existingSignature is null ? new XElement(Dsig + "document-signatures", new XAttribute(Dsig + "version", "1.2"))
            : XDocument.Parse(Encoding.UTF8.GetString(existingSignature.Data)).Root
                ?? throw new DocumentValidationException("The ODF signature part has no root element.");

        var references = new List<XElement>();
        foreach (var entry in entries)
        {
            if (entry.Name == SignaturePartName)
            {
                continue;
            }

            var data = entry.Data;
            var canonical = false;
            if (!encrypted.Contains(entry.Name)
                && entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && TryCanonicalize(data, out var canonicalData))
            {
                canonical = true;
                data = canonicalData!;
            }

            references.Add(Reference(entry.Name, SHA256.HashData(data), canonical));
        }

        var signedInfo = new XElement(
            Ds + "SignedInfo",
            new XElement(Ds + "CanonicalizationMethod", new XAttribute("Algorithm", Canonicalization)),
            new XElement(Ds + "SignatureMethod", new XAttribute("Algorithm", SignatureMethod)),
            references);
        var keyInfo = new XElement(
            Ds + "KeyInfo",
            new XElement(
                Ds + "X509Data",
                new XElement(Ds + "X509Certificate", Convert.ToBase64String(options.Certificate.RawData)),
                options.AdditionalCertificates.Select(certificate =>
                    new XElement(Ds + "X509Certificate", Convert.ToBase64String(certificate.RawData)))));

        // The SignedInfo is signed in the namespace context of the parsed signature file. Building,
        // serializing and re-parsing first makes the in-scope namespace declarations explicit, so
        // a verifier that parses the written file and canonicalizes the same element obtains the
        // exact bytes that were signed.
        var id = NextId(signatures);
        signatures.Add(new XElement(
            Ds + "Signature",
            new XAttribute("Id", id),
            signedInfo,
            new XElement(Ds + "SignatureValue"),
            keyInfo));
        var unsigned = new XDocument(new XDeclaration("1.0", "UTF-8", null), signatures);
        var unsignedBytes = Encoding.UTF8.GetBytes(unsigned.ToString(SaveOptions.DisableFormatting));
        var parsed = XDocument.Parse(Encoding.UTF8.GetString(unsignedBytes), LoadOptions.PreserveWhitespace);
        var parsedSignature = parsed.Root!.Elements(Ds + "Signature")
            .Single(element => (string?)element.Attribute("Id") == id);
        var parsedSignedInfo = parsedSignature.Element(Ds + "SignedInfo")
            ?? throw new DocumentValidationException("The ODF signature has no SignedInfo.");
        var signedBytes = XmlCanonicalizer.Canonicalize(parsedSignedInfo);
        var signature = rsa.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        parsedSignature.Element(Ds + "SignatureValue")!.Value = Convert.ToBase64String(signature);

        var signatureBytes = Encoding.UTF8.GetBytes(parsed.ToString(SaveOptions.DisableFormatting));
        return WritePackage(entries, signatureBytes);
    }

    private static byte[] WritePackage(IReadOnlyList<PackageEntry> entries, byte[] signature)
    {
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mime = entries.FirstOrDefault(entry => entry.Name == "mimetype");
            if (mime is not null)
            {
                WriteEntry(target, mime, CompressionLevel.NoCompression);
            }

            foreach (var entry in entries)
            {
                if (entry.Name == "mimetype" || entry.Name == SignaturePartName)
                {
                    continue;
                }

                WriteEntry(target, entry, entry.Compression);
            }

            var signatureEntry = target.CreateEntry(SignaturePartName, CompressionLevel.Optimal);
            signatureEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var stream = signatureEntry.Open();
            stream.Write(signature);
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, PackageEntry entry, CompressionLevel compression)
    {
        var target = archive.CreateEntry(entry.Name, compression);
        target.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = target.Open();
        stream.Write(entry.Data);
    }

    private static List<PackageEntry> ReadEntries(ZipArchive archive)
    {
        var entries = new List<PackageEntry>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var compression = entry.CompressedLength < entry.Length
                ? CompressionLevel.Optimal
                : CompressionLevel.NoCompression;
            entries.Add(new PackageEntry(entry.FullName, buffer.ToArray(), compression));
        }

        return entries;
    }

    private static HashSet<string> EncryptedParts(IReadOnlyList<PackageEntry> entries)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var manifest = entries.FirstOrDefault(entry => entry.Name == "META-INF/manifest.xml");
        if (manifest is null)
        {
            return result;
        }

        try
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(manifest.Data));
            foreach (var entry in document.Descendants(Manifest + "file-entry"))
            {
                if ((string?)entry.Attribute(Manifest + "full-path") is { } name
                    && entry.Element(Manifest + "encryption-data") is not null)
                {
                    result.Add(name);
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // A malformed manifest is not a reason to refuse signing; the parts are signed raw.
        }

        return result;
    }

    private static bool TryCanonicalize(byte[] data, out byte[]? canonical)
    {
        try
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(data)).Root;
            canonical = root is null ? null : XmlCanonicalizer.Canonicalize(root);
            return canonical is not null;
        }
        catch (System.Xml.XmlException)
        {
            canonical = null;
            return false;
        }
    }

    private static XElement Reference(string name, byte[] digest, bool canonical)
    {
        var reference = new XElement(
            Ds + "Reference",
            new XAttribute("URI", name),
            new XElement(Ds + "DigestMethod", new XAttribute("Algorithm", DigestMethod)),
            new XElement(Ds + "DigestValue", Convert.ToBase64String(digest)));
        if (canonical)
        {
            reference.AddFirst(new XElement(
                Ds + "Transforms",
                new XElement(Ds + "Transform", new XAttribute("Algorithm", Canonicalization))));
        }

        return reference;
    }

    private static string NextId(XElement signatures)
    {
        var ids = signatures.Elements(Ds + "Signature")
            .Select(signature => (string?)signature.Attribute("Id"))
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);
        var index = 1;
        while (ids.Contains("ID" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)))
        {
            index++;
        }

        return "ID" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record PackageEntry(string Name, byte[] Data, CompressionLevel Compression);
}
