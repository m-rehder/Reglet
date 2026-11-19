using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// paragraph formatting (phase X, X01–X07): line height, paragraph borders/shading,
/// break control, tab stops, justification, RTL direction and text columns in DOCX, ODT and PDF.
/// </summary>
internal static class ParagraphFormattingChecks
{
    internal static int Run()
    {
        try
        {
            WritesDocxParagraphProperties();
            WritesOdtParagraphProperties();
            WritesPdfParagraphEffects();
            RejectsInvalidValues();
            KeepsExplicitZeroSpacing();
            KeepsExplicitZeroSpacingOverStyle();
            KeepsExplicitNoBorders();
            KeepsExplicitLeftToRightDirection();
            KeepsExplicitNoTabStops();
            KeepsExplicitOffBreakFlags();
            KeepsExplicitLeftAlignment();
            KeepsExplicitZeroIndents();
            WrapsFirstLineAtItsOwnWidth();
            Console.WriteLine("Paragraph formatting (X): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Paragraph formatting (X): " + exception);
            return 1;
        }
    }

    /// <summary>An explicit zero spacing after must survive a write and read.</summary>
    private static void KeepsExplicitZeroSpacing()
    {
        var explicitZero = new ParagraphStyle { SpaceAfter = Length.Zero };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[]
            {
                new Paragraph(new TextRun("explicit")) { Style = explicitZero },
                new Paragraph(new TextRun("default")),
            }));

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var blocks = DocumentReader.Read(buffer, format).Document.Sections[0].Blocks;

            var explicitRead = (Paragraph)blocks[0];
            Require(explicitRead.Style.SpaceAfter == Length.Zero,
                format + ": the explicit zero spacing was lost.");
            var defaultRead = (Paragraph)blocks[1];
            Require(defaultRead.Style.SpaceAfter is null,
                format + ": an unset spacing became explicit.");
        }

        var spacing = new ParagraphSpacing();
        Require(spacing.SpaceAfter(new Paragraph(new TextRun("x")) { Style = explicitZero }) == 0,
            "The shared spacing rule ignores an explicit zero.");
        Require(
            Math.Abs(spacing.SpaceAfter(new Paragraph(new TextRun("x"))) - Typography.ParagraphSpaceAfter.Points) < 1e-9,
            "The shared spacing rule dropped the default spacing.");

        // The same for the definitions of named styles.
        var explicitNamed = new ParagraphStyle { StyleName = "Eng", SpaceAfter = Length.Zero };
        var defaultNamed = new ParagraphStyle { StyleName = "Wide" };
        var named = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Block[] { new Paragraph(new TextRun("named")) { Style = explicitNamed } }))
        {
            Styles = [explicitNamed, defaultNamed],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(named, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var styles = DocumentReader.Read(buffer, format).Document.Styles;
            Require(styles.Single(style => style.StyleName == "Eng").SpaceAfter == Length.Zero,
                format + ": the explicit zero of a named style was lost.");
            Require(styles.Single(style => style.StyleName == "Wide").SpaceAfter is null,
                format + ": an unset named style became explicit.");
        }
    }

    /// <summary>An explicit zero spacing overrides an inherited spacing of a named style.</summary>
    private static void KeepsExplicitZeroSpacingOverStyle()
    {
        var style = new ParagraphStyle
        {
            StyleName = "Spaced",
            SpaceBefore = Length.FromPoints(18),
            SpaceAfter = Length.FromPoints(12),
        };
        var paragraphStyle = new ParagraphStyle
        {
            StyleName = "Spaced",
            SpaceBefore = Length.Zero,
            SpaceAfter = Length.Zero,
        };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("no spacing")) { Style = paragraphStyle }))
        {
            Styles = [style],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];
            var readStyle = read.Styles.Single(item => item.StyleName == "Spaced");

            Require(readStyle.SpaceBefore?.Points == 18 && readStyle.SpaceAfter?.Points == 12,
                format + ": the spaced style was lost.");
            Require(paragraph.Style.SpaceBefore == Length.Zero && paragraph.Style.SpaceAfter == Length.Zero,
                format + ": the explicit zero spacing was lost.");
        }
    }

    /// <summary>An explicit empty border set overrides the borders of a named style.</summary>
    private static void KeepsExplicitNoBorders()
    {
        var bordered = new ParagraphStyle
        {
            StyleName = "Bordered",
            Borders = ParagraphBorders.All(
                new ParagraphBorder(BorderStyle.Double, Length.FromPoints(1), Color.FromHex("123456"))),
        };
        var noBorders = new ParagraphStyle { StyleName = "Bordered", Borders = ParagraphBorders.None };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("no borders")) { Style = noBorders }))
        {
            Styles = [bordered],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];
            var readStyle = read.Styles.Single(item => item.StyleName == "Bordered");

            Require(readStyle.Borders?.Top is not null, format + ": the bordered style was lost.");
            Require(
                paragraph.Style.Borders is { } borders
                    && borders.Top is null && borders.Right is null
                    && borders.Bottom is null && borders.Left is null,
                format + ": the explicit no-borders state was lost.");
        }
    }

    /// <summary>An explicit left-to-right overrides an inherited right-to-left style.</summary>
    private static void KeepsExplicitLeftToRightDirection()
    {
        var rtl = new ParagraphStyle { StyleName = "Rtl", RightToLeft = true };
        var ltr = new ParagraphStyle { StyleName = "Rtl", RightToLeft = false };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("left to right")) { Style = ltr }))
        {
            Styles = [rtl],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];
            var readStyle = read.Styles.Single(item => item.StyleName == "Rtl");

            Require(readStyle.RightToLeft == true, format + ": the RTL style was lost.");
            Require(paragraph.Style.RightToLeft == false,
                format + ": the explicit left-to-right direction was lost.");
        }
    }

    /// <summary>An explicit empty tab-stop list overrides the tab stops of a named style.</summary>
    private static void KeepsExplicitNoTabStops()
    {
        var styled = new ParagraphStyle
        {
            StyleName = "Tabs",
            TabStops = [new TabStop(Length.FromPoints(100), TabStopAlignment.Right, TabStopLeader.Dot)],
        };
        var noTabs = new ParagraphStyle { StyleName = "Tabs", TabStops = [] };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("no tabs")) { Style = noTabs }))
        {
            Styles = [styled],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];
            var readStyle = read.Styles.Single(item => item.StyleName == "Tabs");

            Require(readStyle.TabStops is { Count: 1 }, format + ": the styled tab stop was lost.");
            Require(paragraph.Style.TabStops is { Count: 0 },
                format + ": the explicit no-tab-stops state was lost.");
        }
    }

    /// <summary>An explicit off of a break flag must survive a write and read, also over a style.</summary>
    private static void KeepsExplicitOffBreakFlags()
    {
        var style = new ParagraphStyle
        {
            StyleName = "Keep",
            KeepWithNext = true,
            KeepTogether = true,
            PageBreakBefore = true,
        };
        var explicitOff = new ParagraphStyle
        {
            StyleName = "Keep",
            KeepWithNext = false,
            KeepTogether = false,
            PageBreakBefore = false,
        };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("explicit off")) { Style = explicitOff }))
        {
            Styles = [style],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];
            var readStyle = read.Styles.Single(item => item.StyleName == "Keep");

            Require(readStyle.KeepWithNext == true && readStyle.KeepTogether == true && readStyle.PageBreakBefore == true,
                format + ": the style's break flags were lost.");
            Require(paragraph.Style.KeepWithNext == false, format + ": the explicit keep-with-next off was lost.");
            Require(paragraph.Style.KeepTogether == false, format + ": the explicit keep-together off was lost.");
            Require(paragraph.Style.PageBreakBefore == false, format + ": the explicit page-break off was lost.");
        }
    }

    /// <summary>An explicit left alignment overrides an inherited centered style.</summary>
    private static void KeepsExplicitLeftAlignment()
    {
        var style = new ParagraphStyle { StyleName = "Centered", Alignment = TextAlignment.Center };
        var paragraphStyle = new ParagraphStyle
        {
            StyleName = "Centered",
            Alignment = TextAlignment.Left,
        };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("left")) { Style = paragraphStyle }))
        {
            Styles = [style],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];

            Require(read.Styles.Single(item => item.StyleName == "Centered").Alignment == TextAlignment.Center,
                format + ": the centered style was lost.");
            Require(paragraph.Style.Alignment == TextAlignment.Left,
                format + ": the explicit left alignment was lost.");
        }
    }

    /// <summary>An explicit zero indent overrides an inherited indent.</summary>
    private static void KeepsExplicitZeroIndents()
    {
        var style = new ParagraphStyle
        {
            StyleName = "Indented",
            LeftIndent = Length.FromPoints(24),
            RightIndent = Length.FromPoints(12),
            FirstLineIndent = Length.FromPoints(6),
        };
        var paragraphStyle = new ParagraphStyle
        {
            StyleName = "Indented",
            LeftIndent = Length.Zero,
            RightIndent = Length.Zero,
            FirstLineIndent = Length.Zero,
        };
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new TextRun("no indent")) { Style = paragraphStyle }))
        {
            Styles = [style],
        };

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            using var buffer = new MemoryStream();
            DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
            buffer.Position = 0;
            var read = DocumentReader.Read(buffer, format).Document;
            var paragraph = (Paragraph)read.Sections[0].Blocks[0];
            var readStyle = read.Styles.Single(item => item.StyleName == "Indented");

            Require(readStyle.LeftIndent?.Points == 24 && readStyle.RightIndent?.Points == 12
                && readStyle.FirstLineIndent?.Points == 6, format + ": the indented style was lost.");
            Require(paragraph.Style.LeftIndent?.Points == 0
                && paragraph.Style.RightIndent?.Points == 0
                && paragraph.Style.FirstLineIndent?.Points == 0, format + ": the explicit zero indents were lost.");
        }
    }

    private static ParagraphStyle DecoratedStyle() => ParagraphStyle.Default with
    {
        LineSpacing = LineSpacing.Multiple(1.5),
        Borders = ParagraphBorders.All(new ParagraphBorder(BorderStyle.Double, Length.FromPoints(1), Color.FromHex("0000FF"))),
        Background = Color.FromHex("EEEEEE"),
        KeepWithNext = true,
        KeepTogether = true,
        PageBreakBefore = true,
        TabStops = new[] { new TabStop(Length.FromPoints(100), TabStopAlignment.Right, TabStopLeader.Dot) },
        RightToLeft = true,
    };

    private static PortableDocument DecoratedDocument()
    {
        var section = new Section(new Block[]
        {
            new Paragraph(new TextRun("First paragraph.")),
            new Paragraph(new TextRun("Second paragraph with tab.\tValue")) { Style = DecoratedStyle() },
            new ColumnBreak(),
        })
        {
            TextColumns = 2,
        };

        return new PortableDocument(new DocumentMetadata(), section);
    }

    private static void WritesDocxParagraphProperties()
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(DecoratedDocument(), buffer).GetAwaiter().GetResult();

        var body = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(body.Contains("w:line=\"360\" w:lineRule=\"auto\"", StringComparison.Ordinal), "DOCX: line height 1.5×.");
        Require(body.Contains("<w:pBdr>", StringComparison.Ordinal) && body.Contains("w:top w:val=\"double\"", StringComparison.Ordinal), "DOCX: paragraph borders.");
        Require(body.Contains("w:fill=\"EEEEEE\"", StringComparison.Ordinal), "DOCX: paragraph shading.");
        Require(body.Contains("<w:keepNext", StringComparison.Ordinal), "DOCX: keepNext.");
        Require(body.Contains("<w:keepLines", StringComparison.Ordinal), "DOCX: keepLines.");
        Require(body.Contains("<w:pageBreakBefore", StringComparison.Ordinal), "DOCX: pageBreakBefore.");
        Require(body.Contains("<w:tabs>", StringComparison.Ordinal) && body.Contains("w:leader=\"dot\"", StringComparison.Ordinal), "DOCX: tab stop.");
        Require(body.Contains("w:tab w:val=\"right\" w:pos=\"2000\"", StringComparison.Ordinal), "DOCX: Tab-Position (100 pt = 2000).");
        Require(body.Contains("<w:bidi", StringComparison.Ordinal), "DOCX: bidi.");
        Require(body.Contains("w:cols w:num=\"2\"", StringComparison.Ordinal), "DOCX: text columns.");
        Require(body.Contains("w:br w:type=\"column\"", StringComparison.Ordinal), "DOCX: column break.");
    }

    private static void WritesOdtParagraphProperties()
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(DecoratedDocument(), buffer).GetAwaiter().GetResult();
        var package = buffer.ToArray();

        var content = ReadZipEntry(package, "content.xml");
        Require(content.Contains("fo:line-height=\"150%\"", StringComparison.Ordinal), "ODT: line height 150 %.");
        Require(content.Contains("fo:border-top=\"1pt double #0000FF\"", StringComparison.Ordinal), "ODT: paragraph borders.");
        Require(content.Contains("fo:background-color=\"#EEEEEE\"", StringComparison.Ordinal), "ODT: paragraph shading.");
        Require(content.Contains("fo:keep-with-next=\"always\"", StringComparison.Ordinal), "ODT: keep-with-next.");
        Require(content.Contains("fo:keep-together=\"always\"", StringComparison.Ordinal), "ODT: keep-together.");
        Require(content.Contains("fo:break-before=\"page\"", StringComparison.Ordinal), "ODT: break-before.");
        Require(content.Contains("style:tab-stops", StringComparison.Ordinal) && content.Contains("style:leader-text=\".\"", StringComparison.Ordinal), "ODT: tab stop.");
        Require(content.Contains("style:tab-stop style:position=\"100pt\"", StringComparison.Ordinal), "ODT: Tab-Position.");
        Require(content.Contains("style:writing-mode=\"rl-tb\"", StringComparison.Ordinal), "ODT: RTL-Richtung.");
        Require(content.Contains("text:br text:type=\"column\"", StringComparison.Ordinal), "ODT: column break.");

        var styles = ReadZipEntry(package, "styles.xml");
        Require(styles.Contains("fo:column-count=\"2\"", StringComparison.Ordinal), "ODT: text columns.");
    }

    private static void WritesPdfParagraphEffects()
    {
        using var buffer = new MemoryStream();
        var result = new PdfDocumentWriter()
            .WriteAsync(DecoratedDocument(), buffer, new PdfWriteOptions { CollectMetrics = true })
            .GetAwaiter().GetResult();

        Require(result.Metrics is not null && result.Metrics.Pages >= 2, "PDF: pageBreakBefore starts a new page.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("multi-column", StringComparison.Ordinal)), "PDF: multi-column is rendered.");

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains(" re\n", StringComparison.Ordinal) && content.Contains("\nf\n", StringComparison.Ordinal), "PDF: paragraph background.");
        Require(content.Contains(" m\n", StringComparison.Ordinal) && content.Contains(" l\n", StringComparison.Ordinal), "PDF: paragraph borders (lines).");
    }

    private static void RejectsInvalidValues()
    {
        Throws<ArgumentOutOfRangeException>(() => LineSpacing.Multiple(0));
        Throws<ArgumentOutOfRangeException>(() => LineSpacing.Exactly(Length.Zero));
        Throws<ArgumentOutOfRangeException>(() => new ParagraphBorder((BorderStyle)9));
        Throws<ArgumentOutOfRangeException>(() => new ParagraphBorder(BorderStyle.Single, Length.Zero));
        Throws<ArgumentOutOfRangeException>(() => new TabStop(Length.FromPoints(-1)));
        Throws<ArgumentOutOfRangeException>(() => new TabStop(Length.FromPoints(10), (TabStopAlignment)9));
        Throws<ArgumentOutOfRangeException>(() => new TabStop(Length.FromPoints(10), TabStopAlignment.Left, (TabStopLeader)9));
        Throws<ArgumentOutOfRangeException>(() => new Section(new Paragraph(new TextRun("x"))) { TextColumns = 0 });
        Throws<ArgumentOutOfRangeException>(() => new Section(new Paragraph(new TextRun("x"))) { TextColumns = 13 });
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
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

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name} was not thrown.");
    }

    /// <summary>The first line wraps at the width left after its indent and never runs past the right margin.</summary>
    private static void WrapsFirstLineAtItsOwnWidth()
    {
        var text = string.Join(" ", Enumerable.Repeat("Wort", 200));
        int FirstLineLength(double indent, double left = 0)
        {
            var document = PortableDocument.Create()
                .Paragraph(p => p.Style(new ParagraphStyle { FirstLineIndent = Length.FromPoints(indent), LeftIndent = Length.FromPoints(left) }).Text(text))
                .Build();
            using var buffer = new MemoryStream();
            new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            buffer.Position = 0;
            return DocumentReader.ReadPdfPages(buffer)[0].Split('\n')[0].Length;
        }

        var plain = FirstLineLength(0);
        Require(FirstLineLength(72) < plain, "PDF: a first-line indent shortens the first line.");
        Require(FirstLineLength(-36, left: 36) > FirstLineLength(0, left: 36), "PDF: a hanging first line holds more text.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
