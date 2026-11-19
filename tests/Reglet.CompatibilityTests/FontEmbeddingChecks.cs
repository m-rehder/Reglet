using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>embedding fonts in DOCX and ODT.</summary>
internal static class FontEmbeddingChecks
{
    internal static int Run()
    {
        try
        {
            ObfuscationIsItsOwnInverse();
            EmbedsInDocx();
            EmbedsInOdt();
            WritesNothingWithoutFonts();
            WritesEditableRanges();
            Console.WriteLine("Font embedding: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Font embedding: " + exception);
            return 1;
        }
    }

    private static (PortableDocument Document, FontCollection Fonts, byte[] Bytes) Sample()
    {
        var bytes = PdfAChecks.Font();
        var fonts = FontCollection.Empty.Add("Hello Sans", FontFace.Load(bytes));
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new TextRun("Hello") { FontFamily = "Hello Sans" }),
        }));
        return (document, fonts, bytes);
    }

    private static void ObfuscationIsItsOwnInverse()
    {
        var data = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        var key = Guid.Parse("11223344-5566-7788-99AA-BBCCDDEEFF00");
        var once = DocxFontEmbedding.Obfuscate(data, key);
        Require(!once.AsSpan(0, 32).SequenceEqual(data.AsSpan(0, 32)), "The first 32 bytes change.");
        Require(once.AsSpan(32).SequenceEqual(data.AsSpan(32)), "The rest stays.");
        Require(DocxFontEmbedding.Obfuscate(once, key).AsSpan().SequenceEqual(data), "XOR twice restores the file.");
        // ECMA-376 17.8.1: the GUID string without dashes is read from its end.
        Require(once[0] == (data[0] ^ 0x00) && once[1] == (data[1] ^ 0xFF), "The key starts with the last byte of the GUID string.");
    }

    private static void EmbedsInDocx()
    {
        var (document, fonts, bytes) = Sample();
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer, new DocxWriteOptions { EmbeddedFonts = fonts }).GetAwaiter().GetResult();
        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()));
        var table = Read(archive, "word/fontTable.xml");
        Require(table.Contains("w:name=\"Hello Sans\"", StringComparison.Ordinal) && table.Contains("embedRegular", StringComparison.Ordinal), "The font table names the font.");
        Require(Read(archive, "word/settings.xml").Contains("embedTrueTypeFonts", StringComparison.Ordinal), "The settings switch embedding on.");
        Require(Read(archive, "[Content_Types].xml").Contains("obfuscatedFont", StringComparison.Ordinal), "The content type of .odttf is registered.");
        Require(Read(archive, "word/_rels/fontTable.xml.rels").Contains("fonts/font1.odttf", StringComparison.Ordinal), "The font table points to the file.");
        var key = Guid.Parse(System.Text.RegularExpressions.Regex.Match(table, "fontKey=\"(\\{[^\"]+\\})\"").Groups[1].Value);
        using var stored = archive.GetEntry("word/fonts/font1.odttf")!.Open();
        using var copy = new MemoryStream();
        stored.CopyTo(copy);
        Require(DocxFontEmbedding.Obfuscate(copy.ToArray(), key).AsSpan().SequenceEqual(bytes), "The stored file de-obfuscates to the font.");
    }

    private static void EmbedsInOdt()
    {
        var (document, fonts, bytes) = Sample();
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer, new OdtWriteOptions { EmbeddedFonts = fonts }).GetAwaiter().GetResult();
        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()));
        Require(Read(archive, "content.xml").Contains("Fonts/font1.ttf", StringComparison.Ordinal), "The declaration points to the font file.");
        Require(Read(archive, "META-INF/manifest.xml").Contains("full-path=\"Fonts/font1.ttf\"", StringComparison.Ordinal), "The manifest lists the font file.");
        using var stored = archive.GetEntry("Fonts/font1.ttf")!.Open();
        using var copy = new MemoryStream();
        stored.CopyTo(copy);
        Require(copy.ToArray().AsSpan().SequenceEqual(bytes), "The font file is stored unchanged.");
    }

    private static void WritesNothingWithoutFonts()
    {
        var (document, _, _) = Sample();
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()));
        Require(archive.GetEntry("word/fontTable.xml") is null, "Without fonts there is no font table.");
    }

    private static void WritesEditableRanges()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
        {
            new Paragraph(new Inline[] { new TextRun("Locked "), new Bookmark("Free"), new TextRun("open"), new BookmarkEnd("Free") }),
        }));
        using var buffer = new MemoryStream();
        var result = new DocxDocumentWriter().WriteAsync(
            document,
            buffer,
            new DocxWriteOptions { Protection = DocxProtection.ReadOnly, EditableRanges = ["Free", "Missing"] }).GetAwaiter().GetResult();
        using var archive = new ZipArchive(new MemoryStream(buffer.ToArray()));
        var body = Read(archive, "word/document.xml");
        var start = body.IndexOf("<w:permStart", StringComparison.Ordinal);
        var end = body.IndexOf("<w:permEnd", StringComparison.Ordinal);
        Require(start > 0 && end > start && body.Contains("w:edGrp=\"everyone\"", StringComparison.Ordinal), "The bookmark range is wrapped in a permission range.");
        Require(body.IndexOf("open", start, StringComparison.Ordinal) is var text && text > start && text < end, "The permission range contains the text.");
        Require(Read(archive, "word/settings.xml").Contains("documentProtection", StringComparison.Ordinal), "The protection is on.");
        Require(result.Warnings.Any(warning => warning.Message.Contains("'Missing'", StringComparison.Ordinal)), "An unknown range is reported.");
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)?.Open() ?? throw new InvalidOperationException("Missing " + name), Encoding.UTF8);
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
