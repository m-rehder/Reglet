using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// OF-E02: the digital signature of a DOCX. The finished package gets an OPC signature part with an
/// XML signature over its parts; the reader verifies the signature value and every digest.
/// </summary>
internal static class DocxSignatureChecks
{
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";

    internal static int Run()
    {
        try
        {
            SignsAndVerifies();
            WritesTheSignatureParts();
            DetectsAModifiedPart();
            DetectsAModifiedSignature();
            RejectsNonRsaCertificates();
            RejectsOversizedPackageEntries();
            SignsSelectedParts();
            ReadsNoSignatureOfAPlainDocument();
            CombinesWithEncryption();
            RejectsInvalidOptions();
            CanonicalizesStably();
            KeepsAnEarlierSignatureValid();
            EvaluatesTheChain();
            ValidatesWithLxmlWhenConfigured();
            Console.WriteLine("DOCX signature (OF-E02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("DOCX signature (OF-E02): " + exception);
            return 1;
        }
    }

    private static X509Certificate2 Certificate(string subject = "CN=Reglet Signer")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static PortableDocument Document()
        => PortableDocument.Create()
            .Paragraph("Signierter Inhalt")
            .Paragraph("Zweiter Absatz")
            .Build();

    private static byte[] Write(PortableDocument document, DocxWriteOptions options)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] Plain() => Write(Document(), new DocxWriteOptions());

    private static void SignsAndVerifies()
    {
        using var certificate = Certificate();
        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(certificate) });

        var signatures = DocxSignatureReader.Read(signed);
        Require(signatures.Count == 1, $"One signature is read: {signatures.Count}");

        var signature = signatures[0];
        Require(signature.IsValid, "The signature is valid: " + string.Join(" | ", signature.Problems));
        Require(signature.Subject == certificate.Subject, "The certificate of the signature is read: " + signature.Subject);
        Require(
            signature.SignedParts.Contains("word/document.xml", StringComparer.Ordinal),
            "The document part is covered: " + string.Join(", ", signature.SignedParts));
        Require(signature.SignedParts.Count > 1, "More than one part is covered.");

        // The signed package is still a DOCX.
        using var buffer = new MemoryStream(signed);
        var read = new DocxDocumentReader().Read(buffer).Document;
        var text = ((Paragraph)read.Sections[0].Blocks[0]).Inlines.OfType<TextRun>().First().Text;
        Require(text == "Signierter Inhalt", "The signed package is readable: " + text);
    }

    private static void WritesTheSignatureParts()
    {
        using var certificate = Certificate();
        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(certificate) });

        using var zip = new ZipArchive(new MemoryStream(signed), ZipArchiveMode.Read);
        Require(zip.GetEntry("_xmlsignatures/origin.sigs") is not null, "The signature origin part exists.");
        Require(zip.GetEntry("_xmlsignatures/sig1.xml") is not null, "The signature part exists.");

        var originRelationships = ReadEntry(zip, "_xmlsignatures/_rels/origin.sigs.rels");
        Require(
            originRelationships.Contains("digital-signature/signature", StringComparison.Ordinal),
            "The origin part relates to the signature: " + originRelationships);
        Require(originRelationships.Contains("Target=\"sig1.xml\"", StringComparison.Ordinal), "The target is sig1.xml.");

        var contentTypes = ReadEntry(zip, "[Content_Types].xml");
        Require(
            contentTypes.Contains("application/vnd.openxmlformats-package.digital-signature-origin", StringComparison.Ordinal),
            "The content type of the origin part is registered.");
        Require(
            contentTypes.Contains("digital-signature-xmlsignature+xml", StringComparison.Ordinal),
            "The content type of the signature part is registered.");

        var rootRelationships = ReadEntry(zip, "_rels/.rels");
        Require(
            rootRelationships.Contains("digital-signature/origin", StringComparison.Ordinal),
            "The package relates to the origin part.");

        var signature = ReadEntry(zip, "_xmlsignatures/sig1.xml");
        Require(signature.Contains("Id=\"idPackageSignature\"", StringComparison.Ordinal), "The signature has the Office id.");
        Require(signature.Contains("idSignedProperties", StringComparison.Ordinal), "The signed properties are written.");
        Require(signature.Contains("SignatureTime", StringComparison.Ordinal), "The signature time is written.");
        Require(signature.Contains("rsa-sha256", StringComparison.Ordinal), "The signature method is RSA/SHA-256.");
    }

    /// <summary>A changed part makes the signature invalid.</summary>
    private static void DetectsAModifiedPart()
    {
        using var certificate = Certificate();
        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(certificate) });
        var tampered = ReplaceEntry(signed, "word/document.xml", data =>
        {
            var text = Encoding.UTF8.GetString(data);
            return Encoding.UTF8.GetBytes(text.Replace("Signierter Inhalt", "Veränderter Inhalt", StringComparison.Ordinal));
        });

        var signature = DocxSignatureReader.Read(tampered).Single();
        Require(!signature.IsValid, "The changed part makes the signature invalid.");
        Require(
            signature.Problems.Any(problem => problem.Contains("word/document.xml", StringComparison.Ordinal)),
            "The problem names the part: " + string.Join(" | ", signature.Problems));
    }

    /// <summary>A changed signature value makes the signature invalid.</summary>
    private static void DetectsAModifiedSignature()
    {
        using var certificate = Certificate();
        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(certificate) });
        var tampered = ReplaceEntry(signed, "_xmlsignatures/sig1.xml", data =>
        {
            var text = Encoding.UTF8.GetString(data);
            var document = XDocument.Parse(text);
            var value = document.Root!.Element(Ds + "SignatureValue")!;
            value.Value = Convert.ToBase64String(new byte[256]);
            return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        });

        var signature = DocxSignatureReader.Read(tampered).Single();
        Require(!signature.IsValid, "A changed signature value is invalid.");
        Require(
            signature.Problems.Any(problem => problem.Contains("does not match SignedInfo", StringComparison.Ordinal)),
            "The problem names the signature value: " + string.Join(" | ", signature.Problems));
    }

    private static void RejectsNonRsaCertificates()
    {
        using var signer = Certificate();
        using var key = ECDsa.Create();
        var request = new CertificateRequest("CN=Non-RSA", key, HashAlgorithmName.SHA256);
        using var other = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(signer) });
        var forged = ReplaceEntry(signed, "_xmlsignatures/sig1.xml", data =>
        {
            var xml = XDocument.Parse(Encoding.UTF8.GetString(data));
            xml.Descendants(Ds + "X509Certificate").First().Value = Convert.ToBase64String(other.RawData);
            return Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        });
        var result = DocxSignatureReader.Read(forged).Single();
        Require(!result.IsValid && result.Problems.Any(p => p.Contains("RSA", StringComparison.Ordinal)),
            "A non-RSA certificate must not bypass signature verification.");
    }

    private static void RejectsOversizedPackageEntries()
    {
        using var oversized = new OversizedStream();
        Throws<DocumentValidationException>(() => DocxSignatureReader.Read(oversized));
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "first", "second" })
            {
                using var entry = archive.CreateEntry(name).Open();
                entry.WriteByte(0);
            }
        }
        var package = buffer.ToArray();
        var entries = 0;
        for (var i = 0; i < package.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(i)) != 0x02014b50) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(package.AsSpan(i + 24), 150 * 1024 * 1024);
            entries++;
        }
        Require(entries == 2, "Both central directory entries were changed.");
        Throws<DocumentValidationException>(() => DocxSignatureReader.Read(package));
    }

    private sealed class OversizedStream : MemoryStream
    {
        public override long Length => 513L * 1024 * 1024;
    }

    /// <summary>Only the named parts are signed; the others stay unprotected.</summary>
    private static void SignsSelectedParts()
    {
        using var certificate = Certificate();
        var signed = Write(Document(), new DocxWriteOptions
        {
            Signature = new DocxSignatureOptions(certificate) { Parts = ["/docProps/core.xml"] },
        });

        var signature = DocxSignatureReader.Read(signed).Single();
        Require(signature.IsValid, "The signature of one part is valid: " + string.Join(" | ", signature.Problems));
        Require(
            signature.SignedParts.Count == 1 && signature.SignedParts[0] == "docProps/core.xml",
            "Only the requested part is covered: " + string.Join(", ", signature.SignedParts));

        // A change outside the signed part does not affect this signature.
        var tampered = ReplaceEntry(signed, "word/document.xml", data =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("Zweiter", "Dritter", StringComparison.Ordinal)));
        Require(DocxSignatureReader.Read(tampered).Single().IsValid, "A part outside the signature stays unaffected.");
    }

    private static void ReadsNoSignatureOfAPlainDocument()
    {
        Require(DocxSignatureReader.Read(Plain()).Count == 0, "A plain document has no signature.");
    }

    /// <summary>The signature is added before the package is encrypted.</summary>
    private static void CombinesWithEncryption()
    {
        using var certificate = Certificate();
        var encrypted = Write(Document(), new DocxWriteOptions
        {
            Signature = new DocxSignatureOptions(certificate),
            Encryption = new DocxEncryptionOptions("Geheim123") { SpinCount = 100 },
        });

        using var buffer = new MemoryStream(encrypted);
        var decrypted = DocxAgileEncryption.Decrypt(encrypted, "Geheim123");
        Require(buffer.Length > 0 && decrypted.Length > 0, "The encrypted package decrypts.");

        var signature = DocxSignatureReader.Read(decrypted).Single();
        Require(signature.IsValid, "The signature inside the encrypted package is valid: " + string.Join(" | ", signature.Problems));
    }

    private static void RejectsInvalidOptions()
    {
        // A certificate without a private key cannot sign.
        using var certificate = Certificate();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Public Only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var publicOnly = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var publicOnlyCertificate = X509CertificateLoader.LoadCertificate(publicOnly.RawData);

        Throws<InvalidOperationException>(() =>
            Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(publicOnlyCertificate) }));

        // An unknown part name is rejected.
        Throws<DocumentValidationException>(() =>
            Write(Document(), new DocxWriteOptions
            {
                Signature = new DocxSignatureOptions(certificate) { Parts = ["/word/gibtsnicht.xml"] },
            }));
    }

    /// <summary>The canonical form is a fixpoint and renders each namespace once.</summary>
    private static void CanonicalizesStably()
    {
        var element = new XElement(
            Ds + "SignedInfo",
            new XElement(Ds + "SignatureMethod", new XAttribute("Algorithm", "b")),
            new XElement(Ds + "Reference", new XAttribute("URI", "a"), new XElement(Ds + "DigestValue", "x")));

        var canonical = Encoding.UTF8.GetString(XmlCanonicalizer.Canonicalize(element));
        Require(
            canonical.StartsWith("<SignedInfo xmlns=\"http://www.w3.org/2000/09/xmldsig#\">", StringComparison.Ordinal),
            "The apex carries the namespace: " + canonical);
        Require(
            !canonical.Contains("SignatureMethod xmlns=", StringComparison.Ordinal),
            "The inherited namespace is not repeated: " + canonical);
        Require(!canonical.Contains("/>", StringComparison.Ordinal), "Empty elements are expanded.");

        // Canonicalizing the parsed result yields the same bytes.
        var again = Encoding.UTF8.GetString(XmlCanonicalizer.Canonicalize(XElement.Parse(canonical)));
        Require(again == canonical, "The canonical form is a fixpoint: " + again);
    }

    /// <summary>A second signature is added next to the first one, which stays valid.</summary>
    private static void KeepsAnEarlierSignatureValid()
    {
        using var first = Certificate("CN=Erste Signatur");
        using var second = Certificate("CN=Zweite Signatur");

        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(first) });
        Require(DocxSignatureReader.Read(signed).Single().IsValid, "The first signature is valid.");

        // Signing the already signed package again (as an application would to countersign it).
        using var input = new MemoryStream(signed);
        var twice = DocxSignatureWriter.Sign(input.ToArray(), new DocxSignatureOptions(second), CancellationToken.None);

        var signatures = DocxSignatureReader.Read(twice);
        Require(signatures.Count == 2, $"Two signatures are read: {signatures.Count}");
        Require(signatures.All(signature => signature.IsValid), "Both signatures are valid: "
            + string.Join(" | ", signatures.SelectMany(signature => signature.Problems)));

        using var zip = new ZipArchive(new MemoryStream(twice), ZipArchiveMode.Read);
        Require(zip.GetEntry("_xmlsignatures/sig1.xml") is not null, "The first signature part is kept.");
        Require(zip.GetEntry("_xmlsignatures/sig2.xml") is not null, "The second signature part is added.");
        var relationships = ReadEntry(zip, "_xmlsignatures/_rels/origin.sigs.rels");
        Require(CountOccurrences(relationships, "digital-signature/signature") == 2, "Both parts are related.");

        // The package plumbing is not signed, so a later signature does not invalidate the first.
        Require(
            !signatures[0].SignedParts.Contains("[Content_Types].xml", StringComparer.Ordinal),
            "The content types are not part of the signature.");

    }

    /// <summary>The certificate chain is evaluated against the trusted roots of the read options.</summary>
    private static void EvaluatesTheChain()
    {
        using var certificate = Certificate("CN=Chain Signer");
        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(certificate) });

        var untrusted = DocxSignatureReader.Read(signed).Single();
        Require(untrusted.IsTrusted is null, "Without trusted roots the chain is not judged.");

        var trusted = DocxSignatureReader.Read(signed, new DocxSignatureReadOptions
        {
            TrustedRoots = [X509CertificateLoader.LoadCertificate(certificate.RawData)],
        }).Single();
        Require(trusted.IsTrusted == true, "The certificate chains to the trusted root: "
            + string.Join(" | ", trusted.ChainProblems));
        Require(trusted.IsValid && trusted.IsTrusted == true, "The signature is valid and trusted.");

        using var stranger = Certificate("CN=Fremde Wurzel");
        var rejected = DocxSignatureReader.Read(signed, new DocxSignatureReadOptions
        {
            TrustedRoots = [X509CertificateLoader.LoadCertificate(stranger.RawData)],
        }).Single();
        Require(rejected.IsTrusted == false, "An unrelated root does not trust the certificate.");
        Require(rejected.ChainProblems.Count > 0, "The chain reports why it failed.");
        Require(rejected.IsValid, "The cryptographic signature stays valid.");
    }

    /// <summary>
    /// OF-E04 (optional): an independent XMLDSig stack checks the canonical form, the signature
    /// value and the digests. Set <c>REGLET_XMLDSIG</c> to a Python interpreter with
    /// <c>lxml</c> and <c>cryptography</c>.
    /// </summary>
    private static void ValidatesWithLxmlWhenConfigured()
    {
        var python = Environment.GetEnvironmentVariable("REGLET_XMLDSIG");
        if (string.IsNullOrEmpty(python) || !File.Exists(python))
        {
            Console.WriteLine("DOCX signature validator skipped (REGLET_XMLDSIG not set).");
            return;
        }

        using var certificate = Certificate();
        var signed = Write(Document(), new DocxWriteOptions { Signature = new DocxSignatureOptions(certificate) });
        var path = Path.Combine(Path.GetTempPath(), "reglet-sig-" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            File.WriteAllBytes(path, signed);

            var script = string.Join(
                "\n",
                "import base64, hashlib, sys, zipfile",
                "from lxml import etree",
                "from cryptography import x509",
                "from cryptography.hazmat.primitives import hashes",
                "from cryptography.hazmat.primitives.asymmetric import padding",
                "DS = 'http://www.w3.org/2000/09/xmldsig#'",
                "z = zipfile.ZipFile(sys.argv[1])",
                "raw = z.read('_xmlsignatures/sig1.xml').decode()",
                "def slice_out(tag):",
                "    start = raw.index('<' + tag); end = raw.index('</' + tag + '>') + len('</' + tag + '>')",
                "    return raw[start:end]",
                "stored = slice_out('SignedInfo')",
                "c14n = etree.tostring(etree.fromstring(stored.encode()), method='c14n')",
                "assert c14n.decode() == stored, 'SignedInfo is not canonical'",
                "sig = etree.fromstring(raw.encode())",
                "cert = x509.load_der_x509_certificate(base64.b64decode(sig.find(f'{{{DS}}}KeyInfo/{{{DS}}}X509Data/{{{DS}}}X509Certificate').text))",
                "cert.public_key().verify(base64.b64decode(sig.find(f'{{{DS}}}SignatureValue').text), c14n, padding.PKCS1v15(), hashes.SHA256())",
                "obj = slice_out('Object')",
                "c14n_obj = etree.tostring(etree.fromstring(obj.encode()), method='c14n')",
                "digest = [r for r in sig.findall(f'{{{DS}}}SignedInfo/{{{DS}}}Reference') if r.get('URI') == '#idSignedProperties'][0]",
                "assert hashlib.sha256(c14n_obj).digest() == base64.b64decode(digest.find(f'{{{DS}}}DigestValue').text), 'properties digest'",
                "for ref in sig.findall(f'{{{DS}}}SignedInfo/{{{DS}}}Reference'):",
                "    uri = ref.get('URI')",
                "    if uri.startswith('#') or uri.startswith('_xmlsignatures'):",
                "        continue",
                "    data = z.read(uri.lstrip('/'))",
                "    assert hashlib.sha256(data).digest() == base64.b64decode(ref.find(f'{{{DS}}}DigestValue').text), uri");

            var startInfo = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The validator could not be started.");
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Require(process.ExitCode == 0, "The independent XMLDSig check failed: " + errors);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>rewrites one entry of the package and returns the new package.</summary>
    private static byte[] ReplaceEntry(byte[] package, string name, Func<byte[], byte[]> replace)
    {
        using var input = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                using var source = entry.Open();
                using var content = new MemoryStream();
                source.CopyTo(content);
                var data = string.Equals(entry.FullName, name, StringComparison.Ordinal)
                    ? replace(content.ToArray())
                    : content.ToArray();
                var written = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var stream = written.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return output.ToArray();
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Erwartete {typeof(TException).Name} blieb aus.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
