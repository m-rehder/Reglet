using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// ADR-0036: a changed document carries no removed content, no foreign package and no macros or signatures of its
/// source along.
/// </summary>
internal static class PackageLeakChecks
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    internal static int Run()
    {
        try
        {
            RemovedContentLeavesThePackage();
            ForeignSourceIsNotCopied();
            OdtMacrosAndSignatureAreDropped();
            MetadataEditDropsSignatures();
            Console.WriteLine("Package leaks (ADR-0036): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Package leaks (ADR-0036): " + exception);
            return 1;
        }
    }

    private static PortableDocument Secret() => new(new DocumentMetadata(), new Section([
        new Paragraph([new TextRun("public"), new Footnote(new Paragraph(new TextRun("SECRET-NOTE")))]),
        new Paragraph([new Comment(0, "Ann", "SECRET-COMMENT"), new TextRun("x"), new CommentEnd(0)]),
        new Paragraph(new TextRun("SECRET-BODY")),
        new ImageBlock(Png, ImageMediaType.Png),
        new Chart(ChartKind.Column, [new ChartSeries("SECRET-SERIES", [1.0])], ["c"]),
    ]) { Header = new HeaderFooter(new Paragraph(new TextRun("SECRET-HEADER"))) });

    private static PortableDocument KeepFirstWord(PortableDocument read)
        => read with { Sections = [new Section(new Paragraph(new TextRun("public")))] };

    private static void RemovedContentLeavesThePackage()
    {
        // Every part the writer did not generate was copied: a removed header, note, image or chart stayed in the file.
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            var read = TestPackages.Read(TestPackages.Write(Secret(), format), format);
            var written = TestPackages.Write(KeepFirstWord(read), format);
            var names = Names(written);
            Require(!Leaks(written).Any(), $"{format}: no removed text is left: {string.Join(", ", Leaks(written))}");
            Require(!names.Any(name => name.Contains("media/", StringComparison.Ordinal) || name.StartsWith("Pictures/", StringComparison.Ordinal)),
                $"{format}: the removed image is gone: {string.Join(", ", names)}");
            Require(!names.Any(name => name.Contains("chart", StringComparison.Ordinal) || name.StartsWith("Object ", StringComparison.Ordinal)),
                $"{format}: the removed chart is gone: {string.Join(", ", names)}");
            Require(format == DocumentFormat.Odt ? OdtPackageValidator.Validate(written).Count == 0 : DocxPackageValidator.Validate(written).Count == 0,
                $"{format}: the package is valid.");
        }
    }

    private static void ForeignSourceIsNotCopied()
    {
        // A DOCX written from a read ODT contained the whole ODT package (content.xml with the original text).
        var read = TestPackages.Read(TestPackages.Write(Secret(), DocumentFormat.Odt), DocumentFormat.Odt);
        var docx = TestPackages.Write(KeepFirstWord(read), DocumentFormat.Docx);
        var names = Names(docx);
        Require(!names.Contains("content.xml") && !names.Contains("styles.xml") && !names.Contains("META-INF/manifest.xml"),
            "ODT -> DOCX: no ODT part: " + string.Join(", ", names));
        Require(!Leaks(docx).Any(), "ODT -> DOCX: no removed text is left.");
    }

    private static byte[] WithMacrosAndSignature(byte[] odt)
    {
        var package = TestPackages.AddPart(odt, "Basic/Standard/Module1.xml", "<module>Sub Main\nEnd Sub</module>");
        package = TestPackages.AddPart(package, "Scripts/python/run.py", "import os");
        return TestPackages.AddPart(package, "META-INF/documentsignatures.xml", "<document-signatures xmlns=\"urn:oasis:names:tc:opendocument:xmlns:digitalsignature:1.0\"/>");
    }

    private static void OdtMacrosAndSignatureAreDropped()
    {
        // A changed ODT kept Basic and Python macros and a signature that no longer covers it, without a warning.
        var source = WithMacrosAndSignature(TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("signed")))), DocumentFormat.Odt));
        var read = TestPackages.Read(source, DocumentFormat.Odt);
        var changed = read with { Sections = [new Section([.. read.Sections[0].Blocks, new Paragraph(new TextRun("added"))])] };
        var (written, warnings) = WriteWithWarnings(changed, DocumentFormat.Odt);
        var names = Names(written);
        Require(!names.Any(name => name.StartsWith("Basic/", StringComparison.Ordinal) || name.StartsWith("Scripts/", StringComparison.Ordinal) || name.Contains("signatures", StringComparison.Ordinal)),
            "ODT: macros and signature are dropped: " + string.Join(", ", names));
        Require(warnings.Any(w => w.Contains("macros", StringComparison.Ordinal)) && warnings.Any(w => w.Contains("signature", StringComparison.Ordinal)),
            "ODT: the dropped macros and signature are reported.");
        Require(OdtPackageValidator.Validate(written).Count == 0, "ODT: the package is valid.");

        // Open and save without a change keeps the package byte for byte (the signature still covers it).
        Require(TestPackages.Write(read, DocumentFormat.Odt).AsSpan().SequenceEqual(source), "ODT: an unchanged document is written back unchanged.");
    }

    private static void MetadataEditDropsSignatures()
    {
        // Only the metadata changed: the original package was adopted with its signature, which no longer covers it.
        var odt = WithMacrosAndSignature(TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("signed")))), DocumentFormat.Odt));
        var read = TestPackages.Read(odt, DocumentFormat.Odt);
        var (written, warnings) = WriteWithWarnings(read with { Metadata = read.Metadata with { Title = "Changed" } }, DocumentFormat.Odt);
        var names = Names(written);
        Require(!names.Any(name => name.StartsWith("Basic/", StringComparison.Ordinal) || name.StartsWith("Scripts/", StringComparison.Ordinal) || name.Contains("signatures", StringComparison.Ordinal)),
            "ODT metadata edit: macros and signature are dropped: " + string.Join(", ", names));
        Require(warnings.Count(w => w.Contains("macros", StringComparison.Ordinal) || w.Contains("signature", StringComparison.Ordinal)) == 2, "ODT metadata edit: both are reported.");
        var manifest = Encoding.UTF8.GetString(TestPackages.ReadPart(written, "META-INF/manifest.xml"));
        Require(!manifest.Contains("Basic/", StringComparison.Ordinal) && !manifest.Contains("signatures", StringComparison.Ordinal) && OdtPackageValidator.Validate(written).Count == 0,
            "ODT metadata edit: the manifest lists no dropped part.");

        var docx = TestPackages.Write(new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("signed")))), DocumentFormat.Docx);
        docx = TestPackages.AddPart(docx, "_xmlsignatures/sig1.xml", "<Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\"/>");
        docx = TestPackages.AddPart(docx, "_xmlsignatures/origin.sigs", string.Empty);
        var rels = Encoding.UTF8.GetString(TestPackages.ReadPart(docx, "_rels/.rels")).Replace("</Relationships>",
            "<Relationship Id=\"rIdSig\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin\" Target=\"_xmlsignatures/origin.sigs\"/></Relationships>", StringComparison.Ordinal);
        docx = TestPackages.ReplacePart(docx, "_rels/.rels", Encoding.UTF8.GetBytes(rels));
        var readDocx = TestPackages.Read(docx, DocumentFormat.Docx);
        (written, warnings) = WriteWithWarnings(readDocx with { Metadata = readDocx.Metadata with { Title = "Changed" } }, DocumentFormat.Docx);
        Require(!Names(written).Any(name => name.StartsWith("_xmlsignatures/", StringComparison.Ordinal))
            && !Encoding.UTF8.GetString(TestPackages.ReadPart(written, "_rels/.rels")).Contains("digital-signature", StringComparison.Ordinal),
            "DOCX metadata edit: the signature and its relationship are dropped.");
        Require(warnings.Any(w => w.Contains("signature", StringComparison.Ordinal)), "DOCX metadata edit: the dropped signature is reported.");
    }

    private static (byte[] Bytes, List<string> Warnings) WriteWithWarnings(PortableDocument document, DocumentFormat format)
    {
        using var buffer = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return (buffer.ToArray(), result.Warnings.Select(warning => warning.Message).ToList());
    }

    private static List<string> Names(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return zip.Entries.Select(entry => entry.FullName).ToList();
    }

    private static IEnumerable<string> Leaks(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        var found = new List<string>();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            if (reader.ReadToEnd().Contains("SECRET", StringComparison.Ordinal))
            {
                found.Add(entry.FullName);
            }
        }

        return found;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
