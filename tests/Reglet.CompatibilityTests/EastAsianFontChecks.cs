using System.IO.Compression;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// East Asian text: the run's and the document's East Asian font (DOCX <c>w:eastAsia</c>, ODT
/// <c>style:font-name-asian</c>) and the fallback fonts of the font collection, which the PDF takes for the characters
/// the named font does not have.
/// </summary>
internal static class EastAsianFontChecks
{
    private const string Japanese = "日本";

    internal static int Run()
    {
        try
        {
            PdfTakesTheEastAsianFont();
            PdfTakesAFallbackFont();
            DocxKeepsTheEastAsianFont();
            OdtKeepsTheEastAsianFont();
            CollectionFontsLoadByFamily();
            Console.WriteLine("East Asian fonts: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("East Asian fonts: " + exception);
            return 1;
        }
    }

    private static FontFace Latin() => FontFace.Load(PdfAChecks.Font("LatinSans", ('H', 3), ('a', 4), ('e', 4), ('l', 5), ('o', 6)));

    private static FontFace Kanji() => FontFace.Load(PdfAChecks.Font("KanjiSans", ('日', 1), ('本', 2), ('o', 6)));

    /// <summary>The PDF's content and the font resources its text shows use.</summary>
    private static (List<string> Fonts, WriteResult Result) Pdf(PortableDocument document, FontCollection fonts)
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter().WriteAsync(document, buffer, new PdfWriteOptions { FontCollection = fonts, UnsupportedContent = UnsupportedContentBehavior.Fail })
            .GetAwaiter().GetResult();
        var content = PdfStreams.DecompressPages(buffer.ToArray())[0];
        return (Regex.Matches(content, @"/(F\d+) [\d.]+ Tf").Select(match => match.Groups[1].Value).Distinct().ToList(), result);
    }

    private static PortableDocument Document(TextRun run, DocumentDefaults? defaults = null)
        => new(new DocumentMetadata(), new Section(new Paragraph(run))) { Defaults = defaults ?? DocumentDefaults.Default with { FontFamily = "LatinSans" } };

    private static void PdfTakesTheEastAsianFont()
    {
        var fonts = FontCollection.Empty.Add(Latin()).Add(Kanji());
        var (own, _) = Pdf(Document(new TextRun("Hello" + Japanese) { EastAsianFontFamily = "KanjiSans" }), fonts);
        Require(own.Count == 2, $"The run's East Asian font draws the Japanese characters ({string.Join(", ", own)}).");
        var (document, _) = Pdf(Document(new TextRun("Hello" + Japanese), DocumentDefaults.Default with { FontFamily = "LatinSans", EastAsianFontFamily = "KanjiSans" }), fonts);
        Require(document.Count == 2, "The document's East Asian font as well.");
        // "o" is in both fonts: a Latin character stays with the run's font.
        var (latin, _) = Pdf(Document(new TextRun("Hello") { EastAsianFontFamily = "KanjiSans" }), fonts);
        Require(latin.Count == 1, "Latin text keeps its font.");
        Throws(() => Pdf(Document(new TextRun("Hello" + Japanese)), fonts), "Without an East Asian or fallback font the glyphs are missing.");
    }

    private static void PdfTakesAFallbackFont()
    {
        var fonts = FontCollection.Empty.Add(Latin()).AddFallback(Kanji());
        Require(fonts.FallbackFamilies.Count == 1, "The fallback family is listed.");
        var (used, _) = Pdf(Document(new TextRun("Hello" + Japanese)), fonts);
        Require(used.Count == 2, $"The fallback font draws what the run's font lacks ({string.Join(", ", used)}).");
    }

    private static void DocxKeepsTheEastAsianFont()
    {
        var document = Document(new TextRun("Hello" + Japanese) { EastAsianFontFamily = "MS Gothic" },
            DocumentDefaults.Default with { FontFamily = "Arial", EastAsianFontFamily = "Microsoft JhengHei" });
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        buffer.Position = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
        {
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            Require(reader.ReadToEnd().Contains("w:eastAsia=\"MS Gothic\"", StringComparison.Ordinal), "DOCX: the run's w:rFonts names the East Asian font.");
        }

        buffer.Position = 0;
        var read = new DocxDocumentReader().Read(buffer).Document;
        var run = (TextRun)((Paragraph)read.Sections[0].Blocks[0]).Inlines[0];
        Require(run.EastAsianFontFamily == "MS Gothic", $"DOCX: the run's East Asian font is read back ({run.EastAsianFontFamily}).");
        Require(read.Defaults.EastAsianFontFamily == "Microsoft JhengHei", $"DOCX: the document's as well ({read.Defaults.EastAsianFontFamily}).");
    }

    private static void OdtKeepsTheEastAsianFont()
    {
        var document = Document(new TextRun("Hello" + Japanese) { EastAsianFontFamily = "MS Gothic" },
            DocumentDefaults.Default with { FontFamily = "Arial", EastAsianFontFamily = "Microsoft JhengHei" });
        var read = TestPackages.Read(TestPackages.Write(document, DocumentFormat.Odt), DocumentFormat.Odt);
        var run = (TextRun)((Paragraph)read.Sections[0].Blocks[0]).Inlines[0];
        Require(run.EastAsianFontFamily == "MS Gothic", $"ODT: the run's East Asian font is read back ({run.EastAsianFontFamily}).");
        Require(read.Defaults.EastAsianFontFamily == "Microsoft JhengHei", $"ODT: the document's as well ({read.Defaults.EastAsianFontFamily}).");
    }

    /// <summary>A TrueType collection of the fonts: its header, then each font with its table offsets from the start of the file.</summary>
    internal static byte[] Collection(params byte[][] fonts)
    {
        var output = new List<byte>(new byte[12 + (4 * fonts.Length)]);
        var offsets = new List<uint>();
        foreach (var source in fonts)
        {
            var start = (uint)output.Count;
            offsets.Add(start);
            var font = source.ToArray();
            for (var table = 0; table < System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4)); table++)
            {
                var record = font.AsSpan(12 + (table * 16) + 8, 4);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(record, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record) + start);
            }

            output.AddRange(font);
            while (output.Count % 4 != 0)
            {
                output.Add(0);
            }
        }

        var result = output.ToArray();
        "ttcf"u8.CopyTo(result);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x00010000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)fonts.Length);
        for (var index = 0; index < offsets.Count; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + (4 * index)), offsets[index]);
        }

        return result;
    }

    private static void CollectionFontsLoadByFamily()
    {
        var latin = PdfAChecks.Font("LatinSans", ('H', 3), ('e', 4), ('l', 5), ('o', 6));
        var latinUi = PdfAChecks.Font("LatinSans UI", ('H', 3), ('e', 4), ('l', 5), ('o', 6));
        var kanji = PdfAChecks.Font("KanjiSans", ('日', 1), ('本', 2));
        var ttc = Collection(latin, latinUi, kanji);
        var all = FontFace.LoadCollection(ttc);
        Require(all.Select(face => face.FamilyName).SequenceEqual(["LatinSans", "LatinSans UI", "KanjiSans"]), $"All fonts of the collection ({string.Join(", ", all.Select(face => face.FamilyName))}).");
        Require(FontFace.Load(ttc).FamilyName == "LatinSans", "Load takes the first font of a collection.");
        var picked = FontFace.Load(ttc, "latinsans ui");
        Require(picked.FamilyName == "LatinSans UI", "The family name picks the font, case-insensitively, and tells a UI variant apart.");
        Require(picked.Data.Length < ttc.Length / 2 && picked.Data.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 })
                && FontFace.Load(picked.Data).FamilyName == "LatinSans UI",
            "Only the chosen font is kept, as a file of its own with its tables.");
        try
        {
            FontFace.Load(ttc, "Missing");
            throw new InvalidOperationException("An unknown family is rejected.");
        }
        catch (FontException exception)
        {
            Require(exception.Message.Contains("KanjiSans", StringComparison.Ordinal), $"The message names the families of the collection ({exception.Message}).");
        }

        var directory = Directory.CreateTempSubdirectory("reglet-ttc");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, "fonts.ttc"), ttc);
            var collection = FontCollection.FromDirectory(directory.FullName);
            Require(collection.Contains("LatinSans UI") && collection.Contains("KanjiSans"), "A directory's collection adds every font of it.");
            var (used, _) = Pdf(Document(new TextRun("Hello" + Japanese) { EastAsianFontFamily = "KanjiSans" }, DocumentDefaults.Default with { FontFamily = "LatinSans" }), collection);
            Require(used.Count == 2, "The PDF draws with fonts from a collection.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void Throws(Action action, string message)
    {
        try
        {
            action();
        }
        catch (UnsupportedFeatureException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
