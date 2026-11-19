using Reglet;

/// <summary>the shipped hyphenation patterns of Reglet.Hyphenation.</summary>
internal static class HyphenationLanguagesChecks
{
    internal static int Run()
    {
        try
        {
            LoadsEveryLanguage();
            ResolvesNames();
            HyphenatesKnownWords();
            BreaksAPdfWithTheShippedPatterns();
            Console.WriteLine("Shipped hyphenation patterns: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Shipped hyphenation patterns: " + exception);
            return 1;
        }
    }

    private static void LoadsEveryLanguage()
    {
        Require(HyphenationLanguages.Available.Count >= 13, "At least thirteen languages ship.");
        foreach (var language in HyphenationLanguages.Available)
        {
            Require(HyphenationLanguages.Load(language).Count > 100, $"The patterns of '{language}' load.");
        }

        Require(ReferenceEquals(HyphenationLanguages.Load("de-1996"), HyphenationLanguages.Load("DE-1996")), "A language is parsed once.");
    }

    private static void ResolvesNames()
    {
        Require(HyphenationLanguages.IsAvailable("de") && HyphenationLanguages.IsAvailable("de_AT") && HyphenationLanguages.IsAvailable("en"), "Short and regional names resolve.");
        Require(ReferenceEquals(HyphenationLanguages.Load("de-CH"), HyphenationLanguages.Load("de-CH-1901")), "Swiss German resolves to its own patterns.");
        Require(!HyphenationLanguages.IsAvailable("xx"), "An unknown language is not available.");
        try
        {
            HyphenationLanguages.Load("xx");
            throw new InvalidOperationException("An unknown language must be refused.");
        }
        catch (ArgumentException)
        {
        }
    }

    private static void HyphenatesKnownWords()
    {
        // Position = the number of characters before the break.
        Require(HyphenationLanguages.Load("en-US").BreakPositions("hyphenation").SequenceEqual([2, 6]), "English: hy-phen-ation.");
        var german = HyphenationLanguages.Load("de").BreakPositions("Silbentrennung");
        Require(german.SequenceEqual([3, 6, 10]), "German: Sil-ben-tren-nung (" + string.Join(",", german) + ").");
        Require(HyphenationLanguages.Load("de").BreakPositions("Donaudampfschifffahrt").Contains(5), "German: a compound is broken at its joint (Donau-).");
        Require(HyphenationLanguages.Load("fr").BreakPositions("informatique").Count > 0, "French words are broken.");
    }

    private static void BreaksAPdfWithTheShippedPatterns()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("Silbentrennung")))
            {
                PageSize = new PageSize(Length.FromPoints(70), Length.FromPoints(200)),
                Margins = new PageMargins(Length.FromPoints(10)),
            });

        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer, new PdfWriteOptions { Hyphenation = HyphenationLanguages.Load("de") })
            .GetAwaiter().GetResult();
        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(-) Tj", StringComparison.Ordinal), "The PDF breaks a German word with the shipped patterns and draws the hyphen.");
        // The break point after "tren" leaves no room for the hyphen: the break moves to "Silben-".
        var hyphen = content.IndexOf("(-) Tj", StringComparison.Ordinal);
        Require(content.IndexOf("(ben) Tj", StringComparison.Ordinal) is var ben and >= 0 && ben < hyphen
            && content.IndexOf("(tren) Tj", StringComparison.Ordinal) is var tren and >= 0 && tren > hyphen,
            "A word is not split without a hyphen: Silben- / trennung.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
