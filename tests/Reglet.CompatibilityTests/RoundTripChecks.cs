using Reglet;

/// <summary>
/// O05: round-trip tests. Write → read → write → read must yield a semantically
/// equal content model (stability). For DOCX it is additionally checked that the
/// first read result matches the source (for the subset covered by the reader).
/// layout is expressly not compared, only content. Comparison uses
/// <see cref="DocumentComparer"/> (AF01).
/// </summary>
internal static class RoundTripChecks
{
    internal static int Run()
    {
        try
        {
            RoundTripsStably(DocumentFormat.Docx);
            RoundTripsStably(DocumentFormat.Odt);
            MatchesSourceForDocx();
            MatchesSourceForOdt();
            Console.WriteLine("Round trip: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Round-Trip: " + exception);
            return 1;
        }
    }

    /// <summary>The second read yields the same model as the first.</summary>
    private static void RoundTripsStably(DocumentFormat format)
    {
        var source = BuildDocument(includeParagraphStyles: true);
        var first = WriteRead(source, format);
        var second = WriteRead(first, format);

        RequireEqual(first, second, $"{format}: the second read deviates from the first.");
    }

    /// <summary>
    /// The DOCX reader maps the source losslessly for the covered subset. The table layout
    /// (borders, cell padding, column widths) is an exception: the DOCX format materializes it
    /// in every table, so the read result holds the materialized values (AJ01).
    /// </summary>
    private static void MatchesSourceForDocx()
    {
        var source = BuildDocument(includeParagraphStyles: true);
        var read = WriteRead(source, DocumentFormat.Docx);

        RequireEqual(source, read, "DOCX: the read result deviates from the source.",
            [.. TableLayoutPaths, .. HeadingStylePaths]);
    }

    /// <summary>
    /// The ODT reader maps the source losslessly for the text-focused subset (without
    /// paragraph formatting that the ODT reader does not read). Secures, among others, spaces in
    /// formatted runs and the section layout.
    /// </summary>
    private static void MatchesSourceForOdt()
    {
        var source = BuildDocument(includeParagraphStyles: false);
        var read = WriteRead(source, DocumentFormat.Odt);

        RequireEqual(source, read, "ODT: the read result deviates from the source.",
            [.. TableLayoutPaths, .. HeadingStylePaths]);
    }

    private static PortableDocument WriteRead(PortableDocument document, DocumentFormat format)
    {
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        buffer.Position = 0;
        return DocumentReader.Read(buffer, format).Document;
    }

    /// <summary>
    /// uses only features that both readers cover: metadata, several sections,
    /// heading, formatted runs (including spaces in spans), lists, table,
    /// page break and page number field. Page dimensions are integral in points so that
    /// twips/pt rounding makes no difference.
    /// </summary>
    private static PortableDocument BuildDocument(bool includeParagraphStyles)
    {
        var metadata = new DocumentMetadata
        {
            Title = "Title",
            Author = "Author",
            Subject = "Subject",
            Keywords = new[] { "one", "two" },
            Language = "de-DE",
            Created = new DateTimeOffset(2026, 9, 24, 14, 30, 5, TimeSpan.FromHours(2)),
        };

        var styled = new Paragraph(new Inline[]
        {
            new TextRun("Normal "),
            new TextRun("bold") { Bold = true },
            new TextRun(" italic ")
            {
                Italic = true,
                FontSize = Length.FromPoints(14),
                Color = Color.FromHex("#112233"),
                FontFamily = "Arial",
            },
            new TextRun("u") { Underline = true },
        })
        {
            Style = includeParagraphStyles
                ? new ParagraphStyle
                {
                    Alignment = TextAlignment.Justify,
                    SpaceBefore = Length.FromPoints(12),
                    SpaceAfter = Length.FromPoints(8),
                    LeftIndent = Length.FromPoints(24),
                    RightIndent = Length.FromPoints(6),
                }
                : ParagraphStyle.Default,
        };

        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("A"), new TableCell("B") }) { IsHeader = true },
            new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
        });

        var section1 = new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Title")),
            styled,
            TestLists.List(ListKind.Bullet, new[]
            {
                new ListItem(new Paragraph(new TextRun("a"))),
                new ListItem(new Paragraph(new TextRun("b"))),
            }),
            TestLists.List(ListKind.Number, new[]
            {
                new ListItem(new Paragraph(new TextRun("1"))),
                new ListItem(new Paragraph(new TextRun("2"))),
            }),
            table,
            new PageBreak(),
        })
        {
            PageSize = PageSize.Letter,
            Margins = PageMargins.Normal,
            Orientation = PageOrientation.Portrait,
        };

        var section2 = new Section(new Block[]
        {
            new Paragraph(new Inline[] { new TextRun("Page "), new PageNumberField() }),
        })
        {
            PageSize = new PageSize(Length.FromPoints(600), Length.FromPoints(800)),
            Margins = PageMargins.Normal,
            Orientation = PageOrientation.Portrait,
        };

        return TestLists.Fix(new PortableDocument(metadata, new[] { section1, section2 }));
    }

    /// <summary>
    /// the paths of the table layout that the formats materialize in every table (the reader
    /// reports them, the model does not have to name them): the borders, the cell padding,
    /// the column widths, the width in percent and the table style (AJ01).
    /// </summary>
    private static readonly string[] TableLayoutPaths =
    [
        ".Border",
        ".CellPadding",
        ".ColumnWidths",
        ".WidthPercent",
        ".Style",
        ".Padding",
    ];

    /// <summary>
    /// The heading typography stands in the built-in heading style of the file. The readers apply
    /// the run formatting of the style to the runs, so a heading read from a DOCX or ODT carries
    /// the bold and the size explicitly; the rendered output stays the same.
    /// </summary>
    private static readonly string[] HeadingStylePaths =
    [
        "(Heading).Inlines[0](TextRun).Bold",
        "(Heading).Inlines[0](TextRun).FontSize",
    ];

    /// <summary>Compares two models deeply and fails with the per-path differences.</summary>
    private static void RequireEqual(PortableDocument expected, PortableDocument actual, string context)
        => RequireEqual(expected, actual, context, []);

    /// <summary>
    /// Compares two models deeply and fails with the per-path differences. The paths that the
    /// format materializes on its own are allowed.
    /// </summary>
    private static void RequireEqual(PortableDocument expected, PortableDocument actual, string context, string[] allowedPaths)
    {
        var differences = DocumentComparer.Compare(expected, actual)
            .Where(difference => !allowedPaths.Any(allowed => difference.Path.Contains(allowed, StringComparison.Ordinal)))
            .ToList();
        if (differences.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            context + "\n--- differences ---\n" + string.Join('\n', differences));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
