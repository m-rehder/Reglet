using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// writes <c>styles.xml</c>: document styles (office:styles), one per section
/// page layout (automatic styles) and a master page as well as section styles that
/// assign a master page. Content styles remain in content.xml.
/// </summary>
internal static class OdtStylesWriter
{
    public static void Write(
        OdtPackageWriter package,
        PortableDocument document,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        bool autoHyphenation = false,
        OdtEmbeddedFonts? embeddedFonts = null)
    {
        package.AddXmlPart(
            "styles.xml",
            writer => WriteDocumentStyles(writer, document, images, xmlBehavior, autoHyphenation, embeddedFonts));
    }

    private static void WriteDocumentStyles(
        XmlWriter writer,
        PortableDocument document,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        bool autoHyphenation,
        OdtEmbeddedFonts? embeddedFonts)
    {
        var bandStyles = OdtContentWriter.CollectBandStyles(document);
        // AJ02: the general fields in the headers and footers are calculated.
        var fields = FieldContext.FromMetadata(document.Metadata);

        writer.WriteStartElement("office", "document-styles", OdtNamespaces.Office);
        writer.WriteAttributeString("office", "version", OdtNamespaces.Office, "1.3");

        OdtFontFaces.Write(
            writer,
            bandStyles.TextStyles.Keys.Select(key => key.FontFamily).OfType<string>().Prepend(document.Defaults.FontFamily),
            xmlBehavior,
            embeddedFonts);

        writer.WriteStartElement("office", "styles", OdtNamespaces.Office);
        writer.WriteStartElement("style", "default-style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
        writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
        var paragraphDefaults = document.Defaults.ParagraphStyle;
        var spaceAfter = paragraphDefaults.SpaceAfter ?? Typography.ParagraphSpaceAfter;
        writer.WriteAttributeString("fo", "margin-bottom", OdtNamespaces.Fo, PointsText(spaceAfter));
        if (paragraphDefaults.SpaceBefore is { } spaceBefore)
        {
            writer.WriteAttributeString("fo", "margin-top", OdtNamespaces.Fo, PointsText(spaceBefore));
        }

        OdtContentWriter.WriteParagraphFormat(writer, paragraphDefaults);
        if (autoHyphenation)
        {
            writer.WriteAttributeString("fo", "hyphenate", OdtNamespaces.Fo, "true");
        }

        writer.WriteEndElement();
        writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "font-name", OdtNamespaces.Style, OdtFontFaces.Name(document.Defaults.FontFamily, xmlBehavior));
        writer.WriteAttributeString("fo", "font-size", OdtNamespaces.Fo, PointsText(document.Defaults.FontSize));
        if (document.Defaults.TextColor != Color.Black)
        {
            writer.WriteAttributeString("fo", "color", OdtNamespaces.Fo, document.Defaults.TextColor.ToString());
        }

        if (!string.IsNullOrEmpty(document.Metadata.Language))
        {
            // Q04: language markup at the document level.
            var parts = document.Metadata.Language.Split('-', 2);
            writer.WriteAttributeString("fo", "language", OdtNamespaces.Fo, parts[0]);
            if (parts.Length == 2 && parts[1].Length == 2)
            {
                writer.WriteAttributeString("fo", "country", OdtNamespaces.Fo, parts[1].ToUpperInvariant());
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        OdtShapes.WriteDashStyles(writer);
        WriteHeadingStyles(writer);
        WriteNamedStyles(writer, document.Styles, document.Defaults.FontSize.Points);
        WriteNamedCharacterStyles(writer, document.CharacterStyles, document.Defaults.FontSize.Points);
        WriteLineNumbering(writer, document);

        for (var index = 1; index <= document.Sections.Count; index++)
        {
            WriteSectionStyle(writer, index, document.Sections[index - 1]);
        }

        writer.WriteEndElement();

        writer.WriteStartElement("office", "automatic-styles", OdtNamespaces.Office);
        OdtContentWriter.WriteImageStyles(writer, images);
        OdtShapes.WriteStyles(writer, document);
        for (var index = 1; index <= document.Sections.Count; index++)
        {
            WritePageLayout(writer, index, document.Sections[index - 1], document.Defaults);
        }

        if (document.Sections.Any(section => section.Header is not null || section.Footer is not null
            || section.FirstHeader is not null || section.FirstFooter is not null || section.EvenHeader is not null || section.EvenFooter is not null))
        {
            OdtContentWriter.WriteBandAutomaticStyles(writer, bandStyles, xmlBehavior);
        }

        writer.WriteEndElement();

        writer.WriteStartElement("office", "master-styles", OdtNamespaces.Office);
        var state = new OdtBlockState(OdtContentWriter.CountBodyTables(document));
        for (var index = 1; index <= document.Sections.Count; index++)
        {
            WriteMasterPage(writer, index, document.Sections[index - 1], bandStyles, images, xmlBehavior, state, fields);
        }

        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    /// <summary>
    /// Writes the line numbering configuration (phase D). ODF defines it document-wide in
    /// <c>office:styles</c>; the settings of the first section with line numbering are used.
    /// </summary>
    private static void WriteLineNumbering(XmlWriter writer, PortableDocument document)
    {
        var numbering = document.Sections
            .Select(section => section.LineNumbering)
            .FirstOrDefault(value => value is not null);
        if (numbering is null)
        {
            return;
        }

        writer.WriteStartElement("text", "linenumbering-configuration", OdtNamespaces.Text);
        writer.WriteAttributeString("text", "number-lines", OdtNamespaces.Text, "true");
        writer.WriteAttributeString("text", "increment", OdtNamespaces.Text, numbering.CountBy.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("text", "offset", OdtNamespaces.Text, PointsText(numbering.Distance));
        writer.WriteAttributeString("text", "number-position", OdtNamespaces.Text, "left");
        writer.WriteAttributeString("text", "restart-on-page", OdtNamespaces.Text, (numbering.Restart != LineNumberRestart.Continuous).ToString().ToLowerInvariant());
        writer.WriteEndElement();
    }

    /// <summary>
    /// Writes the document's named character styles, so the text style references of the runs
    /// resolve. The properties are tri-state (ADR-0010): an unset property stays absent, an explicit
    /// off is written as <c>normal</c>/<c>none</c>.
    /// </summary>
    internal static void WriteNamedCharacterStyles(
        XmlWriter writer,
        IReadOnlyList<CharacterStyle> styles,
        double defaultFontSize)
    {
        foreach (var style in styles)
        {
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, style.Name);
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "text");
            writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);
            WriteTextProperties(writer, RunFormat.Of(style), defaultFontSize);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// writes the run formatting of a style as <c>style:text-properties</c> (ADR-0010). A property
    /// the style does not set stays absent so the inherited value applies; an explicit off is
    /// written as <c>normal</c> or <c>none</c>.
    /// </summary>
    /// <summary>Writes the text effects as outline, shadow and relief attributes.</summary>
    internal static void WriteEffects(XmlWriter writer, TextEffects? effects)
    {
        if (effects is not { } value)
        {
            return;
        }

        writer.WriteAttributeString("style", "text-outline", OdtNamespaces.Style, value.HasFlag(TextEffects.Outline) ? "true" : "false");
        writer.WriteAttributeString("fo", "text-shadow", OdtNamespaces.Fo, value.HasFlag(TextEffects.Shadow) ? "1pt 1pt" : "none");
        writer.WriteAttributeString("style", "font-relief", OdtNamespaces.Style, value.HasFlag(TextEffects.Emboss) ? "embossed" : value.HasFlag(TextEffects.Engrave) ? "engraved" : "none");
    }

    internal static void WriteTextProperties(XmlWriter writer, RunFormat format, double? defaultFontSize = null)
    {
        if (format.Bold is { } bold)
        {
            writer.WriteAttributeString("fo", "font-weight", OdtNamespaces.Fo, bold ? "bold" : "normal");
        }

        if (format.Italic is { } italic)
        {
            writer.WriteAttributeString("fo", "font-style", OdtNamespaces.Fo, italic ? "italic" : "normal");
        }

        if (format.AllCaps is { } allCaps)
        {
            writer.WriteAttributeString("fo", "text-transform", OdtNamespaces.Fo, allCaps ? "uppercase" : "none");
        }

        if (format.SmallCaps is { } smallCaps)
        {
            writer.WriteAttributeString("fo", "font-variant", OdtNamespaces.Fo, smallCaps ? "small-caps" : "normal");
        }

        WriteEffects(writer, format.Effects);

        if (format.Strikethrough is { } strikethrough)
        {
            // The line style stays solid; single or double is the line-through type.
            writer.WriteAttributeString(
                "style",
                "text-line-through-style",
                OdtNamespaces.Style,
                strikethrough == TextStrikethrough.None ? "none" : "solid");
            if (strikethrough != TextStrikethrough.None)
            {
                writer.WriteAttributeString(
                    "style",
                    "text-line-through-type",
                    OdtNamespaces.Style,
                    strikethrough == TextStrikethrough.Double ? "double" : "single");
            }
        }

        if (format.EffectiveUnderline is { } underline)
        {
            writer.WriteAttributeString(
                "style",
                "text-underline-style",
                OdtNamespaces.Style,
                underline == TextUnderlineStyle.None ? "none" : OdtContentWriter.UnderlineValue(underline));
            if (underline != TextUnderlineStyle.None)
            {
                if (underline == TextUnderlineStyle.Double)
                    writer.WriteAttributeString("style", "text-underline-type", OdtNamespaces.Style, "double");
                writer.WriteAttributeString("style", "text-underline-width", OdtNamespaces.Style, "auto");
                if (format.UnderlineColor is { } underlineColor)
                    writer.WriteAttributeString("style", "text-underline-color", OdtNamespaces.Style, underlineColor.ToString());
            }
        }

        if (format.CharacterSpacing is { } spacing)
            writer.WriteAttributeString("fo", "letter-spacing", OdtNamespaces.Fo, PointsText(spacing));
        if (format.HorizontalScaling is { } scaling)
            writer.WriteAttributeString("style", "text-scale", OdtNamespaces.Style, scaling.ToString("0.##", CultureInfo.InvariantCulture) + "%");
        if (OdtAutomaticStyles.BorderText(format.Border) is { } border)
            writer.WriteAttributeString("fo", "border", OdtNamespaces.Fo, border);

        if (format.Kerning is not null)
            writer.WriteAttributeString("style", "letter-kerning", OdtNamespaces.Style, "true");

        if (format.Position is { } position && (format.FontSize?.Points ?? defaultFontSize) is { } sizeForPosition
            && sizeForPosition > 0)
        {
            // The style may inherit the font size from its parent or the document defaults; ODF
            // needs the size to turn the absolute shift into a percentage.
            var percent = position.Points / sizeForPosition * 100.0;
            writer.WriteAttributeString("style", "text-position", OdtNamespaces.Style,
                percent.ToString("0.##", CultureInfo.InvariantCulture) + "%");
        }
        else if (format.Baseline is { } baseline)
        {
            writer.WriteAttributeString(
                "style",
                "text-position",
                OdtNamespaces.Style,
                baseline switch
                {
                    TextBaseline.Superscript => "super",
                    TextBaseline.Subscript => "sub",
                    _ => "0% 0%",
                });
        }

        if (format.Hidden is { } hidden)
        {
            writer.WriteAttributeString("text", "display", OdtNamespaces.Text, hidden ? "none" : "true");
        }

        if (format.FontFamily is { } family)
        {
            writer.WriteAttributeString("style", "font-name", OdtNamespaces.Style, family);
        }

        if (format.FontSize is { } size)
        {
            writer.WriteAttributeString("fo", "font-size", OdtNamespaces.Fo, PointsText(size));
        }

        if (format.Color is { } color)
        {
            writer.WriteAttributeString("fo", "color", OdtNamespaces.Fo, color.ToString());
        }

        // ODF has one background color; a highlight wins when both are set.
        if ((format.Highlight ?? format.Background) is { } background)
        {
            writer.WriteAttributeString("fo", "background-color", OdtNamespaces.Fo, background.ToString());
        }
    }

    /// <summary>Writes the document's named paragraph styles (phase AC), so ODT parent-style references resolve.</summary>
    private static void WriteNamedStyles(
        XmlWriter writer,
        IReadOnlyList<ParagraphStyle> styles,
        double defaultFontSize)
    {
        foreach (var style in styles)
        {
            if (string.IsNullOrEmpty(style.StyleName))
            {
                continue;
            }

            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, style.StyleName);
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
            writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
            OdtContentWriter.WriteNullableLengthIfDifferent(writer, "margin-top", style.SpaceBefore, parent: null);
            OdtContentWriter.WriteSpaceAfter(writer, style, compact: false);
            OdtContentWriter.WriteParagraphFormat(writer, style);
            writer.WriteEndElement();

            // ADR-0010: the run formatting of the paragraph style; the runs inherit it.
            var format = RunFormat.Of(style);
            if (!format.IsEmpty)
            {
                writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);
                WriteTextProperties(writer, format, defaultFontSize);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }
    }

    private static void WriteLengthIfNonZero(XmlWriter writer, string name, Length length)
    {
        if (Math.Abs(length.Points) > 1e-9)
        {
            writer.WriteAttributeString("fo", name, OdtNamespaces.Fo, PointsText(length));
        }
    }

    private static void WriteHeadingStyles(XmlWriter writer)
    {
        for (var level = 1; level <= 6; level++)
        {
            writer.WriteStartElement("style", "style", OdtNamespaces.Style);
            writer.WriteAttributeString("style", "name", OdtNamespaces.Style, $"Heading{level}");
            writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "paragraph");
            writer.WriteStartElement("style", "text-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("fo", "font-weight", OdtNamespaces.Fo, "bold");
            writer.WriteAttributeString("fo", "font-size", OdtNamespaces.Fo, PointsText(Typography.HeadingSize((HeadingLevel)level)));
            writer.WriteEndElement();
            writer.WriteStartElement("style", "paragraph-properties", OdtNamespaces.Style);
            writer.WriteAttributeString("fo", "margin-top", OdtNamespaces.Fo, PointsText(Typography.HeadingSpaceBefore));
            writer.WriteAttributeString("fo", "margin-bottom", OdtNamespaces.Fo, PointsText(Typography.HeadingSpaceAfter));

            // Like keepNext in DOCX: a heading never stands alone at the end of a page.
            writer.WriteAttributeString("fo", "keep-with-next", OdtNamespaces.Fo, "always");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    private static void WriteSectionStyle(XmlWriter writer, int index, Section section)
    {
        writer.WriteStartElement("style", "style", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, OdtSectionNames.SectionStyle(index));
        writer.WriteAttributeString("style", "family", OdtNamespaces.Style, "section");
        writer.WriteStartElement("style", "section-properties", OdtNamespaces.Style);

        if (section.TextColumns is { } columns && columns > 1)
        {
            writer.WriteStartElement("style", "columns", OdtNamespaces.Style);
            writer.WriteAttributeString("fo", "column-count", OdtNamespaces.Fo, columns.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("fo", "column-gap", OdtNamespaces.Fo, "0.5cm");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WritePageLayout(XmlWriter writer, int index, Section section, DocumentDefaults defaults)
    {
        var landscape = section.Orientation == PageOrientation.Landscape;
        var width = landscape ? section.PageSize.Height : section.PageSize.Width;
        var height = landscape ? section.PageSize.Width : section.PageSize.Height;

        writer.WriteStartElement("style", "page-layout", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, OdtSectionNames.PageLayout(index));
        writer.WriteStartElement("style", "page-layout-properties", OdtNamespaces.Style);
        writer.WriteAttributeString("fo", "page-width", OdtNamespaces.Fo, PointsText(width));
        writer.WriteAttributeString("fo", "page-height", OdtNamespaces.Fo, PointsText(height));
        // As in DOCX, a header/footer lies within the page margin, at the distance
        // Typography.HeaderFooterDistance from the sheet edge. In ODT it counts towards the type area; the
        // page margin is therefore reduced to this distance and the rest is added to the area
        // added so that the body still begins at the page margin.
        var header = BandLayout(section.Header ?? section.FirstHeader ?? section.EvenHeader, section.Margins.Top, section.HeaderDistance, defaults, isFooter: false);
        var footer = BandLayout(section.Footer ?? section.FirstFooter ?? section.EvenFooter, section.Margins.Bottom, section.FooterDistance, defaults, isFooter: true);
        writer.WriteAttributeString("fo", "margin-top", OdtNamespaces.Fo, PointsText(header?.PageMargin ?? section.Margins.Top));
        writer.WriteAttributeString("fo", "margin-bottom", OdtNamespaces.Fo, PointsText(footer?.PageMargin ?? section.Margins.Bottom));
        writer.WriteAttributeString("fo", "margin-left", OdtNamespaces.Fo, PointsText(section.Margins.Left));
        writer.WriteAttributeString("fo", "margin-right", OdtNamespaces.Fo, PointsText(section.Margins.Right));

        // The page border of the section (AJ05): ODF 1.3 knows the border properties of the
        // page layout (fo:border-top and so on).
        if (section.PageBorders is { } borders && !borders.IsEmpty)
        {
            WritePageBorder(writer, "fo", "border-top", borders.Top);
            WritePageBorder(writer, "fo", "border-bottom", borders.Bottom);
            WritePageBorder(writer, "fo", "border-left", borders.Left);
            WritePageBorder(writer, "fo", "border-right", borders.Right);
        }

        writer.WriteEndElement();

        if (header is { } headerLayout)
        {
            WriteBandStyle(writer, "header-style", headerLayout, "margin-bottom");
        }

        if (footer is { } footerLayout)
        {
            WriteBandStyle(writer, "footer-style", footerLayout, "margin-top");
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// writes one side of the page border as a border string (width, style and color, as in
    /// the XSL border properties).
    /// </summary>
    private static void WritePageBorder(XmlWriter writer, string prefix, string name, ParagraphBorder? border)
    {
        if (border is null || border.Style == BorderStyle.None)
        {
            return;
        }

        var style = border.Style switch
        {
            BorderStyle.Double => "double",
            BorderStyle.Dotted => "dotted",
            BorderStyle.Dashed => "dashed",
            _ => "solid",
        };

        var color = border.Color.ToString();
        writer.WriteAttributeString(
            prefix,
            name,
            OdtNamespaces.Fo,
            $"{border.Width.Points.ToString("0.##", CultureInfo.InvariantCulture)}pt {style} {color}");
    }

    /// <summary>Page margin, minimum height and body spacing of an area.</summary>
    private sealed record BandMetrics(Length PageMargin, Length MinHeight, Length Spacing);

    /// <summary>
    /// reproduces Word's rule: the body begins at max(page margin, distance + height of the
    /// area). The header fills the whole rest of the page margin as a minimum height for this;
    /// its content sits at the top and grows beyond the margin into the body only later. The
    /// footer has one line of minimum height and the rest as fixed spacing, because ODF
    /// aligns content at the top as well; this is exact for single-line footers, multi-line
    /// shorten the body somewhat more than Word.
    /// </summary>
    private static BandMetrics? BandLayout(HeaderFooter? band, Length margin, Length? distance, DocumentDefaults defaults, bool isFooter)
    {
        if (band is null)
        {
            return null;
        }

        var pageMargin = Math.Min((distance ?? Typography.HeaderFooterDistance).Points, margin.Points);
        var available = margin.Points - pageMargin;
        var minHeight = isFooter
            ? Math.Min(defaults.FontSize.Points * Typography.LineHeightFactor, available)
            : available;
        return new BandMetrics(
            Length.FromPoints(pageMargin),
            Length.FromPoints(minHeight),
            Length.FromPoints(available - minHeight));
    }

    private static void WriteBandStyle(XmlWriter writer, string localName, BandMetrics metrics, string spacingAttribute)
    {
        writer.WriteStartElement("style", localName, OdtNamespaces.Style);
        writer.WriteStartElement("style", "header-footer-properties", OdtNamespaces.Style);
        writer.WriteAttributeString("fo", "min-height", OdtNamespaces.Fo, PointsText(metrics.MinHeight));
        writer.WriteAttributeString("fo", spacingAttribute, OdtNamespaces.Fo, PointsText(metrics.Spacing));

        // Without explicit spacing, LibreOffice actually reserves the spacing.
        writer.WriteAttributeString("style", "dynamic-spacing", OdtNamespaces.Style, "false");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteMasterPage(
        XmlWriter writer,
        int index,
        Section section,
        OdtContentWriter.BandStyles bandStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        InvalidXmlCharacterBehavior xmlBehavior,
        OdtBlockState state,
        FieldContext? fields = null)
    {
        writer.WriteStartElement("style", "master-page", OdtNamespaces.Style);
        writer.WriteAttributeString("style", "name", OdtNamespaces.Style, OdtSectionNames.MasterPage(index));
        writer.WriteAttributeString("style", "page-layout-name", OdtNamespaces.Style, OdtSectionNames.PageLayout(index));

        if (section.Header is { } header)
        {
            OdtContentWriter.WriteBand(writer, "header", header, section, bandStyles, images, xmlBehavior, state, fields);
        }

        if (section.EvenHeader is { } evenHeader)
            OdtContentWriter.WriteBand(writer, "header-left", evenHeader, section, bandStyles, images, xmlBehavior, state, fields);
        if (section.FirstHeader is { } firstHeader)
            OdtContentWriter.WriteBand(writer, "header-first", firstHeader, section, bandStyles, images, xmlBehavior, state, fields);

        if (section.Footer is { } footer)
        {
            OdtContentWriter.WriteBand(writer, "footer", footer, section, bandStyles, images, xmlBehavior, state, fields);
        }

        if (section.EvenFooter is { } evenFooter)
            OdtContentWriter.WriteBand(writer, "footer-left", evenFooter, section, bandStyles, images, xmlBehavior, state, fields);
        if (section.FirstFooter is { } firstFooter)
            OdtContentWriter.WriteBand(writer, "footer-first", firstFooter, section, bandStyles, images, xmlBehavior, state, fields);

        writer.WriteEndElement();
    }

    /// <summary>
    /// Adds <c>fo:hyphenate="true"</c> to the paragraph default style of an adopted
    /// <c>styles.xml</c>. A missing default style is created so the option also reaches a template
    /// or a source package with a minimal styles part.
    /// </summary>
    internal static void EnsureHyphenation(XDocument styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        if (styles.Root is not { } root)
        {
            return;
        }

        XNamespace style = OdtNamespaces.Style;
        XNamespace fo = OdtNamespaces.Fo;
        XNamespace office = OdtNamespaces.Office;
        var defaultStyle = root.Descendants(style + "default-style")
            .FirstOrDefault(element => element.Attribute(style + "family")?.Value == "paragraph");
        if (defaultStyle is null)
        {
            defaultStyle = new XElement(
                style + "default-style",
                new XAttribute(style + "family", "paragraph"));
            var stylesContainer = root.Descendants(office + "styles").FirstOrDefault() ?? root;
            stylesContainer.AddFirst(defaultStyle);
        }

        var properties = defaultStyle.Element(style + "paragraph-properties");
        if (properties is null)
        {
            properties = new XElement(style + "paragraph-properties");
            defaultStyle.AddFirst(properties);
        }

        properties.SetAttributeValue(fo + "hyphenate", "true");
    }

    private static string PointsText(Length length)
        => length.Points.ToString("0.##", CultureInfo.InvariantCulture) + "pt";
}
