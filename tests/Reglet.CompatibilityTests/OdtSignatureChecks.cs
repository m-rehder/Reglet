using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Reglet;

/// <summary>OF-E05: ODT XML signatures in META-INF/documentsignatures.xml.</summary>
internal static class OdtSignatureChecks
{
    private const string Password = "secret";

    internal static int Run()
    {
        try
        {
            RoundTripsSignature();
            DetectsTampering();
            SignsEncryptedPackage();
            Console.WriteLine("ODT signatures (OF-E05): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ODT signatures (OF-E05): " + exception);
            return 1;
        }
    }

    private static void RoundTripsSignature()
    {
        using var certificate = Certificate();
        var signed = Write(Document(), new OdtWriteOptions { Signature = new OdtSignatureOptions(certificate) });

        var signatures = OdtSignatureReader.Read(signed);
        Require(signatures.Count == 1, $"One signature is read: {signatures.Count}");
        var signature = signatures[0];
        Require(signature.IsValid, "The signature is valid: " + string.Join(" | ", signature.Problems));
        Require(signature.Subject == certificate.Subject, "The certificate is read: " + signature.Subject);
        Require(signature.SignedParts.Contains("content.xml", StringComparer.Ordinal),
            "The content part is covered: " + string.Join(", ", signature.SignedParts));
        Require(signature.SignedParts.Contains("mimetype", StringComparer.Ordinal),
            "The mimetype entry is covered.");
        Require(signature.SignedParts.Contains("META-INF/manifest.xml", StringComparer.Ordinal),
            "The manifest is covered.");

        var read = new OdtDocumentReader().Read(new MemoryStream(signed));
        Require(Text(read.Document).Contains("Signed content", StringComparison.Ordinal),
            "The signed ODT is still readable.");
    }

    private static void DetectsTampering()
    {
        using var certificate = Certificate();
        var signed = Write(Document(), new OdtWriteOptions { Signature = new OdtSignatureOptions(certificate) });
        var content = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(Read(signed, "content.xml")));
        content.Root!.SetAttributeValue("tampered", "true");
        var tampered = Rewrite(signed, "content.xml",
            Encoding.UTF8.GetBytes(content.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)));

        var signature = OdtSignatureReader.Read(tampered).Single();
        Require(!signature.IsValid, "A changed part invalidates the signature.");
        Require(signature.Problems.Any(problem => problem.Contains("content.xml", StringComparison.Ordinal)),
            "The problem names the changed part: " + string.Join(" | ", signature.Problems));
    }

    private static void SignsEncryptedPackage()
    {
        using var certificate = Certificate();
        var signed = Write(
            Document(),
            new OdtWriteOptions
            {
                Encryption = new OdtEncryptionOptions(Password),
                Signature = new OdtSignatureOptions(certificate),
            });

        using (var zip = new ZipArchive(new MemoryStream(signed), ZipArchiveMode.Read))
        {
            Require(zip.GetEntry("META-INF/documentsignatures.xml") is not null,
                "The signature file is present in the encrypted package.");
            var manifest = Encoding.UTF8.GetString(Read(zip, "META-INF/manifest.xml"));
            Require(!manifest.Contains("documentsignatures.xml", StringComparison.Ordinal),
                "The signature file is not encrypted.");
        }

        var signature = OdtSignatureReader.Read(signed).Single();
        Require(signature.IsValid, "The signature over the encrypted package is valid: "
            + string.Join(" | ", signature.Problems));

        var read = new OdtDocumentReader().Read(
            new MemoryStream(signed), new OdtReadOptions { Password = Password });
        Require(Text(read.Document).Contains("Signed content", StringComparison.Ordinal),
            "The encrypted signed ODT is readable with its password.");
    }

    private static X509Certificate2 Certificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Reglet ODT Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static PortableDocument Document()
        => PortableDocument.Create()
            .Heading("Signed ODT", level: 1)
            .Paragraph("Signed content")
            .BulletList("one", "two")
            .Build();

    private static byte[] Write(PortableDocument document, OdtWriteOptions options)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer, options).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] Rewrite(byte[] package, string name, byte[] data)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var content = entry.FullName == name ? data : Read(entry);
                var copy = target.CreateEntry(entry.FullName, entry.FullName == "mimetype"
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Optimal);
                using var stream = copy.Open();
                stream.Write(content);
            }
        }

        return output.ToArray();
    }

    private static byte[] Read(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return Read(zip, name);
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        return Read(entry);
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Text(PortableDocument document)
    {
        var builder = new StringBuilder();
        foreach (var section in document.Sections)
        {
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        foreach (var run in paragraph.Inlines.OfType<TextRun>()) builder.Append(run.Text);
                        break;
                    case Heading heading:
                        foreach (var run in heading.Inlines.OfType<TextRun>()) builder.Append(run.Text);
                        break;
                }
            }
        }

        return builder.ToString();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
