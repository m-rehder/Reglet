using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// general fields (phase Z): a field code that the typed fields do not cover, for example
/// <c>SEQ</c>, <c>IF</c> or <c>MERGEFIELD</c>.
/// </summary>
internal static class FieldChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxField();
            KeepsOdtResultAndReports();
            MapsOdtFields();
            DrawsPdfResult();
            ReportsFieldWithoutResult();
            RejectsInvalidValues();
            Console.WriteLine("General fields (E): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("General fields (E): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document(Field field)
        => new(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new Inline[] { new TextRun("Figure "), field }) }));

    private static PortableDocument Document(Field[] fields)
        => new(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(
                    new Inline[] { new Bookmark("target"), new TextRun("Chapter ") }
                        .Concat(fields.SelectMany(field => new Inline[] { field, new TextRun(" ") }))
                        .ToArray()),
            }));

    private static string Snippet(string text, string part)
    {
        var index = text.IndexOf(part, StringComparison.Ordinal);
        return index < 0 ? "(missing)" : text.Substring(Math.Max(0, index - 80), Math.Min(500, text.Length - Math.Max(0, index - 80)));
    }

    private static void WritesDocxField()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(Document(new Field("SEQ Figure \\* ARABIC", "1")), buffer)
            .GetAwaiter()
            .GetResult();

        var document = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(document.Contains("SEQ Figure", StringComparison.Ordinal), "DOCX: the field code is written.");
        Require(document.Contains("w:fldCharType=\"begin\"", StringComparison.Ordinal), "DOCX: the field begins.");
        Require(document.Contains("w:fldCharType=\"end\"", StringComparison.Ordinal), "DOCX: the field ends.");
        Require(document.Contains("<w:t>1</w:t>", StringComparison.Ordinal), "DOCX: the cached result is written.");
    }

    /// <summary>A field ODF cannot express keeps its result as text and is reported (OF-C02).</summary>
    private static void KeepsOdtResultAndReports()
    {
        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter()
            .WriteAsync(Document(new Field("IF 1 = 1 \"yes\" \"no\"", "yes")), buffer)
            .GetAwaiter()
            .GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains(">yes<", StringComparison.Ordinal), "ODT: the cached result is kept as text.");
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("IF 1 = 1", StringComparison.Ordinal)),
            "ODT: the field is reported.");

        buffer.Position = 0;
        var paragraph = (Paragraph)new OdtDocumentReader().Read(buffer).Document.Sections[0].Blocks[0];
        var text = string.Concat(paragraph.Inlines.OfType<TextRun>().Select(run => run.Text));
        Require(text == "Figure yes", "ODT: the cached result is read as text: " + text);
        Require(!paragraph.Inlines.OfType<Field>().Any(), "ODT: the field instruction is not recovered.");
    }

    /// <summary>
    /// OF-C02: SEQ, REF and the document information fields are ODF elements with the result as
    /// their text, they are not reported, and they read back as the same field code.
    /// </summary>
    private static void MapsOdtFields()
    {
        var fields = new Field[]
        {
            new("SEQ Figure \\* ARABIC", "1"),
            new("SEQ Table \\* ROMAN \\* MERGEFORMAT", "II"),
            new("REF target \\h", "Chapter"),
            new("TITLE", "Report"),
            new("AUTHOR", "Ada"),
            new("FILENAME", "report.odt"),
        };

        using var buffer = new MemoryStream();
        var result = new OdtDocumentWriter().WriteAsync(Document(fields), buffer).GetAwaiter().GetResult();
        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(!result.HasWarnings, "ODT: mapped fields are not reported: " + string.Join("; ", result.Warnings.Select(warning => warning.Message)));
        var xml = System.Xml.Linq.XDocument.Parse(content);
        System.Xml.Linq.XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        System.Xml.Linq.XNamespace style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        var sequences = xml.Descendants(text + "sequence").ToList();
        Require(sequences.Count == 2
                && (string?)sequences[0].Attribute(text + "name") == "Figure"
                && (string?)sequences[0].Attribute(text + "formula") == "ooow:Figure+1"
                && (string?)sequences[0].Attribute(style + "num-format") == "1"
                && sequences[0].Value == "1",
            "ODT: SEQ is a text:sequence: " + Snippet(content, "<text:sequence "));
        Require((string?)sequences[1].Attribute(style + "num-format") == "I" && sequences[1].Value == "II", "ODT: the number format of SEQ.");
        Require(xml.Descendants(text + "sequence-decl").Select(decl => (string?)decl.Attribute(text + "name")).SequenceEqual(["Figure", "Table"]), "ODT: the sequences are declared.");
        var reference = xml.Descendants(text + "bookmark-ref").Single();
        Require((string?)reference.Attribute(text + "reference-format") == "text" && (string?)reference.Attribute(text + "ref-name") == "target" && reference.Value == "Chapter", "ODT: REF is a text reference.");
        Require(xml.Descendants(text + "title").Single().Value == "Report"
                && xml.Descendants(text + "initial-creator").Single().Value == "Ada"
                && xml.Descendants(text + "file-name").Single() is { Value: "report.odt" } file
                && (string?)file.Attribute(text + "display") == "name-and-extension", "ODT: document information fields.");

        buffer.Position = 0;
        var read = new OdtDocumentReader().Read(buffer).Document;
        var readFields = ((Paragraph)read.Sections[0].Blocks[0]).Inlines.OfType<Field>().Select(field => (field.Instruction, field.Result)).ToList();
        Require(
            readFields.SequenceEqual(new (string, string?)[]
            {
                ("SEQ Figure \\* ARABIC", "1"),
                ("SEQ Table \\* ROMAN", "II"),
                ("REF target \\h", "Chapter"),
                ("TITLE", "Report"),
                ("AUTHOR", "Ada"),
                ("FILENAME", "report.odt"),
            }),
            "ODT: the fields read back: " + string.Join(", ", readFields));

        // A page reference keeps reading as a PAGEREF field.
        var page = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Inline[] { new PageRefField("target") })));
        using var pageBuffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(page, pageBuffer).GetAwaiter().GetResult();
        pageBuffer.Position = 0;
        Require(((Paragraph)new OdtDocumentReader().Read(pageBuffer).Document.Sections[0].Blocks[0]).Inlines.OfType<PageRefField>().Any(), "ODT: page references stay PAGEREF.");
    }

    private static void DrawsPdfResult()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter()
            .WriteAsync(Document(new Field("SEQ Figure \\* ARABIC", "42")), buffer)
            .GetAwaiter()
            .GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(42) Tj", StringComparison.Ordinal), "PDF: the cached result is drawn.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("SEQ Figure", StringComparison.Ordinal)),
            "PDF: a field with a result is not reported.");
    }

    private static void ReportsFieldWithoutResult()
    {
        // AJ02: a field that cannot be calculated (no result, unknown code) is reported.
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter()
            .WriteAsync(Document(new Field("UNKNOWNCODE")), buffer)
            .GetAwaiter()
            .GetResult();

        Require(
            result.Warnings.Any(warning => warning.Message.Contains("UNKNOWNCODE", StringComparison.Ordinal)),
            "PDF: a field without a result is reported.");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentException>(() => new Field(" "));
        Throws<ArgumentNullException>(() => new Field(null!));
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
