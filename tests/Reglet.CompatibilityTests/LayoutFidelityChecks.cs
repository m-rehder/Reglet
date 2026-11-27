using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// what the PDF block renderer and the DOCX package builder put on the page or into the package:
/// content that crosses a page, table geometry, line breaks and references between parts.
/// </summary>
internal static class LayoutFidelityChecks
{
    private static readonly Color Red = new(255, 0, 0);
    private static readonly Color Yellow = new(255, 255, 0);
    private static readonly string Long = string.Join(' ', Enumerable.Repeat("Lorem ipsum dolor sit amet consectetur", 40));

    internal static int Run()
    {
        var failures = new List<string>();
        foreach (var (name, check) in new (string, Action)[]
        {
            ("marked content across pages", BalancesMarkedContentAcrossPages),
            ("background and border across pages", KeepsBackgroundAndBorderOnEachPage),
            ("required row height", MovesRowWithRequiredHeight),
            ("exact row taller than a page", CutsExactRowAtPageEnd),
            ("automatic row height", UsesHeightOfAutomaticRow),
            ("cell padding", CountsCellPaddingInRowHeight),
            ("highlight", DrawsHighlightBehindText),
            ("cell text below top border", KeepsCellTextBelowTopBorder),
            ("hidden note text", HidesHiddenTextInNotes),
            ("soft hyphen", BreaksAtLastBreakOpportunity),
            ("soft hyphen overflow", KeepsRestAfterHyphenInsideLine),
            ("no-break space", KeepsNoBreakSpaceTogether),
            ("nested list numbers", RestartsDeeperListLevel),
            ("first line width", SplitsLongWordForFirstLine),
            ("cell paragraph spacing", AppliesSpacingInCells),
            ("row span height", SharesRowSpanHeight),
            ("cell image", CropsAndTagsCellImage),
            ("area border", UsesAreaBorderWithoutTableBorder),
            ("decimal tab", AlignsDecimalTab),
            ("double cell border", DrawsDoubleCellBorder),
            ("bookmark in a small line", IgnoresBookmarkInLineHeight),
            ("all caps warning", WarnsAboutUnencodableCaps),
            ("tab origin", CountsTabsFromMargin),
            ("list image", IndentsListImage),
            ("cell behind a row span", KeepsCellBehindRowSpan),
            ("note hyperlinks", WritesNoteHyperlinkRelationships),
            ("media names", KeepsSourceImageWhenInserting),
            ("settings order", KeepsSettingsOrder),
            ("hanging indent", WritesHangingIndent),
            ("merged column span", ContinuesColumnSpanInMergedCell),
            ("bookmark names", KeepsBookmarkReferencesConsistent),
            ("field arguments", QuotesFieldArguments),
            ("invalid names", ReportsInvalidNames),
            ("source numbering ids", ToleratesNonNumericSourceIds),
            ("style ids", WritesEachStyleIdOnce),
            ("even pages", KeepsDefaultBandOnEvenPages),
            ("list paragraphs", IndentsListContinuation),
            ("table look", WritesTableLookOfModel),
            ("comments", KeepsCommentRelationship),
            ("odt tab origin", ConvertsOdtTabs),
            ("odt cell grid", KeepsGridOfStyledOdtCell),
        })
        {
            try
            {
                check();
            }
            catch (Exception exception)
            {
                failures.Add(name + ": " + exception.Message);
            }
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine("Layout fidelity: " + failure);
        }

        Console.WriteLine(failures.Count == 0 ? "Layout fidelity: all checks passed." : $"Layout fidelity: {failures.Count} failed.");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void BalancesMarkedContentAcrossPages()
    {
        foreach (var content in PageContents(Pdf(Filler(600), new Paragraph(new TextRun(Long)))))
        {
            var depth = 0;
            foreach (Match match in Regex.Matches(content, @"\b(BDC|BMC|EMC)\b"))
            {
                depth += match.Value == "EMC" ? -1 : 1;
                Require(depth >= 0, "a page closes marked content it did not open.");
            }

            Require(depth == 0, "a page leaves marked content open.");
        }
    }

    private static void KeepsBackgroundAndBorderOnEachPage()
    {
        var edge = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(1), Red);
        var pages = PageContents(Pdf(Filler(600), new Paragraph(new TextRun(Long))
        {
            Style = new ParagraphStyle { Background = Yellow, Borders = new ParagraphBorders { Top = edge, Bottom = edge, Left = edge, Right = edge } },
        }));
        Require(pages.Count == 2, "the paragraph runs over two pages.");
        foreach (var page in pages)
        {
            Require(page.Contains("1 1 0 rg", StringComparison.Ordinal), "a page lacks the background.");
            Require(page.Contains("1 0 0 RG", StringComparison.Ordinal), "a page lacks the border.");
            foreach (Match rectangle in Regex.Matches(page, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re"))
            {
                Require(double.Parse(rectangle.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) >= 0, "a rectangle runs off the page.");
            }
        }
    }

    private static void MovesRowWithRequiredHeight()
    {
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(Pdf(
            Filler(640),
            new TableBlock([new TableRow(new TableCell("Tall")) { Height = Length.FromPoints(150), HeightRule = TableRowHeightRule.AtLeast }]))));
        Require(pages.Count == 2 && pages[1].Contains("Tall", StringComparison.Ordinal), "the row moves to the next page.");
    }

    private static void CutsExactRowAtPageEnd()
    {
        var document = Document(new TableBlock([new TableRow(new TableCell("Exact")) { Height = Length.FromPoints(2000), HeightRule = TableRowHeightRule.Exactly }]));
        using var output = new MemoryStream();
        var result = DocumentWriter.WriteAsync(document, output, DocumentFormat.Pdf).GetAwaiter().GetResult();
        Require(result.Warnings.Any(warning => warning.ToString().Contains("cut off", StringComparison.Ordinal)), "the cut is reported.");
        foreach (var page in PageContents(output.ToArray()))
        {
            foreach (Match line in Regex.Matches(page, @"(-?[\d.]+) (-?[\d.]+) [lm]\b"))
            {
                Require(double.Parse(line.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) >= 0, "the row runs off the page.");
            }
        }
    }

    private static void UsesHeightOfAutomaticRow()
    {
        var text = Words(Pdf(new TableBlock([new TableRow(new TableCell("Auto")) { Height = Length.FromPoints(100) }, new TableRow(new TableCell("Next"))])));
        Require(Y(text, "Auto") - Y(text, "Next") >= 99, "the height applies as a minimum.");
    }

    private static void CountsCellPaddingInRowHeight()
    {
        var text = Words(Pdf(new TableBlock([new TableRow(new TableCell("First") { Padding = Length.FromPoints(20) }), new TableRow(new TableCell("Next"))])));
        Require(Y(text, "First") - Y(text, "Next") > 30, "the next row starts below the padding.");
    }

    private static void DrawsHighlightBehindText()
    {
        var page = PageContents(Pdf(new Paragraph(new TextRun("Marked") { Highlight = Yellow, FontSize = Length.FromPoints(24) }))).Single();
        var rectangle = Regex.Match(page, @"1 1 0 rg\s+(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");
        Require(rectangle.Success, "the highlight is drawn.");
        var bottom = Number(rectangle.Groups[2].Value);
        var top = bottom + Number(rectangle.Groups[4].Value);
        var baseline = Number(Regex.Match(page, @"(-?[\d.]+) (-?[\d.]+) Td").Groups[2].Value);
        Require(bottom < baseline && top > baseline + 12, "the highlight surrounds the glyphs.");
    }

    private static void KeepsCellTextBelowTopBorder()
    {
        // Helvetica capitals with an umlaut reach about 0.9 em; the AFM ascender (0.718) let them cross the border.
        var page = PageContents(Pdf(new TableBlock([new TableRow(new TableCell("Ångström"))]))).Single();
        var cell = Regex.Match(page, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");
        var top = Number(cell.Groups[2].Value) + Number(cell.Groups[4].Value);
        var baseline = Number(Regex.Match(page, @"(-?[\d.]+) (-?[\d.]+) Td").Groups[2].Value);
        Require(top - baseline >= 0.9 * 11, $"the text starts an ascender below the border ({top - baseline:0.##} pt).");
    }

    private static void HidesHiddenTextInNotes()
    {
        var bytes = Pdf(new Paragraph(new Inline[]
        {
            new TextRun("Text"),
            new Footnote([new Paragraph(new Inline[] { new TextRun("visible "), new TextRun("SECRET") { Hidden = true } })]),
        }));
        Require(!string.Concat(DocumentReader.ReadPdfPages(new MemoryStream(bytes))).Contains("SECRET", StringComparison.Ordinal), "hidden text stays hidden.");
    }

    private static void BreaksAtLastBreakOpportunity()
    {
        var text = Words(Pdf(new Paragraph(new TextRun("house\u00ADhold " + string.Join(' ', Enumerable.Repeat("short", 40))))));
        Require(Y(text, "house") == Y(text, "hold"), "the line is filled before it breaks.");
    }

    private static void KeepsRestAfterHyphenInsideLine()
    {
        var longPart = new string('B', 59);
        var spaced = Words(Pdf(new Paragraph(new TextRun("A\u00AD" + longPart + " " + new string('C', 20)))));
        Require(Y(spaced, longPart) > Y(spaced, new string('C', 20)), "a word after a space wraps instead of an earlier hyphen.");

        var joined = Words(Pdf(new Paragraph(new Inline[] { new TextRun("A\u00AD" + longPart), new TextRun(new string('D', 20)) })));
        Require(Y(joined, longPart) > Y(joined, new string('D', 20)), "what does not fit after the hyphen break wraps again.");
    }

    private static void KeepsNoBreakSpaceTogether()
    {
        var pages = Pdf(new Paragraph(new TextRun(string.Join(' ', Enumerable.Repeat("word", 15)) + " abcdefgh 10 000 km end")));
        Require(Words(pages).All(word => word.Text != "km"), "the number and its unit stay together.");
    }

    private static void RestartsDeeperListLevel()
    {
        var bytes = Pdf(new ListBlock("Num", [
            Item("one", new ListBlock("Num", [Item("a1"), Item("a2")])),
            Item("two", new ListBlock("Num", [Item("b1")])),
        ]));
        var text = string.Concat(DocumentReader.ReadPdfPages(new MemoryStream(bytes)));
        Require(!text.Contains("c.", StringComparison.Ordinal), "the deeper level starts again.");
    }

    private static void SplitsLongWordForFirstLine()
    {
        var words = Words(Pdf(new Paragraph(new TextRun(new string('W', 44))) { Style = new ParagraphStyle { FirstLineIndent = Length.FromPoints(100) } }));
        Require(words.All(word => word.X + (word.Text.Length * 10.4) <= 530), "the first line stays inside the text area.");
    }

    private static void AppliesSpacingInCells()
    {
        var text = Words(Pdf(new TableBlock([new TableRow(new TableCell(new Block[]
        {
            new Paragraph(new TextRun("One")) { Style = new ParagraphStyle { SpaceAfter = Length.FromPoints(30) } },
            new Paragraph(new TextRun("Two")),
        }))])));
        Require(Y(text, "One") - Y(text, "Two") >= 40, "the space after applies.");
    }

    private static void SharesRowSpanHeight()
    {
        var text = Words(Pdf(new TableBlock([
            new TableRow(new TableCell(new Block[] { T("L1"), T("L2"), T("L3") }) { RowSpan = 3 }, new TableCell("r1")),
            new TableRow(new TableCell("r2")),
            new TableRow(new TableCell("r3")),
        ])));
        Require(Y(text, "r1") - Y(text, "r2") < 16, "the rows share the height of the cell.");
    }

    private static void CropsAndTagsCellImage()
    {
        var bytes = Pdf(new TableBlock([new TableRow(new TableCell(new ImageBlock(Png(255, 0, 0), ImageMediaType.Png)
        {
            Width = Length.FromPoints(100),
            Height = Length.FromPoints(50),
            Crop = new ImageCrop(0, 0, 0.5, 0),
            AltText = "CellPicture",
        }))]));
        var page = PageContents(bytes).Single();
        Require(Regex.IsMatch(page, @"\bW n\b"), "the crop clips the image.");
        Require(Regex.IsMatch(page, @"/Figure <</MCID"), "the image is a figure.");
        Require(AllText(bytes).Contains("CellPicture", StringComparison.Ordinal), "the alternative text is kept.");
    }

    private static void UsesAreaBorderWithoutTableBorder()
    {
        var page = PageContents(Pdf(new TableBlock([new TableRow(new TableCell("Head")) { IsHeader = true }, new TableRow(new TableCell("Body"))])
        {
            Style = new TableStyle("S") { HeaderRow = new TableStyleArea { Border = new ParagraphBorder(BorderStyle.Single, Length.FromPoints(3), Red) } },
        })).Single();
        Require(page.Contains("1 0 0 RG", StringComparison.Ordinal), "the area border is drawn.");
    }

    private static void AlignsDecimalTab()
    {
        var style = new ParagraphStyle { TabStops = [new TabStop(Length.FromPoints(200), TabStopAlignment.Decimal)] };
        var text = Words(Pdf(new Paragraph(new TextRun("\t123.45")) { Style = style }, new Paragraph(new TextRun("\t1.5")) { Style = style }));
        var digit = 11 * 0.556;
        Require(Math.Abs(text.Single(word => word.Text == "1.5").X - text.Single(word => word.Text == "123.45").X - (2 * digit)) < 0.5, "the decimal points line up.");
    }

    private static void DrawsDoubleCellBorder()
    {
        static int Lines(BorderStyle style) => Regex.Matches(PageContents(Pdf(new TableBlock([new TableRow(new TableCell("x")
        {
            Borders = new ParagraphBorders { Top = new ParagraphBorder(style, Length.FromPoints(1), Red) },
        })]))).Single(), @"\bl\b").Count;
        Require(Lines(BorderStyle.Double) == Lines(BorderStyle.Single) + 1, "a double border has a second line.");
    }

    private static void IgnoresBookmarkInLineHeight()
    {
        var size = Length.FromPoints(6);
        var style = new ParagraphStyle { SpaceAfter = Length.Zero };
        var text = Words(Pdf(
            new Paragraph(new TextRun("aa") { FontSize = size }) { Style = style },
            new Paragraph(new Inline[] { new Bookmark("b"), new TextRun("bb") { FontSize = size } }) { Style = style },
            new Paragraph(new TextRun("cc") { FontSize = size })));
        Require(Math.Abs((Y(text, "aa") - Y(text, "bb")) - (Y(text, "bb") - Y(text, "cc"))) < 0.01, "a bookmark has no height.");
    }

    private static void WarnsAboutUnencodableCaps()
    {
        using var output = new MemoryStream();
        var result = DocumentWriter.WriteAsync(Document(new Paragraph(new TextRun("5 µm") { AllCaps = true })), output, DocumentFormat.Pdf).GetAwaiter().GetResult();
        Require(result.Warnings.Count > 0, "the replaced character is reported.");
    }

    private static void CountsTabsFromMargin()
    {
        var text = Words(Pdf(new Paragraph(new TextRun("A\tB"))
        {
            Style = new ParagraphStyle { LeftIndent = Length.FromPoints(72), TabStops = [new TabStop(Length.FromPoints(144))] },
        }));
        Require(Math.Abs(text.Single(word => word.Text == "B").X - 216) < 0.1, "the stop counts from the margin (72 + 144).");
    }

    private static void IndentsListImage()
    {
        var page = PageContents(Pdf(new ListBlock("Num", [new ListItem([T("Item"), new ImageBlock(Png(255, 0, 0), ImageMediaType.Png) { Width = Length.FromPoints(50), Height = Length.FromPoints(50) }])]))).Single();
        Require(Regex.IsMatch(page, @"\b0 0 50 108 "), "the image lines up with the text of the item.");
    }

    private static void KeepsCellBehindRowSpan()
    {
        var table = new TableBlock([new TableRow(new TableCell("A") { RowSpan = 2 }), new TableRow(new TableCell("B"), new TableCell("KEPT"))]);
        Require(string.Concat(DocumentReader.ReadPdfPages(new MemoryStream(Pdf(table)))).Contains("KEPT", StringComparison.Ordinal), "the PDF has the cell.");
        Require(Part(Docx(Document(table)), "word/document.xml").Contains("KEPT", StringComparison.Ordinal), "the DOCX has the cell.");
        using var odt = new MemoryStream();
        DocumentWriter.WriteAsync(Document(table), odt, DocumentFormat.Odt).GetAwaiter().GetResult();
        Require(Part(odt.ToArray(), "content.xml").Contains("KEPT", StringComparison.Ordinal), "the ODT has the cell.");
    }

    private static void WritesNoteHyperlinkRelationships()
    {
        var bytes = Docx(Document(new Paragraph(new Inline[]
        {
            new TextRun("T"),
            new Footnote([new Paragraph(new Inline[] { new Hyperlink("https://example.org/x", "Link") })]),
        })));
        var id = Regex.Match(Part(bytes, "word/footnotes.xml"), "r:id=\"(rId\\d+)\"").Groups[1].Value;
        Require(Regex.IsMatch(Part(bytes, "word/_rels/footnotes.xml.rels"), $"Id=\"{id}\""), "the notes part has the relationship.");
        Require(!Part(bytes, "word/_rels/document.xml.rels").Contains("example.org", StringComparison.Ordinal), "the body has none.");
    }

    private static void KeepsSourceImageWhenInserting()
    {
        var red = Png(255, 0, 0);
        var blue = Png(0, 0, 255);
        var read = Read(Docx(Document(T("a"), new ImageBlock(red, ImageMediaType.Png) { Width = Length.FromPoints(50), Height = Length.FromPoints(50) }, T("b"))));
        var changed = read with
        {
            Sections = [read.Sections[0] with { Blocks = [new ImageBlock(blue, ImageMediaType.Png) { Width = Length.FromPoints(50), Height = Length.FromPoints(50) }, .. read.Sections[0].Blocks] }],
        };
        var bytes = Docx(changed);
        var relationships = XDocument.Parse(Part(bytes, "word/_rels/document.xml.rels")).Root!.Elements()
            .ToDictionary(element => element.Attribute("Id")!.Value, element => element.Attribute("Target")!.Value);
        var shown = Regex.Matches(Part(bytes, "word/document.xml"), "r:embed=\"(rId\\d+)\"")
            .Select(match => Bytes(bytes, "word/" + relationships[match.Groups[1].Value])).ToList();
        Require(shown.Count == 2 && shown[0].SequenceEqual(blue) && shown[1].SequenceEqual(red), "both images keep their own content.");
    }

    private static void KeepsSettingsOrder()
    {
        var read = Read(Docx(Document(T("a"))));
        var changed = read with { Sections = [read.Sections[0] with { EvenHeader = new HeaderFooter([T("even")]) }] };
        var names = XDocument.Parse(Part(Docx(changed, new DocxWriteOptions { Protection = DocxProtection.ReadOnly }), "word/settings.xml")).Root!
            .Elements().Select(element => element.Name.LocalName).ToList();
        Require(names.IndexOf("documentProtection") < names.IndexOf("evenAndOddHeaders") && names.IndexOf("evenAndOddHeaders") < names.IndexOf("compat"), "the elements follow CT_Settings.");
    }

    private static void WritesHangingIndent()
    {
        var bytes = Docx(Document(new Paragraph(new TextRun("x")) { Style = new ParagraphStyle { LeftIndent = Length.FromPoints(40), FirstLineIndent = Length.FromPoints(-20) } }));
        Require(Part(bytes, "word/document.xml").Contains("w:hanging=\"400\"", StringComparison.Ordinal), "a negative first line is a hanging indent.");
        Require(((Paragraph)Read(bytes).Sections[0].Blocks[0]).Style.FirstLineIndent == Length.FromPoints(-20), "it reads back.");
    }

    private static void ContinuesColumnSpanInMergedCell()
    {
        var xml = Part(Docx(Document(new TableBlock([
            new TableRow(new TableCell("AB") { ColumnSpan = 2, RowSpan = 2 }, new TableCell("C1")),
            new TableRow(new TableCell("C2")),
        ]))), "word/document.xml");
        var second = xml[xml.IndexOf("</w:tr>", StringComparison.Ordinal)..];
        Require(Regex.Matches(second, "<w:tc>").Count == 2 && second.Contains("<w:gridSpan w:val=\"2\"", StringComparison.Ordinal), "the continuation spans both columns.");
    }

    private static void KeepsBookmarkReferencesConsistent()
    {
        var xml = Part(Docx(Document(
            new Paragraph(new Inline[] { new Bookmark("My Target"), new Bookmark("My-Target"), new TextRun("Z") }),
            new Paragraph(new Inline[] { new Hyperlink("#My Target", "L"), new PageRefField("My-Target") }))), "word/document.xml");
        var names = Regex.Matches(xml, "w:bookmarkStart [^>]*w:name=\"([^\"]*)\"").Select(match => match.Groups[1].Value).ToList();
        Require(names.Count == 2 && names[0] != names[1], "distinct bookmarks stay distinct.");
        Require(xml.Contains($"w:anchor=\"{names[0]}\"", StringComparison.Ordinal), "the link names the written bookmark.");
        Require(xml.Contains($"PAGEREF {names[1]} ", StringComparison.Ordinal), "the page reference names the written bookmark.");
    }

    private static void QuotesFieldArguments()
    {
        var read = Read(Docx(Document(new Paragraph(new Inline[] { new DocPropertyField("My Value"), new DocVariableField("Say \"Hi\" now") }))));
        var inlines = ((Paragraph)read.Sections[0].Blocks[0]).Inlines;
        Require(inlines.OfType<DocPropertyField>().Single().Name == "My Value", "a property name with spaces reads back.");
        Require(inlines.OfType<DocVariableField>().Single().Name == "Say \"Hi\" now", "quotes in a name read back.");
    }

    private static void ReportsInvalidNames()
    {
        foreach (var document in new[]
        {
            Document(new Paragraph(new Inline[] { new Field("AB\u0001") })),
            Document(new Paragraph(new TextRun("x")) { Style = new ParagraphStyle { StyleName = "S\u0001" } }),
            Document(new TableBlock([new TableRow(new TableCell("x"))]) { Style = new TableStyle("T\u0001") }),
        })
        {
            foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
            {
                try
                {
                    using var output = new MemoryStream();
                    DocumentWriter.WriteAsync(document, output, format).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    Require(exception is IRegletError, $"{format}: {exception.GetType().Name} is a library error.");
                }
            }
        }
    }

    private static void ToleratesNonNumericSourceIds()
    {
        var source = Patch(Docx(Document(new ListBlock("Num", [Item("x")]), T("a"))), "word/numbering.xml", xml => xml.Replace("w:numId=\"1\"", "w:numId=\"abc\"", StringComparison.Ordinal));
        var read = Read(source);
        Docx(read with { Sections = [read.Sections[0] with { Blocks = [.. read.Sections[0].Blocks, T("new")] }] });
    }

    private static void WritesEachStyleIdOnce()
    {
        var document = new PortableDocument(new DocumentMetadata(), [new Section([new Heading(HeadingLevel.Level1, new TextRun("H"))])])
        {
            Styles = [new ParagraphStyle { StyleName = "Heading1", Bold = true }, new ParagraphStyle { StyleName = "Normal" }],
            CharacterStyles = [new CharacterStyle("FootnoteReference")],
        };
        var xml = Part(Docx(document), "word/styles.xml");
        var ids = Regex.Matches(xml, "w:styleId=\"([^\"]*)\"").Select(match => match.Groups[1].Value).ToList();
        Require(ids.Count == ids.Distinct(StringComparer.Ordinal).Count(), "every style id exists once.");
        Require(xml.Contains("w:val=\"heading 1\"", StringComparison.Ordinal), "the own heading style stays a heading.");
    }

    private static void KeepsDefaultBandOnEvenPages()
    {
        var first = new Section([T("one")]) { Header = new HeaderFooter([T("DEFAULT")]) };
        var second = new Section([T("two")]) { Header = new HeaderFooter([T("OTHER")]), EvenHeader = new HeaderFooter([T("EVEN")]) };
        var xml = Part(Docx(new PortableDocument(new DocumentMetadata(), [first, second])), "word/document.xml");
        var firstSection = xml[..xml.IndexOf("</w:sectPr>", StringComparison.Ordinal)];
        Require(firstSection.Contains("w:type=\"even\"", StringComparison.Ordinal), "the first section has a band for even pages.");
    }

    private static void IndentsListContinuation()
    {
        var xml = Part(Docx(Document(new ListBlock("Num", [Item("Item", T("Second"))]))), "word/document.xml");
        Require(Regex.IsMatch(xml, "<w:ind w:left=\"720\" />.*?Second"), "a further paragraph lines up with the text.");
    }

    private static void WritesTableLookOfModel()
    {
        var xml = Part(Docx(Document(new TableBlock([new TableRow(new TableCell("a")), new TableRow(new TableCell("b"))])
        {
            Style = new TableStyle("S") { LastRow = new TableStyleArea { Bold = true } },
        })), "word/document.xml");
        Require(xml.Contains("w:firstRow=\"0\"", StringComparison.Ordinal) && xml.Contains("w:lastRow=\"1\"", StringComparison.Ordinal), "the look follows the model.");
    }

    private static void KeepsCommentRelationship()
    {
        const string Type = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments";
        var source = Docx(Document(T("a"), T("b")));
        source = Patch(source, "word/_rels/document.xml.rels", xml => xml.Replace("</Relationships>", $"<Relationship Id=\"rId99\" Type=\"{Type}\" Target=\"comments.xml\"/></Relationships>", StringComparison.Ordinal));
        source = Patch(source, "[Content_Types].xml", xml => xml.Replace("</Types>", "<Override PartName=\"/word/comments.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml\"/></Types>", StringComparison.Ordinal));
        source = Patch(source, "word/document.xml", xml => xml.Replace("<w:t>a</w:t></w:r>", "<w:t>a</w:t></w:r><w:commentRangeStart w:id=\"7\"/><w:commentRangeEnd w:id=\"7\"/><w:r><w:commentReference w:id=\"7\"/></w:r>", StringComparison.Ordinal), ("word/comments.xml", "<w:comments xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>"));
        var read = Read(source);
        var bytes = Docx(read with { Sections = [read.Sections[0] with { Blocks = [.. read.Sections[0].Blocks, T("new")] }] });
        Require(Part(bytes, "word/document.xml").Contains("commentReference", StringComparison.Ordinal), "the kept block keeps its anchor.");
        Require(Part(bytes, "word/_rels/document.xml.rels").Contains(Type, StringComparison.Ordinal), "the anchor reaches its comments.");
    }

    private static void ConvertsOdtTabs()
    {
        var document = Document(new Paragraph(new TextRun("A\tB"))
        {
            Style = new ParagraphStyle { LeftIndent = Length.FromPoints(72), TabStops = [new TabStop(Length.FromPoints(216))] },
        });
        using var output = new MemoryStream();
        DocumentWriter.WriteAsync(document, output, DocumentFormat.Odt).GetAwaiter().GetResult();
        Require(Regex.IsMatch(Part(output.ToArray(), "content.xml"), "style:position=\"144"), "ODF counts from the indent.");
        output.Position = 0;
        var read = (Paragraph)DocumentReader.Read(output, DocumentFormat.Odt).Document.Sections[0].Blocks[0];
        Require(Math.Abs(read.Style.TabStops![0].Position.Points - 216) < 0.01, "the model counts from the margin.");
    }

    private static void KeepsGridOfStyledOdtCell()
    {
        using var output = new MemoryStream();
        DocumentWriter.WriteAsync(Document(new TableBlock([new TableRow(new TableCell("x") { Background = Yellow })])), output, DocumentFormat.Odt).GetAwaiter().GetResult();
        var xml = Part(output.ToArray(), "content.xml");
        var style = Regex.Match(xml, "<style:style style:name=\"TC\\d+\".*?</style:style>", RegexOptions.Singleline).Value;
        Require(style.Contains("fo:border-left", StringComparison.Ordinal) && style.Contains("fo:padding-left", StringComparison.Ordinal), "a styled cell keeps the grid and the padding.");
    }

    private static Paragraph T(string text) => new(new TextRun(text));

    private static ListItem Item(string text, params Block[] more) => new(new Block[] { T(text) }.Concat(more));

    private static Paragraph Filler(double after) => new(new TextRun("Filler")) { Style = new ParagraphStyle { SpaceAfter = Length.FromPoints(after) } };

    private static PortableDocument Document(params Block[] blocks) => new(new DocumentMetadata(), [new Section(blocks)])
    {
        Numbering = [new NumberingDefinition("Num", [new NumberingLevel { Kind = ListKind.Number }, new NumberingLevel { Kind = ListKind.Number, Format = ListNumberFormat.LowerLetter }])],
    };

    private static byte[] Pdf(params Block[] blocks)
    {
        using var output = new MemoryStream();
        DocumentWriter.WriteAsync(Document(blocks), output, DocumentFormat.Pdf).GetAwaiter().GetResult();
        return output.ToArray();
    }

    private static byte[] Docx(PortableDocument document, DocxWriteOptions? options = null)
    {
        using var output = new MemoryStream();
        DocumentWriter.WriteAsync(document, output, DocumentFormat.Docx, options ?? new DocxWriteOptions()).GetAwaiter().GetResult();
        return output.ToArray();
    }

    private static PortableDocument Read(byte[] docx) => DocumentReader.Read(new MemoryStream(docx), DocumentFormat.Docx).Document;

    private static string Part(byte[] package, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(package));
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static byte[] Bytes(byte[] package, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(package));
        using var stream = archive.GetEntry(name)!.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Patch(byte[] package, string name, Func<string, string> change, params (string Name, string Text)[] added)
    {
        var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package)))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = entry.FullName == name ? Encoding.UTF8.GetBytes(change(Encoding.UTF8.GetString(buffer.ToArray()))) : buffer.ToArray();
                using var written = target.CreateEntry(entry.FullName).Open();
                written.Write(bytes);
            }

            foreach (var (addedName, text) in added)
            {
                using var written = target.CreateEntry(addedName).Open();
                written.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        return output.ToArray();
    }

    /// <summary>The decoded streams of a PDF that draw a page, in file order.</summary>
    private static List<string> PageContents(byte[] pdf) => Streams(pdf).Where(content => Regex.IsMatch(content, @"\b(BT|re|cm)\b") && !content.Contains("/CIDInit", StringComparison.Ordinal)).ToList();

    private static string AllText(byte[] pdf) => Encoding.Latin1.GetString(pdf) + string.Concat(Streams(pdf));

    private static IEnumerable<string> Streams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        foreach (Match match in Regex.Matches(text, "stream\r?\n"))
        {
            var start = match.Index + match.Length;
            if (match.Index >= 3 && text.AsSpan(match.Index - 3, 3).SequenceEqual("end"))
            {
                continue;
            }

            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            string decoded;
            try
            {
                using var inflate = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
                using var output = new MemoryStream();
                inflate.CopyTo(output);
                decoded = Encoding.Latin1.GetString(output.ToArray());
            }
            catch (InvalidDataException)
            {
                decoded = text[start..end];
            }

            yield return decoded;
        }
    }

    private sealed record Word(double X, double Y, string Text);

    private static List<Word> Words(byte[] pdf) => PageContents(pdf)
        .SelectMany(content => Regex.Matches(content, @"(-?[\d.]+) (-?[\d.]+) Td\s+\((.*?)\) Tj").Select(match => new Word(Number(match.Groups[1].Value), Number(match.Groups[2].Value), match.Groups[3].Value)))
        .ToList();

    private static double Y(List<Word> words, string text) => words.First(word => word.Text == text).Y;

    private static double Number(string text) => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    internal static byte[] Png(byte red, byte green, byte blue)
    {
        using var output = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            var name = Encoding.ASCII.GetBytes(type);
            output.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(data.Length)));
            output.Write(name);
            output.Write(data);
            var crc = 0xFFFFFFFFu;
            foreach (var value in name.Concat(data))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }

            output.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder((int)~crc)));
        }

        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write([0, red, green, blue]);
        }

        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return output.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
