using System.Text;
using System.Xml.Linq;
using Reglet;

/// <summary>How a corpus case is verified.</summary>
internal enum CorpusCheck
{
    /// <summary>The package parts of the original must survive (modulo the allowlist).</summary>
    Parts,

    /// <summary>The extracted page text must be unchanged (used for PDF, which is text-only).</summary>
    Text,

    /// <summary>The unchanged document must be written back byte-identically.</summary>
    Verbatim,

    /// <summary>Replacing one block must keep the unmodelled content of the other blocks.</summary>
    PreserveBlock,

    /// <summary>Inserting a block must keep the unmodelled content of the other blocks.</summary>
    PreserveAfterInsert,
}

/// <summary>A generated round-trip corpus case with its expected classification.</summary>
internal sealed record CorpusCase(
    string Id,
    DocumentFormat Format,
    byte[] Original,
    IReadOnlySet<string> AllowedLosses,
    CorpusCheck Check);

/// <summary>
/// AF03: the round-trip corpus. The case bytes are generated deterministically in code (no
/// binary fixtures, no licensing questions); the manifest
/// <c>fixtures/roundtrip/corpus.txt</c> carries only the expected classification. The loader
/// enforces that manifest and generators stay in sync.
/// </summary>
internal static class RoundTripCorpus
{
    private const string UnknownPart = "custom/extra.xml";
    private const string ManifestRelativePath = "tests/Reglet.CompatibilityTests/fixtures/roundtrip/corpus.txt";

    private static readonly string[] GeneratorIds =
    [
        "docx-generated",
        "odt-generated",
        "docx-unknown-part",
        "odt-unknown-part",
        "docx-unmodelled-body",
        "docx-edit-unmodelled",
        "docx-edit-unmodelled-styled",
        "docx-edit-unmodelled-insert",
        "odt-edit-unmodelled",
        "odt-edit-unmodelled-heading",
        "odt-edit-unmodelled-insert",
        "odt-edit-unmodelled-multisection",
        "odt-edit-unmodelled-formatted",
        "odt-edit-unmodelled-list",
        "odt-edit-unmodelled-table",
        "odt-edit-unmodelled-formatted-table",
        "odt-edit-unmodelled-table-level-formatted",
        "odt-edit-unmodelled-broken-table",
        "pdf-generated",
    ];

    /// <summary>Loads the manifest and materialises every case.</summary>
    public static IReadOnlyList<CorpusCase> Load()
    {
        var entries = ReadManifest(Path.Combine(GoldenPaths.RepositoryRoot, ManifestRelativePath));
        ValidateGenerators(entries);

        return entries
            .Select(entry => new CorpusCase(
                entry.Id,
                entry.Format,
                Generate(entry.Id),
                entry.AllowedLosses,
                entry.Check))
            .ToList();
    }

    private static byte[] Generate(string id) => id switch
    {
        "docx-generated" => TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx),
        "odt-generated" => TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Odt),
        "docx-unknown-part" => TestPackages.AddPart(
            TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx), UnknownPart, "<extra/>"),
        "odt-unknown-part" => TestPackages.AddPart(
            TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Odt), UnknownPart, "<extra/>"),
        "docx-unmodelled-body" => InjectDocxCommentRange(
            TestPackages.Write(TestPackages.SampleDocument(), DocumentFormat.Docx)),
        "docx-edit-unmodelled" => InjectDocxCommentRange(WritePlainParagraphs(DocumentFormat.Docx)),
        "docx-edit-unmodelled-styled" => InjectDocxCommentRange(WriteHeadingAndParagraphs(DocumentFormat.Docx)),
        "docx-edit-unmodelled-insert" => InjectDocxCommentRange(WritePlainParagraphs(DocumentFormat.Docx)),
        "odt-edit-unmodelled" => InjectOdtAnnotation(WritePlainParagraphs(DocumentFormat.Odt)),
        "odt-edit-unmodelled-heading" => InjectOdtAnnotation(WriteHeadingAndParagraphs(DocumentFormat.Odt)),
        "odt-edit-unmodelled-insert" => InjectOdtAnnotation(WritePlainParagraphs(DocumentFormat.Odt)),
        "odt-edit-unmodelled-multisection" => InjectOdtAnnotation(WriteMultiSectionAndParagraphs()),
        "odt-edit-unmodelled-formatted" => InjectOdtAnnotation(WriteFormattedAndParagraphs()),
        "odt-edit-unmodelled-list" => InjectOdtAnnotation(WriteListAndParagraphs()),
        "odt-edit-unmodelled-table" => InjectOdtAnnotation(WriteTableAndParagraphs()),
        "odt-edit-unmodelled-formatted-table" => InjectOdtAnnotation(WriteFormattedTableAndParagraphs()),
        "odt-edit-unmodelled-table-level-formatted" => InjectOdtAnnotation(WriteTableLevelFormattedAndParagraphs()),
        "odt-edit-unmodelled-broken-table" => InjectOdtAnnotation(WriteBrokenTableAndParagraphs()),
        "pdf-generated" => TestPackages.Write(
            PortableDocument.Create().Paragraph("RoundTripCorpusText").Build(), DocumentFormat.Pdf),
        _ => throw new InvalidOperationException($"No generator for corpus case '{id}'."),
    };

    private static byte[] WritePlainParagraphs(DocumentFormat format)
        => TestPackages.Write(
            PortableDocument.Create().Paragraph("first").Paragraph("second").Paragraph("third").Build(),
            format);

    private static byte[] WriteMultiSectionAndParagraphs()
        => TestPackages.Write(
            new PortableDocument(
                new DocumentMetadata { Language = "en-US" },
                new[]
                {
                    new Section(new Block[] { new Paragraph(new TextRun("first")), new Paragraph(new TextRun("second")) }),
                    new Section(new Block[] { new Paragraph(new TextRun("third")), new Paragraph(new TextRun("fourth")) }),
                }),
            DocumentFormat.Odt);

    private static byte[] WriteHeadingAndParagraphs(DocumentFormat format)
        => TestPackages.Write(
            new PortableDocument(
                new DocumentMetadata { Language = "en-US" },
                new Section(new Block[]
                {
                    new Heading(HeadingLevel.Level1, new TextRun("title")),
                    new Paragraph(new TextRun("body")),
                    new Paragraph(new TextRun("tail")),
                })),
            format);

    private static byte[] WriteFormattedAndParagraphs()
        => TestPackages.Write(
            new PortableDocument(
                new DocumentMetadata { Language = "en-US" },
                new Section(new Block[]
                {
                    new Paragraph(new TextRun("bold") { Bold = true })
                    {
                        Style = new ParagraphStyle { Alignment = TextAlignment.Justify },
                    },
                    new Paragraph(new TextRun("second")),
                    new Paragraph(new TextRun("third")),
                })),
            DocumentFormat.Odt);

    private static byte[] WriteListAndParagraphs()
        => TestPackages.Write(
            TestLists.Document(
                new DocumentMetadata { Language = "en-US" },
                new Section(new Block[]
                {
                    TestLists.List(ListKind.Bullet, new[]
                    {
                        new ListItem(new Paragraph(new TextRun("a"))),
                        new ListItem(new Paragraph(new TextRun("b"))),
                    }),
                    new Paragraph(new TextRun("second")),
                    new Paragraph(new TextRun("third")),
                })),
            DocumentFormat.Odt);

    private static byte[] WriteTableAndParagraphs()
        => TestPackages.Write(
            new PortableDocument(
                new DocumentMetadata { Language = "en-US" },
                new Section(new Block[]
                {
                    new TableBlock(new[]
                    {
                        new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
                        new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                    }),
                    new Paragraph(new TextRun("second")),
                    new Paragraph(new TextRun("third")),
                })),
            DocumentFormat.Odt);

    private static byte[] WriteFormattedTableAndParagraphs()
        => TestPackages.Write(
            new PortableDocument(
                new DocumentMetadata { Language = "en-US" },
                new Section(new Block[]
                {
                    new TableBlock(new[]
                    {
                        new TableRow(new[]
                        {
                            new TableCell("A") { Background = Color.FromHex("#FF0000") },
                            new TableCell("B"),
                        }),
                        new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                    }),
                    new Paragraph(new TextRun("second")),
                    new Paragraph(new TextRun("third")),
                })),
            DocumentFormat.Odt);

    private static byte[] WriteTableLevelFormattedAndParagraphs()
        => TestPackages.Write(
            new PortableDocument(
                new DocumentMetadata { Language = "en-US" },
                new Section(new Block[]
                {
                    new TableBlock(new[]
                    {
                        new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
                        new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                    })
                    {
                        Border = new ParagraphBorder(),
                    },
                    new Paragraph(new TextRun("second")),
                    new Paragraph(new TextRun("third")),
                })),
            DocumentFormat.Odt);

    private static byte[] WriteBrokenTableAndParagraphs()
        => TestPackages.Write(
            new PortableDocument(
                new DocumentMetadata { Language = "en-US" },
                new Section(new Block[]
                {
                    new PageBreak(),
                    new TableBlock(new[]
                    {
                        new TableRow(new[] { new TableCell("A"), new TableCell("B") }),
                        new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
                    })
                    {
                        Border = new ParagraphBorder(),
                    },
                    new Paragraph(new TextRun("second")),
                    new Paragraph(new TextRun("third")),
                })),
            DocumentFormat.Odt);

    /// <summary>Adds an unmodelled comment range to the first DOCX paragraph.</summary>
    private static byte[] InjectDocxCommentRange(byte[] package)
    {
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "word/document.xml")));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var paragraph = xml.Root!.Element(w + "body")!.Elements(w + "p").First();
        paragraph.AddFirst(new XElement(w + "commentRangeStart", new XAttribute(w + "id", "1")));
        paragraph.Add(new XElement(w + "commentRangeEnd", new XAttribute(w + "id", "1")));

        var bytes = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        return TestPackages.ReplacePart(package, "word/document.xml", bytes);
    }

    /// <summary>Adds an unmodelled annotation to the first ODT paragraph.</summary>
    private static byte[] InjectOdtAnnotation(byte[] package)
    {
        var xml = XDocument.Parse(Encoding.UTF8.GetString(TestPackages.ReadPart(package, "content.xml")));
        XNamespace office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";

        var body = xml.Root!.Element(office + "body")!.Element(office + "text")!;
        var container = body.Elements(text + "section").FirstOrDefault() ?? body;
        var first = container.Descendants().First(element => element.Name == text + "p" || element.Name == text + "h");
        first.Add(new XElement(
            office + "annotation",
            new XElement(dc + "creator", "me"),
            new XElement(dc + "date", "2020-01-01T00:00:00"),
            new XElement(text + "p", "note")));

        var bytes = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        return TestPackages.ReplacePart(package, "content.xml", bytes);
    }

    private static void ValidateGenerators(IReadOnlyList<ManifestEntry> entries)
    {
        var manifestIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var missing = GeneratorIds.Where(id => !manifestIds.Contains(id)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Corpus cases have no manifest entry: " + string.Join(", ", missing));
        }

        var unknown = manifestIds.Where(id => !GeneratorIds.Contains(id, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                "Manifest cases have no generator: " + string.Join(", ", unknown));
        }
    }

    private static List<ManifestEntry> ReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Corpus manifest is missing: {path}");
        }

        var entries = new List<ManifestEntry>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('|').Select(field => field.Trim()).ToArray();
            if (fields.Length != 4)
            {
                throw new InvalidOperationException($"Invalid corpus manifest line: {raw}");
            }

            var id = fields[0];
            if (entries.Any(entry => entry.Id == id))
            {
                throw new InvalidOperationException($"Duplicate corpus case id '{id}'.");
            }

            var allowed = fields[2] == "-"
                ? new HashSet<string>(StringComparer.Ordinal)
                : fields[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);

            entries.Add(new ManifestEntry(id, ParseFormat(fields[1]), allowed, ParseCheck(fields[3])));
        }

        return entries;
    }

    private static DocumentFormat ParseFormat(string value) => value switch
    {
        "docx" => DocumentFormat.Docx,
        "odt" => DocumentFormat.Odt,
        "pdf" => DocumentFormat.Pdf,
        _ => throw new InvalidOperationException($"Unknown corpus format '{value}'."),
    };

    private static CorpusCheck ParseCheck(string value) => value switch
    {
        "parts" => CorpusCheck.Parts,
        "text" => CorpusCheck.Text,
        "verbatim" => CorpusCheck.Verbatim,
        "preserve-block" => CorpusCheck.PreserveBlock,
        "preserve-insert" => CorpusCheck.PreserveAfterInsert,
        _ => throw new InvalidOperationException($"Unknown corpus check '{value}'."),
    };

    private sealed record ManifestEntry(
        string Id,
        DocumentFormat Format,
        IReadOnlySet<string> AllowedLosses,
        CorpusCheck Check);
}
