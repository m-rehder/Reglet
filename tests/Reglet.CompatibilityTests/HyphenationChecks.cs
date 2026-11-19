using Reglet;

/// <summary>
/// hyphenation (phase AJ, BJ01): the Liang algorithm with the patterns of the caller and the
/// soft hyphens as break opportunities in the PDF.
/// </summary>
internal static class HyphenationChecks
{
    internal static int Run()
    {
        try
        {
            CalculatesBreakPositions();
            AppliesToWordsAndText();
            IgnoresShortWords();
            BreaksAtSoftHyphen();
            BreaksWithPatterns();
            EnablesWordHyphenation();
            KeepsSoftHyphenInDocx();
            Console.WriteLine("Hyphenation (BJ01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Hyphenation (BJ01): " + exception);
            return 1;
        }
    }

    /// <summary>The patterns of the test: "hy3ph" allows the break in "hy-phen".</summary>
    private static HyphenationPatterns Patterns() => HyphenationPatterns.Parse("hy3ph\n");

    private static void CalculatesBreakPositions()
    {
        var patterns = Patterns();

        var positions = patterns.BreakPositions("hyphen");
        Require(positions.Count == 1, "The pattern allows one break.");
        Require(positions[0] == 2, "The break is after the second character.");
        Require(patterns.BreakPositions("cat").Count == 0, "A word without a pattern is not broken.");

        // A point marks the word border: ".ab1c" only matches at the start of the word.
        Require(
            HyphenationPatterns.Parse(".ab1c").BreakPositions("abcabcab").SequenceEqual([2]),
            "A border pattern only matches at the word border.");
    }

    private static void AppliesToWordsAndText()
    {
        var patterns = Patterns();

        Require(patterns.Apply("hyphen") == "hy\u00ADphen", "The soft hyphen is inserted.");
        Require(
            patterns.ApplyToText("a hyphen here") == "a hy\u00ADphen here",
            "The soft hyphens are inserted in the text.");
    }

    private static void IgnoresShortWords()
    {
        var patterns = Patterns();

        Require(patterns.Apply("hy") == "hy", "A short word is not broken.");
        Require(patterns.Apply("") == string.Empty, "An empty word stays empty.");
    }

    private static void BreaksAtSoftHyphen()
    {
        // A narrow page: the word does not fit on one line and is broken at the soft hyphen.
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("Extra\u00ADordinarily")))
            {
                PageSize = new PageSize(Length.FromPoints(60), Length.FromPoints(200)),
                Margins = new PageMargins(Length.FromPoints(10)),
            });

        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(-) Tj", StringComparison.Ordinal), "The PDF draws the hyphen at the break.");
        Require(
            !content.Contains("\u00AD", StringComparison.Ordinal),
            "The PDF draws no soft hyphen.");
    }

    private static void BreaksWithPatterns()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("hyphenation")))
            {
                PageSize = new PageSize(Length.FromPoints(60), Length.FromPoints(200)),
                Margins = new PageMargins(Length.FromPoints(10)),
            });

        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer, new PdfWriteOptions { Hyphenation = Patterns() })
            .GetAwaiter()
            .GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(-) Tj", StringComparison.Ordinal), "The PDF breaks the word with the patterns.");
    }

    private static void EnablesWordHyphenation()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Text"))));

        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(document, buffer, new DocxWriteOptions { AutoHyphenation = true })
            .GetAwaiter()
            .GetResult();

        var settings = ReadZipEntry(buffer.ToArray(), "word/settings.xml");
        Require(settings.Contains("autoHyphenation", StringComparison.Ordinal), "DOCX: the automatic hyphenation is enabled.");
        Require(settings.Contains("hyphenationZone", StringComparison.Ordinal), "DOCX: the hyphenation zone is written.");
    }

    private static void KeepsSoftHyphenInDocx()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("Extra\u00ADordinarily"))));

        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(content.Contains('\u00AD'), "DOCX: the soft hyphen stays in the text.");
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(package), System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
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
