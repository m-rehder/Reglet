using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>Writes <c>content.xml</c> (office:document-content with automatic styles).</summary>
internal static class OdtContentWriter
{
    public static void Write(
        OdtPackageWriter package,
        PortableDocument document,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        CancellationToken cancellationToken,
        IProgress<WriteProgress>? progress = null,
        IReadOnlyDictionary<Block, XElement>? preservedBlocks = null,
        int paragraphNameStart = 0,
        int textNameStart = 0,
        int tableNameStart = 0,
        int cellStyleStart = 0,
        int rowStyleStart = 0,
        int tableStyleStart = 0,
        int tableBreakStart = 0,
        IReadOnlyList<XElement>? preservedStyles = null,
        IReadOnlySet<Block>? coveredBlocks = null,
        OdtEmbeddedFonts? embeddedFonts = null)
    {
        var charts = OdtCharts.Collect(document);
        OdtCharts.WriteParts(package, charts, xmlBehavior);
        var objects = OdtEmbeddedObjects.Collect(document, charts.Count + 1);
        OdtEmbeddedObjects.WriteParts(package, objects);
        var equations = OdtMath.Collect(document, charts.Count + objects.Count + 1);
        OdtMath.WriteParts(package, equations);
        package.AddXmlPart("content.xml", writer => WriteDocumentContent(writer, document, images, xmlBehavior, preservedBlocks, paragraphNameStart, textNameStart, tableNameStart, cellStyleStart, rowStyleStart, tableStyleStart, tableBreakStart, preservedStyles, coveredBlocks, cancellationToken, progress, embeddedFonts, charts, objects, equations));
    }

    private static void WriteDocumentContent(
        XmlWriter writer,
        PortableDocument document,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        IReadOnlyDictionary<Block, XElement>? preservedBlocks,
        int paragraphNameStart,
        int textNameStart,
        int tableNameStart,
        int cellStyleStart,
        int rowStyleStart,
        int tableStyleStart,
        int tableBreakStart,
        IReadOnlyList<XElement>? preservedStyles,
        IReadOnlySet<Block>? coveredBlocks,
        CancellationToken cancellationToken,
        IProgress<WriteProgress>? progress,
        OdtEmbeddedFonts? embeddedFonts,
        IReadOnlyDictionary<Chart, string> charts,
        IReadOnlyDictionary<EmbeddedObject, string> objects,
        IReadOnlyDictionary<Equation, string> equations)
    {
        var textStyles = OdtAutomaticStyles.Collect(document, textNameStart);
        var paragraphStyles = OdtParagraphStyles.Collect(document, paragraphNameStart, cellStyleStart, rowStyleStart, tableStyleStart, tableBreakStart);
        paragraphStyles.CharacterStyles = document.CharacterStyles;
        paragraphStyles.BookmarkRanges = BookmarkScan.Collect(document).RangeNames;
        OdtComments.Prepare(paragraphStyles, CommentScan.Collect(document));
        paragraphStyles.Revisions = OdtRevisions.MakePlan(document);
        paragraphStyles.Images = images;
        var columnWidths = CollectColumnWidths(document);
        var numbering = new NumberingLookup(document.Numbering);
        // AJ02: the general fields without a cached result are calculated.
        var fields = FieldContext.FromMetadata(document.Metadata);

        writer.WriteStartElement("office", "document-content", OdtNamespaces.Office);
        writer.WriteAttributeString("office", "version", OdtNamespaces.Office, "1.3");

        OdtFontFaces.Write(
            writer,
            textStyles.Keys.Select(key => key.FontFamily).OfType<string>(),
            xmlBehavior,
            embeddedFonts);

        writer.WriteStartElement("office", "automatic-styles", OdtNamespaces.Office);
        WriteTextStyles(writer, textStyles, xmlBehavior);
        WriteImageStyles(writer, images);
        OdtShapes.WriteStyles(writer, document);
        WriteParagraphStyles(writer, paragraphStyles, document.Defaults.FontSize.Points);
        WriteListStyles(writer, document.Sections.SelectMany(section => ListScan.Collect(section.Blocks)), numbering);
        if (TableOfContentsScan.Contains(document) || FigureIndexScan.HasListBlock(document))
        {
            WriteTocStyles(writer);
        }

        WriteColumnStyles(writer, columnWidths);
        if (columnWidths.Count > 0)
        {
            WriteTableCellStyles(writer);
            paragraphStyles.CellStyles.Write(writer);
        }
        WritePageBreakStyleIfNeeded(writer, document);
        if (equations.Count > 0)
        {
            OdtMath.WriteAlignmentStyles(writer);
        }

        WriteSectionBreakStyles(writer, document);
        WriteTableBreakStyles(writer, document, paragraphStyles.Breaks);

        // AF12/AF13: append the preserved source paragraph/text styles (ids above the generated
        // ones), so kept blocks' source style references stay valid.
        if (preservedStyles is { Count: > 0 })
        {
            foreach (var style in preservedStyles)
            {
                style.WriteTo(writer);
            }
        }

        writer.WriteEndElement();

        writer.WriteStartElement("office", "body", OdtNamespaces.Office);
        writer.WriteStartElement("office", "text", OdtNamespaces.Office);
        var forms = OdtForms.Collect(document);
        OdtForms.WriteForms(writer, forms, xmlBehavior);
        OdtRevisions.WriteRegions(
            writer,
            paragraphStyles.Revisions,
            (target, inlines) => WriteInlines(target, inlines, textStyles, xmlBehavior, paragraphStyles, fields),
            xmlBehavior);

        // OF-C02: the sequences (SEQ fields) are declared, as LibreOffice does.
        var sequences = FieldScan.Collect(document)
            .Select(field => OdtFields.Map(field.Instruction)?.Sequence)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (sequences.Count > 0)
        {
            writer.WriteStartElement("text", "sequence-decls", OdtNamespaces.Text);
            foreach (var sequence in sequences)
            {
                writer.WriteStartElement("text", "sequence-decl", OdtNamespaces.Text);
                writer.WriteAttributeString("text", "display-outline-level", OdtNamespaces.Text, "0");
                writer.WriteAttributeString("text", "name", OdtNamespaces.Text, sequence);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        // Document variables are declared as user fields that text:user-field-get shows.
        if (document.Metadata.Variables is { Count: > 0 } variables)
        {
            writer.WriteStartElement("text", "user-field-decls", OdtNamespaces.Text);
            foreach (var (name, value) in variables.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteStartElement("text", "user-field-decl", OdtNamespaces.Text);
                writer.WriteAttributeString("office", "value-type", OdtNamespaces.Office, "string");
                writer.WriteAttributeString("office", "string-value", OdtNamespaces.Office, XmlText.Clean(value, xmlBehavior));
                writer.WriteAttributeString("text", "name", OdtNamespaces.Text, XmlText.Clean(name, xmlBehavior));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        var state = new OdtBlockState(tableNameStart) { Forms = forms, Charts = charts, Objects = objects, Equations = equations };
        for (var sectionIndex = 0; sectionIndex < document.Sections.Count; sectionIndex++)
        {
            var section = document.Sections[sectionIndex];
            var number = sectionIndex + 1;

            writer.WriteStartElement("text", "section", OdtNamespaces.Text);
            writer.WriteAttributeString(
                "text", "name", OdtNamespaces.Text, "Section" + number.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, OdtSectionNames.SectionStyle(number));
            var availableWidth = ContentWidth(section);
            cancellationToken.ThrowIfCancellationRequested();

            // A continuous section continues on the same page: no break paragraph.
            var continuous = section.StartType == SectionStartType.Continuous;
            if (sectionIndex > 0 && !continuous && !paragraphStyles.Breaks.IsSectionMerged(number))
            {
                writer.WriteStartElement("text", "p", OdtNamespaces.Text);
                writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, OdtSectionNames.BreakStyle(number));
                writer.WriteEndElement();
            }

            foreach (var block in section.Blocks)
            {
                // A source element that produced several model blocks (a paragraph and the images
                // hoisted from its frames) is written once; the other blocks are covered by the
                // preserved XML (AF12).
                if (coveredBlocks is not null && coveredBlocks.Contains(block))
                {
                    continue;
                }

                // AF12: re-emit an unmodified, style-free source block verbatim (keeps unmodelled
                // XML); everything else is generated from the model.
                if (preservedBlocks is not null && preservedBlocks.TryGetValue(block, out var sourceElement))
                {
                    sourceElement.WriteTo(writer);
                }
                else
                {
                    WriteBlock(writer, block, textStyles, paragraphStyles, images, availableWidth, xmlBehavior, state, numbering, fields);
                }
            }

            progress?.Report(new WriteProgress("section", sectionIndex + 1, document.Sections.Count));

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    /// <summary>Automatic styles of the headers/footers; they live in styles.xml.</summary>
    internal sealed record BandStyles(
        IReadOnlyDictionary<OdtTextStyleKey, string> TextStyles,
        OdtParagraphStyles ParagraphStyles,
        IReadOnlyList<double> ColumnWidths,
        IReadOnlyList<ListBlock> Lists,
        NumberingLookup Numbering,
        double DefaultFontSize);

    /// <summary>
    /// collects the styles of all headers/footers. Prefixes (MT, MP) separate them from the
    /// styles in content.xml.
    /// </summary>
    internal static BandStyles CollectBandStyles(PortableDocument document)
    {
        var bands = document.Sections
            .SelectMany(section => new[] { section.Header, section.Footer, section.FirstHeader, section.FirstFooter, section.EvenHeader, section.EvenFooter }
                .OfType<HeaderFooter>()
                .Select(band => (Blocks: (IEnumerable<Block>)band.Blocks, Available: ContentWidth(section))))
            .ToList();

        var defaultFontSize = document.Defaults.FontSize.Points;
        var paragraphStyles = OdtParagraphStyles.Collect(
            bands.Select(band => band.Blocks),
            "MP",
            bands: true,
            defaultFontSize: defaultFontSize,
            styles: document.Styles,
            sourceStyles: document.SourceStyles);
        paragraphStyles.CharacterStyles = document.CharacterStyles;
        paragraphStyles.BookmarkRanges = BookmarkScan.Collect(document).RangeNames;
        paragraphStyles.BookmarkRanges = BookmarkScan.Collect(document).RangeNames;
        return new BandStyles(
            OdtAutomaticStyles.Collect(
                bands.SelectMany(band => band.Blocks),
                "MT",
                characterStyles: document.CharacterStyles,
                defaultFontSize: defaultFontSize),
            paragraphStyles,
            CollectColumnWidths(bands),
            bands.SelectMany(band => ListScan.Collect(band.Blocks)).ToList(),
            new NumberingLookup(document.Numbering),
            defaultFontSize);
    }

    internal static void WriteBandAutomaticStyles(XmlWriter writer, BandStyles styles, InvalidXmlCharacterBehavior xmlBehavior)
    {
        WriteTextStyles(writer, styles.TextStyles, xmlBehavior);
        WriteParagraphStyles(writer, styles.ParagraphStyles, styles.DefaultFontSize);
        WriteListStyles(writer, styles.Lists, styles.Numbering);
        WriteColumnStyles(writer, styles.ColumnWidths);
        if (styles.ColumnWidths.Count > 0)
        {
            WriteTableCellStyles(writer);
            styles.ParagraphStyles.CellStyles.Write(writer);
        }
    }

    /// <summary>Writes <c>style:header</c> or <c>style:footer</c> with the same block rules as the body.</summary>
    internal static void WriteBand(
        XmlWriter writer,
        string localName,
        HeaderFooter band,
        Section section,
        BandStyles styles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        FieldContext? fields = null)
    {
        writer.WriteStartElement("style", localName, OdtNamespaces.Style);
        styles.ParagraphStyles.Images = images;
        using (styles.ParagraphStyles.Spacing.EnterCompact())
        {
            foreach (var block in band.Blocks)
            {
                WriteBlock(writer, block, styles.TextStyles, styles.ParagraphStyles, images, ContentWidth(section), xmlBehavior, state, styles.Numbering, fields);
            }
        }

        writer.WriteEndElement();
    }

    /// <summary>Tables in the body; tables in headers/footers are numbered after them.</summary>
    internal static int CountBodyTables(PortableDocument document) => EnumerateTables(document).Count();

    private static void WriteTextStyles(
        XmlWriter writer,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        foreach (var (key, name) in textStyles.OrderBy(pair => StyleIndex(pair.Value)))
        {
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "text");
            if (key.StyleName is { Length: > 0 } styleName)
            {
                // The named character style provides the base; the properties below are the
                // direct formatting of the run.
                writer.WriteAttributeString("style", "parent-style-name", OdtNamespaces.Style, styleName);
            }

            writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);

            if (key.Bold is { } bold)
            {
                writer.WriteAttributeString("fo", "font-weight", OdtNamespaces.Fo, bold ? "bold" : "normal");
            }

            if (key.Hidden is { } hidden)
            {
                writer.WriteAttributeString("text", "display", OdtNamespaces.Text, hidden ? "none" : "true");
            }

            if (key.Border is not null)
            {
                writer.WriteAttributeString("fo", "border", OdtNamespaces.Fo, key.Border);
            }

            if (key.Italic is { } italic)
            {
                writer.WriteAttributeString("fo", "font-style", OdtNamespaces.Fo, italic ? "italic" : "normal");
            }

            if (key.AllCaps is { } allCaps)
            {
                writer.WriteAttributeString("fo", "text-transform", OdtNamespaces.Fo, allCaps ? "uppercase" : "none");
            }

            if (key.SmallCaps is { } smallCaps)
            {
                writer.WriteAttributeString("fo", "font-variant", OdtNamespaces.Fo, smallCaps ? "small-caps" : "normal");
            }

            OdtStylesWriter.WriteEffects(writer, key.Effects);

            if (key.Strikethrough is { } strikethrough)
            {
                // The line style stays solid; single or double is the line-through type.
                writer.WriteAttributeString(
                    "style", "text-line-through-style", OdtNamespaces.Style,
                    strikethrough == TextStrikethrough.None ? "none" : "solid");
                if (strikethrough != TextStrikethrough.None)
                {
                    writer.WriteAttributeString(
                        "style", "text-line-through-type", OdtNamespaces.Style,
                        strikethrough == TextStrikethrough.Double ? "double" : "single");
                    if (key.ColorHex is not null)
                    {
                        writer.WriteAttributeString("style", "text-line-through-color", OdtNamespaces.Style, key.ColorHex);
                    }
                }
            }

            if (key.UnderlineStyle is { } underlineStyle)
            {
                writer.WriteAttributeString(
                    "style",
                    "text-underline-style",
                    OdtNamespaces.Style,
                    underlineStyle == TextUnderlineStyle.None ? "none" : UnderlineValue(underlineStyle));
                if (underlineStyle != TextUnderlineStyle.None)
                {
                    if (underlineStyle == TextUnderlineStyle.Double)
                        writer.WriteAttributeString("style", "text-underline-type", OdtNamespaces.Style, "double");
                    writer.WriteAttributeString("style", "text-underline-width", OdtNamespaces.Style, "auto");
                    writer.WriteAttributeString(
                        "style", "text-underline-color", OdtNamespaces.Style, key.UnderlineColorHex ?? "font-color");
                }
            }

            if (key.BackgroundHex is not null)
            {
                writer.WriteAttributeString("fo", "background-color", OdtNamespaces.Fo, key.BackgroundHex);
            }

            if (key.CharacterSpacingPoints is { } characterSpacing)
            {
                // An explicit 0 pt switches off a spacing inherited from the paragraph style.
                writer.WriteAttributeString(
                    "fo", "letter-spacing", OdtNamespaces.Fo,
                    characterSpacing.ToString("0.##", CultureInfo.InvariantCulture) + "pt");
            }

            if (key.HorizontalScaling is { } scaling)
            {
                // An explicit 100 % switches off a scaling inherited from the paragraph style.
                writer.WriteAttributeString(
                    "style", "text-scale", OdtNamespaces.Style,
                    scaling.ToString("0.##", CultureInfo.InvariantCulture) + "%");
            }

            if (key.Kerning is { } kerning)
            {
                writer.WriteAttributeString("style", "letter-kerning", OdtNamespaces.Style, kerning ? "true" : "false");
            }

            if (key.PositionPercent is { } position)
            {
                // An explicit 0 % switches off a shift inherited from the paragraph style.
                writer.WriteAttributeString(
                    "style", "text-position", OdtNamespaces.Style,
                    position.ToString("0.##", CultureInfo.InvariantCulture) + "%");
            }
            else if (key.Baseline == TextBaseline.Superscript)
            {
                writer.WriteAttributeString("style", "text-position", OdtNamespaces.Style, "super 58%");
            }
            else if (key.Baseline == TextBaseline.Subscript)
            {
                writer.WriteAttributeString("style", "text-position", OdtNamespaces.Style, "sub 58%");
            }
            else if (key.Baseline == TextBaseline.Normal)
            {
                writer.WriteAttributeString("style", "text-position", OdtNamespaces.Style, "0% 0%");
            }

            if (key.FontFamily is not null)
            {
                writer.WriteAttributeString("style", "font-name", OdtNamespaces.Style, OdtFontFaces.Name(key.FontFamily, xmlBehavior));
            }

            if (key.FontSizePoints is { } size)
            {
                writer.WriteAttributeString(
                    "fo", "font-size", OdtNamespaces.Fo, size.ToString("0.##", CultureInfo.InvariantCulture) + "pt");
            }

            if (key.ColorHex is not null)
            {
                writer.WriteAttributeString("fo", "color", OdtNamespaces.Fo, key.ColorHex);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    internal static string UnderlineValue(TextUnderlineStyle style) => style switch
    {
        TextUnderlineStyle.Double => "solid",
        TextUnderlineStyle.Dotted => "dotted",
        TextUnderlineStyle.Dashed => "dash",
        TextUnderlineStyle.Wave => "wave",
        _ => "solid",
    };

    /// <summary>Counter at the end of a style name (T3, MP12, ...) for a stable order.</summary>
    private static int StyleIndex(string styleName)
    {
        var start = styleName.Length;
        while (start > 0 && char.IsAsciiDigit(styleName[start - 1]))
        {
            start--;
        }

        return start < styleName.Length
            && int.TryParse(styleName.AsSpan(start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : 0;
    }

    private static void WriteParagraphStyles(
        XmlWriter writer,
        OdtParagraphStyles paragraphStyles,
        double defaultFontSize)
    {
        foreach (var (key, name) in paragraphStyles.Entries.OrderBy(pair => StyleIndex(pair.Value)))
        {
            var style = key.Style;
            var parent = NamedStyle(paragraphStyles.Styles, style.StyleName);
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
            if (!string.IsNullOrEmpty(style.StyleName))
            {
                writer.WriteAttributeString("style", "parent-style-name", OdtNamespaces.Style, style.StyleName);
            }

            if (key.Break?.MasterPage is { } masterPage)
            {
                writer.WriteAttributeString("style", "master-page-name", OdtNamespaces.Style, masterPage);
            }

            writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
            if (key.Break is { PageNumberOnly: false })
            {
                writer.WriteAttributeString("fo", "break-before", OdtNamespaces.Fo, "page");
            }

            if (key.Break?.PageNumberStart is { } pageNumberStart)
            {
                writer.WriteAttributeString("style", "page-number", OdtNamespaces.Style, pageNumberStart.ToString(CultureInfo.InvariantCulture));
            }

            // Like Word: after a manual page break the space before is dropped (also the
            // of the heading style); after a section break Word/LibreOffice reduce
            // it by Typography.SpaceBeforeReductionAtSectionStart (measured: about 5 pt at 12 pt).
            if (key.Break is { MasterPage: null })
            {
                writer.WriteAttributeString("fo", "margin-top", OdtNamespaces.Fo, "0pt");
            }
            else if (key.Break is { MasterPage: not null, PageNumberOnly: false })
            {
                WriteLengthIfNonZero(writer, "margin-top", ReducedAtSectionStart(key));
            }
            else
            {
                WriteNullableLengthIfDifferent(writer, "margin-top", style.SpaceBefore, parent?.SpaceBefore);
            }

            WriteSpaceAfter(writer, style, key.Compact, parent);

            WriteParagraphFormat(writer, style, breakWritten: key.Break is not null, parent: parent);
            writer.WriteEndElement();

            // ADR-0010: the run formatting of the paragraph style, so the spans only state deviations.
            var format = RunFormat.HasFormatting(style) ? RunFormat.Of(style) : RunFormat.Empty;
            if (!format.IsEmpty)
            {
                writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);
                OdtStylesWriter.WriteTextProperties(writer, format, defaultFontSize);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// Writes the content of <c>style:paragraph-properties</c> that the automatic styles of the
    /// body and the named styles share: all attributes, then the tab stops.
    /// </summary>
    internal static void WriteParagraphFormat(
        XmlWriter writer,
        ParagraphStyle style,
        bool breakWritten = false,
        ParagraphStyle? parent = null)
    {
        // A child of a named style must state a value that differs from the parent, including the
        // built-in default (e.g. left alignment in a right-aligned parent, ADR-0011).
        if (style.Alignment is { } alignment && (parent is null || alignment != parent.Alignment))
        {
            var alignmentText = alignment switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Right => "end",
                TextAlignment.Justify => "justify",
                _ => "start",
            };
            writer.WriteAttributeString("fo", "text-align", OdtNamespaces.Fo, alignmentText);
        }

        WriteNullableLengthIfDifferent(writer, "margin-left", style.LeftIndent, parent?.LeftIndent);
        WriteNullableLengthIfDifferent(writer, "margin-right", style.RightIndent, parent?.RightIndent);
        WriteNullableLengthIfDifferent(writer, "text-indent", style.FirstLineIndent, parent?.FirstLineIndent);

        if (style.KeepWithNext is { } keepWithNext
            && (parent is null || parent.KeepWithNext != keepWithNext))
        {
            writer.WriteAttributeString("fo", "keep-with-next", OdtNamespaces.Fo, keepWithNext ? "always" : "auto");
        }

        if (style.KeepTogether is { } keepTogether
            && (parent is null || parent.KeepTogether != keepTogether))
        {
            writer.WriteAttributeString("fo", "keep-together", OdtNamespaces.Fo, keepTogether ? "always" : "auto");
        }

        if (style.PageBreakBefore is { } pageBreakBefore
            && !breakWritten
            && (parent is null || parent.PageBreakBefore != pageBreakBefore))
        {
            writer.WriteAttributeString("fo", "break-before", OdtNamespaces.Fo, pageBreakBefore ? "page" : "auto");
        }

        if (style.Background is { } background && (parent is null || !Equals(parent.Background, style.Background)))
        {
            writer.WriteAttributeString("fo", "background-color", OdtNamespaces.Fo, background.ToString());
        }

        if (style.Borders is { } borders && (parent is null || !Equals(parent.Borders, style.Borders)))
        {
            WriteParagraphBorders(writer, borders);
        }

        if (style.LineSpacing is { } lineSpacing && (parent is null || !Equals(parent.LineSpacing, style.LineSpacing)))
        {
            switch (lineSpacing.Rule)
            {
                case LineSpacingRule.Multiple:
                    writer.WriteAttributeString(
                        "fo", "line-height", OdtNamespaces.Fo,
                        Math.Max(0.01, lineSpacing.Factor * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%");
                    break;
                case LineSpacingRule.AtLeast:
                    writer.WriteAttributeString("style", "line-height-at-least", OdtNamespaces.Style, PositivePointsText(lineSpacing.Height));
                    break;
                default:
                    writer.WriteAttributeString("fo", "line-height", OdtNamespaces.Fo, PositivePointsText(lineSpacing.Height));
                    break;
            }
        }

        if (style.RightToLeft is { } rightToLeft
            && (parent is null || parent.RightToLeft != rightToLeft))
        {
            writer.WriteAttributeString(
                "style", "writing-mode", OdtNamespaces.Style, rightToLeft ? "rl-tb" : "lr-tb");
        }

        if (style.TabStops is { } tabStops
            && (parent is null || !ParagraphStyleResolver.SameTabStops(style.TabStops, parent.TabStops)))
        {
            writer.WriteStartElement("style", "tab-stops", OdtNamespaces.Style);
            foreach (var tab in tabStops)
            {
                writer.WriteStartElement("style", "tab-stop", OdtNamespaces.Style);
                // ODF counts from the paragraph indent, the model from the margin.
                // shortcut: a stop left of the indent moves to the indent, because the reader's TabStop cannot be negative.
                var position = Length.FromPoints(Math.Max(0, tab.Position.Points - (style.LeftIndent?.Points ?? 0)));
                writer.WriteAttributeString("style", "position", OdtNamespaces.Style, PointsText(position));
                writer.WriteAttributeString("style", "type", OdtNamespaces.Style, TabTypeValue(tab.Alignment));
                if (tab.Alignment == TabStopAlignment.Decimal)
                {
                    writer.WriteAttributeString("style", "char", OdtNamespaces.Style, ".");
                }

                if (LeaderText(tab.Leader) is { } leader)
                {
                    writer.WriteAttributeString("style", "leader-text", OdtNamespaces.Style, leader);
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }
    }

    /// <summary>The model style with the given name (the parent of an automatic style).</summary>
    private static ParagraphStyle? NamedStyle(IReadOnlyList<ParagraphStyle> styles, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var style in styles)
        {
            if (string.Equals(style.StyleName, name, StringComparison.Ordinal))
            {
                return style;
            }
        }

        return null;
    }

    private static void WriteParagraphBorders(XmlWriter writer, ParagraphBorders borders)
    {
        // An empty set is an explicit "no borders": every side is cleared, including sides that
        // the inherited style may have (ADR-0016).
        var clearAll = !HasVisibleBorder(borders);
        WriteParagraphBorder(writer, "border-top", borders.Top, clearAll);
        WriteParagraphBorder(writer, "border-right", borders.Right, clearAll);
        WriteParagraphBorder(writer, "border-bottom", borders.Bottom, clearAll);
        WriteParagraphBorder(writer, "border-left", borders.Left, clearAll);
    }

    private static bool HasVisibleBorder(ParagraphBorders borders)
        => borders.Top is { Style: not BorderStyle.None }
            || borders.Right is { Style: not BorderStyle.None }
            || borders.Bottom is { Style: not BorderStyle.None }
            || borders.Left is { Style: not BorderStyle.None };

    private static void WriteParagraphBorder(XmlWriter writer, string name, ParagraphBorder? border, bool clearAll)
    {
        if (border is null || border.Style == BorderStyle.None)
        {
            if (clearAll)
            {
                writer.WriteAttributeString("fo", name, OdtNamespaces.Fo, "none");
            }

            return;
        }

        writer.WriteAttributeString(
            "fo", name, OdtNamespaces.Fo,
            PointsText(border.Width) + " " + BorderStyleValue(border.Style) + " " + border.Color);
    }

    private static string BorderStyleValue(BorderStyle style) => style switch
    {
        BorderStyle.Double => "double",
        BorderStyle.Dotted => "dotted",
        BorderStyle.Dashed => "dashed",
        _ => "solid",
    };

    private static string TabTypeValue(TabStopAlignment alignment) => alignment switch
    {
        TabStopAlignment.Center => "center",
        TabStopAlignment.Right => "right",
        TabStopAlignment.Decimal => "char",
        _ => "left",
    };

    private static string? LeaderText(TabStopLeader leader) => leader switch
    {
        TabStopLeader.Dot => ".",
        TabStopLeader.Hyphen => "-",
        TabStopLeader.Underscore => "_",
        _ => null,
    };

    private static string PointsText(Length length)
        => length.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    /// <summary>A length that must stay above zero after rounding to two decimals.</summary>
    private static string PositivePointsText(Length length)
        => Math.Max(0.01, length.Points).ToString("0.##", CultureInfo.InvariantCulture) + "pt";

    private static void WriteLengthIfNonZero(XmlWriter writer, string name, Length length)
    {
        if (length != Length.Zero)
        {
            writer.WriteAttributeString("fo", name, OdtNamespaces.Fo,
                length.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt");
        }
    }

    internal static void WriteNullableLengthIfDifferent(XmlWriter writer, string name, Length? length, Length? parent)
    {
        if (length is not { } value)
        {
            return;
        }

        if (parent is { } inherited && inherited == value)
        {
            return;
        }

        writer.WriteAttributeString("fo", name, OdtNamespaces.Fo,
            value.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt");
    }

    /// <summary>
    /// Writes a length when it differs from the parent style; a difference to zero is written
    /// explicitly so it overrides an inherited value (ADR-0011).
    /// </summary>
    private static void WriteLengthIfDifferent(XmlWriter writer, string name, Length length, Length? parent)
    {
        if (parent is { } inherited ? inherited == length : length == Length.Zero)
        {
            return;
        }

        writer.WriteAttributeString("fo", name, OdtNamespaces.Fo,
            length.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt");
    }

    /// <summary>
    /// Writes the spacing after a paragraph. An unset spacing inherits; compact paragraphs state an
    /// explicit 0 so they do not fall back to the document default on a read.
    /// </summary>
    internal static void WriteSpaceAfter(
        XmlWriter writer,
        ParagraphStyle style,
        bool compact,
        ParagraphStyle? parent = null)
    {
        if (parent is not null && !compact && style.SpaceAfter == parent.SpaceAfter)
        {
            return;
        }

        if (style.SpaceAfter is { } spaceAfter)
        {
            if (spaceAfter == Length.Zero)
            {
                writer.WriteAttributeString("fo", "margin-bottom", OdtNamespaces.Fo, "0pt");
            }
            else
            {
                WriteLengthIfNonZero(writer, "margin-bottom", spaceAfter);
            }

            return;
        }

        if (compact)
        {
            writer.WriteAttributeString("fo", "margin-bottom", OdtNamespaces.Fo, "0pt");
        }
    }

    /// <summary>
    /// space before after a section break: max(0, SpaceBefore − reduction), as
    /// Word/LibreOffice. Headings take their space before from the named style
    /// (Typography.HeadingSpaceBefore); the automatic break style then shortens it.
    /// </summary>
    private static Length ReducedAtSectionStart(OdtParagraphKey key)
    {
        var style = key.Style;
        var full = style.SpaceBefore
            ?? (style.StyleName is { } name && name.StartsWith("Heading", StringComparison.Ordinal)
                ? Typography.HeadingSpaceBefore
                : Length.Zero);
        return Length.FromPoints(Math.Max(0, full.Points - Typography.SpaceBeforeReductionAtSectionStart.Points));
    }

    private static void WriteBlock(
        XmlWriter writer,
        Block block,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        double availableWidth,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields = null)
    {
        switch (block)
        {
            case Paragraph paragraph:
            {
                var (style, referenceOnly, forceStyle) = OdtParagraphStyles.Resolve(
                    paragraph, paragraphStyles.Styles, paragraphStyles.SourceStyles);
                WriteParagraph(
                    writer, style, paragraph.Inlines, textStyles, paragraphStyles, xmlBehavior,
                    paragraphStyles.Spacing.IsCompact(paragraph), paragraphStyles.Breaks.BreakBefore(paragraph), fields,
                    referenceOnly, forceStyle);
                break;
            }

            case Heading heading:
                WriteHeading(writer, heading, heading.Style, textStyles, paragraphStyles, xmlBehavior, fields);
                break;
            case ListBlock list when OdtListTables.HasTable(list):
                foreach (var part in OdtListTables.Split(list))
                {
                    WriteBlock(writer, part, textStyles, paragraphStyles, images, availableWidth, xmlBehavior, state, numbering, fields);
                }

                break;
            case ListBlock list:
                WriteListBlock(writer, list, textStyles, paragraphStyles, images, availableWidth, xmlBehavior, state, numbering, fields);
                break;
            case TableBlock table:
                WriteTableBlock(writer, table, textStyles, paragraphStyles, images, availableWidth, xmlBehavior, state, numbering, fields);
                break;
            case TableOfContents toc:
                WriteTableOfContents(writer, toc, paragraphStyles, xmlBehavior);
                break;
            case TableOfFigures figures:
                WriteTableOfFigures(writer, figures, paragraphStyles, xmlBehavior);
                break;
            case AlphabeticalIndex index:
                WriteAlphabeticalIndex(writer, index, paragraphStyles, xmlBehavior);
                break;
            case ImageBlock image:
                WriteImageBlock(writer, image, images, paragraphStyles, xmlBehavior);
                break;
            case DrawingShape shape:
                WriteDrawingShape(writer, shape, textStyles, paragraphStyles, images, xmlBehavior, state, numbering, fields);
                break;
            case Diagram diagram:
                WriteDiagram(writer, diagram, textStyles, paragraphStyles, images, xmlBehavior, state, numbering, fields);
                break;
            case FormField field when state.Forms.TryGetValue(field, out var controlId):
                OdtForms.WritePlacement(writer, field, controlId, null);
                break;
            case Equation equation when state.Equations.TryGetValue(equation, out var equationName):
                OdtMath.WritePlacement(writer, equation, equationName);
                break;
            case Chart chart when state.Charts.TryGetValue(chart, out var objectName):
                OdtCharts.WritePlacement(writer, chart, objectName, xmlBehavior);
                break;
            case EmbeddedObject embedded when state.Objects.TryGetValue(embedded, out var embeddedName):
                OdtEmbeddedObjects.WritePlacement(writer, embedded, embeddedName);
                break;
            case PageBreak when paragraphStyles.SuppressPageBreaks:
                break;
            case ColumnBreak when paragraphStyles.SuppressPageBreaks:
                break;
            case ColumnBreak:
                WriteColumnBreak(writer);
                break;
            case PageBreak pageBreak when paragraphStyles.Breaks.IsAbsorbed(pageBreak):
                // The following block carries the break itself.
                break;
            case PageBreak:
                WritePageBreak(writer);
                break;
        }
    }

    private static void WritePageBreak(XmlWriter writer)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, "PageBreak");
        writer.WriteEndElement();
    }

    private static void WriteColumnBreak(XmlWriter writer)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteStartElement("text", "br", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "type", OdtNamespaces.Text, "column");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSectionBreakStyles(XmlWriter writer, PortableDocument document)
    {
        for (var index = 2; index <= document.Sections.Count; index++)
        {
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, OdtSectionNames.BreakStyle(index));
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
            writer.WriteAttributeString("style", "master-page-name", OdtNamespaces.Style, OdtSectionNames.MasterPage(index));
            writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("fo", "break-before", OdtNamespaces.Fo, "page");
            if (document.Sections[index - 1].PageNumberStart is { } pageNumberStart)
            {
                writer.WriteAttributeString("style", "page-number", OdtNamespaces.Style, pageNumberStart.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    /// <summary>Table styles for tables that carry a page or section break.</summary>
    private static void WriteTableBreakStyles(XmlWriter writer, PortableDocument document, OdtBreakPlan breaks)
    {
        foreach (var section in document.Sections)
        {
            var available = ContentWidth(section);
            foreach (var table in EnumerateTables(section.Blocks))
            {
                if (breaks.TableStyleName(table) is not { } name || breaks.BreakBefore(table) is not { } tableBreak)
                {
                    continue;
                }

                var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Cells.Count);
                var width = TableLayout.ResolveColumnWidthsWithPercent(table, columns, available).Sum();

                writer.WriteStartElement("style", "style", OdtNamespaces.Style);
                writer.WriteAttributeString("style", "name", OdtNamespaces.Style, name);
                writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "table");
                if (tableBreak.MasterPage is { } masterPage)
                {
                    writer.WriteAttributeString("style", "master-page-name", OdtNamespaces.Style, masterPage);
                }

                // width and alignment as for a table without a style (sum of columns, left).
                writer.WriteStartElement("style", "table-properties", OdtNamespaces.Style);
                writer.WriteAttributeString("style", "width", OdtNamespaces.Style, CentimetersText(width));
                writer.WriteAttributeString("table", "align", OdtNamespaces.Table, "left");
                if (!tableBreak.PageNumberOnly)
                {
                    writer.WriteAttributeString("fo", "break-before", OdtNamespaces.Fo, "page");
                }

                if (tableBreak.PageNumberStart is { } pageNumberStart)
                {
                    writer.WriteAttributeString("style", "page-number", OdtNamespaces.Style, pageNumberStart.ToString(CultureInfo.InvariantCulture));
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }
        }
    }

    private static void WritePageBreakStyleIfNeeded(XmlWriter writer, PortableDocument document)
    {
        if (!ContainsPageBreak(document))
        {
            return;
        }

        WritePageBreakStyle(writer);
    }

    private static void WritePageBreakStyle(XmlWriter writer)
    {
        writer.WriteStartElement("style", "style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, "PageBreak");
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
        writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
        writer.WriteAttributeString("fo", "break-before", OdtNamespaces.Fo, "page");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static bool ContainsPageBreak(PortableDocument document)
    {
        foreach (var section in document.Sections)
        {
            if (ContainsPageBreak(section.Blocks))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ContainsPageBreak(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case PageBreak:
                    return true;
                case ListBlock list when list.Items.Any(item => ContainsPageBreak(item.Blocks)):
                    return true;
                case TableBlock table when table.Rows.Any(row => row.Cells.Any(cell => ContainsPageBreak(cell.Blocks))):
                    return true;
            }
        }

        return false;
    }

    private static void WriteImageBlock(
        XmlWriter writer,
        ImageBlock image,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        OdtParagraphStyles paragraphStyles,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (!images.TryGetValue(image, out var resource))
        {
            // All images are registered in advance; if one is missing, that is an error in the
            // writer and must not lead to silent data loss (as in the DOCX writer).
            throw new InvalidOperationException("ODT: image without a registered resource.");
        }

        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        if (paragraphStyles.Breaks.BreakBefore(image) is { } imageBreak
            && paragraphStyles.TryGetName(ParagraphStyle.Default, compact: false, imageBreak, out var breakStyle))
        {
            writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, breakStyle);
        }

        WriteImageFrame(writer, image, resource, xmlBehavior, inLine: false);
        writer.WriteEndElement();
    }

    /// <summary>The <c>draw:frame</c> of an image; an inline image (or one without wrap) is anchored as a character.</summary>
    private static void WriteImageFrame(XmlWriter writer, ImageBlock image, OdtImageResource resource, InvalidXmlCharacterBehavior xmlBehavior, bool inLine)
    {
        var (width, height) = ImageLayout.Resolve(image);
        var floating = !inLine && image.Wrap != ImageWrap.None;
        writer.WriteStartElement("draw", "frame", OdtNamespaces.Draw);
        writer.WriteAttributeString("draw", "name", OdtNamespaces.Draw, $"Image {resource.PartName}");
        if (NeedsImageStyle(image)) writer.WriteAttributeString("draw", "style-name", OdtNamespaces.Draw, resource.StyleName);
        writer.WriteAttributeString(
            "text", "anchor-type", OdtNamespaces.Text,
            floating ? "paragraph" : "as-char");
        writer.WriteAttributeString("svg", "width", OdtNamespaces.Svg, CentimetersText(width));
        writer.WriteAttributeString("svg", "height", OdtNamespaces.Svg, CentimetersText(height));

        if (floating)
        {
            if (image.OffsetX is { } offsetX)
            {
                writer.WriteAttributeString("svg", "x", OdtNamespaces.Svg, CentimetersText(offsetX));
            }

            if (image.OffsetY is { } offsetY)
            {
                writer.WriteAttributeString("svg", "y", OdtNamespaces.Svg, CentimetersText(offsetY));
            }
        }

        writer.WriteStartElement("draw", "image", OdtNamespaces.Draw);
        writer.WriteAttributeString("xlink", "href", OdtNamespaces.XLink, resource.PartName);
        writer.WriteAttributeString("xlink", "type", OdtNamespaces.XLink, "simple");
        writer.WriteAttributeString("xlink", "show", OdtNamespaces.XLink, "embed");
        writer.WriteAttributeString("xlink", "actuate", OdtNamespaces.XLink, "onLoad");
        writer.WriteEndElement();

        // Alternative text for accessibility; svg:title supplies the accessible
        // name, svg:desc the description (order per ODF schema after draw:image).
        if (!string.IsNullOrEmpty(image.AltText))
        {
            var altText = XmlText.Clean(image.AltText, xmlBehavior);
            writer.WriteElementString("svg", "title", OdtNamespaces.Svg, altText);
            writer.WriteElementString("svg", "desc", OdtNamespaces.Svg, altText);
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// A shape or text box (ADR-0022) in a paragraph of its own: the basic draw elements for lines,
    /// rectangles, ellipses and freeforms, a frame with a text box for text.
    /// </summary>
    private static void WriteDrawingShape(
        XmlWriter writer,
        DrawingShape shape,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        if (paragraphStyles.Breaks.BreakBefore(shape) is { } shapeBreak
            && paragraphStyles.TryGetName(ParagraphStyle.Default, compact: false, shapeBreak, out var breakStyle))
        {
            writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, breakStyle);
        }

        WriteShapeElement(writer, shape, textStyles, paragraphStyles, images, xmlBehavior, state, numbering, fields, null);
        writer.WriteEndElement();
    }

    /// <summary>A diagram (ADR-0030): a group, anchored as a character, of the shapes it expands to.</summary>
    private static void WriteDiagram(
        XmlWriter writer,
        Diagram diagram,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteStartElement("draw", "g", OdtNamespaces.Draw);
        writer.WriteAttributeString("text", "anchor-type", OdtNamespaces.Text, "as-char");
        if (!string.IsNullOrEmpty(diagram.AltText))
        {
            var altText = XmlText.Clean(diagram.AltText, xmlBehavior);
            writer.WriteElementString("svg", "title", OdtNamespaces.Svg, altText);
            writer.WriteElementString("svg", "desc", OdtNamespaces.Svg, altText);
        }

        foreach (var placed in OdtDiagrams.Shapes(diagram))
        {
            WriteShapeElement(writer, placed.Shape, textStyles, paragraphStyles, images, xmlBehavior, state, numbering, fields, placed);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteShapeElement(
        XmlWriter writer,
        DrawingShape shape,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields,
        PlacedShape? placed)
    {
        var element = shape.Kind switch
        {
            ShapeKind.Line => "line",
            ShapeKind.Ellipse => "ellipse",
            ShapeKind.Freeform => "path",
            ShapeKind.TextBox => "frame",
            _ => "rect",
        };
        writer.WriteStartElement("draw", element, OdtNamespaces.Draw);
        writer.WriteAttributeString("draw", "style-name", OdtNamespaces.Draw, OdtShapes.StyleName(shape, placed is not null && shape.Kind == ShapeKind.TextBox));
        if (placed is null)
        {
            writer.WriteAttributeString("text", "anchor-type", OdtNamespaces.Text, shape.Wrap == ImageWrap.None ? "as-char" : "paragraph");
        }

        OdtShapes.WritePlacement(writer, shape, placed?.X, placed?.Y);
        if (shape.Kind == ShapeKind.RoundRectangle)
        {
            writer.WriteAttributeString("draw", "corner-radius", OdtNamespaces.Draw,
                CentimetersText(Math.Min(shape.Width.Points, shape.Height.Points) / 6));
        }

        if (shape.Kind == ShapeKind.Freeform)
        {
            var (viewBox, data) = OdtShapes.Path(shape);
            writer.WriteAttributeString("svg", "viewBox", OdtNamespaces.Svg, viewBox);
            writer.WriteAttributeString("svg", "d", OdtNamespaces.Svg, data);
        }

        if (shape.Kind == ShapeKind.TextBox)
        {
            writer.WriteStartElement("draw", "text-box", OdtNamespaces.Draw);
            using (paragraphStyles.Spacing.EnterCompact())
            {
                foreach (var block in shape.Content!)
                {
                    WriteBlock(writer, block, textStyles, paragraphStyles, images, shape.Width.Points, xmlBehavior, state, numbering, fields);
                }
            }

            writer.WriteEndElement();
        }

        if (!string.IsNullOrEmpty(shape.AltText))
        {
            var altText = XmlText.Clean(shape.AltText, xmlBehavior);
            writer.WriteElementString("svg", "title", OdtNamespaces.Svg, altText);
            writer.WriteElementString("svg", "desc", OdtNamespaces.Svg, altText);
        }

        writer.WriteEndElement();
    }

    /// <summary>The position attributes of an anchored frame's graphic style (ODF 20.290, 20.291, 20.387, 20.388).</summary>
    internal static void WriteFramePosition(XmlWriter writer, FloatPlacement placement)
    {
        writer.WriteAttributeString("style", "horizontal-pos", OdtNamespaces.Style, placement.HorizontalAlignment switch
        {
            HorizontalFloatAlignment.Left => "left",
            HorizontalFloatAlignment.Center => "center",
            HorizontalFloatAlignment.Right => "right",
            _ => "from-left",
        });
        writer.WriteAttributeString("style", "horizontal-rel", OdtNamespaces.Style, placement.HorizontalRelativeTo switch
        {
            HorizontalAnchor.Page => "page",
            HorizontalAnchor.Margin => "page-content",
            HorizontalAnchor.Character => "char",
            _ => "paragraph-content",
        });
        writer.WriteAttributeString("style", "vertical-pos", OdtNamespaces.Style, placement.VerticalAlignment switch
        {
            VerticalFloatAlignment.Top => "top",
            VerticalFloatAlignment.Center => "middle",
            VerticalFloatAlignment.Bottom => "bottom",
            _ => "from-top",
        });
        writer.WriteAttributeString("style", "vertical-rel", OdtNamespaces.Style, placement.VerticalRelativeTo switch
        {
            VerticalAnchor.Page => "page",
            VerticalAnchor.Margin => "page-content",
            VerticalAnchor.Line => "line",
            _ => "paragraph",
        });
    }

    private static bool NeedsImageStyle(ImageBlock image)
        => image.Wrap != ImageWrap.None || image.Crop is { IsEmpty: false };

    internal static void WriteImageStyles(XmlWriter writer, IReadOnlyDictionary<ImageBlock, OdtImageResource> images)
    {
        foreach (var (image, resource) in images)
        {
            if (!NeedsImageStyle(image)) continue;
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, resource.StyleName);
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "graphic");
            writer.WriteStartElement("style", "graphic-properties", OdtNamespaces.Style);
            if (image.Wrap != ImageWrap.None)
            {
                // OF-B06: ODF has no top-and-bottom wrap, so an anchored image without wrap does
                // that; behind and in front use run-through with a layer.
                writer.WriteAttributeString(
                    "style",
                    "wrap",
                    OdtNamespaces.Style,
                    image.Wrap switch
                    {
                        ImageWrap.Square => "parallel",
                        ImageWrap.Through or ImageWrap.Behind or ImageWrap.InFront => "run-through",
                        _ => "none",
                    });
                if (image.Wrap is ImageWrap.Through or ImageWrap.Behind or ImageWrap.InFront)
                {
                    writer.WriteAttributeString(
                        "style",
                        "run-through",
                        OdtNamespaces.Style,
                        image.Wrap == ImageWrap.InFront ? "foreground" : "background");
                }

                WriteFramePosition(writer, FloatPlacement.Of(image));
            }
            if (image.Crop is { IsEmpty: false } crop)
            {
                var (width, height) = ImageLayout.Resolve(image);
                writer.WriteAttributeString("fo", "clip", OdtNamespaces.Fo,
                    $"rect({PointsText(Length.FromPoints(crop.Top * height.Points))}, "
                    + $"{PointsText(Length.FromPoints(crop.Right * width.Points))}, "
                    + $"{PointsText(Length.FromPoints(crop.Bottom * height.Points))}, "
                    + $"{PointsText(Length.FromPoints(crop.Left * width.Points))})");
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// writes one <c>text:list-style</c> per numbering definition a list uses (OF-B01); the level
    /// styles carry kind, number format, start value, color and weight, so every level of the
    /// definition is in the file.
    /// </summary>
    private static void WriteListStyles(XmlWriter writer, IEnumerable<ListBlock> lists, NumberingLookup numbering)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            var definition = numbering.Definition(list);
            if (written.Add(definition.Name))
            {
                WriteListStyle(writer, definition);
            }
        }
    }

    private static void WriteListStyle(XmlWriter writer, NumberingDefinition definition)
    {
        writer.WriteStartElement("text", "list-style", OdtNamespaces.Text);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, OdtListStyles.StyleName(definition));

        for (var level = 1; level <= NumberingDefinition.MaxLevels; level++)
        {
            var properties = definition.Levels[level - 1];
            if (properties.Kind == ListKind.Bullet)
            {
                writer.WriteStartElement("text", "list-level-style-bullet", OdtNamespaces.Text);
                writer.WriteAttributeString("text", "level", OdtNamespaces.Text, OdtListStyles.Number(level));
                writer.WriteAttributeString(
                    "text", "bullet-char", OdtNamespaces.Text, OdtListStyles.BulletChar(level));
            }
            else
            {
                writer.WriteStartElement("text", "list-level-style-number", OdtNamespaces.Text);
                writer.WriteAttributeString("text", "level", OdtNamespaces.Text, OdtListStyles.Number(level));
                writer.WriteAttributeString(
                    "style", "num-format", OdtNamespaces.Style, OdtListStyles.NumberedFormat(properties.Format));
                writer.WriteAttributeString("style", "num-suffix", OdtNamespaces.Style, ".");
                if (properties.Start != 1)
                {
                    writer.WriteAttributeString(
                        "text", "start-value", OdtNamespaces.Text, OdtListStyles.Number(properties.Start));
                }
            }

            // Same geometry as DOCX (w:ind left = level * 720, hanging 360) and PDF: the text is
            // indented 36 pt per level, the label hangs 18 pt before it and a tab leads to the text.
            // label-alignment is the mode LibreOffice writes itself; it also mirrors right to left.
            var textIndent = PointsText(Length.FromPoints(level * 36));
            writer.WriteStartElement("style", "list-level-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("text", "list-level-position-and-space-mode", OdtNamespaces.Text, "label-alignment");
            writer.WriteStartElement("style", "list-level-label-alignment", OdtNamespaces.Style);
            writer.WriteAttributeString("text", "label-followed-by", OdtNamespaces.Text, properties.Suffix switch
            {
                ListSuffix.Space => "space",
                ListSuffix.Nothing => "nothing",
                _ => "listtab",
            });
            writer.WriteAttributeString("text", "list-tab-stop-position", OdtNamespaces.Text, textIndent);
            writer.WriteAttributeString("fo", "text-indent", OdtNamespaces.Fo, PointsText(Length.FromPoints(-18)));
            writer.WriteAttributeString("fo", "margin-left", OdtNamespaces.Fo, textIndent);
            writer.WriteEndElement();
            writer.WriteEndElement();

            // AJ04: the color and the weight of the number (schema order: list-level-properties
            // before text-properties).
            if (properties.NumberColor is not null || properties.NumberBold)
            {
                writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);
                if (properties.NumberColor is { } color)
                {
                    writer.WriteAttributeString("fo", "color", OdtNamespaces.Fo, color.ToString());
                }

                if (properties.NumberBold)
                {
                    writer.WriteAttributeString("fo", "font-weight", OdtNamespaces.Fo, "bold");
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteListBlock(
        XmlWriter writer,
        ListBlock list,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        double availableWidth,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields = null)
    {
        using var spacing = paragraphStyles.Spacing.EnterList(list);
        var level = numbering.Level(list, list.NestingLevel);
        var definitionName = numbering.Definition(list).Name;
        var listId = state.NextListId();
        var previousId = state.LastListId(definitionName, list.NestingLevel);
        writer.WriteStartElement("text", "list", OdtNamespaces.Text);
        writer.WriteAttributeString(
            "text", "style-name", OdtNamespaces.Text,
            OdtListStyles.StyleName(numbering.Definition(list)));
        writer.WriteAttributeString("xml", "id", "http://www.w3.org/XML/1998/namespace", listId);
        state.SetLastListId(definitionName, list.NestingLevel, listId);

        // OF-B02: lists that share a definition continue each other. ODF starts a list at the start
        // value of its style unless text:continue-list names the list that is continued. The attribute
        // is meant for top-level lists (the counter domain of a list and its sub-lists), so a nested
        // list never continues a sibling.
        var continues = list.NestingLevel == 1 && previousId is not null;
        if (continues)
        {
            writer.WriteAttributeString("text", "continue-list", OdtNamespaces.Text, previousId);
        }

        var firstItem = true;
        foreach (var item in list.Items)
        {
            writer.WriteStartElement("text", "list-item", OdtNamespaces.Text);
            if (firstItem && !continues && level.Kind == ListKind.Number && level.Start != 1)
            {
                writer.WriteAttributeString(
                    "text", "start-value", OdtNamespaces.Text, level.Start.ToString(CultureInfo.InvariantCulture));
            }

            firstItem = false;
            foreach (var block in item.Blocks)
            {
                WriteBlock(writer, block, textStyles, paragraphStyles, images, availableWidth, xmlBehavior, state, numbering, fields);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteTableOfContents(
        XmlWriter writer,
        TableOfContents toc,
        OdtParagraphStyles paragraphStyles,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("text", "table-of-content", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "name", OdtNamespaces.Text, "TOC");
        writer.WriteStartElement("text", "table-of-content-source", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "outline-level", OdtNamespaces.Text, toc.MaxLevel.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteStartElement("text", "index-body", OdtNamespaces.Text);

        if (!string.IsNullOrEmpty(toc.Title))
        {
            WriteTocParagraph(writer, "TOCHeading", toc.Title!, xmlBehavior);
        }

        foreach (var (level, text) in paragraphStyles.Headings)
        {
            if (level > toc.MaxLevel)
            {
                continue;
            }

            WriteTocParagraph(writer, "TOC" + Math.Min(level, 6).ToString(CultureInfo.InvariantCulture), text, xmlBehavior);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>
    /// A table of figures: an illustration index (a table index for the label "Table") over the
    /// captions of the label; the cached entries are the caption texts and the application fills in
    /// the page numbers when it updates the index.
    /// </summary>
    private static void WriteTableOfFigures(
        XmlWriter writer,
        TableOfFigures figures,
        OdtParagraphStyles paragraphStyles,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        var isTable = figures.Label.Equals("Table", StringComparison.OrdinalIgnoreCase);
        var kind = isTable ? "table-index" : "illustration-index";
        writer.WriteStartElement("text", kind, OdtNamespaces.Text);
        writer.WriteAttributeString("text", "name", OdtNamespaces.Text, isTable ? "TableIndex" : "IllustrationIndex");
        writer.WriteStartElement("text", kind + "-source", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "use-caption", OdtNamespaces.Text, "true");
        writer.WriteAttributeString("text", "caption-sequence-name", OdtNamespaces.Text, XmlText.Clean(figures.Label, xmlBehavior));
        writer.WriteAttributeString("text", "caption-sequence-format", OdtNamespaces.Text, "text");
        writer.WriteEndElement();
        writer.WriteStartElement("text", "index-body", OdtNamespaces.Text);
        if (!string.IsNullOrEmpty(figures.Title))
        {
            WriteTocParagraph(writer, "TOCHeading", figures.Title!, xmlBehavior);
        }

        foreach (var caption in FigureIndexScan.Captions(paragraphStyles.Groups, figures.Label))
        {
            WriteTocParagraph(writer, "TOC1", caption.Text, xmlBehavior);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>
    /// An alphabetical index over the index marks; the cached entries are the sorted entries (a main
    /// entry once, its sub entries below it) and the application fills in the page numbers.
    /// </summary>
    private static void WriteAlphabeticalIndex(
        XmlWriter writer,
        AlphabeticalIndex index,
        OdtParagraphStyles paragraphStyles,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("text", "alphabetical-index", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "name", OdtNamespaces.Text, "AlphabeticalIndex");
        writer.WriteStartElement("text", "alphabetical-index-source", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "index-scope", OdtNamespaces.Text, "document");
        writer.WriteAttributeString("text", "alphabetical-separators", OdtNamespaces.Text, "true");
        writer.WriteEndElement();
        writer.WriteStartElement("text", "index-body", OdtNamespaces.Text);
        if (!string.IsNullOrEmpty(index.Title))
        {
            WriteTocParagraph(writer, "TOCHeading", index.Title!, xmlBehavior);
        }

        string? previous = null;
        foreach (var (text, subText) in FigureIndexScan.Entries(paragraphStyles.Groups))
        {
            if (text != previous)
            {
                WriteTocParagraph(writer, "TOC1", text, xmlBehavior);
                previous = text;
            }

            if (subText is not null)
            {
                WriteTocParagraph(writer, "TOC2", subText, xmlBehavior);
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteTocParagraph(XmlWriter writer, string styleName, string text, InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, styleName);
        writer.WriteString(XmlText.Clean(text, xmlBehavior));
        writer.WriteEndElement();
    }

    private static void WriteTocStyles(XmlWriter writer)
    {
        writer.WriteStartElement("style", "style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, "TOCHeading");
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
        writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
        writer.WriteAttributeString("fo", "margin-bottom", OdtNamespaces.Fo, PointsText(Typography.ParagraphSpaceAfter));
        writer.WriteEndElement();
        writer.WriteEndElement();

        for (var level = 1; level <= 6; level++)
        {
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, "TOC" + level.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
            writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("fo", "margin-left", OdtNamespaces.Fo, PointsText(Length.FromPoints((level - 1) * 18)));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    private static void WriteTableBlock(
        XmlWriter writer,
        TableBlock table,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        double availableWidth,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields = null)
    {
        var columns = TableLayout.LogicalColumns(table);
        if (columns == 0)
        {
            return;
        }

        var widths = TableLayout.ResolveColumnWidthsWithPercent(table, columns, availableWidth);
        var tableWidth = widths.Sum();
        var remaining = new int[columns];

        writer.WriteStartElement("table", "table", OdtNamespaces.Table);
        writer.WriteAttributeString("table", "name", OdtNamespaces.Table, "Table" + state.NextTableIndex().ToString(CultureInfo.InvariantCulture));
        var breakStyle = paragraphStyles.Breaks.TableStyleName(table);
        var tableStyle = paragraphStyles.CellStyles.TableStyleName(table, breakStyle) ?? breakStyle;
        if (tableStyle is not null)
        {
            writer.WriteAttributeString("table", "style-name", OdtNamespaces.Table, tableStyle);
        }


        for (var column = 0; column < columns; column++)
        {
            writer.WriteStartElement("table", "table-column", OdtNamespaces.Table);
            writer.WriteAttributeString("table", "style-name", OdtNamespaces.Table, ColumnStyleName(widths[column]));
            writer.WriteEndElement();
        }

        var index = 0;
        while (index < table.Rows.Count)
        {
            if (table.Rows[index].IsHeader)
            {
                writer.WriteStartElement("table", "table-header-rows", OdtNamespaces.Table);
                while (index < table.Rows.Count && table.Rows[index].IsHeader)
                {
                    WriteTableRow(
                        writer, table, index, table.Rows[index], index == table.Rows.Count - 1,
                        table.Style?.HeaderBackground, table.Style, remaining, columns, textStyles, paragraphStyles, images, tableWidth, xmlBehavior, state, numbering, fields);
                    index++;
                }

                writer.WriteEndElement();
            }
            else
            {
                var conditional = TableConditionalStyle.IsBanded(table, index) ? table.Style?.BandBackground : null;
                if (index == table.Rows.Count - 1 && table.Style?.LastRowBackground is { } lastRowBackground)
                {
                    conditional = lastRowBackground;
                }

                WriteTableRow(
                    writer, table, index, table.Rows[index], index == table.Rows.Count - 1,
                    conditional, table.Style, remaining, columns, textStyles, paragraphStyles, images, tableWidth, xmlBehavior, state, numbering, fields);
                index++;
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteTableRow(
        XmlWriter writer,
        TableBlock table,
        int rowIndex,
        TableRow row,
        bool lastRow,
        Color? conditionalBackground,
        TableStyle? style,
        int[] remaining,
        int columns,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        double availableWidth,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields = null)
    {
        writer.WriteStartElement("table", "table-row", OdtNamespaces.Table);
        if (paragraphStyles.CellStyles.RowStyleName(table, row, lastRow) is { } rowStyle)
        {
            writer.WriteAttributeString("table", "style-name", OdtNamespaces.Table, rowStyle);
        }

        var cellIndex = 0;
        var column = 0;
        while (column < columns)
        {
            if (remaining[column] > 0)
            {
                remaining[column]--;
                writer.WriteStartElement("table", "covered-table-cell", OdtNamespaces.Table);
                writer.WriteEndElement();
                column++;
                continue;
            }

            if (cellIndex >= row.Cells.Count)
            {
                column++;
                continue;
            }

            var cell = row.Cells[cellIndex++];
            var span = Math.Clamp(cell.ColumnSpan, 1, Math.Max(1, columns - column));
            if (cell.RowSpan > 1)
            {
                for (var offset = 0; offset < span && column + offset < columns; offset++)
                {
                    remaining[column + offset] = cell.RowSpan - 1;
                }
            }

            var styleBackground = conditionalBackground;
            if (!row.IsHeader && column == 0 && style?.FirstColumnBackground is { } firstColumn)
            {
                styleBackground = firstColumn;
            }
            else if (!row.IsHeader && column + span - 1 == columns - 1 && style?.LastColumnBackground is { } lastColumnBackground)
            {
                styleBackground = lastColumnBackground;
            }

            var lastColumn = cellIndex == row.Cells.Count || column + span - 1 == columns - 1;
            var area = TableConditionalStyle.AreaOf(table, style, row, rowIndex, column, columns);
            writer.WriteStartElement("table", "table-cell", OdtNamespaces.Table);
            writer.WriteAttributeString(
                "table", "style-name", OdtNamespaces.Table,
                paragraphStyles.CellStyles.CellStyleName(
                    table, cell, lastColumn, lastRow, cell.Background ?? styleBackground, area));
            if (span > 1)
            {
                writer.WriteAttributeString(
                    "table", "number-columns-spanned", OdtNamespaces.Table,
                    span.ToString(CultureInfo.InvariantCulture));
            }

            if (cell.RowSpan > 1)
            {
                writer.WriteAttributeString(
                    "table", "number-rows-spanned", OdtNamespaces.Table,
                    cell.RowSpan.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteAttributeString("office", "value-type", OdtNamespaces.Office, "string");
            using var spacing = paragraphStyles.Spacing.EnterCompact();

            if (cell.Blocks.Count == 0)
            {
                writer.WriteStartElement("text", "p", OdtNamespaces.Text);
                writer.WriteEndElement();
            }
            else
            {
                foreach (var block in cell.Blocks)
                {
                    switch (block)
                    {
                        case Paragraph paragraph:
                        {
                            var (paragraphStyle, referenceOnly, forceStyle) = OdtParagraphStyles.Resolve(
                                paragraph, paragraphStyles.Styles, paragraphStyles.SourceStyles);
                            var cellStyle = TableLayout.CellParagraphStyle(paragraphStyle, cell);
                            WriteParagraph(
                                writer, cellStyle, paragraph.Inlines,
                                textStyles, paragraphStyles, xmlBehavior, compact: true, paragraphStyles.Breaks.BreakBefore(paragraph), fields,
                                referenceOnly && Equals(cellStyle, paragraphStyle),
                                forceStyle || !Equals(cellStyle, paragraphStyle));
                            break;
                        }

                        case Heading heading:
                            WriteHeading(
                                writer, heading, TableLayout.CellParagraphStyle(heading.Style, cell),
                                textStyles, paragraphStyles, xmlBehavior, fields);
                            break;
                        default:
                            WriteBlock(writer, block, textStyles, paragraphStyles, images, availableWidth, xmlBehavior, state, numbering, fields);
                            break;
                    }
                }
            }

            writer.WriteEndElement();
            for (var extra = 1; extra < span; extra++)
            {
                writer.WriteStartElement("table", "covered-table-cell", OdtNamespaces.Table);
                writer.WriteEndElement();
            }

            column += span;
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// cell styles with borders and padding as in DOCX. Each cell draws its border
    /// left and top, the last cell of a row additionally right and the last row
    /// additionally bottom; this way no double lines arise inside.
    /// </summary>
    private static void WriteTableCellStyles(XmlWriter writer)
    {
        var border = Typography.TableBorderWidth.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt solid #000000";
        var horizontal = Typography.TableCellPaddingHorizontal.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";
        var vertical = Typography.TableCellPaddingVertical.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";

        foreach (var lastColumn in new[] { false, true })
        {
            foreach (var lastRow in new[] { false, true })
            {
                writer.WriteStartElement("style", "style", OdtNamespaces.Style);
                writer.WriteAttributeString("style", "name", OdtNamespaces.Style, CellStyleName(lastColumn, lastRow));
                writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "table-cell");
                writer.WriteStartElement("style", "table-cell-properties", OdtNamespaces.Style);
                writer.WriteAttributeString("fo", "padding-left", OdtNamespaces.Fo, horizontal);
                writer.WriteAttributeString("fo", "padding-right", OdtNamespaces.Fo, horizontal);
                writer.WriteAttributeString("fo", "padding-top", OdtNamespaces.Fo, vertical);
                writer.WriteAttributeString("fo", "padding-bottom", OdtNamespaces.Fo, vertical);
                writer.WriteAttributeString("fo", "border-left", OdtNamespaces.Fo, border);
                writer.WriteAttributeString("fo", "border-top", OdtNamespaces.Fo, border);
                writer.WriteAttributeString("fo", "border-right", OdtNamespaces.Fo, lastColumn ? border : "none");
                writer.WriteAttributeString("fo", "border-bottom", OdtNamespaces.Fo, lastRow ? border : "none");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
        }
    }

    private static string CellStyleName(bool lastColumn, bool lastRow)
        => "Cell" + (lastColumn ? "R" : string.Empty) + (lastRow ? "U" : string.Empty);

    private static void WriteColumnStyles(XmlWriter writer, IReadOnlyList<double> widths)
    {
        foreach (var width in widths)
        {
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, ColumnStyleName(width));
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "table-column");
            writer.WriteStartElement("style", "table-column-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "column-width", OdtNamespaces.Style, CentimetersText(width));
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    private static IReadOnlyList<double> CollectColumnWidths(PortableDocument document)
        => CollectColumnWidths(document.Sections.Select(section => ((IEnumerable<Block>)section.Blocks, ContentWidth(section))));

    private static IReadOnlyList<double> CollectColumnWidths(IEnumerable<(IEnumerable<Block> Blocks, double Available)> groups)
    {
        var widths = new List<double>();
        var styleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (blocks, available) in groups)
        {
            foreach (var table in EnumerateTables(blocks))
            {
                var columns = TableLayout.LogicalColumns(table);
                foreach (var width in TableLayout.ResolveColumnWidthsWithPercent(table, columns, available))
                {
                    if (styleNames.Add(ColumnStyleName(width)))
                    {
                        widths.Add(width);
                    }
                }
            }
        }

        return widths;
    }

    private static double ContentWidth(Section section)
    {
        var landscape = section.Orientation == PageOrientation.Landscape;
        var width = landscape ? section.PageSize.Height.Points : section.PageSize.Width.Points;
        return width - section.Margins.Left.Points - section.Margins.Right.Points;
    }

    private static IEnumerable<TableBlock> EnumerateTables(PortableDocument document)
    {
        foreach (var section in document.Sections)
        {
            foreach (var table in EnumerateTables(section.Blocks))
            {
                yield return table;
            }
        }
    }

    private static IEnumerable<TableBlock> EnumerateTables(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TableBlock table:
                    yield return table;
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var nested in EnumerateTables(cell.Blocks))
                            {
                                yield return nested;
                            }
                        }
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        foreach (var nested in EnumerateTables(item.Blocks))
                        {
                            yield return nested;
                        }
                    }

                    break;
            }
        }
    }

    private static string ColumnStyleName(double points)
        => "co" + CentimetersText(points).Replace('.', '_');

    private static string CentimetersText(Length length) => CentimetersText(length.Points);

    private static string CentimetersText(double points)
        => (points / (72.0 / 2.54)).ToString("0.###", CultureInfo.InvariantCulture) + "cm";

    /// <summary>Writes the automatic styles for bound template content.</summary>
    internal static void WriteTemplateAutomaticStyles(
        XmlWriter writer,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyList<double> columnWidths,
        bool needsPageBreakStyle,
        InvalidXmlCharacterBehavior xmlBehavior,
        IReadOnlyList<NumberingDefinition> numbering,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        IEnumerable<ListBlock>? lists = null)
    {
        WriteTextStyles(writer, textStyles, xmlBehavior);
        WriteImageStyles(writer, images);
        WriteParagraphStyles(writer, paragraphStyles, paragraphStyles.DefaultFontSize);
        var lookup = new NumberingLookup(numbering);
        WriteListStyles(writer, lists ?? Array.Empty<ListBlock>(), lookup);
        WriteColumnStyles(writer, columnWidths);
        if (columnWidths.Count > 0)
        {
            WriteTableCellStyles(writer);
            paragraphStyles.CellStyles.Write(writer);
        }

        if (needsPageBreakStyle)
        {
            WritePageBreakStyle(writer);
        }
    }

    /// <summary>Writes bound blocks with the pre-collected styles (for templates).</summary>
    internal static void WriteTemplateBlocks(
        XmlWriter writer,
        IEnumerable<Block> blocks,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        double availableWidth,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        NumberingLookup numbering,
        FieldContext? fields = null)
    {
        foreach (var block in blocks)
        {
            WriteBlock(writer, block, textStyles, paragraphStyles, images, availableWidth, xmlBehavior, state, numbering, fields);
        }
    }

    /// <summary>Collects the column widths of the tables of bound template content.</summary>
    internal static IReadOnlyList<double> CollectTemplateColumnWidths(IEnumerable<Block> blocks, double availableWidth)
        => CollectColumnWidths(new[] { ((IEnumerable<Block>)blocks, availableWidth) });

    private static void WriteParagraph(
        XmlWriter writer,
        ParagraphStyle style,
        IReadOnlyList<Inline> inlines,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        InvalidXmlCharacterBehavior xmlBehavior,
        bool compact = false,
        OdtBreak? pageBreak = null,
        FieldContext? fields = null,
        bool referenceOnly = false,
        bool forceStyle = false)
    {
        writer.WriteStartElement("text", "p", OdtNamespaces.Text);
        if (paragraphStyles.TryGetName(style, compact, pageBreak, referenceOnly, forceStyle, out var name))
        {
            writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, name);
        }
        else if (!string.IsNullOrEmpty(style.StyleName))
        {
            writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, style.StyleName);
        }

        // The paragraph style (automatic or named) carries the run formatting; the spans state the
        // deviations only.
        WriteInlines(
            writer,
            inlines,
            textStyles,
            xmlBehavior,
            paragraphStyles,
            fields,
            style,
            RunFormat.HasFormatting(style) ? RunFormat.Of(style) : RunFormat.Empty);
        writer.WriteEndElement();
    }

    private static void WriteHeading(
        XmlWriter writer,
        Heading heading,
        ParagraphStyle style,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        InvalidXmlCharacterBehavior xmlBehavior,
        FieldContext? fields = null)
    {
        writer.WriteStartElement("text", "h", OdtNamespaces.Text);
        writer.WriteAttributeString(
            "text", "outline-level", OdtNamespaces.Text, ((int)heading.Level).ToString(CultureInfo.InvariantCulture));
        var automatic = paragraphStyles.TryGetName(style, compact: false, paragraphStyles.Breaks.BreakBefore(heading), out var name);
        writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text,
            automatic ? name : Heading.StyleNameFor(heading.Level));
        WriteInlines(writer, heading.Inlines, textStyles, xmlBehavior, paragraphStyles, fields, style);
        writer.WriteEndElement();
    }

    private static void WriteNote(
        XmlWriter writer,
        Footnote footnote,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtParagraphStyles paragraphStyles,
        FieldContext? fields = null)
    {
        if (paragraphStyles.InBand)
        {
            // A band carries no note: LibreOffice drops a text:note there (so does Word for
            // DOCX), so the text stays visible at the reference (see NoteScan.Text).
            var bandText = NoteScan.Text(footnote);
            if (bandText.Length > 0)
            {
                WriteTextRun(writer, new TextRun(bandText), textStyles, xmlBehavior);
            }

            return;
        }

        var isEndnote = footnote.Kind == NoteKind.Endnote;
        var number = isEndnote ? paragraphStyles.NextEndnoteIndex() : paragraphStyles.NextFootnoteIndex();
        var id = (isEndnote ? "end" : "ftn") + number.ToString(CultureInfo.InvariantCulture);

        writer.WriteStartElement("text", "note", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "id", OdtNamespaces.Text, id);
        writer.WriteAttributeString("text", "note-class", OdtNamespaces.Text, isEndnote ? "endnote" : "footnote");
        writer.WriteStartElement("text", "note-citation", OdtNamespaces.Text);
        writer.WriteString(number.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteStartElement("text", "note-body", OdtNamespaces.Text);
        foreach (var block in footnote.Blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    WriteParagraph(writer, paragraph.Style, paragraph.Inlines, textStyles, paragraphStyles, xmlBehavior, compact: true, fields: fields);
                    break;
                case Heading heading:
                    WriteHeading(writer, heading, heading.Style, textStyles, paragraphStyles, xmlBehavior, fields);
                    break;
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>
    /// the run as it is written: the paragraph style provides the base, the character style overrides
    /// it and the own formatting wins (ADR-0010). ODF has no style mechanism for the layout of a run
    /// inside a paragraph's own style, so the span carries the resolved formatting.
    /// </summary>
    private static TextRun Resolve(
        TextRun run,
        ParagraphStyle? paragraphStyle,
        OdtParagraphStyles? paragraphStyles,
        RunFormat baseFormat,
        out double fontSizeForPosition)
    {
        var defaultFontSize = paragraphStyles?.DefaultFontSize ?? 11.0;

        // The common case: nothing to resolve, so no temporary objects.
        if (run.CharacterStyleName is null
            && (paragraphStyle is null || !RunFormat.HasFormatting(paragraphStyle)))
        {
            fontSizeForPosition = run.FontSize?.Points ?? paragraphStyle?.FontSize?.Points ?? defaultFontSize;
            return run;
        }

        var characterStyle = paragraphStyles is null
            ? null
            : RunFormatResolver.Find(paragraphStyles.CharacterStyles, run.CharacterStyleName);
        var resolved = RunFormatResolver.Resolve(run, paragraphStyle, characterStyle);
        fontSizeForPosition = resolved.FontSize?.Points ?? defaultFontSize;
        return RunFormat.Of(resolved).Difference(baseFormat).ToRun(run.Text, run.CharacterStyleName);
    }

    internal static void WriteInlines(
        XmlWriter writer,
        IReadOnlyList<Inline> inlines,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtParagraphStyles? paragraphStyles = null,
        FieldContext? fields = null,
        ParagraphStyle? paragraphStyle = null,
        RunFormat? baseFormat = null)
    {
        // A paragraph with tracked changes (or inside a deletion) is written along its plan (ADR-0027).
        if (paragraphStyles?.Revisions is { } plan && plan.Paragraphs.TryGetValue(inlines, out var ops))
        {
            foreach (var op in ops)
            {
                if (op is OdtRevisions.Content content)
                {
                    WriteInlines(writer, [content.Inline], textStyles, xmlBehavior, paragraphStyles, fields, paragraphStyle, baseFormat);
                }
                else
                {
                    OdtRevisions.WriteMarker(writer, op);
                }
            }

            return;
        }

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextRun textRun:
                    var resolved = Resolve(textRun, paragraphStyle, paragraphStyles, baseFormat ?? RunFormat.Empty, out var fontSizeForPosition);
                    WriteTextRun(writer, resolved, textStyles, xmlBehavior, fontSizeForPosition);
                    break;
                case Hyperlink hyperlink:
                    writer.WriteStartElement("text", "a", OdtNamespaces.Text);
                    writer.WriteAttributeString("xlink", "href", OdtNamespaces.XLink, XmlText.Clean(hyperlink.Target, xmlBehavior));
                    writer.WriteAttributeString("xlink", "type", OdtNamespaces.XLink, "simple");
                    WriteInlines(writer, hyperlink.Inlines, textStyles, xmlBehavior, paragraphStyles, fields, paragraphStyle, baseFormat);
                    writer.WriteEndElement();
                    break;
                case Bookmark bookmark:
                    // A bookmark with an explicit end is a range (OF-C03).
                    writer.WriteStartElement(
                        "text",
                        paragraphStyles is not null && paragraphStyles.BookmarkRanges.Contains(bookmark.Name)
                            ? "bookmark-start"
                            : "bookmark",
                        OdtNamespaces.Text);
                    writer.WriteAttributeString("text", "name", OdtNamespaces.Text, bookmark.Name);
                    writer.WriteEndElement();
                    break;
                case AlignmentTab:
                    // Only left in a table cell or a text box (see AlignmentTabs): a plain tab there.
                    writer.WriteStartElement("text", "tab", OdtNamespaces.Text);
                    writer.WriteEndElement();
                    break;
                case IndexEntry indexEntry:
                    // A sub entry is stated as the entry with the main entry as its first key.
                    writer.WriteStartElement("text", "alphabetical-index-mark", OdtNamespaces.Text);
                    writer.WriteAttributeString("text", "string-value", OdtNamespaces.Text, XmlText.Clean(indexEntry.SubText ?? indexEntry.Text, xmlBehavior));
                    if (indexEntry.SubText is not null)
                    {
                        writer.WriteAttributeString("text", "key1", OdtNamespaces.Text, XmlText.Clean(indexEntry.Text, xmlBehavior));
                    }

                    writer.WriteEndElement();
                    break;
                case BookmarkEnd bookmarkEnd:
                    writer.WriteStartElement("text", "bookmark-end", OdtNamespaces.Text);
                    writer.WriteAttributeString("text", "name", OdtNamespaces.Text, bookmarkEnd.Name);
                    writer.WriteEndElement();
                    break;
                case Comment comment when paragraphStyles is not null:
                    OdtComments.WriteStart(writer, comment, paragraphStyles, xmlBehavior);
                    break;
                case CommentEnd commentEnd when paragraphStyles is not null:
                    OdtComments.WriteEnd(writer, commentEnd, paragraphStyles, handler: null);
                    break;
                case Footnote footnote when paragraphStyles is not null:
                    WriteNote(writer, footnote, textStyles, xmlBehavior, paragraphStyles);
                    break;
                case InlineImage inlineImage:
                    // All images are registered in advance (see WriteImageBlock).
                    if (paragraphStyles?.Images is not { } inlineImages || !inlineImages.TryGetValue(inlineImage.Image, out var inlineResource))
                    {
                        throw new InvalidOperationException("ODT: inline image without a registered resource.");
                    }

                    WriteImageFrame(writer, inlineImage.Image, inlineResource, xmlBehavior, inLine: true);
                    break;
                case PageNumberField:
                    // The text is only a placeholder; the application inserts the current page.
                    writer.WriteStartElement("text", "page-number", OdtNamespaces.Text);
                    writer.WriteAttributeString("text", "select-page", OdtNamespaces.Text, "current");
                    writer.WriteString("1");
                    writer.WriteEndElement();
                    break;
                case PageCountField:
                    writer.WriteStartElement("text", "page-count", OdtNamespaces.Text);
                    writer.WriteString("1");
                    writer.WriteEndElement();
                    break;
                case DateField dateField:
                    var dateValue = dateField.Value ?? DateTimeOffset.Now;
                    writer.WriteStartElement("text", "date", OdtNamespaces.Text);
                    writer.WriteAttributeString(
                        "text", "date-value", OdtNamespaces.Text,
                        dateValue.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                    writer.WriteString(dateValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                    break;
                case DocPropertyField docProperty:
                    // The built-in properties live in dc:/meta: elements, not in meta:user-defined;
                    // LibreOffice shows them only through their own fields.
                    if (OdtFields.InfoElementOf(docProperty.Name) is { } info)
                    {
                        writer.WriteStartElement("text", info, OdtNamespaces.Text);
                        if (info == "initial-creator")
                        {
                            // As for AUTHOR (OdtFields.Map): without fixed LibreOffice shows nothing.
                            writer.WriteAttributeString("text", "fixed", OdtNamespaces.Text, "true");
                        }
                    }
                    else
                    {
                        writer.WriteStartElement("text", "user-defined", OdtNamespaces.Text);
                        writer.WriteAttributeString("meta", "name", OdtNamespaces.Meta, docProperty.Name);
                    }

                    if (fields?.Properties.GetValueOrDefault(docProperty.Name) is { } property)
                    {
                        writer.WriteString(XmlText.Clean(property, xmlBehavior));
                    }

                    writer.WriteEndElement();
                    break;
                case DocVariableField docVariable:
                    writer.WriteStartElement("text", "user-field-get", OdtNamespaces.Text);
                    writer.WriteAttributeString("text", "name", OdtNamespaces.Text, docVariable.Name);
                    if (fields?.Variables.GetValueOrDefault(docVariable.Name) is { } variable)
                    {
                        writer.WriteString(XmlText.Clean(variable, xmlBehavior));
                    }

                    writer.WriteEndElement();
                    break;
                case PageRefField pageRef:
                    writer.WriteStartElement("text", "bookmark-ref", OdtNamespaces.Text);
                    writer.WriteAttributeString("text", "ref-name", OdtNamespaces.Text, pageRef.BookmarkName);
                    writer.WriteAttributeString("text", "reference-format", OdtNamespaces.Text, "page");
                    writer.WriteEndElement();
                    break;
                case Field field:
                {
                    // A field without a result is calculated (AJ02).
                    var result = field.Result;
                    if (result is null && fields is not null)
                    {
                        result = FieldEvaluator.Evaluate(field, fields);
                    }

                    // OF-C02: a field ODF can express is written as its element with the result as
                    // the displayed text; any other field keeps only its result as text.
                    if (OdtFields.Map(field.Instruction) is { } mapped)
                    {
                        writer.WriteStartElement("text", mapped.LocalName, OdtNamespaces.Text);
                        foreach (var (ns, prefix, name, value) in mapped.Attributes)
                        {
                            writer.WriteAttributeString(prefix, name, ns.NamespaceName, XmlText.Clean(value, xmlBehavior));
                        }

                        if (result is not null)
                        {
                            WriteText(writer, result, xmlBehavior);
                        }

                        writer.WriteEndElement();
                    }
                    else if (result is not null)
                    {
                        WriteTextRun(writer, new TextRun(result), textStyles, xmlBehavior);
                    }

                    break;
                }
            }
        }
    }

    private static void WriteTextRun(
        XmlWriter writer,
        TextRun textRun,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        InvalidXmlCharacterBehavior xmlBehavior,
        double? fontSizeForPosition = null)
    {
        if (OdtAutomaticStyles.KeyOf(textRun, fontSizeForPosition) is { } key && textStyles.TryGetValue(key, out var styleName))
        {
            writer.WriteStartElement("text", "span", OdtNamespaces.Text);
            writer.WriteAttributeString("text", "style-name", OdtNamespaces.Text, styleName);
            WriteText(writer, textRun.Text, xmlBehavior);
            writer.WriteEndElement();
        }
        else
        {
            WriteText(writer, textRun.Text, xmlBehavior);
        }
    }

    internal static void WriteText(XmlWriter writer, string text, InvalidXmlCharacterBehavior xmlBehavior)
    {
        text = XmlText.Clean(text, xmlBehavior);
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];

            if (character == ' ')
            {
                var start = index;
                while (index < text.Length && text[index] == ' ')
                {
                    index++;
                }

                var count = index - start;
                writer.WriteStartElement("text", "s", OdtNamespaces.Text);
                if (count > 1)
                {
                    writer.WriteAttributeString(
                        "text", "c", OdtNamespaces.Text, count.ToString(CultureInfo.InvariantCulture));
                }

                writer.WriteEndElement();
            }
            else if (character == '\t')
            {
                writer.WriteStartElement("text", "tab", OdtNamespaces.Text);
                writer.WriteEndElement();
                index++;
            }
            else if (character is '\n' or '\r')
            {
                writer.WriteStartElement("text", "line-break", OdtNamespaces.Text);
                writer.WriteEndElement();
                index += character == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            }
            else
            {
                var start = index;
                while (index < text.Length && text[index] is not (' ' or '\t' or '\n' or '\r'))
                {
                    index++;
                }

                writer.WriteString(text[start..index]);
            }
        }
    }
}
