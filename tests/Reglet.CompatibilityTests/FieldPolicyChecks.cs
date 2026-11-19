using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// OF-C04: one policy for the calculation and the caching of fields. A cached result is shown as
/// it is, a field without a result is calculated when the file is written, and a field without a
/// value stays empty in all three formats.
/// </summary>
internal static class FieldPolicyChecks
{
    internal static int Run()
    {
        try
        {
            CachedResultWinsEverywhere();
            SameVisibleResultInAllFormats();
            FieldWithoutValueStaysEmpty();
            ResolveKeepsThePolicy();
            Console.WriteLine("Field policy (OF-C04): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Field policy (OF-C04): " + exception);
            return 1;
        }
    }

    private static DocumentMetadata Metadata() => new()
    {
        Title = "Report",
        Created = new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeSpan.Zero),
        Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Author"] = "Short" },
    };

    /// <summary>A cached result is never replaced by the calculated value.</summary>
    private static void CachedResultWinsEverywhere()
    {
        var fields = new Inline[] { new Field(" DOCPROPERTY Title ", result: "cached") };
        var document = new PortableDocument(Metadata(), new Section(new Paragraph(fields)));

        var docx = DocxText(document);
        var odt = OdtText(document);
        var pdf = PdfText(document);

        foreach (var (name, text) in new[] { ("DOCX", docx), ("ODT", odt), ("PDF", pdf) })
        {
            Require(text.Contains("cached", StringComparison.Ordinal), $"{name}: the cached result is shown: {text}");
            Require(!text.Contains("Report", StringComparison.Ordinal), $"{name}: the cached result is not replaced: {text}");
        }
    }

    /// <summary>Every calculated field appears in all three formats, in the same order.</summary>
    private static void SameVisibleResultInAllFormats()
    {
        var expected = new[] { "Report", "Short", "2024-05-06", "1", "2", "yes" };
        var paragraphs = new[]
        {
            new Paragraph(new Inline[] { new Field(" DOCPROPERTY Title ") }),
            new Paragraph(new Inline[] { new Field(" DOCVARIABLE Author ") }),
            new Paragraph(new Inline[] { new Field(" CREATEDATE \\@ \"yyyy-MM-dd\" ") }),
            new Paragraph(new Inline[] { new Field(" SEQ Figure ") }),
            new Paragraph(new Inline[] { new Field(" SEQ Figure ") }),
            new Paragraph(new Inline[] { new Field(" IF 1 = 1 \"yes\" \"no\" ") }),
        };
        var document = new PortableDocument(Metadata(), new Section(paragraphs));

        var texts = new[] { ("DOCX", DocxText(document)), ("ODT", OdtText(document)), ("PDF", PdfText(document)) };
        foreach (var (name, text) in texts)
        {
            var position = 0;
            foreach (var value in expected)
            {
                var found = text.IndexOf(value, position, StringComparison.Ordinal);
                Require(found >= 0, $"{name}: the field value '{value}' is shown in order: {text}");
                position = found + value.Length;
            }
        }
    }

    /// <summary>A field without a value produces no visible text and is reported by the PDF.</summary>
    private static void FieldWithoutValueStaysEmpty()
    {
        var document = new PortableDocument(
            Metadata(),
            new Section(new Paragraph(new Inline[] { new TextRun("before"), new Field(" MERGEFIELD Name "), new TextRun("after") })));

        var odt = OdtText(document);
        Require(odt.Contains("before", StringComparison.Ordinal) && odt.Contains("after", StringComparison.Ordinal), "ODT: the neighbours stay.");
        Require(!odt.Contains("Name", StringComparison.Ordinal), "ODT: the field code is not written as text: " + odt);

        var pdf = PdfText(document);
        Require(pdf.Contains("(before) Tj", StringComparison.Ordinal) && pdf.Contains("(after) Tj", StringComparison.Ordinal), "PDF: the neighbours stay.");
        Require(!pdf.Contains("Name", StringComparison.Ordinal), "PDF: the field code is not drawn: " + pdf);

        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("MERGEFIELD Name", StringComparison.Ordinal)),
            "PDF: the field without a result is reported.");

        var docx = DocxText(document);
        Require(docx.Contains("before", StringComparison.Ordinal) && docx.Contains("after", StringComparison.Ordinal), "DOCX: the neighbours stay.");
    }

    /// <summary><see cref="FieldResolver"/> follows the same rule inside the model.</summary>
    private static void ResolveKeepsThePolicy()
    {
        var cached = new Field(" DOCPROPERTY Title ", result: "cached");
        var calculated = new Field(" DOCPROPERTY Title ");
        var unknown = new Field(" MERGEFIELD Name ");
        var document = new PortableDocument(
            Metadata(),
            new Section(new Paragraph(new Inline[] { cached, calculated, unknown })));

        var context = FieldContext.FromMetadata(Metadata());
        var resolved = FieldResolver.Resolve(document, context);
        var runs = ((Paragraph)resolved.Sections[0].Blocks[0]).Inlines;
        var fields = runs.OfType<Field>().ToList();

        Require(fields[0].Result == "cached", "FieldResolver: the cached result stays: " + fields[0].Result);
        Require(fields[1].Result == "Report", "FieldResolver: a missing result is calculated: " + fields[1].Result);
        Require(fields[2].Result is null, "FieldResolver: an unknown value keeps the field without a result.");
        Require(((Field)((Paragraph)document.Sections[0].Blocks[0]).Inlines[1]).Result is null, "FieldResolver: the input document is unchanged.");
    }

    private static string DocxText(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var part = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        return string.Concat(Regex.Matches(part, "<w:t(?: [^>]*)?>(.*?)</w:t>").Select(match => match.Groups[1].Value));
    }

    private static string OdtText(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var part = ReadZipEntry(buffer.ToArray(), "content.xml");
        var bodyStart = part.IndexOf("<office:body>", StringComparison.Ordinal);
        var body = bodyStart >= 0 ? part[bodyStart..] : part;
        return string.Concat(Regex.Matches(body, ">([^<>]+)<").Select(match => match.Groups[1].Value));
    }

    private static string PdfText(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return PdfStreams.Decompress(buffer.ToArray());
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
