using Reglet;

/// <summary>
/// import fidelity (phase AC, AC01): the DOCX reader takes over the paragraph, section and
/// list properties that the model offers.
/// </summary>
internal static class ImportFidelityChecks
{
    internal static int Run()
    {
        try
        {
            ReadsParagraphFormatting();
            ReadsRunFormatting();
            ReadsSectionColumns();
            ReadsNumbering();
            ReadsParagraphBordersAndShading();
            ReadsOdtParagraphFormatting();
            Console.WriteLine("Import fidelity (AC01): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Import fidelity (AC01): " + exception);
            return 1;
        }
    }

    private static PortableDocument Document()
    {
        var style = new ParagraphStyle
        {
            Alignment = TextAlignment.Center,
            SpaceBefore = Length.FromPoints(6),
            SpaceAfter = Length.FromPoints(9),
            LeftIndent = Length.FromPoints(12),
            LineSpacing = LineSpacing.Multiple(1.5),
            KeepWithNext = true,
            KeepTogether = true,
            PageBreakBefore = true,
            RightToLeft = true,
            TabStops = [new TabStop(Length.FromPoints(120), TabStopAlignment.Right, TabStopLeader.Dot)],
            Borders = ParagraphBorders.All(new ParagraphBorder(BorderStyle.Dashed, Length.FromPoints(1), new Color(255, 0, 0))),
            Background = new Color(240, 240, 240),
        };

        var paragraph = new Paragraph(new TextRun("Text")) { Style = style };
        var list = TestLists.List(ListKind.Number, [new ListItem(new Paragraph(new TextRun("Entry")))], format: ListNumberFormat.UpperRoman, start: 5);

        var section = new Section([paragraph, list])
        {
            TextColumns = 2,
        };

        return TestLists.Fix(new PortableDocument(new DocumentMetadata(), section));
    }

    private static PortableDocument Read(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        using var input = new MemoryStream(buffer.ToArray());
        return new DocxDocumentReader().Read(input).Document;
    }

    private static void ReadsParagraphFormatting()
    {
        var read = Read(Document());
        var paragraph = read.Sections[0].Blocks.OfType<Paragraph>().First();
        var style = paragraph.Style;

        Require(style.Alignment == TextAlignment.Center, "Alignment.");
        Require(style.SpaceBefore?.Points == 6, "Space before.");
        Require(style.SpaceAfter?.Points == 9, "Space after.");
        Require((style.LeftIndent?.Points ?? 0) == 12, "Left indent.");
        Require(style.LineSpacing is { Rule: LineSpacingRule.Multiple } spacing && Math.Abs(spacing.Factor - 1.5) < 0.01, "Line spacing.");
        Require(style.KeepWithNext == true, "Keep with next.");
        Require(style.KeepTogether == true, "Keep together.");
        Require(style.PageBreakBefore == true, "Page break before.");
        Require(style.RightToLeft == true, "Right to left.");
        var tabs = style.TabStops;
        Require(tabs is { Count: 1 }, "Tab stops: count.");
        Require(tabs![0].Position.Points == 120, "Tab stops: position.");
        Require(tabs[0].Alignment == TabStopAlignment.Right, "Tab stops: alignment.");
        Require(tabs[0].Leader == TabStopLeader.Dot, "Tab stops: leader.");
    }

    private static void ReadsRunFormatting()
    {
        var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new TextRun("Text")
        {
            UnderlineStyle = TextUnderlineStyle.Wave,
            UnderlineColor = Color.FromHex("#123456"),
            Background = Color.FromHex("#ABCDEF"),
            CharacterSpacing = Length.FromPoints(-1),
            HorizontalScaling = 85,
            Kerning = Length.FromPoints(10),
            Position = Length.FromPoints(2),
        })));
        var run = ((Paragraph)Read(document).Sections[0].Blocks[0]).Inlines.OfType<TextRun>().Single();
        Require(run.UnderlineStyle == TextUnderlineStyle.Wave && run.UnderlineColor?.ToString() == "#123456", "DOCX: underline style and color.");
        Require(run.Background?.ToString() == "#ABCDEF", "DOCX: text background.");
        Require(run.CharacterSpacing?.Points == -1 && run.HorizontalScaling == 85 && run.Kerning?.Points == 10 && run.Position?.Points == 2,
            "DOCX: character spacing, scaling, kerning and position.");
    }

    private static void ReadsSectionColumns()
    {
        var read = Read(Document());
        Require(read.Sections[0].TextColumns == 2, "Text columns.");
    }

    private static void ReadsNumbering()
    {
        var read = Read(Document());
        var list = read.Sections[0].Blocks.OfType<ListBlock>().First();
        var level = TestLists.Level(read, list);
        Require(level.Kind == ListKind.Number, "List kind.");
        Require(level.Format == ListNumberFormat.UpperRoman, "Number format.");
        Require(level.Start == 5, "Start value.");
    }

    private static void ReadsOdtParagraphFormatting()
    {
        // The ODT reader takes over the same properties (phase AC, AC01).
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(
            [
                new Paragraph(new TextRun("First")) { Style = new ParagraphStyle { KeepWithNext = true, KeepTogether = true } },
                new Paragraph(new TextRun("Second"))
                {
                    Style = new ParagraphStyle
                    {
                        PageBreakBefore = true,
                        TabStops = [new TabStop(Length.FromPoints(120), TabStopAlignment.Right, TabStopLeader.Dot)],
                    },
                },
            ]));

        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        using var input = new MemoryStream(buffer.ToArray());
        var read = new OdtDocumentReader().Read(input).Document;

        var paragraphs = read.Sections[0].Blocks.OfType<Paragraph>().ToList();
        var style = paragraphs[0].Style;
        Require(style.KeepWithNext == true, "ODT: keep with next.");
        Require(style.KeepTogether == true, "ODT: keep together.");

        // A page break before a paragraph becomes a block of its own (as in DOCX).
        var blocks = read.Sections[0].Blocks;
        Require(blocks.OfType<PageBreak>().Any(), "ODT: the page break is a block of its own.");
        var tabs = paragraphs[1].Style.TabStops;
        Require(tabs is { Count: 1 }, "ODT: tab stops.");
        Require(tabs![0].Alignment == TabStopAlignment.Right, "ODT: tab stops: alignment.");
        Require(tabs[0].Leader == TabStopLeader.Dot, "ODT: tab stops: leader.");
    }

    private static void ReadsParagraphBordersAndShading()
    {
        var read = Read(Document());
        var paragraph = read.Sections[0].Blocks.OfType<Paragraph>().First();

        var borders = paragraph.Style.Borders;
        Require(borders is not null, "Borders.");
        var top = borders!.Top;
        Require(top is not null, "Borders: top.");
        Require(top!.Style == BorderStyle.Dashed, "Borders: style.");
        Require(Math.Abs(top.Width.Points - 1) < 0.01, "Borders: width.");
        Require(top.Color.Red == 255 && top.Color.Green == 0 && top.Color.Blue == 0, "Borders: color.");
        var background = paragraph.Style.Background;
        Require(background is not null, "Background.");
        var color = background!.Value;
        Require(color.Red == 240 && color.Green == 240 && color.Blue == 240, "Background: color.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
