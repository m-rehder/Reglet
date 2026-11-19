using System.Collections.Generic;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// adds an OPC digital signature to a finished DOCX package (OF-E02, ECMA-376 Part 2 §10.2): the
/// signature origin part, one signature part with an XML signature (XMLDSig, RSA/SHA-256) over
/// the parts of the package and the relationships and content types that connect them.
/// </summary>
internal static class DocxSignatureWriter
{
    /// <summary>The folder of the signature parts (ECMA-376 Part 2, 10.1.1).</summary>
    public const string Folder = "_xmlsignatures/";

    /// <summary>The part name of the signature origin.</summary>
    public const string OriginPart = Folder + "origin.sigs";

    /// <summary>The part name of the relationships of the signature origin.</summary>
    public const string OriginRelationshipsPart = Folder + "_rels/origin.sigs.rels";

    public const string OriginContentType = "application/vnd.openxmlformats-package.digital-signature-origin";
    public const string SignatureContentType = "application/vnd.openxmlformats-package.digital-signature-xmlsignature+xml";
    public const string OriginRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin";
    public const string SignatureRelationship = "http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/signature";

    /// <summary>The XMLDSig namespace.</summary>
    public static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    private const string CanonicalizationAlgorithm = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
    private const string SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    private const string DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256";

    /// <summary>
    /// signs the package and returns the signed package. The input is not modified.
    /// </summary>
    /// <exception cref="InvalidOperationException">The certificate has no RSA private key.</exception>
    public static byte[] Sign(byte[] package, DocxSignatureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        using var rsa = options.Certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("The signature certificate has no RSA private key.");

        // A second signature goes next to the existing ones, so an earlier signature keeps its
        // value and stays valid (its parts are not changed to add the new one).
        var entries = ReadEntries(package);
        var index = NextIndex(entries);
        var signaturePartName = SignaturePart(index);

        // The content types and the package relationships gain the signature entries first, so
        // their digests cover what the signed package holds.
        entries = entries
            .Select(entry => entry.Name switch
            {
                "[Content_Types].xml" => (entry.Name, ContentTypes(entry.Data, signaturePartName)),
                "_rels/.rels" => (entry.Name, RootRelationships(entry.Data)),
                OriginRelationshipsPart => (entry.Name, OriginRelationships(entry.Data, index)),
                _ => entry,
            })
            .ToList();
        var partsToSign = SelectParts(entries, options);

        // The references: one per signed part (raw part bytes, no transform) and the signed
        // properties of the signature object (canonicalized).
        var references = new List<XElement>();
        foreach (var part in partsToSign)
        {
            // A reference names the part with a leading slash (ECMA-376 Part 2, 10.2.1.1).
            references.Add(Reference("/" + part.Name, type: null, SHA256.HashData(part.Data)));
        }

        var properties = SignedProperties(DateTimeOffset.Now);
        var propertiesBytes = XmlCanonicalizer.Canonicalize(properties);
        references.Add(Reference("#idSignedProperties", SignatureRelationship, SHA256.HashData(propertiesBytes)));

        var signedInfo = new XElement(
            Ds + "SignedInfo",
            new XElement(Ds + "CanonicalizationMethod", new XAttribute("Algorithm", CanonicalizationAlgorithm)),
            new XElement(Ds + "SignatureMethod", new XAttribute("Algorithm", SignatureMethod)),
            references);

        // The signed bytes are the canonical form of SignedInfo; the file stores exactly that, so a
        // verifier that canonicalizes again arrives at the same bytes.
        var signedBytes = XmlCanonicalizer.Canonicalize(signedInfo);
        var signature = rsa.SignData(signedBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var keyInfo = new XElement(
            Ds + "KeyInfo",
            new XElement(
                Ds + "X509Data",
                new XElement(Ds + "X509Certificate", Convert.ToBase64String(options.Certificate.RawData)),
                options.AdditionalCertificates.Select(certificate =>
                    new XElement(Ds + "X509Certificate", Convert.ToBase64String(certificate.RawData)))));

        // The signed subtrees are written as the exact bytes that were signed and digested
        // (WriteRaw), so a verifier that canonicalizes them again arrives at the same bytes.
        var signatureXml = SignaturePartXml(Convert.ToBase64String(signature), keyInfo, signedBytes, propertiesBytes);

        return WritePackage(entries, signaturePartName, signatureXml, index, cancellationToken);
    }

    /// <summary>the index of the signature part this run writes.</summary>
    private static int NextIndex(List<(string Name, byte[] Data)> entries)
    {
        var index = 1;
        while (entries.Any(entry => string.Equals(entry.Name, SignaturePart(index), StringComparison.Ordinal)))
        {
            index++;
        }

        return index;
    }

    /// <summary>writes the signature part with the canonical subtrees as raw bytes.</summary>
    private static byte[] SignaturePartXml(string signatureValue, XElement keyInfo, byte[] signedInfo, byte[] properties)
    {
        using var buffer = new MemoryStream();
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
        };

        using (var writer = System.Xml.XmlWriter.Create(buffer, settings))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteStartElement("Signature", Ds.NamespaceName);
            writer.WriteAttributeString("Id", "idPackageSignature");
            writer.WriteRaw(Encoding.UTF8.GetString(signedInfo));
            writer.WriteElementString("SignatureValue", Ds.NamespaceName, signatureValue);
            keyInfo.WriteTo(writer);
            writer.WriteRaw(Encoding.UTF8.GetString(properties));
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return buffer.ToArray();
    }

    /// <summary>the parts the signature covers: every part except the signature parts.</summary>
    private static List<(string Name, byte[] Data)> SelectParts(
        List<(string Name, byte[] Data)> entries,
        DocxSignatureOptions options)
    {
        if (options.Parts is { Count: > 0 } requested)
        {
            var result = new List<(string Name, byte[] Data)>(requested.Count);
            foreach (var name in requested)
            {
                var normalized = name.StartsWith('/') ? name : "/" + name;
                var entry = entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, normalized[1..], StringComparison.Ordinal));
                result.Add(entry.Name is null
                    ? throw new DocumentValidationException($"The part '{name}' to sign does not exist in the package.")
                    : entry);
            }

            return result;
        }

        // The package plumbing stays out of the signature: a further signature has to change the
        // content types and the package relationships, and it must not invalidate this one.
        return entries
            .Where(entry => !entry.Name.StartsWith(Folder, StringComparison.Ordinal)
                && !string.Equals(entry.Name, "[Content_Types].xml", StringComparison.Ordinal)
                && !string.Equals(entry.Name, "_rels/.rels", StringComparison.Ordinal))
            .ToList();
    }

    private static XElement Reference(string uri, string? type, byte[] digest)
    {
        var reference = new XElement(Ds + "Reference", new XAttribute("URI", uri));
        if (type is not null)
        {
            reference.Add(new XAttribute("Type", type));
        }

        reference.Add(new XElement(Ds + "DigestMethod", new XAttribute("Algorithm", DigestMethod)));
        reference.Add(new XElement(Ds + "DigestValue", Convert.ToBase64String(digest)));
        return reference;
    }

    /// <summary>
    /// the Office signature properties (the time of the signature) as the <c>Object</c> of the
    /// signature; a verifier digests its canonical form.
    /// </summary>
    private static XElement SignedProperties(DateTimeOffset time)
    {
        XNamespace properties = "http://schemas.openxmlformats.org/package/2006/digital-signature/signature-properties";
        XNamespace mdssi = "http://schemas.openxmlformats.org/package/2006/digital-signature";

        var value = time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        return new XElement(
            Ds + "Object",
            new XAttribute("Id", "idSignedProperties"),
            new XElement(
                properties + "SignatureProperties",
                new XAttribute(XNamespace.Xmlns + "mdssi", mdssi),
                new XElement(
                    properties + "SignatureProperty",
                    new XAttribute("Id", "idSignatureTime"),
                    new XAttribute("Target", "#idPackageSignature"),
                    new XElement(
                        mdssi + "SignatureTime",
                        new XElement(mdssi + "Format", "YYYY-MM-DDThh:mm:ssTZD"),
                        new XElement(mdssi + "Value", value)))));
    }

    /// <summary>reads all entries of the package (the input stays untouched).</summary>
    private static List<(string Name, byte[] Data)> ReadEntries(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        var entries = new List<(string Name, byte[] Data)>(zip.Entries.Count);
        foreach (var entry in zip.Entries)
        {
            using var content = new MemoryStream();
            using (var stream = entry.Open())
            {
                stream.CopyTo(content);
            }

            entries.Add((entry.FullName, content.ToArray()));
        }

        return entries;
    }

    /// <summary>
    /// writes the package with the signature parts, the content types and the relationships.
    /// </summary>
    private static byte[] WritePackage(
        List<(string Name, byte[] Data)> entries,
        string signaturePartName,
        byte[] signatureXml,
        int index,
        CancellationToken cancellationToken)
    {
        var originBytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n");

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.Equals(name, OriginPart, StringComparison.Ordinal))
                {
                    continue; // written once below
                }

                WriteEntry(zip, name, data);
            }

            WriteEntry(zip, OriginPart, originBytes);
            WriteEntry(zip, signaturePartName, signatureXml);
            if (!entries.Any(entry => string.Equals(entry.Name, OriginRelationshipsPart, StringComparison.Ordinal)))
            {
                WriteEntry(zip, OriginRelationshipsPart, OriginRelationships(null, index));
            }
        }

        return output.ToArray();
    }

    /// <summary>the part name of the n-th signature.</summary>
    public static string SignaturePart(int index)
        => Folder + "sig" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".xml";

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(data, 0, data.Length);
    }

    /// <summary>adds the content type overrides of the signature parts that are missing.</summary>
    private static byte[] ContentTypes(byte[] data, string signaturePartName)
    {
        XNamespace contentTypes = OpcNamespaces.ContentTypes;
        var document = XDocument.Parse(Encoding.UTF8.GetString(data));
        var root = document.Root ?? throw new DocumentValidationException("The package has no content types part.");

        var existing = root.Elements(contentTypes + "Override")
            .Select(element => (string?)element.Attribute("PartName"))
            .Where(partName => partName is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (partName, contentType) in new[] { (OriginPart, OriginContentType), (signaturePartName, SignatureContentType) })
        {
            if (!existing.Contains("/" + partName))
            {
                root.Add(new XElement(contentTypes + "Override",
                    new XAttribute("PartName", "/" + partName),
                    new XAttribute("ContentType", contentType)));
            }
        }

        return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + document.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>adds the relationship of the package to the signature origin part, once.</summary>
    private static byte[] RootRelationships(byte[] data)
    {
        XNamespace relationships = OpcNamespaces.Relationships;
        var document = XDocument.Parse(Encoding.UTF8.GetString(data));
        var root = document.Root ?? throw new DocumentValidationException("The package has no relationships part.");
        if (!root.Elements(relationships + "Relationship")
            .Any(element => (string?)element.Attribute("Type") == OriginRelationship))
        {
            root.Add(new XElement(
                relationships + "Relationship",
                new XAttribute("Id", "rIdSignatureOrigin"),
                new XAttribute("Type", OriginRelationship),
                new XAttribute("Target", OriginPart)));
        }

        return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + document.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>the relationships of the origin part to the signature parts.</summary>
    private static byte[] OriginRelationships(byte[]? data, int index)
    {
        XNamespace relationships = OpcNamespaces.Relationships;
        XDocument document;
        if (data is null)
        {
            document = new XDocument(new XElement(relationships + "Relationships"));
        }
        else
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(data));
        }

        var root = document.Root ?? throw new DocumentValidationException("The signature origin has no relationships part.");
        var target = SignaturePart(index)[Folder.Length..];
        var ids = root.Elements(relationships + "Relationship")
            .Select(element => (string?)element.Attribute("Id"))
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);

        var number = 1;
        while (ids.Contains("rId" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            number++;
        }

        root.Add(new XElement(
            relationships + "Relationship",
            new XAttribute("Id", "rId" + number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new XAttribute("Type", SignatureRelationship),
            new XAttribute("Target", target)));

        return Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + document.ToString(SaveOptions.DisableFormatting));
    }
}
