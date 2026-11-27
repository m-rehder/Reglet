using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// reads an ODT package (subset) into the content model: paragraphs, headings, text with
/// formatting, lists (also nested), tables, page number fields, sections
/// (page/margins/orientation) and metadata from meta.xml. Layout is not preserved; not
/// mappable content (e.g. images, tables in headers/footers) is reported instead of
/// dropped silently.
/// </summary>
public sealed class OdtDocumentReader : IDocumentReader
{
    /// <summary>The properties of a list style (kind, number format, color and weight).</summary>
    private static readonly XNamespace Office = OdtNamespaces.Office;
    private static readonly XNamespace Text = OdtNamespaces.Text;
    private static readonly XNamespace Table = OdtNamespaces.Table;
    private static readonly XNamespace Style = OdtNamespaces.Style;
    private static readonly XNamespace Fo = OdtNamespaces.Fo;
    private static readonly XNamespace RegletNs = OdtNamespaces.Reglet;
    private static readonly XNamespace Dc = OdtNamespaces.DublinCore;
    private static readonly XNamespace Meta = OdtNamespaces.Meta;

    /// <summary>
    /// ODF field elements the model cannot represent. Their displayed text is kept, and the
    /// reader reports the concrete field so a changed document does not lose it silently.
    /// </summary>
    private static readonly HashSet<string> UnmappedFieldElements = new(StringComparer.Ordinal)
    {
        "chapter", "page-continuation", "page-variable-set", "page-variable-get",
        "sheet-name", "creation-date", "creation-time", "description",
        "user-field-input", "sequence-ref", "expression", "text-input",
        "database-display", "database-next", "database-row-select", "database-row-number",
        "database-name", "table-formula", "cell-address", "measure", "time",
        "editing-duration", "editing-cycles", "print-date", "print-time", "printed-by",
        "modification-date", "modification-time", "author-name", "author-initials",
        "variable-set", "variable-get", "variable-input", "reference-ref",
    };

    /// <summary>Reads an ODT from the stream into the content model.</summary>
    /// <param name="input">Source stream; it remains open after reading.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public ReadResult Read(Stream input, CancellationToken cancellationToken = default)
        => Read(input, OdtReadOptions.Default, cancellationToken);

    /// <summary>
    /// Reads an ODT from the stream; an encrypted package is opened with the password of
    /// <paramref name="options"/> (OF-E05).
    /// </summary>
    /// <param name="input">Source stream; it remains open after reading.</param>
    /// <param name="options">Reader options (password).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="DocumentValidationException">The password is missing or wrong, or the package is damaged.</exception>
    public ReadResult Read(Stream input, OdtReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        return ReaderGuard.Run("ODT", () =>
        {
            // The bytes are kept as the preserved source so that unmodelled package parts survive
            // a rewrite and an unchanged document can be written back byte-identically (phase AF,
            // AF11). Own buffer, so the foreign stream stays open.
            var sourcePackage = ReaderGuard.ReadAllBounded(input, "ODT");

            // An encrypted package is decrypted to a plain in-memory package first, so the rest of
            // the reader sees the same parts and the preserved source is a plain ODT (OF-E05).
            var package = OdtEncryption.TryDecryptPackage(sourcePackage, options.Password)
                ?? sourcePackage.ToArray();

            using var zip = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
            var warnings = new List<ReadWarning>();
            var document = Parse(zip, warnings, package, cancellationToken);
            SourcePreservation.MarkPristine(document);
            return new ReadResult(document, warnings);
        });
    }

    private static PortableDocument Parse(
        ZipArchive zip,
        List<ReadWarning> warnings,
        ReadOnlyMemory<byte> sourcePackage,
        CancellationToken cancellationToken)
    {
        var metadata = ReadMetadata(zip);
        var content = ReadXml(zip, "content.xml")
            ?? throw new DocumentValidationException("The ODT package contains no content.xml.");
        metadata = metadata with { Variables = ReadUserFields(content) };
        var styles = ReadXml(zip, "styles.xml");

        var paragraphDefaults = ReadDefaultParagraphFormat(styles);
        var documentDefaults = ReadDocumentDefaults(styles) with { ParagraphStyle = paragraphDefaults.ToStyle() };
        var characterStyles = ReadNamedCharacterStyles(styles, documentDefaults.FontSize);
        var runDefaults = ReadDefaultRunProperties(styles);
        var paragraphStyles = ReadParagraphStyles(content, styles);
        var textStyles = ReadTextStyles(content, characterStyles);
        var paragraphRunFormats = ReadParagraphRunFormats(paragraphStyles, runDefaults, documentDefaults.FontSize);
        var namedStyles = ReadNamedStyles(styles, paragraphStyles, paragraphRunFormats);
        var pageLayouts = ReadPageLayouts(styles);
        var masterPages = ReadMasterPages(styles);
        var context = new ReadContext(
            zip,
            paragraphStyles,
            textStyles,
            ReadListDefinitions(content, styles),
            ReadTableBreaks(content, styles),
            warnings)
        {
            Formats = new ParagraphFormatResolver(paragraphStyles, paragraphDefaults),
            Controls = OdtForms.ReadControls(content),
            Comments = OdtComments.Read(content, message => Warn(warnings, message)),
            Revisions = OdtRevisions.Read(content),
            CellStyles = ReadCellStyles(content, styles, warnings),
            ColumnStyles = ReadColumnStyles(content, styles, warnings),
            RowStyles = ReadRowStyles(content, styles, warnings),
            TableStyles = ReadTableStyles(content, styles, warnings),
            GraphicStyles = ReadGraphicStyles(content, styles, warnings),
            BandGraphicStyles = ReadGraphicStyles(null, styles, warnings),
            ParagraphDefaults = paragraphDefaults,
            DefaultFontSize = documentDefaults.FontSize,
            ParagraphRunFormats = paragraphRunFormats,
            NamedStyleNames = namedStyles
                .Select(style => style.StyleName)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal),
            ParagraphRunDefault = runDefaults is null
                ? new TextRun(string.Empty)
                : RunOf(ReadCharacterStyle("default", runDefaults, documentDefaults.FontSize), null),
        };
        var bandContext = styles is null ? context : context with
        {
            TextStyles = ReadTextStyles(styles.Element(Office + "automatic-styles") ?? new XElement(Office + "automatic-styles"), characterStyles),
            CellStyles = ReadCellStyles(null, styles, warnings),
            ColumnStyles = ReadColumnStyles(null, styles, warnings),
            RowStyles = ReadRowStyles(null, styles, warnings),
            TableStyles = ReadTableStyles(null, styles, warnings),
        };
        var masterPageBands = ReadMasterPageBands(styles, bandContext, pageLayouts);

        var sections = ReadSections(content, context, pageLayouts, masterPages, masterPageBands, cancellationToken);
        return new PortableDocument(metadata, sections)
        {
            Defaults = documentDefaults,
            Numbering = context.Numbering,
            Styles = namedStyles,
            SourceStyles = namedStyles,
            CharacterStyles = characterStyles,
            SourceCharacterStyles = characterStyles,
            SourcePackage = sourcePackage,
            SourceBlockOrigins = context.BlockOrigins.Origins,
            SourceHasUnmodelledContent = warnings.Count > 0,
            SourceUnmodelledWarnings = warnings.Select(warning => warning.Message).ToArray(),
        };
    }

    private static DocumentMetadata ReadMetadata(ZipArchive zip)
    {
        var meta = ReadXml(zip, "meta.xml");
        if (meta is null)
        {
            return new DocumentMetadata();
        }

        string[]? keywords = null;
        var keywordList = meta.Descendants(Meta + "keyword").Select(k => k.Value).Where(v => !string.IsNullOrEmpty(v)).ToArray();
        if (keywordList.Length > 0)
        {
            keywords = keywordList;
        }

        DateTimeOffset? created = null;
        if (DateTimeOffset.TryParse(
            meta.Descendants(Meta + "creation-date").FirstOrDefault()?.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var createdValue))
        {
            created = createdValue;
        }

        return new DocumentMetadata
        {
            Title = FirstValue(meta, Dc + "title"),
            Author = FirstValue(meta, Dc + "creator"),
            Subject = FirstValue(meta, Dc + "subject"),
            Keywords = keywords,
            Language = FirstValue(meta, Dc + "language"),
            Created = created,
        };
    }

    /// <summary>
    /// The declared user fields (<c>text:user-field-decl</c>) as document variables; a string
    /// field carries <c>office:string-value</c>, other types <c>office:value</c>.
    /// </summary>
    private static Dictionary<string, string>? ReadUserFields(XElement content)
    {
        Dictionary<string, string>? variables = null;
        foreach (var declaration in content.Descendants(Text + "user-field-decl"))
        {
            if (declaration.Attribute(Text + "name")?.Value is { Length: > 0 } name)
            {
                var value = declaration.Attribute(Office + "string-value")?.Value
                    ?? declaration.Attribute(Office + "value")?.Value
                    ?? string.Empty;
                variables ??= new Dictionary<string, string>(StringComparer.Ordinal);
                variables.TryAdd(name, value);
            }
        }

        return variables;
    }

    private static string? FirstValue(XElement root, XName name)
    {
        var value = root.Descendants(name).FirstOrDefault()?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Reads the automatic text styles (formatting) from content.xml.</summary>
    /// <summary>
    /// The cell styles, including properties inherited from named styles.
    /// </summary>
    private sealed record CellStyle(Color? Background, (Length? Horizontal, Length? Vertical) Padding, ParagraphBorders? Borders, TableCellVerticalAlignment VerticalAlignment);

    /// <summary>A non-negative length, or null.</summary>
    private static Length? Padding(string? text) => ParseLength(text) is { Points: >= 0 } padding ? padding : null;

    private static IReadOnlyDictionary<string, CellStyle> ReadCellStyles(XElement? content, XElement? styles, List<ReadWarning> warnings)
    {
        var links = new Dictionary<string, (string? Parent, XElement? Properties)>(StringComparer.Ordinal);
        foreach (var root in new[] { styles, content })
        {
            foreach (var style in root?.Descendants(Style + "style") ?? [])
            {
                if (style.Attribute(Style + "family")?.Value != "table-cell"
                    || style.Attribute(Style + "name")?.Value is not { Length: > 0 } name) continue;
                var properties = style.Element(Style + "table-cell-properties") is { } source ? new XElement(source) : null;
                if (properties is not null)
                {
                    foreach (var kind in new[] { "border", "padding" })
                    {
                        if (properties.Attribute(Fo + kind) is not { } all) continue;
                        foreach (var side in new[] { "top", "right", "bottom", "left" })
                            if (properties.Attribute(Fo + (kind + "-" + side)) is null)
                                properties.SetAttributeValue(Fo + (kind + "-" + side), all.Value);
                    }
                }
                links[name] = ((string?)style.Attribute(Style + "parent-style-name"), properties);
            }
        }
        if (HasBrokenChain(links))
            Warn(warnings, "ODT: a table cell style has a missing or cyclic parent and may be incomplete.");
        var resolved = ResolveAllProperties(links);
        var result = new Dictionary<string, CellStyle>(StringComparer.Ordinal);
        foreach (var name in links.Keys)
        {
            var properties = resolved[name];
            Color? background = null;
            (Length? Horizontal, Length? Vertical) padding = (null, null);
            if (properties is not null)
            {
                var fill = properties.Attribute(Fo + "background-color")?.Value;
                if (!string.IsNullOrEmpty(fill) && !string.Equals(fill, "transparent", StringComparison.OrdinalIgnoreCase))
                {
                    background = ParseColor(fill);
                }

                var uniform = properties.Attribute(Fo + "padding")?.Value;
                padding = (Padding(properties.Attribute(Fo + "padding-left")?.Value ?? uniform),
                    Padding(properties.Attribute(Fo + "padding-top")?.Value ?? uniform));
            }

            ParagraphBorders? borders = null;
            var verticalAlignment = TableCellVerticalAlignment.Top;
            if (properties is not null)
            {
                var general = properties.Attribute(Fo + "border")?.Value;
                var top = ParseCellBorder(properties.Attribute(Fo + "border-top")?.Value ?? general);
                var right = ParseCellBorder(properties.Attribute(Fo + "border-right")?.Value ?? general);
                var bottom = ParseCellBorder(properties.Attribute(Fo + "border-bottom")?.Value ?? general);
                var left = ParseCellBorder(properties.Attribute(Fo + "border-left")?.Value ?? general);
                if (top is not null || right is not null || bottom is not null || left is not null)
                    borders = new ParagraphBorders { Top = top, Right = right, Bottom = bottom, Left = left };
                verticalAlignment = (string?)properties.Attribute(Style + "vertical-align") switch
                {
                    "middle" => TableCellVerticalAlignment.Center,
                    "bottom" => TableCellVerticalAlignment.Bottom,
                    _ => TableCellVerticalAlignment.Top,
                };
            }
            result[name] = new CellStyle(background, padding, borders, verticalAlignment);
        }

        return result;
    }

    private static ParagraphBorder? ParseCellBorder(string? value)
        => value?.Trim() is "none" or "hidden" ? new ParagraphBorder(BorderStyle.None) : ParseBorder(value);

    /// <summary>
    /// Column widths from automatic and named styles, including inheritance.
    /// </summary>
    private static IReadOnlyDictionary<string, (Length? Width, double? Weight)> ReadColumnStyles(XElement? content, XElement? styles, List<ReadWarning> warnings)
    {
        var links = new Dictionary<string, (string? Parent, XElement? Properties)>(StringComparer.Ordinal);
        foreach (var root in new[] { styles, content })
        {
            foreach (var style in root?.Descendants(Style + "style") ?? [])
            {
                if ((string?)style.Attribute(Style + "family") == "table-column"
                    && (string?)style.Attribute(Style + "name") is { Length: > 0 } name)
                    links[name] = ((string?)style.Attribute(Style + "parent-style-name"), style.Element(Style + "table-column-properties"));
            }
        }
        if (HasBrokenChain(links))
            Warn(warnings, "ODT: a table column style has a missing or cyclic parent and may be incomplete.");
        var resolved = ResolveAllProperties(links);
        var result = new Dictionary<string, (Length?, double?)>(StringComparer.Ordinal);
        foreach (var name in links.Keys)
        {
            var properties = resolved[name];
            var width = ParseLength((string?)properties?.Attribute(Style + "column-width"));
            if (width is not { Points: > 0 }) width = null;
            var relative = ((string?)properties?.Attribute(Style + "rel-column-width"))?.Trim();
            double? weight = relative is { Length: > 1 } && relative.EndsWith('*')
                && long.TryParse(relative.AsSpan(0, relative.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && number > 0 ? number : null;
            // An automatic column is what the file asks for, not a width that got lost.
            var automatic = (string?)properties?.Attribute(Style + "use-optimal-column-width") == "true";
            if (width is not null || weight is not null || automatic) result[name] = (width, weight);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, (Length? Height, TableRowHeightRule Rule, bool KeepTogether)> ReadRowStyles(
        XElement? content, XElement? styles, List<ReadWarning> warnings)
    {
        var links = new Dictionary<string, (string? Parent, XElement? Properties)>(StringComparer.Ordinal);
        foreach (var root in new[] { styles, content })
        {
            foreach (var style in root?.Descendants(Style + "style") ?? [])
            {
                if ((string?)style.Attribute(Style + "family") == "table-row"
                    && (string?)style.Attribute(Style + "name") is { Length: > 0 } name)
                    links[name] = ((string?)style.Attribute(Style + "parent-style-name"), style.Element(Style + "table-row-properties"));
            }
        }

        if (HasBrokenChain(links))
            Warn(warnings, "ODT: a table row style has a missing or cyclic parent and may be incomplete.");
        // The nearest style along the chain that states a height wins (StyleChain, linear time).
        var heights = StyleChain.ResolveAll<(string? Height, bool Minimum, string? Optimal, string? Keep)>(
            links.Keys,
            name => links.TryGetValue(name, out var link) ? link.Parent : null,
            links.ContainsKey,
            (null, false, null, null),
            (inherited, name) =>
            {
                var properties = links[name].Properties;
                var optimal = (string?)properties?.Attribute(Style + "use-optimal-row-height") ?? inherited.Optimal;
                var keep = (string?)properties?.Attribute(Fo + "keep-together") ?? inherited.Keep;
                if ((string?)properties?.Attribute(Style + "row-height") is { } exact)
                    return (exact, false, optimal, keep);
                if ((string?)properties?.Attribute(Style + "min-row-height") is { } least)
                    return (least, true, optimal, keep);
                return (inherited.Height, inherited.Minimum, optimal, keep);
            });
        var result = new Dictionary<string, (Length?, TableRowHeightRule, bool)>(StringComparer.Ordinal);
        foreach (var name in links.Keys)
        {
            var (heightValue, minimum, optimal, keepValue) = heights[name];
            var keep = keepValue == "always";
            if (heightValue is not null && ParseLength(heightValue) is { Points: > 0 } height)
            {
                // Earlier Reglet versions wrote a minimum height as row-height with optimal=true.
                var rule = minimum || optimal is "true" or "1" ? TableRowHeightRule.AtLeast : TableRowHeightRule.Exactly;
                result[name] = (height, rule, keep);
            }
            else if (keep)
            {
                result[name] = (null, TableRowHeightRule.Auto, true);
            }
        }
        return result;
    }

    /// <summary>The properties of a table style: width, border and default cell padding.</summary>
    private sealed record TableStyleInfo(
        string? Relative,
        Length? Absolute,
        ParagraphBorder? Border,
        Length? Padding,
        Length? PaddingHorizontal,
        Length? PaddingVertical,
        Length? LeftIndent);

    private static IReadOnlyDictionary<string, TableStyleInfo> ReadTableStyles(XElement? content, XElement? styles, List<ReadWarning> warnings)
    {
        var links = new Dictionary<string, (string? Parent, XElement? Properties)>(StringComparer.Ordinal);
        foreach (var root in new[] { styles, content })
        {
            foreach (var style in root?.Descendants(Style + "style") ?? [])
            {
                if ((string?)style.Attribute(Style + "family") == "table"
                    && (string?)style.Attribute(Style + "name") is { Length: > 0 } name)
                    links[name] = ((string?)style.Attribute(Style + "parent-style-name"), style.Element(Style + "table-properties"));
            }
        }
        if (HasBrokenChain(links))
            Warn(warnings, "ODT: a table style has a missing or cyclic parent and may be incomplete.");
        // Each value comes from the nearest style along the chain that states it (StyleChain).
        var values = StyleChain.ResolveAll<(string? Width, string? Absolute, string? Border, string? Padding, string? Left, string? Top, string? Margin)>(
            links.Keys,
            name => links.TryGetValue(name, out var link) ? link.Parent : null,
            links.ContainsKey,
            (null, null, null, null, null, null, null),
            (inherited, name) =>
            {
                var properties = links[name].Properties;
                return (
                    (string?)properties?.Attribute(Style + "rel-width") ?? inherited.Width,
                    (string?)properties?.Attribute(Style + "width") ?? inherited.Absolute,
                    // Reglet's own attributes; files from Reglet 1.5 and older carry them as fo:* (outside the schema).
                    (string?)properties?.Attribute(RegletNs + "border") ?? (string?)properties?.Attribute(Fo + "border") ?? inherited.Border,
                    (string?)properties?.Attribute(RegletNs + "padding") ?? (string?)properties?.Attribute(Fo + "padding") ?? inherited.Padding,
                    (string?)properties?.Attribute(RegletNs + "padding-left") ?? (string?)properties?.Attribute(Fo + "padding-left") ?? inherited.Left,
                    (string?)properties?.Attribute(RegletNs + "padding-top") ?? (string?)properties?.Attribute(Fo + "padding-top") ?? inherited.Top,
                    (string?)properties?.Attribute(Fo + "margin-left") ?? inherited.Margin);
            });
        var result = new Dictionary<string, TableStyleInfo>(StringComparer.Ordinal);
        foreach (var name in links.Keys)
        {
            var (width, absolute, border, padding, left, top, margin) = values[name];
            var length = ParseLength(absolute);
            if (length is not { Points: > 0 }) length = null;
            var tableBorder = ParseBorder(border);
            var tablePadding = Padding(padding);
            var (horizontal, vertical) = (Padding(left), Padding(top));
            var leftIndent = ParseLength(margin) is { } indent && indent.Points != 0 ? indent : (Length?)null;
            if (width is not null || length is not null || tableBorder is not null || tablePadding is not null
                || horizontal is not null || vertical is not null || leftIndent is not null)
            {
                result[name] = new TableStyleInfo(width, length, tableBorder, tablePadding, horizontal, vertical, leftIndent);
            }
        }
        return result;
    }

    /// <summary>
    /// The document-wide run defaults (<c>style:default-style</c> of the paragraph family): the
    /// root of the run formatting that styles and runs inherit.
    /// </summary>
    private static DocumentDefaults ReadDocumentDefaults(XElement? styles)
    {
        var textProperties = DefaultStyle(styles)?.Element(Style + "text-properties");
        if (textProperties is null)
        {
            return DocumentDefaults.Default;
        }

        var format = ReadCharacterStyle("Standard", textProperties);
        var defaults = DocumentDefaults.Default;
        // ODF stores the family either as a font-face name or as a CSS-style family list.
        if ((format.FontFamily ?? FirstFontFamily(textProperties.Attribute(Fo + "font-family")?.Value)) is { } family)
        {
            defaults = defaults with { FontFamily = family };
        }

        if (format.FontSize is { } size)
        {
            defaults = defaults with { FontSize = size };
        }

        if (format.Color is { } color)
        {
            defaults = defaults with { TextColor = color };
        }

        return defaults;
    }

    /// <summary>
    /// The run properties of <c>style:default-style</c> that the runs inherit (bold, italic, …).
    /// Font, size and colour stay in <see cref="DocumentDefaults"/>.
    /// </summary>
    private static XElement? ReadDefaultRunProperties(XElement? styles)
    {
        if (DefaultStyle(styles)?.Element(Style + "text-properties") is not { } textProperties)
        {
            return null;
        }

        var properties = new XElement(textProperties);
        properties.Attribute(Style + "font-name")?.Remove();
        properties.Attribute(Fo + "font-family")?.Remove();
        properties.Attribute(Fo + "font-size")?.Remove();
        properties.Attribute(Fo + "color")?.Remove();
        return properties.HasAttributes ? properties : null;
    }

    /// <summary>The <c>style:default-style</c> of the paragraph family.</summary>
    private static XElement? DefaultStyle(XElement? styles)
        => styles?.Element(Office + "styles")?
            .Elements(Style + "default-style")
            .FirstOrDefault(style => style.Attribute(Style + "family")?.Value == "paragraph");

    /// <summary>
    /// The paragraph defaults (<c>style:default-style</c> of the paragraph family) as the root of
    /// the paragraph inheritance. The built-in spacing is dropped so it is not materialized on
    /// every paragraph.
    /// </summary>
    private static ParagraphFormat ReadDefaultParagraphFormat(XElement? styles)
    {
        if (DefaultStyle(styles) is not { } defaultStyle || ReadParagraphFormat(defaultStyle) is not { } format)
        {
            return ParagraphFormat.Empty;
        }

        if (format.SpaceAfter is { } after
            && Math.Abs(after.Points - Typography.ParagraphSpaceAfter.Points) < 1e-9)
        {
            format = format with { SpaceAfter = null };
        }

        return format;
    }

    /// <summary>The first family of an ODF font list (<c>'Georgia', serif</c>); quotes are stripped.</summary>
    private static string? FirstFontFamily(string? value)
    {
        var first = value?.Split(',')[0].Trim().Trim('\'', '"');
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }

    /// <summary>
    /// The named character styles of the document (<c>style:family="text"</c> in
    /// <c>office:styles</c>). They are part of the model as
    /// <see cref="PortableDocument.CharacterStyles"/> and are written again by the writers.
    /// A named style inherits the properties of its parent style (<c>style:parent-style-name</c>).
    /// </summary>
    private static IReadOnlyList<CharacterStyle> ReadNamedCharacterStyles(XElement? styles, Length defaultFontSize)
    {
        var container = styles?.Element(Office + "styles");
        if (container is null)
        {
            return [];
        }

        var links = new Dictionary<string, (string? Parent, XElement? Properties)>(StringComparer.Ordinal);
        foreach (var style in container.Elements(Style + "style"))
        {
            if (style.Attribute(Style + "family")?.Value == "text"
                && style.Attribute(Style + "name")?.Value is { Length: > 0 } name)
            {
                links[name] = (
                    style.Attribute(Style + "parent-style-name")?.Value,
                    style.Element(Style + "text-properties"));
            }
        }

        var resolved = ResolveAllProperties(links);
        var result = new List<CharacterStyle>(links.Count);
        foreach (var style in container.Elements(Style + "style"))
        {
            if (style.Attribute(Style + "family")?.Value != "text"
                || style.Attribute(Style + "name")?.Value is not { Length: > 0 } name)
            {
                continue;
            }

            result.Add(ReadCharacterStyle(name, MergeProperties(
                style.Attribute(Style + "parent-style-name")?.Value is { } parent
                    ? resolved.GetValueOrDefault(parent)
                    : null,
                style.Element(Style + "text-properties")), defaultFontSize));
        }

        return result;
    }

    /// <summary>The properties of a style over the given inherited ones (child attributes win).</summary>
    private static XElement? MergeProperties(XElement? parent, XElement? own)
    {
        if (parent is null && own is null)
        {
            return null;
        }

        var merged = new XElement(Style + "text-properties");
        foreach (var attribute in parent?.Attributes() ?? [])
        {
            merged.SetAttributeValue(attribute.Name, attribute.Value);
        }

        foreach (var attribute in own?.Attributes() ?? [])
        {
            merged.SetAttributeValue(attribute.Name, attribute.Value);
        }

        return merged;
    }

    /// <summary>
    /// Every style's properties merged over its parent chain: an attribute that the style itself
    /// sets wins over the parent (ODF inheritance works per attribute). Each style is resolved once
    /// (StyleChain), so a crafted long chain costs linear time.
    /// </summary>
    private static Dictionary<string, XElement?> ResolveAllProperties(
        IReadOnlyDictionary<string, (string? Parent, XElement? Properties)> links)
        => StyleChain.ResolveAll<XElement?>(
            links.Keys,
            name => links.TryGetValue(name, out var link) ? link.Parent : null,
            links.ContainsKey,
            null,
            (parent, name) => MergeProperties(parent, links[name].Properties));

    /// <summary>Whether a style's parent is missing or its chain is cyclic.</summary>
    private static bool HasBrokenChain(IReadOnlyDictionary<string, (string? Parent, XElement? Properties)> links)
        => StyleChain.HasBrokenChain(
            links.Keys, name => links.TryGetValue(name, out var link) ? link.Parent : null, links.ContainsKey);

    /// <summary>
    /// A text style as a run uses it: the own formatting (automatic style) and, for a named
    /// character style, the reference whose formatting stays in the style definition.
    /// </summary>
    private sealed record TextStyle(XElement? Properties, string? NamedName, Length? NamedFontSize = null);

    /// <summary>
    /// Text styles of the document: the named character styles from styles.xml and the
    /// automatic text styles from content.xml. A run that uses one of them takes over its
    /// formatting; a reference to a named character style becomes its
    /// <see cref="TextRun.CharacterStyleName"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, TextStyle> ReadTextStyles(
        XElement content, IReadOnlyList<CharacterStyle> characterStyles)
    {
        var named = characterStyles.Select(style => style.Name).ToHashSet(StringComparer.Ordinal);
        var links = new Dictionary<string, (string? Parent, XElement? Properties)>(StringComparer.Ordinal);
        foreach (var style in content.Descendants(Style + "style"))
        {
            if (style.Attribute(Style + "family")?.Value != "text"
                || style.Attribute(Style + "name")?.Value is not { Length: > 0 } name)
            {
                continue;
            }

            // An automatic text style inherits from a named one via style:parent-style-name;
            // files of older versions of this library used style:text-style-name.
            links[name] = (
                style.Attribute(Style + "parent-style-name")?.Value
                    ?? style.Attribute(Style + "text-style-name")?.Value,
                style.Element(Style + "text-properties"));
        }

        var result = new Dictionary<string, TextStyle>(StringComparer.Ordinal);
        foreach (var style in characterStyles)
        {
            // Only the reference: the formatting lives in the style definition (as in DOCX), so a
            // later change of the style reaches the runs.
            // shortcut: a named character style cannot switch a paragraph-style toggle off; the
            // model's CharacterStyle has no "not set" state.
            result[style.Name] = new TextStyle(null, style.Name, style.FontSize);
        }

        // A named parent ends the chain as a reference; each style is resolved once (StyleChain).
        var resolved = StyleChain.ResolveAll<(XElement? Properties, string? NamedName)>(
            links.Keys,
            name => links.TryGetValue(name, out var link) ? link.Parent : null,
            links.ContainsKey,
            (null, null),
            (parent, name) => (MergeProperties(parent.Properties, links[name].Properties), parent.NamedName),
            parent => named.Contains(parent) ? (true, (null, parent)) : (false, default));
        foreach (var name in links.Keys)
        {
            var (properties, namedParent) = resolved[name];
            var namedFontSize = namedParent is null
                ? null
                : characterStyles.LastOrDefault(style => style.Name == namedParent)?.FontSize;
            result[name] = new TextStyle(properties, namedParent, namedFontSize);
        }

        return result;
    }

    /// <summary>
    /// Reads the character format of a text style (the shared subset of the model). ADR-0010: a
    /// property the style does not set stays null, so the inherited value applies; an explicit
    /// <c>normal</c>/<c>none</c> is false or <see cref="TextUnderlineStyle.None"/>.
    /// </summary>
    private static CharacterStyle ReadCharacterStyle(string name, XElement? tp, Length? inheritedFontSize = null) => new(name)
    {
        Bold = Attribute(tp, Fo + "font-weight") is { } weight ? IsBold(weight) : null,
        Italic = Attribute(tp, Fo + "font-style") is { } fontStyle
            ? fontStyle is "italic" or "oblique"
            : null,
        Underline = Attribute(tp, Style + "text-underline-style") is { } underline
            ? underline != "none"
            : null,
        UnderlineStyle = UnderlineStyleOf(tp),
        UnderlineColor = ParseColor(Attribute(tp, Style + "text-underline-color")),
        Strikethrough = Attribute(tp, Style + "text-line-through-style") is { } lineThrough
            ? lineThrough switch
            {
                "double" => TextStrikethrough.Double,
                "none" => TextStrikethrough.None,
                _ when Attribute(tp, Style + "text-line-through-type") == "double" => TextStrikethrough.Double,
                _ => TextStrikethrough.Single,
            }
            : null,
        Baseline = ReadBaseline(Attribute(tp, Style + "text-position")),
        AllCaps = Attribute(tp, Fo + "text-transform") is { } transform
            ? transform == "uppercase"
            : null,
        SmallCaps = Attribute(tp, Fo + "font-variant") is { } variant
            ? variant == "small-caps"
            : null,
        Hidden = Attribute(tp, Text + "display") is { } display ? display == "none" : null,
        Effects = EffectsOf(tp),
        FontFamily = tp?.Attribute(Style + "font-name")?.Value is { Length: > 0 } family
            && !string.IsNullOrWhiteSpace(family)
                ? family
                : null,
        FontSize = tp is not null && ParseLength(tp.Attribute(Fo + "font-size")?.Value) is { Points: > 0 } size
            ? size
            : null,
        Color = tp is not null && ParseColor(tp.Attribute(Fo + "color")?.Value) is { } color ? color : null,
        // shortcut: ODT writes background and highlight to fo:background-color, so a highlight
        // reads back as a background; split them when the model needs the difference.
        Background = tp is not null && ParseColor(tp.Attribute(Fo + "background-color")?.Value) is { } back
            ? back
            : null,
        CharacterSpacing = ReadLetterSpacing(Attribute(tp, Fo + "letter-spacing")),
        HorizontalScaling = ReadTextScale(Attribute(tp, Style + "text-scale")),
        Border = ParseBorder(Attribute(tp, Fo + "border")),
        // ODF has a boolean, the model a minimum font size: "true" is kerning from every size,
        // "false" cannot switch an inherited kerning off.
        Kerning = Attribute(tp, Style + "letter-kerning") == "true" ? Length.Zero : null,
        Position = ReadPosition(tp, inheritedFontSize),
    };

    private static Length? ReadLetterSpacing(string? value)
        => string.Equals(value?.Trim(), "normal", StringComparison.Ordinal) ? Length.Zero : ParseLength(value);

    private static TextUnderlineStyle? UnderlineStyleOf(XElement? properties)
    {
        var value = Attribute(properties, Style + "text-underline-style");
        if (value is null) return null;
        if (value == "none") return TextUnderlineStyle.None;
        if (Attribute(properties, Style + "text-underline-type") == "double" || value == "double") return TextUnderlineStyle.Double;
        return value switch
        {
            "dotted" => TextUnderlineStyle.Dotted,
            "dash" or "dashed" => TextUnderlineStyle.Dashed,
            "wave" => TextUnderlineStyle.Wave,
            _ => null,
        };
    }

    private static double? ReadTextScale(string? value)
    {
        if (value is null || !value.EndsWith('%') || !double.TryParse(value.AsSpan(0, value.Length - 1),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || !double.IsFinite(scale) || scale <= 0) return null;
        return scale;
    }

    /// <summary>The text effects of a text-properties element; null when it has none of the three attributes.</summary>
    private static TextEffects? EffectsOf(XElement? tp)
    {
        var outline = Attribute(tp, Style + "text-outline");
        var shadow = Attribute(tp, Fo + "text-shadow");
        var relief = Attribute(tp, Style + "font-relief");
        if (outline is null && shadow is null && relief is null)
        {
            return null;
        }

        var effects = TextEffects.None;
        if (outline == "true")
        {
            effects |= TextEffects.Outline;
        }

        if (shadow is not null && shadow != "none")
        {
            effects |= TextEffects.Shadow;
        }

        effects |= relief switch
        {
            "embossed" => TextEffects.Emboss,
            "engraved" => TextEffects.Engrave,
            _ => TextEffects.None,
        };
        return effects;
    }

    private static TextBaseline? ReadBaseline(string? value)
        => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() switch
        {
            "super" => TextBaseline.Superscript,
            "sub" => TextBaseline.Subscript,
            "0%" => TextBaseline.Normal,
            _ => null,
        };

    private static Length? ReadPosition(XElement? properties, Length? inheritedFontSize = null)
    {
        var value = Attribute(properties, Style + "text-position")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (value is null || !value.EndsWith('%')
            || !double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            || !double.IsFinite(percent)) return null;
        if (percent == 0)
        {
            // An explicit zero switches off a shift inherited from the paragraph style.
            return Length.Zero;
        }

        var fontSize = ParseLength(Attribute(properties, Fo + "font-size")) ?? inheritedFontSize;
        if (fontSize is not { Points: > 0 }) return null;
        var points = fontSize.Value.Points * (percent / 100.0);
        return double.IsFinite(points) ? Length.FromPoints(points) : null;
    }

    /// <summary>The value of an attribute, or null when the style does not set it.</summary>
    private static string? Attribute(XElement? properties, XName name) => properties?.Attribute(name)?.Value;

    /// <summary>The formatting of a text style as run formatting.</summary>
    private static TextRun RunOf(CharacterStyle style, string? characterStyleName) => new(string.Empty)
    {
        Bold = style.Bold,
        Italic = style.Italic,
        Underline = style.Underline,
        UnderlineStyle = style.UnderlineStyle,
        UnderlineColor = style.UnderlineColor,
        Strikethrough = style.Strikethrough,
        Baseline = style.Baseline,
        AllCaps = style.AllCaps,
        SmallCaps = style.SmallCaps,
        Effects = style.Effects,
        Hidden = style.Hidden,
        FontFamily = style.FontFamily,
        FontSize = style.FontSize,
        Color = style.Color,
        Background = style.Background,
        Highlight = style.Highlight,
        Kerning = style.Kerning,
        CharacterSpacing = style.CharacterSpacing,
        HorizontalScaling = style.HorizontalScaling,
        Position = style.Position,
        Border = style.Border,
        CharacterStyleName = characterStyleName,
    };

    /// <summary>
    /// The list styles of the package as numbering definitions, one per <c>text:list-style</c>:
    /// every <c>text:list-level-style-*</c> becomes the level with its number; kind, number
    /// format, start value, color and weight come from the level style. Lists that use the same
    /// style therefore share one definition (OF-B01). Foreign files name their styles freely
    /// (LibreOffice: "L1"), so the name alone says nothing about bullet or number.
    /// </summary>
    private static IReadOnlyDictionary<string, NumberingDefinition> ReadListDefinitions(XElement content, XElement? styles)
    {
        var result = new Dictionary<string, NumberingDefinition>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { styles, content })
        {
            if (root is null)
            {
                continue;
            }

            foreach (var style in root.Descendants(Text + "list-style"))
            {
                if (style.Attribute(Style + "name")?.Value is not { } styleName)
                {
                    continue;
                }

                var levels = ReadListLevels(style);
                if (levels.Count == 0)
                {
                    continue;
                }

                // The style name is the definition name, so the model keeps the identity of the
                // source; a name ODF allows but the model does not is replaced.
                var name = NumberingDefinition.IsNcName(styleName) && used.Add(styleName)
                    ? styleName
                    : NextDefinitionName(used);
                result[styleName] = new NumberingDefinition(name, levels);
            }
        }

        return result;
    }

    /// <summary>The levels of a <c>text:list-style</c>, level 1 first, without gaps.</summary>
    private static List<NumberingLevel> ReadListLevels(XElement style)
    {
        var byLevel = new SortedDictionary<int, NumberingLevel>();
        foreach (var level in style.Elements())
        {
            if (level.Name != Text + "list-level-style-number" && level.Name != Text + "list-level-style-bullet")
            {
                continue;
            }

            var number = int.TryParse(
                level.Attribute(Text + "level")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed is >= 1 and <= NumberingDefinition.MaxLevels
                    ? parsed
                    : 1;
            var properties = level.Element(Style + "text-properties");
            var color = properties?.Attribute(Fo + "color")?.Value;
            var start = int.TryParse(
                level.Attribute(Text + "start-value")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedStart)
                && parsedStart >= 1
                    ? parsedStart
                    : 1;
            byLevel[number] = new NumberingLevel
            {
                Kind = level.Name == Text + "list-level-style-bullet" ? ListKind.Bullet : ListKind.Number,
                Format = NumberFormatOf(level.Attribute(Style + "num-format")?.Value),
                Start = start,
                Suffix = level.Element(Style + "list-level-properties")?.Element(Style + "list-level-label-alignment")?.Attribute(Text + "label-followed-by")?.Value switch
                {
                    "space" => ListSuffix.Space,
                    "nothing" => ListSuffix.Nothing,
                    _ => ListSuffix.Tab,
                },
                NumberColor = color is not null && Color.TryParseHex(color, out var parsedColor) ? parsedColor : null,
                NumberBold = IsBold(properties?.Attribute(Fo + "font-weight")?.Value),
            };
        }

        if (byLevel.Count == 0)
        {
            return [];
        }

        // A level without a style of its own repeats the one before it; levels before the first
        // defined one repeat the first (the model keeps nine levels without gaps).
        var levels = new List<NumberingLevel>(NumberingDefinition.MaxLevels);
        var current = byLevel[byLevel.Keys.First()];
        for (var number = 1; number <= NumberingDefinition.MaxLevels; number++)
        {
            if (byLevel.TryGetValue(number, out var level))
            {
                current = level;
            }

            levels.Add(current);
        }

        return levels;
    }

    /// <summary>A definition name that is free and a valid NCName.</summary>
    private static string NextDefinitionName(HashSet<string> used)
    {
        for (var index = 1; ; index++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"Numbering{index}");
            if (used.Add(name))
            {
                return name;
            }
        }
    }

    /// <summary>"bold" or a numeric weight of at least 600 (ODF uses the XSL-FO values).</summary>
    private static bool IsBold(string? weight)
        => weight == "bold"
            || (int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) && numeric >= 600);

    /// <summary>The number format of a list level (ODF uses the XSL-FO values).</summary>
    private static ListNumberFormat NumberFormatOf(string? value) => value switch
    {
        "i" => ListNumberFormat.LowerRoman,
        "I" => ListNumberFormat.UpperRoman,
        "a" => ListNumberFormat.LowerLetter,
        "A" => ListNumberFormat.UpperLetter,
        _ => ListNumberFormat.Decimal,
    };

    /// <summary>Page layouts (pm1 …) from styles.xml: name → (width, height, margins, landscape).</summary>
    private static IReadOnlyDictionary<string, SectionProps> ReadPageLayouts(XElement? styles)
    {
        var result = new Dictionary<string, SectionProps>(StringComparer.Ordinal);
        if (styles is null)
        {
            return result;
        }

        foreach (var layout in styles.Descendants(Style + "page-layout"))
        {
            var name = layout.Attribute(Style + "name")?.Value;
            var props = layout.Descendants(Style + "page-layout-properties").FirstOrDefault();
            if (name is null || props is null)
            {
                continue;
            }

            // LibreOffice writes lengths in cm or in; negative or missing values fall back.
            var width = Points(props.Attribute(Fo + "page-width")?.Value, PageSize.A4.Width.Points);
            var height = Points(props.Attribute(Fo + "page-height")?.Value, PageSize.A4.Height.Points);
            if (width <= 0 || height <= 0)
            {
                (width, height) = (PageSize.A4.Width.Points, PageSize.A4.Height.Points);
            }

            result[name] = new SectionProps(
                new PageSize(Length.FromPoints(width), Length.FromPoints(height)),
                new PageMargins(
                    Length.FromPoints(Math.Max(0, Points(props.Attribute(Fo + "margin-top")?.Value, 72))),
                    Length.FromPoints(Math.Max(0, Points(props.Attribute(Fo + "margin-right")?.Value, 72))),
                    Length.FromPoints(Math.Max(0, Points(props.Attribute(Fo + "margin-bottom")?.Value, 72))),
                    Length.FromPoints(Math.Max(0, Points(props.Attribute(Fo + "margin-left")?.Value, 72)))),
                props.Attribute(Style + "print-orientation")?.Value == "landscape" || width > height
                    ? PageOrientation.Landscape
                    : PageOrientation.Portrait,
                ReadPageBorders(props));
        }

        return result;
    }

    /// <summary>
    /// Reads the page border of a page layout (AJ05): the border strings of the four sides
    /// (width, style and color).
    /// </summary>
    private static PageBorders? ReadPageBorders(XElement props)
    {
        var top = ParseBorder(props.Attribute(Fo + "border-top")?.Value);
        var bottom = ParseBorder(props.Attribute(Fo + "border-bottom")?.Value);
        var left = ParseBorder(props.Attribute(Fo + "border-left")?.Value);
        var right = ParseBorder(props.Attribute(Fo + "border-right")?.Value);
        if (top is null && bottom is null && left is null && right is null)
        {
            return null;
        }

        return new PageBorders { Top = top, Bottom = bottom, Left = left, Right = right };
    }

    /// <summary>Parses a border string (for example "0.5pt solid #000000").</summary>
    private static ParagraphBorder? ParseBorder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var width = Points(parts[0], 0.5);
        if (width <= 0)
        {
            return null;
        }

        var style = parts[1].ToLowerInvariant() switch
        {
            "double" => BorderStyle.Double,
            "dotted" => BorderStyle.Dotted,
            "dashed" => BorderStyle.Dashed,
            "none" or "hidden" => BorderStyle.None,
            _ => BorderStyle.Single,
        };

        if (style == BorderStyle.None)
        {
            return null;
        }

        var color = parts.Length > 2 ? ParseColor(parts[2]) : null;
        return new ParagraphBorder(style, Length.FromPoints(width), color);
    }

    /// <summary>Reads the header/footer blocks of the master pages (styles.xml).</summary>
    private sealed record MasterBands(HeaderFooter? Header, HeaderFooter? Footer, HeaderFooter? FirstHeader, HeaderFooter? FirstFooter, HeaderFooter? EvenHeader, HeaderFooter? EvenFooter);

    private static IReadOnlyDictionary<string, MasterBands> ReadMasterPageBands(
        XElement? styles,
        ReadContext context,
        IReadOnlyDictionary<string, SectionProps> pageLayouts)
    {
        var result = new Dictionary<string, MasterBands>(StringComparer.Ordinal);
        if (styles is null)
        {
            return result;
        }

        foreach (var master in styles.Descendants(Style + "master-page"))
        {
            if (master.Attribute(Style + "name")?.Value is not { } name)
            {
                continue;
            }

            // A table in a band with relative columns needs the width of its page layout.
            var masterContext = master.Attribute(Style + "page-layout-name")?.Value is { } layoutName
                && pageLayouts.TryGetValue(layoutName, out var layout)
                    ? context with
                    {
                        AvailableWidth = ContentWidth(layout.PageSize, layout.Margins, layout.Orientation),
                    }
                    : context;
            result[name] = new MasterBands(
                ReadBand(master.Element(Style + "header"), masterContext),
                ReadBand(master.Element(Style + "footer"), masterContext),
                ReadBand(master.Element(Style + "header-first"), masterContext),
                ReadBand(master.Element(Style + "footer-first"), masterContext),
                ReadBand(master.Element(Style + "header-left"), masterContext),
                ReadBand(master.Element(Style + "footer-left"), masterContext));
        }

        return result;
    }

    private static HeaderFooter? ReadBand(XElement? band, ReadContext context)
    {
        if (band is null)
        {
            return null;
        }

        return new HeaderFooter(band.Attribute(Style + "display")?.Value == "false" ? [] : ReadBlocks(context, band.Elements()));
    }

    private static IReadOnlyDictionary<string, string> ReadMasterPages(XElement? styles)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (styles is null)
        {
            return result;
        }

        foreach (var master in styles.Descendants(Style + "master-page"))
        {
            var name = master.Attribute(Style + "name")?.Value;
            var layout = master.Attribute(Style + "page-layout-name")?.Value;
            if (name is not null && layout is not null)
            {
                result[name] = layout;
            }
        }

        return result;
    }

    private static IReadOnlyList<Section> ReadSections(
        XElement content,
        ReadContext context,
        IReadOnlyDictionary<string, SectionProps> pageLayouts,
        IReadOnlyDictionary<string, string> masterPages,
        IReadOnlyDictionary<string, MasterBands> masterPageBands,
        CancellationToken cancellationToken)
    {
        var paragraphStyles = context.ParagraphStyles;
        // The master page of every paragraph style: the nearest one along its parent chain, each style
        // resolved once (StyleChain).
        var styleMasterPages = StyleChain.ResolveAll<string?>(
            paragraphStyles.Keys,
            name => paragraphStyles.TryGetValue(name, out var link) ? link.Parent : null,
            paragraphStyles.ContainsKey,
            null,
            (inherited, name) => paragraphStyles[name].MasterPage ?? inherited);
        var bodyText = content
            .Element(Office + "body")?
            .Element(Office + "text")
            ?? throw new DocumentValidationException("content.xml contains no office:text.");

        // Our writer puts each section into a text:section. Foreign files (LibreOffice,
        // Word) usually have content directly in office:text; contiguous content outside
        // a text:section therefore form their own section.
        var sections = new List<Section>();
        var loose = new List<XElement>();

        void AddSection(IReadOnlyList<XElement> elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (props, masterPageName) = ResolveSectionProps(elements, styleMasterPages, masterPages, pageLayouts);
            var pageSize = props?.PageSize ?? PageSize.A4;
            var margins = props?.Margins ?? PageMargins.Normal;
            var orientation = props?.Orientation ?? PageOrientation.Portrait;
            // Relative column widths need the width of the text area; it is known before the
            // blocks are read, so a table without an absolute width can still be resolved.
            var sectionContext = context with { AvailableWidth = ContentWidth(pageSize, margins, orientation) };
            var blocks = ReadBlocks(sectionContext, elements, origins: context.BlockOrigins);

            MasterBands? bands = null;
            if (masterPageBands.Count > 0)
            {
                bands = masterPageName is not null && masterPageBands.TryGetValue(masterPageName, out var named)
                    ? named
                    : masterPageBands.Values.First();
            }
            sections.Add(new Section(blocks)
            {
                PageSize = pageSize,
                Margins = margins,
                Orientation = orientation,
                PageBorders = props?.PageBorders,
                Header = bands?.Header,
                Footer = bands?.Footer,
                FirstHeader = bands?.FirstHeader,
                FirstFooter = bands?.FirstFooter,
                EvenHeader = bands?.EvenHeader,
                EvenFooter = bands?.EvenFooter,
            });
        }

        foreach (var element in bodyText.Elements())
        {
            if (element.Name != Text + "section")
            {
                loose.Add(element);
                continue;
            }

            if (loose.Any(IsContent))
            {
                AddSection(loose.Where(element => element.Name != Text + "user-field-decls").ToList());
            }

            loose = [];
            AddSection(element.Elements().ToList());
        }

        if (loose.Any(IsContent) || sections.Count == 0)
        {
            AddSection(loose.Where(element => element.Name != Text + "user-field-decls").ToList());
        }

        return sections;
    }

    /// <summary>The width of the text area of a page in points.</summary>
    private static double ContentWidth(PageSize pageSize, PageMargins margins, PageOrientation orientation)
    {
        var width = orientation == PageOrientation.Landscape ? pageSize.Height.Points : pageSize.Width.Points;
        return width - margins.Left.Points - margins.Right.Points;
    }

    /// <summary>
    /// determines the page layout of a section. The section break is represented in ODT via
    /// represented by a paragraph style with <c>style:master-page-name</c> (break paragraph before the
    /// section or indented into its first paragraph). The first paragraph of the section,
    /// whose style names a master page therefore determines the page layout.
    /// </summary>
    private static (SectionProps? Props, string? MasterPage) ResolveSectionProps(
        IEnumerable<XElement> sectionElements,
        IReadOnlyDictionary<string, string?> styleMasterPages,
        IReadOnlyDictionary<string, string> masterPages,
        IReadOnlyDictionary<string, SectionProps> pageLayouts)
    {
        foreach (var element in sectionElements.SelectMany(element => element.DescendantsAndSelf()))
        {
            if (element.Name != Text + "p" && element.Name != Text + "h")
            {
                continue;
            }

            var styleName = element.Attribute(Text + "style-name")?.Value;
            var masterPage = styleName is null ? null : styleMasterPages.GetValueOrDefault(styleName);
            if (masterPage is not null
                && masterPages.TryGetValue(masterPage, out var layoutName)
                && pageLayouts.TryGetValue(layoutName, out var layout))
            {
                return (layout, masterPage);
            }
        }

        // Fallback for sections without their own break (e.g. the first) or foreign files:
        // the first page layout.
        foreach (var fallback in pageLayouts.Values)
        {
            return (fallback, null);
        }

        return (null, null);
    }

    /// <summary>The style names that the writer generates itself and that are therefore not read.</summary>
    private static readonly HashSet<string> BuiltInStyleNames = new(StringComparer.Ordinal)
    {
        "Standard",
        "Heading1", "Heading2", "Heading3", "Heading4", "Heading5", "Heading6",
    };

    /// <summary>
    /// The named paragraph styles of the document (phase AC, AC01). They are part of the model
    /// as <see cref="PortableDocument.Styles"/> and are written again by the writers. A style
    /// inherits the properties of its parent style (<c>style:parent-style-name</c>).
    /// </summary>
    private static IReadOnlyList<ParagraphStyle> ReadNamedStyles(
        XElement? styles,
        IReadOnlyDictionary<string, ParagraphStyleLink> links,
        IReadOnlyDictionary<string, TextRun> runFormats)
    {
        var container = styles?.Element(Office + "styles");
        if (container is null)
        {
            return [];
        }

        var parents = new ParagraphFormatResolver(links, ParagraphFormat.Empty);
        var result = new List<ParagraphStyle>();
        foreach (var style in container.Elements(Style + "style"))
        {
            if (style.Attribute(Style + "family")?.Value != "paragraph")
            {
                continue;
            }

            var name = style.Attribute(Style + "name")?.Value;
            if (name is null || BuiltInStyleNames.Contains(name))
            {
                continue;
            }

            var paragraph = ResolveNamedStyle(style, name, parents);
            // ADR-0010: the run formatting of the style belongs to its definition. The readers do
            // not bake it into the runs, so it has to stay on the named style (as in DOCX).
            if (runFormats.TryGetValue(name, out var runFormat))
            {
                paragraph = RunFormatOf(runFormat).ApplyTo(paragraph);
            }

            result.Add(paragraph);
        }

        return result;
    }

    /// <summary>
    /// The formatting of a named paragraph style: its own properties win over the inherited ones
    /// of its parent chain.
    /// </summary>
    private static ParagraphStyle ResolveNamedStyle(XElement style, string name, ParagraphFormatResolver parents)
    {
        var parent = style.Attribute(Style + "parent-style-name")?.Value is { } parentName
            ? parents.Resolve(parentName)
            : ParagraphFormat.Empty;
        var format = ParagraphFormat.Merge(parent, ReadParagraphFormat(style) ?? ParagraphFormat.Empty);
        return format.ToStyle() with { StyleName = name };
    }

    /// <summary>
    /// paragraph styles from content.xml (automatic) and styles.xml (shared) with
    /// <c>style:master-page-name</c> and <c>style:parent-style-name</c>. Automatic styles
    /// take priority.
    /// </summary>
    private static IReadOnlyDictionary<string, ParagraphStyleLink> ReadParagraphStyles(XElement? content, XElement? styles)
    {
        var result = new Dictionary<string, ParagraphStyleLink>(StringComparer.Ordinal);
        foreach (var root in new[] { styles, content })
        {
            if (root is null)
            {
                continue;
            }

            foreach (var style in root.Descendants(Style + "style"))
            {
                if (style.Attribute(Style + "family")?.Value != "paragraph")
                {
                    continue;
                }

                var name = style.Attribute(Style + "name")?.Value;
                if (name is null)
                {
                    continue;
                }

                result[name] = new ParagraphStyleLink(
                    style.Attribute(Style + "master-page-name")?.Value,
                    style.Attribute(Style + "parent-style-name")?.Value,
                    ReadParagraphFormat(style),
                    style.Element(Style + "text-properties"));
            }
        }

        return result;
    }

    /// <summary>Linkage of a paragraph style: master page, parent style and direct formatting.</summary>
    private sealed record ParagraphStyleLink(
        string? MasterPage,
        string? Parent,
        ParagraphFormat? Format,
        XElement? TextProperties = null);

    /// <summary>Direct formatting of a paragraph style (a subset of ODF).</summary>
    private sealed record ParagraphFormat(
        TextAlignment? Alignment,
        Length? LeftIndent,
        Length? RightIndent,
        Length? FirstLineIndent,
        Length? SpaceBefore,
        Length? SpaceAfter,
        LineSpacing? LineSpacing,
        Color? Background,
        bool? RightToLeft,
        bool? BreakBefore = null,
        bool? KeepWithNext = null,
        bool? KeepTogether = null,
        IReadOnlyList<TabStop>? TabStops = null,
        ParagraphBorders? Borders = null)
    {
        public static ParagraphFormat Empty { get; } = new(null, null, null, null, null, null, null, null, null);

        public static ParagraphFormat Merge(ParagraphFormat parent, ParagraphFormat child) => new(
            child.Alignment ?? parent.Alignment,
            child.LeftIndent ?? parent.LeftIndent,
            child.RightIndent ?? parent.RightIndent,
            child.FirstLineIndent ?? parent.FirstLineIndent,
            child.SpaceBefore ?? parent.SpaceBefore,
            child.SpaceAfter ?? parent.SpaceAfter,
            child.LineSpacing ?? parent.LineSpacing,
            child.Background ?? parent.Background,
            child.RightToLeft ?? parent.RightToLeft,
            child.BreakBefore ?? parent.BreakBefore,
            child.KeepWithNext ?? parent.KeepWithNext,
            child.KeepTogether ?? parent.KeepTogether,
            child.TabStops ?? parent.TabStops,
            child.Borders ?? parent.Borders);

        public ParagraphStyle ToStyle()
        {
            var style = ParagraphStyle.Default;
            if (Alignment is { } alignment)
            {
                style = style with { Alignment = alignment };
            }

            if (LeftIndent is { } left)
            {
                style = style with { LeftIndent = left };
            }

            if (RightIndent is { } right)
            {
                style = style with { RightIndent = right };
            }

            if (FirstLineIndent is { } first)
            {
                style = style with { FirstLineIndent = first };
            }

            // ODF allows negative vertical margins; the model does not.
            if (SpaceBefore is { } before)
            {
                style = style with { SpaceBefore = Length.FromPoints(Math.Max(0, before.Points)) };
            }

            if (SpaceAfter is { } after)
            {
                style = style with { SpaceAfter = Length.FromPoints(Math.Max(0, after.Points)) };
            }

            if (LineSpacing is { } spacing)
            {
                style = style with { LineSpacing = spacing };
            }

            if (Background is { } background)
            {
                style = style with { Background = background };
            }

            if (RightToLeft is { } rightToLeft)
            {
                style = style with { RightToLeft = rightToLeft };
            }

            if (BreakBefore is { } breakBefore)
            {
                style = style with { PageBreakBefore = breakBefore };
            }

            if (KeepWithNext is { } keepWithNext)
            {
                style = style with { KeepWithNext = keepWithNext };
            }

            if (KeepTogether is { } keepTogether)
            {
                style = style with { KeepTogether = keepTogether };
            }

            if (TabStops is { } tabStops)
            {
                // ODF counts from the paragraph indent, the model from the margin.
                var indent = LeftIndent?.Points ?? 0;
                style = style with
                {
                    TabStops = tabStops.Select(tab => new TabStop(Length.FromPoints(tab.Position.Points + indent), tab.Alignment, tab.Leader)).ToList(),
                };
            }

            if (Borders is { } borders)
            {
                style = style with { Borders = borders };
            }

            return style;
        }
    }

    private static ParagraphFormat? ReadParagraphFormat(XElement style)
    {
        if (style.Element(Style + "paragraph-properties") is not { } properties)
        {
            return null;
        }

        var format = new ParagraphFormat(
            ParseAlignment(properties.Attribute(Fo + "text-align")?.Value),
            ParseLength(properties.Attribute(Fo + "margin-left")?.Value),
            ParseLength(properties.Attribute(Fo + "margin-right")?.Value),
            ParseLength(properties.Attribute(Fo + "text-indent")?.Value),
            ParseLength(properties.Attribute(Fo + "margin-top")?.Value),
            ParseLength(properties.Attribute(Fo + "margin-bottom")?.Value),
            ParseLineHeight(properties.Attribute(Fo + "line-height")?.Value)
                ?? (ParseLength(properties.Attribute(Style + "line-height-at-least")?.Value) is { Points: > 0 } atLeast ? LineSpacing.AtLeast(atLeast) : null)
                ?? ParseLineHeight(properties.Attribute(Style + "line-height")?.Value), // written by earlier versions
            ParseColor(properties.Attribute(Fo + "background-color")?.Value),
            properties.Attribute(Style + "writing-mode")?.Value switch
            {
                "rl-tb" => true,
                "lr-tb" or "lr" => false,
                _ => null,
            },
            properties.Attribute(Fo + "break-before")?.Value switch
            {
                "page" => true,
                "auto" => false,
                _ => null,
            },
            properties.Attribute(Fo + "keep-with-next")?.Value switch
            {
                "always" => true,
                "auto" => false,
                _ => null,
            },
            properties.Attribute(Fo + "keep-together")?.Value switch
            {
                "always" => true,
                "auto" => false,
                _ => null,
            },
            ReadTabStops(properties.Element(Style + "tab-stops")),
            ReadParagraphBorders(properties));
        return format == ParagraphFormat.Empty ? null : format;
    }

    private static ParagraphBorders? ReadParagraphBorders(XElement properties)
    {
        // Present border attributes are an explicit border state; all "none" values mean
        // "no borders" (ParagraphBorders.None) and must not inherit the style (ADR-0016).
        var present = false;
        ParagraphBorder? ReadSide(string name)
        {
            if (properties.Attribute(Fo + name) is not { } attribute)
            {
                return null;
            }

            present = true;
            return ParseBorder(attribute.Value);
        }

        var top = ReadSide("border-top");
        var right = ReadSide("border-right");
        var bottom = ReadSide("border-bottom");
        var left = ReadSide("border-left");
        return present
            ? new ParagraphBorders { Top = top, Right = right, Bottom = bottom, Left = left }
            : null;
    }

    /// <summary>Reads the tab stops of a paragraph style (phase AC, AC01).</summary>
    private static IReadOnlyList<TabStop>? ReadTabStops(XElement? tabStops)
    {
        if (tabStops is null)
        {
            return null;
        }

        var stops = new List<TabStop>();
        foreach (var tab in tabStops.Elements(Style + "tab-stop"))
        {
            var position = ParseLength(tab.Attribute(Style + "position")?.Value);
            if (position is not { Points: >= 0 })
            {
                continue;
            }

            var alignment = tab.Attribute(Style + "type")?.Value switch
            {
                "center" => TabStopAlignment.Center,
                "right" => TabStopAlignment.Right,
                "char" => TabStopAlignment.Decimal,
                _ => TabStopAlignment.Left,
            };
            var leader = tab.Attribute(Style + "leader-text")?.Value switch
            {
                "." => TabStopLeader.Dot,
                "-" => TabStopLeader.Hyphen,
                "_" => TabStopLeader.Underscore,
                _ => TabStopLeader.None,
            };
            stops.Add(new TabStop(position.Value, alignment, leader));
        }

        return stops;
    }

    private static TextAlignment? ParseAlignment(string? value) => value switch
    {
        "center" => TextAlignment.Center,
        "right" or "end" => TextAlignment.Right,
        "justify" => TextAlignment.Justify,
        "left" or "start" => TextAlignment.Left,
        _ => null,
    };

    internal static Length? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.EndsWith("pt", StringComparison.Ordinal))
        {
            return Length.FromPoints(ParseNumber(value[..^2]));
        }

        if (value.EndsWith("cm", StringComparison.Ordinal))
        {
            return Length.FromCentimeters(ParseNumber(value[..^2]));
        }

        if (value.EndsWith("mm", StringComparison.Ordinal))
        {
            return Length.FromMillimeters(ParseNumber(value[..^2]));
        }

        if (value.EndsWith("in", StringComparison.Ordinal))
        {
            return Length.FromInches(ParseNumber(value[..^2]));
        }

        if (value.EndsWith("pc", StringComparison.Ordinal))
        {
            return Length.FromPoints(ParseNumber(value[..^2]) * 12);
        }

        if (value.EndsWith("px", StringComparison.Ordinal))
        {
            return Length.FromPoints(ParseNumber(value[..^2]) * 0.75);
        }

        return null;
    }

    private static double Points(string? value, double fallback) => ParseLength(value)?.Points ?? fallback;

    private static double ParseNumber(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;

    private static LineSpacing? ParseLineHeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.EndsWith('%') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            return double.IsFinite(percent) && percent > 0 ? LineSpacing.Multiple(percent / 100.0) : null;
        }

        return ParseLength(value) is { Points: > 0 } length ? LineSpacing.Exactly(length) : null;
    }

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return Color.FromHex(value);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The paragraph style of an element: its resolved paragraph formatting (parent chain, defaults)
    /// and, ADR-0010, the run formatting of the style, so the writers apply it as the base of the runs.
    /// </summary>
    private static ParagraphStyle ResolveParagraphStyle(XElement element, ReadContext context)
        => element.Attribute(Text + "style-name")?.Value is { } name
            ? ResolveParagraphStyle(name, context)
            : ResolveRunFormat(null, context).ApplyTo(context.ParagraphDefaults.ToStyle());

    /// <summary>
    /// The formatting a paragraph inherits from its named style (parent chain and run formatting
    /// included, ADR-0011) without any direct formatting of the paragraph.
    /// </summary>
    private static ParagraphStyle ResolveParagraphStyle(string name, ReadContext context)
    {
        var style = context.Formats.Resolve(name).ToStyle();
        return ResolveRunFormat(name, context).ApplyTo(style);
    }

    /// <summary>The run formatting of a resolved text style.</summary>
    private static RunFormat RunFormatOf(TextRun format) => RunFormat.Of(format);

    /// <summary>The run formatting of a paragraph style (the default when the style sets none).</summary>
    private static RunFormat ResolveRunFormat(string? name, ReadContext context)
        => context.ParagraphRunFormats.TryGetValue(name ?? string.Empty, out var named)
            ? RunFormatOf(named)
            : RunFormatOf(context.ParagraphRunDefault);

    private static ParagraphStyle ResolveParagraphStyleBase(XElement element, ReadContext context)
        => element.Attribute(Text + "style-name")?.Value is { } name
            ? ResolveParagraphStyle(name, context)
            : context.ParagraphDefaults.ToStyle();

    /// <summary>
    /// The paragraph formats of all styles along their <c>style:parent-style-name</c> chain over the
    /// defaults. Each style is resolved once (StyleChain), so a crafted long chain costs linear time;
    /// a style left out (the writer's own heading style) ends the chain like a missing one.
    /// </summary>
    private sealed class ParagraphFormatResolver(
        IReadOnlyDictionary<string, ParagraphStyleLink> styles, ParagraphFormat defaults)
    {
        private readonly Dictionary<string, Dictionary<string, ParagraphFormat>> _excluding = new(StringComparer.Ordinal);
        private Dictionary<string, ParagraphFormat>? _all;

        public ParagraphFormat Resolve(string name)
            => (_all ??= Build(null)).TryGetValue(name, out var format) ? format : defaults;

        public ParagraphFormat ResolveExcluding(string name, string excluded)
        {
            if (!_excluding.TryGetValue(excluded, out var formats))
            {
                formats = Build(excluded);
                _excluding[excluded] = formats;
            }

            return formats.TryGetValue(name, out var format) ? format : defaults;
        }

        private Dictionary<string, ParagraphFormat> Build(string? excluded)
            => StyleChain.ResolveAll(
                styles.Keys,
                name => styles.TryGetValue(name, out var link) ? link.Parent : null,
                name => name != excluded && styles.ContainsKey(name),
                defaults,
                (parent, name) => ParagraphFormat.Merge(parent, styles[name].Format ?? ParagraphFormat.Empty));
    }

    /// <summary>
    /// The run formatting of every paragraph style with the inherited text properties merged in;
    /// the own attributes win over the parent's (ODF inheritance works per attribute).
    /// shortcut: the formatting is written into the runs instead of being modelled, so a later
    /// change of a paragraph style does not reach them.
    /// </summary>
    private static IReadOnlyDictionary<string, TextRun> ReadParagraphRunFormats(
        IReadOnlyDictionary<string, ParagraphStyleLink> styles,
        XElement? defaults,
        Length defaultFontSize)
    {
        // Each style is resolved once over its parent chain (StyleChain), so a crafted long chain
        // costs linear time.
        var properties = StyleChain.ResolveAll(
            styles.Keys,
            name => styles.TryGetValue(name, out var link) ? link.Parent : null,
            styles.ContainsKey,
            defaults,
            (parent, name) => MergeProperties(parent, styles[name].TextProperties));
        var result = new Dictionary<string, TextRun>(StringComparer.Ordinal);
        foreach (var name in styles.Keys)
        {
            if (name.Length == 0)
            {
                continue;
            }

            result[name] = RunOf(ReadCharacterStyle(name, properties[name], defaultFontSize), null);
        }

        return result;
    }

    /// <summary>Runs start without formatting: the paragraph style provides the base (ADR-0010).</summary>
    private static readonly TextRun EmptyRunFormat = new(string.Empty);

    /// <summary>
    /// Reads a sequence of block elements. A page break that the writer absorbed into the
    /// following paragraph or table style (<c>fo:break-before="page"</c>) is read back as a
    /// page break, except before the first block (there it is the section break).
    /// </summary>
    private static List<Block> ReadBlocks(
        ReadContext context,
        IEnumerable<XElement> elements,
        string? listStyle = null,
        BlockOriginRecorder? origins = null)
    {
        var blocks = new List<Block>();
        foreach (var element in elements)
        {
            var first = blocks.Count;
            var origin = origins?.Next();
            if (blocks.Count > 0 && BreaksBefore(context, element) && blocks[^1] is not PageBreak)
            {
                blocks.Add(new PageBreak());
            }

            ReadBlock(context, element, blocks, listStyle);
            if (origin is { } index)
            {
                origins!.Record(blocks, first, index);
            }
        }

        return blocks;
    }

    private static bool BreaksBefore(ReadContext context, XElement element)
    {
        if (element.Name == Table + "table")
        {
            return element.Attribute(Table + "style-name")?.Value is { } tableStyle
                && context.TableBreaks.Contains(tableStyle);
        }

        return (element.Name == Text + "p" || element.Name == Text + "h")
            && !IsPageBreakParagraph(element)
            && element.Attribute(Text + "style-name")?.Value is { } styleName
            && context.Formats.Resolve(styleName).BreakBefore == true;
    }

    private static void ReadBlock(ReadContext context, XElement element, List<Block> blocks, string? listStyle = null)
    {
        ReaderGuard.EnsureStack();
        if (element.Name == Text + "h")
        {
            var level = OutlineLevel(element);
            var headingStyleName = element.Attribute(Text + "style-name")?.Value;
            // The formatting of the writer's named heading style (HeadingN) is the heading default
            // and not direct formatting of this heading.
            var headingStyle = headingStyleName is not null
                ? context.Formats.ResolveExcluding(headingStyleName, Heading.StyleNameFor(level)).ToStyle()
                : context.ParagraphDefaults.ToStyle();
            var headingFontSize = headingStyleName is not null
                && context.ParagraphRunFormats.TryGetValue(headingStyleName, out var headingRunFormat)
                ? headingRunFormat.FontSize
                : null;
            var inlines = ReadParagraphContent(
                element,
                EmptyRunFormat,
                context,
                headingFontSize ?? context.DefaultFontSize);
            blocks.Add(new Heading(level, inlines)
            {
                Style = WithoutBreak(headingStyle) with { StyleName = Heading.StyleNameFor(level) },
            });
            ReadFrames(context, element, blocks);
        }
        else if (element.Name == Text + "p")
        {
            var paragraphStyleName = element.Attribute(Text + "style-name")?.Value;
            var paragraphStyle = WithoutBreak(ResolveParagraphStyle(element, context));
            var inlines = ReadParagraphContent(
                element,
                EmptyRunFormat,
                context,
                paragraphStyle.FontSize ?? context.DefaultFontSize);
            if (IsPageBreakParagraph(element))
            {
                blocks.Add(new PageBreak());
            }
            else if (inlines.Count > 0)
            {
                var keepsReference = paragraphStyleName is not null
                    && context.NamedStyleNames.Contains(paragraphStyleName);
                blocks.Add(new Paragraph(inlines)
                {
                    Style = keepsReference ? paragraphStyle with { StyleName = paragraphStyleName } : paragraphStyle,
                    // ADR-0011: the formatting of the named style is kept apart, so a later edit
                    // of the style reaches this paragraph and the writer writes a reference.
                    SourceStyleBase = keepsReference
                        ? WithoutBreak(ResolveParagraphStyleBase(element, context))
                        : null,
                });
            }

            ReadFrames(context, element, blocks);
        }
        else if (element.Name == Text + "list")
        {
            blocks.Add(ReadList(context, element, listStyle));
        }
        else if (element.Name == Table + "table")
        {
            blocks.Add(ReadTable(context, element));
        }
        else if (element.Name == DrawFrame)
        {
            if (ReadFrame(context, element) is { } frameBlock)
            {
                blocks.Add(frameBlock);
            }
        }
    }

    private static readonly HashSet<XName> BasicShapes =
    [
        XName.Get("rect", OdtNamespaces.Draw), XName.Get("ellipse", OdtNamespaces.Draw),
        XName.Get("line", OdtNamespaces.Draw), XName.Get("path", OdtNamespaces.Draw),
    ];

    /// <summary>
    /// images and shapes are anchored to paragraphs in ODT; they are read as block-level images and
    /// shapes. A frame or shape inside another one, or inside a group (a diagram, ADR-0030), belongs to it and is not read on its own.
    /// </summary>
    private static void ReadFrames(ReadContext context, XElement paragraph, List<Block> blocks)
    {
        // ADR-0024: a form control placed in the paragraph becomes a form field block.
        foreach (var control in paragraph.Descendants(XName.Get("control", OdtNamespaces.Draw)))
        {
            if (OdtForms.ToField(control, context.Controls) is { } formField)
            {
                blocks.Add(formField);
            }
            else
            {
                Warn(context.Warnings, "ODT: a form control that is not a text, check box, list box or radio button is not mapped when reading.");
            }
        }

        foreach (var frame in paragraph.Descendants().Where(element =>
            (element.Name == DrawFrame || BasicShapes.Contains(element.Name))
            && !element.Ancestors().TakeWhile(ancestor => ancestor != paragraph)
                .Any(ancestor => ancestor.Name == DrawFrame || BasicShapes.Contains(ancestor.Name) || ancestor.Name == XName.Get("g", OdtNamespaces.Draw))))
        {
            if (frame.Name != DrawFrame || frame.Element(XName.Get("text-box", OdtNamespaces.Draw)) is not null)
            {
                if (ReadShape(context, frame) is { } shape)
                {
                    blocks.Add(shape);
                }
            }
            else if (ReadFrame(context, frame) is { } frameBlock)
            {
                blocks.Add(frameBlock);
            }
        }
    }

    /// <summary>
    /// Reads a shape or text box (ADR-0022): <c>draw:rect</c>, <c>draw:ellipse</c>, <c>draw:line</c>,
    /// <c>draw:path</c> and a frame with a text box. Solid fill and outline are mapped; a rotated
    /// line comes back as an unrotated line between its end points.
    /// </summary>
    private static DrawingShape? ReadShape(ReadContext context, XElement element)
    {
        XNamespace draw = OdtNamespaces.Draw;
        XNamespace svg = OdtNamespaces.Svg;
        var warnings = context.Warnings;
        var name = (string?)element.Attribute(draw + "style-name");
        var graphicStyles = element.Ancestors(Office + "document-styles").Any() ? context.BandGraphicStyles : context.GraphicStyles;
        var properties = name is not null && graphicStyles.TryGetValue(name, out var graphic) ? graphic : null;
        var anchored = (string?)element.Attribute(Text + "anchor-type") is not (null or "as-char");
        var placement = anchored ? ReadFramePlacement(properties, warnings) : default;
        var wrap = (string?)properties?.Attribute(Style + "wrap");
        var runThrough = (string?)properties?.Attribute(Style + "run-through");
        var shapeWrap = !anchored
            ? ImageWrap.None
            : wrap switch
            {
                "none" => ImageWrap.TopAndBottom,
                "run-through" => runThrough == "foreground" ? ImageWrap.InFront : ImageWrap.Behind,
                _ => ImageWrap.Square,
            };

        Length? width, height;
        double x = 0, y = 0;
        var isLine = element.Name.LocalName == "line";
        if (isLine)
        {
            var points = new[] { "x1", "y1", "x2", "y2" }
                .Select(attribute => ReadImageLength((string?)element.Attribute(svg + attribute), positive: false, warnings)?.Points).ToArray();
            if (points.Any(point => point is null))
            {
                Warn(warnings, "ODT: a line without end points is not mapped when reading.");
                return null;
            }

            width = Length.FromPoints(Math.Abs(points[2]!.Value - points[0]!.Value));
            height = Length.FromPoints(Math.Abs(points[3]!.Value - points[1]!.Value));
            x = Math.Min(points[0]!.Value, points[2]!.Value);
            y = Math.Min(points[1]!.Value, points[3]!.Value);
            if ((points[2]!.Value - points[0]!.Value) * (points[3]!.Value - points[1]!.Value) < 0)
            {
                Warn(warnings, "ODT: a line that rises from left to right is approximated when reading.");
            }
        }
        else
        {
            width = ReadImageLength((string?)element.Attribute(svg + "width"), positive: true, warnings);
            height = ReadImageLength((string?)element.Attribute(svg + "height"), positive: true, warnings);
            x = ReadImageLength((string?)element.Attribute(svg + "x"), positive: false, warnings)?.Points ?? 0;
            y = ReadImageLength((string?)element.Attribute(svg + "y"), positive: false, warnings)?.Points ?? 0;
        }

        if (width is null || height is null)
        {
            var drawingName = (string?)element.Attribute(draw + "name");
            Warn(warnings, string.IsNullOrWhiteSpace(drawingName)
                ? $"ODT: the drawing object 'draw:{element.Name.LocalName}' is not mapped when reading."
                : $"ODT: the drawing object 'draw:{element.Name.LocalName}' ('{drawingName}') is not mapped when reading.");
            return null;
        }

        // "rotate (a) translate (tx ty)": the shape turns about its top left corner, then moves by t.
        var rotation = 0.0;
        if (!isLine && (string?)element.Attribute(draw + "transform") is { } transform)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                transform, @"^\s*rotate\s*\(\s*(\S+?)\s*\)\s*translate\s*\(\s*(\S+)\s+(\S+?)\s*\)\s*$");
            if (match.Success
                && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var radians) && double.IsFinite(radians)
                && ReadImageLength(match.Groups[2].Value, positive: false, warnings) is { } tx
                && ReadImageLength(match.Groups[3].Value, positive: false, warnings) is { } ty)
            {
                rotation = -radians * 180.0 / Math.PI;
                double halfWidth = width.Value.Points / 2, halfHeight = height.Value.Points / 2;
                var (rx, ry) = OdtShapes.Rotate(halfWidth, halfHeight, 0, 0, rotation);
                x = tx.Points + rx - halfWidth;
                y = ty.Points + ry - halfHeight;
            }
            else
            {
                Warn(warnings, "ODT: a shape transform other than rotate and translate is not mapped when reading.");
            }
        }

        var fillKind = (string?)properties?.Attribute(draw + "fill");
        Color? fill = null;
        if (fillKind == "solid" && Color.TryParseHex((string?)properties?.Attribute(draw + "fill-color"), out var fillColor))
        {
            fill = fillColor;
        }
        else if (fillKind is not (null or "none" or "solid"))
        {
            Warn(warnings, $"ODT: the shape fill '{fillKind}' is not mapped when reading.");
        }

        var strokeKind = (string?)properties?.Attribute(draw + "stroke");
        Color? outline = null;
        var dash = ShapeDash.Solid;
        var outlineWidth = Length.FromPoints(1);
        if (strokeKind is "solid" or "dash")
        {
            outline = Color.TryParseHex((string?)properties?.Attribute(svg + "stroke-color"), out var strokeColor) ? strokeColor : Color.Black;
            if (ParseLength((string?)properties?.Attribute(svg + "stroke-width")) is { Points: > 0 } strokeWidth)
            {
                outlineWidth = strokeWidth;
            }

            if (strokeKind == "dash")
            {
                dash = ((string?)properties?.Attribute(draw + "stroke-dash") ?? string.Empty).Contains("dot", StringComparison.OrdinalIgnoreCase)
                    ? ShapeDash.Dot : ShapeDash.Dash;
            }
        }
        else if (strokeKind is not (null or "none"))
        {
            Warn(warnings, $"ODT: the shape outline '{strokeKind}' is not mapped when reading.");
        }

        ShapeKind kind;
        List<Block>? content = null;
        List<ShapePathStep>? steps = null;
        switch (element.Name.LocalName)
        {
            case "frame":
                kind = ShapeKind.TextBox;
                content = [];
                foreach (var child in element.Element(draw + "text-box")!.Elements())
                {
                    var read = new List<Block>();
                    ReadBlock(context, child, read);
                    content.AddRange(read.Where(block => block is Paragraph or Heading));
                    if (read.Any(block => block is not (Paragraph or Heading)))
                    {
                        Warn(warnings, "ODT: a text box holds only paragraphs when read; other content in it is not mapped.");
                    }
                }

                break;
            case "ellipse":
                kind = ShapeKind.Ellipse;
                break;
            case "line":
                kind = ShapeKind.Line;
                break;
            case "path":
                kind = ShapeKind.Freeform;
                steps = ReadShapePath(element, width.Value, height.Value);
                if (steps is null)
                {
                    Warn(warnings, "ODT: a path other than absolute M, L, C and Z commands is not mapped when reading.");
                    return null;
                }

                break;
            default:
                kind = element.Attribute(draw + "corner-radius") is { } radius && ParseLength(radius.Value) is { Points: > 0 }
                    ? ShapeKind.RoundRectangle : ShapeKind.Rectangle;
                break;
        }

        try
        {
            return new DrawingShape(kind, width.Value, height.Value, content, steps)
            {
                Fill = fill,
                Outline = outline,
                OutlineWidth = outlineWidth,
                Dash = dash,
                Rotation = rotation,
                Wrap = shapeWrap,
                OffsetX = anchored ? Length.FromPoints(x) : null,
                OffsetY = anchored ? Length.FromPoints(y) : null,
                HorizontalRelativeTo = placement.HorizontalRelativeTo,
                VerticalRelativeTo = placement.VerticalRelativeTo,
                HorizontalAlignment = placement.HorizontalAlignment,
                VerticalAlignment = placement.VerticalAlignment,
                AltText = element.Element(svg + "title")?.Value,
            };
        }
        catch (ArgumentException)
        {
            Warn(warnings, "ODT: a shape with an invalid size or path is not mapped when reading.");
            return null;
        }
    }

    /// <summary>The steps of <c>svg:d</c> (absolute M, L, C, Z), scaled from the view box to the shape's size.</summary>
    private static List<ShapePathStep>? ReadShapePath(XElement element, Length width, Length height)
    {
        XNamespace svg = OdtNamespaces.Svg;
        var box = ((string?)element.Attribute(svg + "viewBox") ?? string.Empty)
            .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        if (box.Length != 4 || box.Any(value => !double.IsFinite(value)) || box[2] <= 0 || box[3] <= 0)
        {
            return null;
        }

        var tokens = System.Text.RegularExpressions.Regex.Matches((string?)element.Attribute(svg + "d") ?? string.Empty, @"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")
            .Select(match => match.Value).ToList();
        double scaleX = width.Points / box[2], scaleY = height.Points / box[3];
        var steps = new List<ShapePathStep>();
        var index = 0;
        bool Take(int count, out Length[] values)
        {
            values = new Length[count];
            for (var i = 0; i < count; i++)
            {
                if (index >= tokens.Count || !double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || !double.IsFinite(number))
                {
                    return false;
                }

                index++;
                values[i] = Length.FromPoints((i % 2 == 0 ? (number - box[0]) * scaleX : (number - box[1]) * scaleY));
            }

            return true;
        }

        while (index < tokens.Count)
        {
            var command = tokens[index++];
            switch (command)
            {
                case "M" or "L" when Take(2, out var p):
                    steps.Add(new ShapePathStep(command == "M" ? ShapePathCommand.MoveTo : ShapePathCommand.LineTo, p[0], p[1]));
                    break;
                case "C" when Take(6, out var c):
                    steps.Add(new ShapePathStep(ShapePathCommand.CubicTo, c[4], c[5], c[0], c[1], c[2], c[3]));
                    break;
                case "Z" or "z":
                    steps.Add(new ShapePathStep(ShapePathCommand.Close, Length.Zero, Length.Zero));
                    break;
                default:
                    return null;
            }
        }

        return steps.Count > 0 ? steps : null;
    }

    /// <summary>The break before a paragraph is represented by a separate page break block.</summary>
    private static ParagraphStyle WithoutBreak(ParagraphStyle style)
        => style.PageBreakBefore == true ? style with { PageBreakBefore = false } : style;

    /// <summary>Table styles (automatic and shared) that start the table on a new page.</summary>
    private static IReadOnlySet<string> ReadTableBreaks(XElement content, XElement? styles)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in new[] { styles, content })
        {
            foreach (var style in root?.Descendants(Style + "style") ?? [])
            {
                if (style.Attribute(Style + "family")?.Value == "table"
                    && style.Attribute(Style + "name")?.Value is { } name
                    && style.Element(Style + "table-properties")?.Attribute(Fo + "break-before")?.Value == "page")
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    /// <summary>The style tables and the warning list shared while reading one package.</summary>
    private sealed record ReadContext(
        ZipArchive Zip,
        IReadOnlyDictionary<string, ParagraphStyleLink> ParagraphStyles,
        IReadOnlyDictionary<string, TextStyle> TextStyles,
        IReadOnlyDictionary<string, NumberingDefinition> ListDefinitions,
        IReadOnlySet<string> TableBreaks,
        List<ReadWarning> Warnings)
    {
        public TableReadBudget TableBudget { get; init; } = new();

        /// <summary>The media budget of this read (shared by copies made with <c>with</c>).</summary>
        public ReaderGuard.MediaBudget Media { get; init; } = new();

        /// <summary>The chart point budget of this read (shared by copies made with <c>with</c>).</summary>
        public ReaderGuard.ChartBudget Charts { get; init; } = new();

        /// <summary>The form controls of <c>office:forms</c> by id (ADR-0024).</summary>
        public IReadOnlyDictionary<string, XElement> Controls { get; init; } = new Dictionary<string, XElement>(StringComparer.Ordinal);

        /// <summary>The comments of <c>content.xml</c> (ADR-0026).</summary>
        public OdtComments.Comments Comments { get; init; } = OdtComments.Comments.Empty;

        /// <summary>The tracked changes of <c>content.xml</c> (ADR-0027).</summary>
        public OdtRevisions.Source Revisions { get; init; } = OdtRevisions.Source.Empty;
        /// <summary>The numbering definitions collected while reading; a list style adds its own.</summary>
        public List<NumberingDefinition> Numbering { get; init; } = [];

        /// <summary>The cell styles of the automatic styles (name → background and padding).</summary>
        public IReadOnlyDictionary<string, CellStyle> CellStyles { get; init; } =
            new Dictionary<string, CellStyle>(StringComparer.Ordinal);

        /// <summary>The column styles of the automatic styles (name → width in points).</summary>
        public IReadOnlyDictionary<string, (Length? Width, double? Weight)> ColumnStyles { get; init; } =
            new Dictionary<string, (Length?, double?)>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, (Length? Height, TableRowHeightRule Rule, bool KeepTogether)> RowStyles { get; init; } =
            new Dictionary<string, (Length?, TableRowHeightRule, bool)>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, TableStyleInfo> TableStyles { get; init; } =
            new Dictionary<string, TableStyleInfo>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, XElement> GraphicStyles { get; init; } = new Dictionary<string, XElement>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, XElement> BandGraphicStyles { get; init; } = new Dictionary<string, XElement>(StringComparer.Ordinal);

        /// <summary>Where each top-level block came from, used by the writer (AF12).</summary>
        public BlockOriginRecorder BlockOrigins { get; init; } = new();

        /// <summary>The run formatting per paragraph style name, resolved once per read.</summary>
        public IReadOnlyDictionary<string, TextRun> ParagraphRunFormats { get; init; } =
            new Dictionary<string, TextRun>(StringComparer.Ordinal);

        /// <summary>The paragraph defaults (<c>style:default-style</c>), the root of the inheritance.</summary>
        public ParagraphFormat ParagraphDefaults { get; init; } = ParagraphFormat.Empty;

        /// <summary>The paragraph formats of all styles over the defaults, each resolved once.</summary>
        public ParagraphFormatResolver Formats { get; init; } =
            new(new Dictionary<string, ParagraphStyleLink>(StringComparer.Ordinal), ParagraphFormat.Empty);

        /// <summary>
        /// the paragraph style names that exist in the model (<see cref="PortableDocument.Styles"/>);
        /// a body paragraph keeps a reference only to one of them (ADR-0011).
        /// </summary>
        public IReadOnlySet<string> NamedStyleNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The document font size, the last fallback for a numeric baseline shift.</summary>
        public Length DefaultFontSize { get; init; } = DocumentDefaults.Default.FontSize;

        /// <summary>
        /// The width of the text area of the current section in points. Relative column widths and
        /// a percent table width are resolved against it when the table states no absolute width.
        /// </summary>
        public double AvailableWidth { get; init; } =
            PageSize.A4.Width.Points - PageMargins.Normal.Left.Points - PageMargins.Normal.Right.Points;

        /// <summary>The run formatting of <c>style:default-style</c> for paragraphs without a style.</summary>
        public TextRun ParagraphRunDefault { get; init; } = new(string.Empty);

        private readonly Dictionary<(string Style, int Start), string> _listNames = new();

        /// <summary>
        /// the name of the numbering definition of a list style with the given start value of its
        /// first item; the definition is created once, so all lists that use the style share it
        /// (OF-B01). A start value that differs from the style needs a definition of its own,
        /// because the model keeps the start value on the level.
        /// </summary>
        public string ListName(string styleName, int start)
        {
            if (_listNames.TryGetValue((styleName, start), out var existing))
            {
                return existing;
            }

            var template = ListDefinitions.GetValueOrDefault(styleName) ?? FallbackDefinition(styleName);
            var definition = start == template.Levels[0].Start
                ? new NumberingDefinition(UniqueName(template.Name), template.Levels)
                : new NumberingDefinition(
                    UniqueName(template.Name + "_" + start.ToString(CultureInfo.InvariantCulture)),
                    [template.Levels[0] with { Start = start }, .. template.Levels.Skip(1)]);

            Numbering.Add(definition);
            _listNames[(styleName, start)] = definition.Name;
            return definition.Name;
        }

        /// <summary>A definition for a list style the package does not define.</summary>
        private static NumberingDefinition FallbackDefinition(string styleName)
            => new(
                "ListFallback",
                [
                    new NumberingLevel
                    {
                        Kind = styleName.Contains("Bullet", StringComparison.Ordinal) ? ListKind.Bullet : ListKind.Number,
                    },
                ]);

        /// <summary>A definition name that is free in this document and a valid NCName.</summary>
        private string UniqueName(string name)
        {
            if (NumberingDefinition.IsNcName(name) && Numbering.All(entry => entry.Name != name))
            {
                return name;
            }

            for (var index = 1; ; index++)
            {
                var candidate = name + "_" + index.ToString(CultureInfo.InvariantCulture);
                if (Numbering.All(entry => entry.Name != candidate))
                {
                    return candidate;
                }
            }
        }
    }

    private sealed class TableReadBudget
    {
        public long Rows { get; private set; }
        public long Cells { get; private set; }

        public void Add(long rows, long cells)
        {
            if (rows > ResourceLimits.Default.MaxBlocks - Rows || cells > ResourceLimits.Default.MaxTableCells - Cells)
                throw new DocumentValidationException("ODT: expanded tables exceed the reader resource limits.");
            Rows += rows;
            Cells += cells;
        }
    }

    /// <summary>Reads a body-level <c>draw:frame</c> with an embedded image (phase AC).</summary>
    private static IReadOnlyDictionary<string, XElement> ReadGraphicStyles(XElement? content, XElement? styles, List<ReadWarning> warnings)
    {
        var links = new Dictionary<string, (string? Parent, XElement? Properties)>(StringComparer.Ordinal);
        foreach (var document in new[] { styles, content })
        {
            if (document is null) continue;
            foreach (var style in document.Descendants(Style + "style"))
            {
                if ((string?)style.Attribute(Style + "family") == "graphic" && (string?)style.Attribute(Style + "name") is { } name)
                    links[name] = ((string?)style.Attribute(Style + "parent-style-name"), style.Element(Style + "graphic-properties"));
            }
        }
        if (HasBrokenChain(links))
            Warn(warnings, "ODT: a graphic style has a missing or cyclic parent and may be incomplete.");
        var resolved = ResolveAllProperties(links);
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var name in links.Keys)
        {
            var properties = resolved[name];
            if (properties is not null) result[name] = properties;
        }
        return result;
    }

    /// <summary>A frame with a chart object (ADR-0025) becomes a chart, any other frame is read as an image.</summary>
    private static Block? ReadFrame(ReadContext context, XElement frame)
    {
        if (frame.Element(XName.Get("object-ole", OdtNamespaces.Draw)) is { } ole)
        {
            return ReadEmbeddedObject(context, frame, ole);
        }

        if (frame.Element(XName.Get("object", OdtNamespaces.Draw)) is not { } obj
            || obj.Attribute(XName.Get("href", OdtNamespaces.XLink))?.Value is not { Length: > 0 } href)
        {
            return ReadImage(context, frame);
        }

        var path = Uri.UnescapeDataString(href);
        var folder = (path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path).Trim('/');
        if (context.Zip.GetEntry(folder + "/content.xml") is not { } entry)
        {
            Warn(context.Warnings, $"ODT: the embedded object '{href}' is missing and is not mapped when reading.");
            return null;
        }

        XElement root;
        try
        {
            root = ReaderGuard.LoadXml(context.Media.Read(entry, ResourceLimits.Default.MaxImageBytes)).Root!;
        }
        catch (Exception exception) when (exception is DocumentValidationException or System.Xml.XmlException)
        {
            Warn(context.Warnings, $"ODT: the embedded object '{href}' cannot be read and is not mapped.");
            return null;
        }

        if (OdtMath.Read(root, frame.Element(XName.Get("title", OdtNamespaces.Svg))?.Value, message => Warn(context.Warnings, message)) is { } equation)
        {
            var paragraphStyle = (string?)frame.Parent?.Attribute(XName.Get("style-name", OdtNamespaces.Text));
            return OdtMath.AlignmentOf(paragraphStyle) is { } alignment
                ? new Equation(equation.Root) { AltText = equation.AltText, Alignment = alignment }
                : equation;
        }

        if (!OdtChartReader.IsChart(root))
        {
            Warn(context.Warnings, $"ODT: the embedded object '{href}' is not a chart and is not mapped when reading.");
            return null;
        }

        var alt = frame.Element(XName.Get("title", OdtNamespaces.Svg))?.Value;
        return OdtChartReader.Read(
            root, ParseLength(frame.Attribute(XName.Get("width", OdtNamespaces.Svg))?.Value),
            ParseLength(frame.Attribute(XName.Get("height", OdtNamespaces.Svg))?.Value),
            string.IsNullOrEmpty(alt) ? null : alt, context.Charts, message => Warn(context.Warnings, message));
    }

    /// <summary>A frame with <c>draw:object-ole</c> becomes an <see cref="EmbeddedObject"/> (ADR-0029).</summary>
    private static Block? ReadEmbeddedObject(ReadContext context, XElement frame, XElement ole)
    {
        static string? PartOf(XElement element)
            => element.Attribute(XName.Get("href", OdtNamespaces.XLink))?.Value is { Length: > 0 } href
                && !href.Contains("://", StringComparison.Ordinal)
                    ? (Uri.UnescapeDataString(href).StartsWith("./", StringComparison.Ordinal) ? Uri.UnescapeDataString(href)[2..] : Uri.UnescapeDataString(href)).Trim('/')
                    : null;

        var name = PartOf(ole);
        if (name is null || context.Zip.GetEntry(name) is not { Length: > 0 } entry)
        {
            Warn(context.Warnings, $"ODT: the embedded object '{name ?? "draw:object-ole"}' has no data part (a linked object?) and is not mapped when reading.");
            return null;
        }

        var width = ParseLength(frame.Attribute(XName.Get("width", OdtNamespaces.Svg))?.Value);
        var height = ParseLength(frame.Attribute(XName.Get("height", OdtNamespaces.Svg))?.Value);
        if (width is not { Points: > 0 } || height is not { Points: > 0 })
        {
            Warn(context.Warnings, $"ODT: the embedded object '{name}' has no size and is not mapped when reading.");
            return null;
        }

        // Charged to the media budget: every reference becomes its own copy of the data.
        byte[] data;
        try
        {
            data = context.Media.Read(entry, ReaderGuard.MaxDecodedBytes);
        }
        catch (DocumentValidationException)
        {
            Warn(context.Warnings, $"ODT: the embedded object '{name}' exceeds the size limit and is not mapped.");
            return null;
        }

        ImageBlock? preview = null;
        if (frame.Element(XName.Get("image", OdtNamespaces.Draw)) is { } image && PartOf(image) is { } previewName
            && context.Zip.GetEntry(previewName) is { Length: > 0 } previewEntry)
        {
            try
            {
                var bytes = context.Media.Read(previewEntry, ResourceLimits.Default.MaxImageBytes);
                if (bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
                {
                    preview = new ImageBlock(bytes, ImageMediaType.Png);
                }
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
                {
                    preview = new ImageBlock(bytes, ImageMediaType.Jpeg);
                }
                else
                {
                    Warn(context.Warnings, $"ODT: the preview of the embedded object '{name}' is not a PNG or JPEG and is dropped.");
                }
            }
            catch (DocumentValidationException)
            {
                Warn(context.Warnings, $"ODT: the preview of the embedded object '{name}' exceeds the size limit and is dropped.");
            }
        }

        return new EmbeddedObject(data, EmbeddedObjectKind.OleStorage, width.Value, height.Value) { Preview = preview };
    }

    private static ImageBlock? ReadImage(ReadContext context, XElement frame)
    {
        var zip = context.Zip;
        var warnings = context.Warnings;
        var image = frame.Element(XName.Get("image", OdtNamespaces.Draw));
        var href = image?.Attribute(XName.Get("href", OdtNamespaces.XLink))?.Value;
        if (string.IsNullOrEmpty(href))
        {
            Warn(warnings, "ODT: a frame without an image is not mapped when reading.");
            return null;
        }

        var path = Uri.UnescapeDataString(href);
        var entry = zip.GetEntry(path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path);
        if (entry is null)
        {
            Warn(warnings, $"ODT: the image part '{href}' is missing.");
            return null;
        }

        // Bounded while inflating: a compressed image bomb must not occupy unbounded memory.
        // An oversized image is left out instead of failing the whole document.
        byte[] bytes;
        try
        {
            bytes = context.Media.Read(entry, ResourceLimits.Default.MaxImageBytes);
        }
        catch (DocumentValidationException)
        {
            Warn(warnings, $"ODT: the image part '{href}' exceeds the size limit and is not mapped.");
            return null;
        }

        if (bytes.Length == 0 || MediaTypeOf(href) is not { } mediaType)
        {
            Warn(warnings, $"ODT: the image part '{href}' cannot be mapped.");
            return null;
        }

        XNamespace draw = OdtNamespaces.Draw;
        XNamespace svg = OdtNamespaces.Svg;
        var name = (string?)frame.Attribute(draw + "style-name");
        var isBand = frame.Ancestors(Office + "document-styles").Any();
        var graphicStyles = isBand ? context.BandGraphicStyles : context.GraphicStyles;
        var properties = name is not null && graphicStyles.TryGetValue(name, out var graphic) ? graphic : null;
        var anchored = (string?)frame.Attribute(Text + "anchor-type") is not (null or "as-char");
        var wrap = (string?)properties?.Attribute(Style + "wrap") ?? (string?)frame.Attribute(Style + "wrap");
        if (anchored && wrap is not (null or "none" or "parallel" or "run-through"))
            Warn(warnings, $"ODT: image wrap '{wrap}' is approximated when reading.");
        if (anchored && (string?)frame.Attribute(Text + "anchor-type") != "paragraph")
            Warn(warnings, "ODT: an image anchor other than paragraph is approximated when reading.");
        var placement = anchored ? ReadFramePlacement(properties, warnings) : default;
        // OF-B06: run-through carries the layer in style:run-through (background = behind the text).
        var runThrough = (string?)properties?.Attribute(Style + "run-through");
        var imageWrap = !anchored
            ? ImageWrap.None
            : wrap switch
            {
                "none" => ImageWrap.TopAndBottom,
                "run-through" => runThrough == "foreground" ? ImageWrap.InFront : ImageWrap.Behind,
                _ => ImageWrap.Square,
            };
        var width = ReadImageLength((string?)frame.Attribute(svg + "width"), positive: true, warnings);
        var height = ReadImageLength((string?)frame.Attribute(svg + "height"), positive: true, warnings);
        var clip = (string?)properties?.Attribute(Fo + "clip") ?? (string?)frame.Attribute(Fo + "clip");
        return new ImageBlock(bytes, mediaType)
        {
            AltText = frame.Element(svg + "title")?.Value ?? (string?)frame.Attribute(draw + "name"),
            Width = width,
            Height = height,
            Wrap = imageWrap,
            OffsetX = anchored ? ReadImageLength((string?)frame.Attribute(svg + "x"), positive: false, warnings) : null,
            OffsetY = anchored ? ReadImageLength((string?)frame.Attribute(svg + "y"), positive: false, warnings) : null,
            HorizontalRelativeTo = placement.HorizontalRelativeTo,
            VerticalRelativeTo = placement.VerticalRelativeTo,
            HorizontalAlignment = placement.HorizontalAlignment,
            VerticalAlignment = placement.VerticalAlignment,
            Crop = ReadImageCrop(clip, width, height, warnings),
        };
    }

    /// <summary>The anchor and alignment of a frame from its graphic style; the offsets are svg:x and svg:y.</summary>
    private static FloatPlacement ReadFramePlacement(XElement? properties, List<ReadWarning> warnings)
    {
        var horizontalPos = (string?)properties?.Attribute(Style + "horizontal-pos");
        var verticalPos = (string?)properties?.Attribute(Style + "vertical-pos");
        HorizontalAnchor? horizontal = (string?)properties?.Attribute(Style + "horizontal-rel") switch
        {
            null or "paragraph" or "paragraph-content" => HorizontalAnchor.Column,
            "page" => HorizontalAnchor.Page,
            "page-content" => HorizontalAnchor.Margin,
            "char" => HorizontalAnchor.Character,
            _ => null,
        };
        VerticalAnchor? vertical = (string?)properties?.Attribute(Style + "vertical-rel") switch
        {
            null or "paragraph" or "paragraph-content" => VerticalAnchor.Paragraph,
            "page" => VerticalAnchor.Page,
            "page-content" => VerticalAnchor.Margin,
            "line" => VerticalAnchor.Line,
            _ => null,
        };
        HorizontalFloatAlignment? horizontalAlignment = horizontalPos switch
        {
            "left" => HorizontalFloatAlignment.Left,
            "center" => HorizontalFloatAlignment.Center,
            "right" => HorizontalFloatAlignment.Right,
            _ => null,
        };
        VerticalFloatAlignment? verticalAlignment = verticalPos switch
        {
            "top" => VerticalFloatAlignment.Top,
            "middle" => VerticalFloatAlignment.Center,
            "bottom" => VerticalFloatAlignment.Bottom,
            _ => null,
        };
        if (horizontal is null || vertical is null
            || (horizontalPos is not (null or "from-left") && horizontalAlignment is null)
            || (verticalPos is not (null or "from-top") && verticalAlignment is null))
            Warn(warnings, "ODT: a frame position other than page, margin, paragraph, character or line with an offset, left, centre, right, top, middle or bottom is approximated when reading.");
        return new FloatPlacement(
            horizontal ?? HorizontalAnchor.Column,
            vertical ?? VerticalAnchor.Paragraph,
            horizontalAlignment,
            verticalAlignment,
            null,
            null);
    }

    private static Length? ReadImageLength(string? value, bool positive, List<ReadWarning> warnings)
    {
        if (value is null) return null;
        try
        {
            var text = value.Trim();
            var validNumber = text.Length > 2 && double.TryParse(text.AsSpan(0, text.Length - 2), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);
            var length = validNumber ? ParseLength(text) : null;
            if (length is not null && (!positive || length.Value.Points > 0)) return length;
        }
        catch (ArgumentOutOfRangeException) { }
        Warn(warnings, "ODT: an invalid image length was ignored when reading.");
        return null;
    }

    private static ImageCrop? ReadImageCrop(string? clip, Length? width, Length? height, List<ReadWarning> warnings)
    {
        if (string.IsNullOrEmpty(clip) || clip == "auto") return null;
        var match = System.Text.RegularExpressions.Regex.Match(clip, @"^rect\(\s*([^,\s]+)[,\s]+([^,\s]+)[,\s]+([^,\s]+)[,\s]+([^,\s]+)\s*\)$");
        if (match.Success && width is { Points: > 0 } w && height is { Points: > 0 } h)
        {
            var values = Enumerable.Range(1, 4).Select(index => ReadImageLength(match.Groups[index].Value, positive: false, warnings)?.Points).ToArray();
            if (values.All(value => value is >= 0 && double.IsFinite(value.Value)))
            {
                var crop = new ImageCrop(values[3]!.Value / w.Points, values[0]!.Value / h.Points,
                    values[1]!.Value / w.Points, values[2]!.Value / h.Points);
                if (crop.Left <= 0.5 && crop.Top <= 0.5 && crop.Right <= 0.5 && crop.Bottom <= 0.5
                    && crop.Left + crop.Right < 1 && crop.Top + crop.Bottom < 1) return crop;
            }
        }
        Warn(warnings, "ODT: an image crop could not be mapped when reading.");
        return null;
    }

    private static ImageMediaType? MediaTypeOf(string href)
        => Path.GetExtension(href).ToLowerInvariant() switch
        {
            ".png" => ImageMediaType.Png,
            ".jpg" or ".jpeg" => ImageMediaType.Jpeg,
            ".svg" => ImageMediaType.Svg,
            _ => null,
        };

        // draw:frame in the body (image) is reported instead of swallowed.
        private static readonly XName DrawFrame = XName.Get("frame", OdtNamespaces.Draw);

    /// <summary>Whether an element of the body carries content (instead of, e.g., variable declarations).</summary>
    private static bool IsContent(XElement element)
        => element.Name == Text + "p" || element.Name == Text + "h" || element.Name == Text + "list"
            || element.Name == Table + "table" || element.Name == DrawFrame;

    /// <summary>Outline level of a heading (1–6); missing or invalid values yield 1.</summary>
    private static HeadingLevel OutlineLevel(XElement heading)
        => (HeadingLevel)(int.TryParse(
            heading.Attribute(Text + "outline-level")?.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var level)
                ? Math.Clamp(level, 1, 6)
                : 1);

    /// <summary>Adds a warning if the same message is not already present.</summary>
    private static void Warn(List<ReadWarning> warnings, string message)
    {
        if (!warnings.Any(warning => warning.Message == message))
        {
            warnings.Add(new ReadWarning(message));
        }
    }

    private static bool IsPageBreakParagraph(XElement p)
    {
        // A paragraph whose style sets fo:break-before="page" and that carries no text.
        var styleName = p.Attribute(Text + "style-name")?.Value;
        if (styleName is null)
        {
            return false;
        }

        return p.Value.Length == 0 && (styleName == "PageBreak" || styleName.StartsWith("SectBreak", StringComparison.Ordinal));
    }

    private static ListBlock ReadList(ReadContext context, XElement list, string? parentStyle)
    {
        ReaderGuard.EnsureStack();
        // A nested list without a style name continues the list of its parent.
        var styleName = list.Attribute(Text + "style-name")?.Value
            ?? (string.IsNullOrEmpty(parentStyle) ? string.Empty : parentStyle);
        var items = new List<ListItem>();
        var start = context.ListDefinitions.GetValueOrDefault(styleName)?.Levels[0].Start ?? 1;
        var startRead = false;

        foreach (var item in list.Elements(Text + "list-item"))
        {
            if (!startRead
                && item.Attribute(Text + "start-value")?.Value is { } startValue
                && int.TryParse(startValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedStart)
                && parsedStart >= 1)
            {
                start = parsedStart;
                startRead = true;
            }

            var itemBlocks = ReadBlocks(context, item.Elements(), styleName);
            if (itemBlocks.Count > 0)
            {
                items.Add(new ListItem(itemBlocks));
            }
        }

        // OF-B01: lists that use the same list style share one definition; the level follows from
        // the nesting depth. Continuing a numbering across two lists needs text:continue-list,
        // which stays a documented gap.
        return new ListBlock(context.ListName(styleName, start), items);
    }

    private static TableBlock ReadTable(ReadContext context, XElement table)
    {
        ReaderGuard.EnsureStack();
        var rows = new List<TableRow>();
        foreach (var group in table.Elements())
        {
            if (group.Name == Table + "table-header-rows")
            {
                foreach (var tr in group.Elements(Table + "table-row"))
                {
                    AddRepeatedRow(context, rows, tr, isHeader: true);
                }
            }
            else if (group.Name == Table + "table-row")
            {
                AddRepeatedRow(context, rows, group, isHeader: false);
            }
            else if (group.Name == Table + "table-rows")
            {
                foreach (var tr in group.Elements(Table + "table-row"))
                {
                    AddRepeatedRow(context, rows, tr, isHeader: false);
                }
            }
        }

        // The column widths (the automatic column styles of the table).
        var columns = new List<(Length? Width, double? Weight)>();
        var columnCount = 0;
        var namedMissingWidth = false;
        foreach (var column in table.Elements(Table + "table-column"))
        {
            var count = RepeatCount(column, "number-columns-repeated");
            if (count > ResourceLimits.Default.MaxTableCells - columnCount)
                throw new DocumentValidationException("ODT: expanded table columns exceed the reader resource limits.");
            columnCount += count;
            var name = (string?)column.Attribute(Table + "style-name");
            if (name is null || !context.ColumnStyles.TryGetValue(name, out var columnStyle))
            {
                namedMissingWidth |= name is not null;
                columnStyle = default;
            }
            for (var index = 0; index < count; index++) columns.Add(columnStyle);
        }
        var usedColumns = rows.Count == 0 ? 0 : rows.Max(row => row.Cells.Sum(cell => (long)cell.ColumnSpan));
        if (columns.Count > usedColumns)
        {
            Warn(context.Warnings, "ODT: excess table column widths are ignored.");
            columns.RemoveRange((int)usedColumns, columns.Count - (int)usedColumns);
        }

        double? widthPercent = null;
        Length? absoluteWidth = null;
        ParagraphBorder? tableBorder = null;
        Length? tablePadding = null;
        TableStyleInfo? tableInfo = null;
        if ((string?)table.Attribute(Table + "style-name") is { } tableStyle
            && context.TableStyles.TryGetValue(tableStyle, out var tableStyleInfo))
        {
            absoluteWidth = tableStyleInfo.Absolute;
            tableBorder = tableStyleInfo.Border;
            tablePadding = tableStyleInfo.Padding;
            tableInfo = tableStyleInfo;
            if (tableStyleInfo.Relative is { } rawWidth)
            {
                var text = rawWidth.Trim();
                if (text.EndsWith('%') && double.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var percent) && double.IsFinite(percent) && percent > 0 && percent <= TableBlock.MaxWidthPercent)
                    widthPercent = percent;
                else
                    Warn(context.Warnings, $"ODT: the table width '{rawWidth}' cannot be represented and is ignored.");
            }
        }

        var missingWidth = columns.Any(column => column.Width is null && column.Weight is null);
        var relativeWeight = columns.Where(column => column.Width is null).Sum(column => column.Weight ?? 0);
        var fixedWidth = columns.Sum(column => column.Width?.Points ?? 0);
        // A relative column width is a share of the table width. Without an absolute table width
        // the model still needs points: the percent width or, when absent, the text area is used.
        var tableWidth = absoluteWidth?.Points
            ?? (widthPercent is { } relativePercent
                ? context.AvailableWidth * relativePercent / 100.0
                : context.AvailableWidth);
        var remaining = tableWidth - fixedWidth;
        if (relativeWeight > 0 && (!double.IsFinite(remaining) || remaining <= 0)) missingWidth = true;
        if (missingWidth && (columns.Any(column => column.Width is not null || column.Weight is not null) || namedMissingWidth))
            Warn(context.Warnings, "ODT: some table column widths cannot be represented; automatic widths are used.");
        var widths = missingWidth ? null : columns.Select(column => column.Width
            ?? Length.FromPoints(remaining * (column.Weight!.Value / relativeWeight))).ToList();

        var block = new TableBlock(rows)
        {
            ColumnWidths = widths is { Count: > 0 } ? widths : null,
            WidthPercent = widthPercent,
            Border = tableBorder,
            CellPadding = tablePadding,
            CellPaddingHorizontal = tableInfo?.PaddingHorizontal,
            CellPaddingVertical = tableInfo?.PaddingVertical,
            LeftIndent = tableInfo?.LeftIndent,
        };
        return block.Rows.Any(row => row.Height is not null && row.HeightRule != TableRowHeightRule.Exactly)
            ? block.WithRows(block.Rows.Select((row, index) => ContentMinimum(block, row, index == block.Rows.Count - 1)))
            : block;
    }

    /// <summary>
    /// ODF's min-row-height covers the whole row, the model's minimum only the content (as in Word): the cells' top and
    /// bottom padding, the border above the row and, in the last row, the border below come off. A minimum they fill
    /// completely is no minimum.
    /// </summary>
    private static TableRow ContentMinimum(TableBlock table, TableRow row, bool lastRow)
    {
        if (row.Height is not { } height || row.HeightRule == TableRowHeightRule.Exactly)
        {
            return row;
        }

        // Rounded so that 15 pt written as 17.9 pt (1.2 pt padding, 0.5 pt border) reads back as 15 pt, not 15.000000000000002.
        var content = Math.Round(height.Points - OdtTableCellStyles.RowExtra(table, row, lastRow), 6);
        return content > 1e-6
            ? row with { Height = Length.FromPoints(content) }
            : row with { Height = null, HeightRule = TableRowHeightRule.Auto };
    }

    private static void AddRepeatedRow(ReadContext context, List<TableRow> rows, XElement tr, bool isHeader)
    {
        var count = RepeatCount(tr, "number-rows-repeated");
        var budget = context.TableBudget;
        var previousRows = budget.Rows;
        var previousCells = budget.Cells;
        budget.Add(1, 0);
        var row = ReadTableRow(context, tr, isHeader);
        budget.Add((budget.Rows - previousRows) * (count - 1L), (budget.Cells - previousCells) * (count - 1L));
        for (var index = 0; index < count; index++) rows.Add(row);
    }

    private static int RepeatCount(XElement element, string attribute)
    {
        var value = (string?)element.Attribute(Table + attribute);
        if (value is null) return 1;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1
            || count > ResourceLimits.Default.MaxTableCells)
            throw new DocumentValidationException($"ODT: invalid or excessive {attribute}.");
        return count;
    }

    private static TableRow ReadTableRow(ReadContext context, XElement tr, bool isHeader)
    {
        var cells = new List<TableCell>();
        foreach (var tc in tr.Elements(Table + "table-cell"))
        {
            var count = RepeatCount(tc, "number-columns-repeated");
            var span = int.TryParse(tc.Attribute(Table + "number-columns-spanned")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var columns) && columns > 1
                ? Math.Min(columns, TableCell.MaxSpan)
                : 1;
            var rowSpan = int.TryParse(tc.Attribute(Table + "number-rows-spanned")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var spannedRows) && spannedRows > 1
                ? Math.Min(spannedRows, TableCell.MaxSpan)
                : 1;
            var styleName = tc.Attribute(Table + "style-name")?.Value;
            var cellStyle = styleName is not null && context.CellStyles.TryGetValue(styleName, out var style)
                ? style
                : default;

            var budget = context.TableBudget;
            var previousRows = budget.Rows;
            var previousCells = budget.Cells;
            budget.Add(0, count);
            var cell = new TableCell(ReadBlocks(context, tc.Elements()))
            {
                ColumnSpan = span,
                RowSpan = rowSpan,
                Background = cellStyle?.Background,
                Padding = cellStyle?.Padding is ({ } h, { } v) && h == v ? h : null,
                PaddingHorizontal = cellStyle?.Padding is (var ph, var pv) && ph != pv ? ph : null,
                PaddingVertical = cellStyle?.Padding is (var qh, var qv) && qh != qv ? qv : null,
                Borders = cellStyle?.Borders,
                VerticalAlignment = cellStyle?.VerticalAlignment ?? TableCellVerticalAlignment.Top,
            };
            budget.Add((budget.Rows - previousRows) * (count - 1L), (budget.Cells - previousCells - count) * (count - 1L));
            for (var index = 0; index < count; index++) cells.Add(cell);
        }

        var rowStyle = (string?)tr.Attribute(Table + "style-name");
        (Length? Height, TableRowHeightRule Rule, bool KeepTogether) height = default;
        var hasHeight = rowStyle is not null && context.RowStyles.TryGetValue(rowStyle, out height);
        return new TableRow(cells)
        {
            IsHeader = isHeader,
            Height = hasHeight ? height.Height : null,
            HeightRule = hasHeight ? height.Rule : TableRowHeightRule.Auto,
            KeepTogether = hasHeight && height.KeepTogether,
        };
    }

    /// <summary>Reads the content of a paragraph: text, spaces, tabs, line breaks, page number, spans.</summary>
    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;

    private static List<Inline> ReadParagraphContent(
        XElement paragraph,
        TextRun baseFormat,
        ReadContext context,
        Length? inheritedFontSize)
        => ReadInlineContent(paragraph.Nodes(), baseFormat, context, inheritedFontSize);

    private static List<Inline> ReadInlineContent(
        IEnumerable<XNode> nodes,
        TextRun paragraphFormat,
        ReadContext context,
        Length? inheritedFontSize)
    {
        var inlines = new List<Inline>();
        var buffer = new StringBuilder();

        void Append(char value, int count)
        {
            if ((long)buffer.Length + count > ReaderGuard.MaxTextLength)
            {
                throw new DocumentValidationException(
                    $"ODT: a paragraph exceeds the limit of {ReaderGuard.MaxTextLength} characters.");
            }

            buffer.Append(value, count);
        }

        void Flush(TextRun format)
        {
            if (buffer.Length > 0)
            {
                inlines.Add(Format(buffer.ToString(), format));
                buffer.Clear();
            }
        }

        void AppendNodes(IEnumerable<XNode> children, TextRun format, Length? inheritedSize)
        {
            ReaderGuard.EnsureStack();
            foreach (var node in children)
            {
                if (node is XText bareText)
                {
                    // Unformatted text stands directly in the paragraph (bare text node).
                    if ((long)buffer.Length + bareText.Value.Length > ReaderGuard.MaxTextLength)
                    {
                        throw new DocumentValidationException(
                            $"ODT: a paragraph exceeds the limit of {ReaderGuard.MaxTextLength} characters.");
                    }

                    buffer.Append(bareText.Value);
                    continue;
                }

                if (node is not XElement element)
                {
                    continue;
                }

                if (element.Name == Text + "s")
                {
                    var count = int.TryParse(element.Attribute(Text + "c")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 1;
                    Append(' ', Math.Max(1, count));
                }
                else if (element.Name == Text + "tab")
                {
                    Append('\t', 1);
                }
                else if (element.Name == Text + "line-break")
                {
                    Append('\n', 1);
                }
                else if ((element.Name == Text + "bookmark" || element.Name == Text + "bookmark-start")
                    && element.Attribute(Text + "name")?.Value is { } bookmarkName
                    && !string.IsNullOrWhiteSpace(bookmarkName))
                {
                    Flush(format);
                    inlines.Add(new Bookmark(bookmarkName));
                }
                else if (element.Name == Text + "bookmark-end"
                    && element.Attribute(Text + "name")?.Value is { } bookmarkEndName
                    && !string.IsNullOrWhiteSpace(bookmarkEndName))
                {
                    // OF-C03: the end of a bookmark range; a REF field shows the text between.
                    Flush(format);
                    inlines.Add(new BookmarkEnd(bookmarkEndName));
                }
                else if (element.Name == Text + "page-number")
                {
                    Flush(format);
                    inlines.Add(new PageNumberField());
                }
                else if (element.Name == Text + "page-count")
                {
                    // AF-E: ODT field parity with DOCX.
                    Flush(format);
                    inlines.Add(new PageCountField());
                }
                else if (element.Name == Text + "date")
                {
                    Flush(format);
                    inlines.Add(new DateField(ParseDate(element.Attribute(Text + "date-value")?.Value)));
                }
                else if (element.Name == Text + "user-defined")
                {
                    Flush(format);
                    inlines.Add(new DocPropertyField(
                        element.Attribute(XName.Get("name", OdtNamespaces.Meta))?.Value ?? string.Empty));
                }
                else if (element.Name == Text + "user-field-get")
                {
                    Flush(format);
                    inlines.Add(new DocVariableField(element.Attribute(Text + "name")?.Value ?? string.Empty));
                }
                else if (OdtFields.InstructionOf(element) is { } instruction)
                {
                    // OF-C02: a general field ODF expresses (sequence, reference text, document
                    // information) with its displayed text as the cached result.
                    Flush(format);
                    inlines.Add(new Field(instruction, element.Value));
                }
                else if (element.Name == Text + "bookmark-ref")
                {
                    Flush(format);
                    inlines.Add(new PageRefField(element.Attribute(Text + "ref-name")?.Value ?? string.Empty));
                }
                else if (element.Name.Namespace == Text && UnmappedFieldElements.Contains(element.Name.LocalName))
                {
                    // An ODF field the model cannot represent: keep its displayed text.
                    Warn(context.Warnings, $"ODT: the field 'text:{element.Name.LocalName}' is not mapped when reading; its text is kept.");
                    AppendNodes(element.Nodes(), format, inheritedSize);
                }
                else if (element.Name == Text + "span")
                {
                    // spaces and tabs stand in ODT as their own elements (text:s,
                    // text:tab) next to the text nodes. Therefore the children are recursively
                    // processed with the span formatting instead of only element.Value to
                    // read (which would omit the text:s elements).
                    Flush(format);
                    var styleName = element.Attribute(Text + "style-name")?.Value;
                    var spanStyle = styleName is not null && context.TextStyles.TryGetValue(styleName, out var span)
                        ? span
                        : null;
                    var spanFormat = spanStyle is not null ? FormatOf(spanStyle, inheritedSize) : format;
                    // A nested span inherits the size the outer span states, or the paragraph size.
                    var spanFontSize = ParseLength(Attribute(spanStyle?.Properties, Fo + "font-size"))
                        ?? spanStyle?.NamedFontSize
                        ?? inheritedSize;
                    AppendNodes(element.Nodes(), spanFormat, spanFontSize);
                    Flush(spanFormat);
                }
                else if (element.Name == Text + "a")
                {
                    // AF-E: a hyperlink becomes a Hyperlink inline with its target.
                    Flush(format);
                    var inner = ReadInlineContent(element.Nodes(), format, context, inheritedSize);
                    var href = element.Attribute(XName.Get("href", OdtNamespaces.XLink))?.Value;
                    if (!string.IsNullOrEmpty(href) && !ReaderGuard.IsAllowedLinkTarget(href))
                    {
                        Warn(context.Warnings, "ODT: a hyperlink with a disallowed target is read as text.");
                        href = null;
                    }

                    if (!string.IsNullOrEmpty(href))
                    {
                        inlines.Add(new Hyperlink(href, inner));
                    }
                    else
                    {
                        inlines.AddRange(inner);
                    }
                }
                else if (element.Name == XName.Get("frame", OdtNamespaces.Draw))
                {
                    // read as a block-level image by ReadBlock.
                }
                else if (BasicShapes.Contains(element.Name) || element.Name == XName.Get("control", OdtNamespaces.Draw))
                {
                    // read as a block-level shape or form field by ReadBlock.
                }
                else if (element.Name.Namespace == OdtNamespaces.Draw)
                {
                    var drawingName = element.Attribute(XName.Get("name", OdtNamespaces.Draw))?.Value;
                    Warn(context.Warnings, string.IsNullOrWhiteSpace(drawingName)
                        ? $"ODT: the drawing object 'draw:{element.Name.LocalName}' is not mapped when reading."
                        : $"ODT: the drawing object 'draw:{element.Name.LocalName}' ('{drawingName}') is not mapped when reading.");
                }
                else if (element.Name == Text + "note")
                {
                    // AF-E: a footnote/endnote becomes a Footnote inline with its body.
                    Flush(format);
                    var endnote = element.Attribute(Text + "note-class")?.Value == "endnote";
                    var blocks = new List<Block>();
                    if (element.Element(Text + "note-body") is { } body)
                    {
                        foreach (var child in body.Elements())
                        {
                            if (child.Name == Text + "p" || child.Name == Text + "h")
                            {
                                // A note body is its own text flow: the style of the note's
                                // paragraph counts, not the style of the paragraph with the note.
                                var noteStyle = WithoutBreak(ResolveParagraphStyle(child, context));
                                blocks.Add(new Paragraph(ReadInlineContent(
                                    child.Nodes(),
                                    EmptyRunFormat,
                                    context,
                                    noteStyle.FontSize ?? context.DefaultFontSize))
                                {
                                    Style = noteStyle,
                                });
                            }
                        }
                    }

                    inlines.Add(new Footnote(blocks)
                    {
                        Kind = endnote ? NoteKind.Endnote : NoteKind.Footnote,
                    });
                }
                else if (element.Name == Office + "annotation")
                {
                    if (context.Comments.ByElement.TryGetValue(element, out var entry))
                    {
                        if (!entry.IsReply)
                        {
                            Flush(format);
                            inlines.Add(entry.Comment);
                        }
                    }
                    else
                    {
                        // An annotation outside content.xml (a master page): not mapped.
                        var author = element.Descendants(XName.Get("creator", OdtNamespaces.DublinCore))
                            .FirstOrDefault()?.Value.Trim();
                        Warn(context.Warnings, string.IsNullOrEmpty(author)
                            ? "ODT: a comment is not mapped when reading."
                            : $"ODT: the comment by '{author}' is not mapped when reading.");
                    }
                }
                else if (element.Name == Text + "change-start" || element.Name == Text + "change-end" || element.Name == Text + "change")
                {
                    ReadTrackedChange(element, format, inheritedSize);
                }
                else if (element.Name == Office + "annotation-end")
                {
                    // The end of a commented range; one that follows its start directly leaves a point comment.
                    if (element.Attribute(Office + "name")?.Value is { } endName
                        && context.Comments.ByName.TryGetValue(endName, out var ended)
                        && !ended.IsReply)
                    {
                        Flush(format);
                        if (inlines is not [.., Comment last] || last.Id != ended.Comment.Id)
                        {
                            inlines.Add(new CommentEnd(ended.Comment.Id));
                        }
                    }
                }
                else
                {
                    // other elements (e.g. text:bookmark-ref) with their content
                    // read; recursively so that text:s/text:tab within them are preserved.
                    AppendNodes(element.Nodes(), format, inheritedSize);
                }
            }
        }

        // ADR-0027: an insertion is a start and an end in the text; a deletion is a point, its text lives in the region.
        void ReadTrackedChange(XElement element, TextRun format, Length? size)
        {
            var changeId = element.Attribute(Text + "change-id")?.Value;
            if (changeId is null || !context.Revisions.Regions.TryGetValue(changeId, out var region))
            {
                Warn(context.Warnings, "ODT: a tracked change without a region is not mapped when reading.");
                return;
            }

            if (region.Kind is null)
            {
                if (element.Name != Text + "change-end")
                {
                    Warn(context.Warnings, "ODT: a tracked change of the formatting is not mapped when reading.");
                }

                return;
            }

            Flush(format);
            if (element.Name == Text + "change-start" && region.Kind == TrackedChangeKind.Insertion)
            {
                var id = context.Revisions.Next++;
                context.Revisions.Started[changeId] = id;
                inlines.Add(new TrackedChange(id, TrackedChangeKind.Insertion, region.Author) { Date = region.Date });
            }
            else if (element.Name == Text + "change-end" && context.Revisions.Started.Remove(changeId, out var started))
            {
                inlines.Add(new TrackedChangeEnd(started));
            }
            else if (element.Name == Text + "change" && region.Kind == TrackedChangeKind.Deletion)
            {
                var id = context.Revisions.Next++;
                inlines.Add(new TrackedChange(id, TrackedChangeKind.Deletion, region.Author) { Date = region.Date });
                var paragraphs = region.Element.Elements().Where(child => child.Name == Text + "p" || child.Name == Text + "h").ToList();
                for (var index = 0; index < paragraphs.Count; index++)
                {
                    if (index > 0)
                    {
                        Warn(context.Warnings, "ODT: a deleted text across paragraphs is read as one paragraph with line breaks.");
                        inlines.Add(new TextRun("\n"));
                    }

                    AppendNodes(paragraphs[index].Nodes(), format, size);
                    Flush(format);
                }

                inlines.Add(new TrackedChangeEnd(id));
            }
        }

        AppendNodes(nodes, paragraphFormat, inheritedFontSize);
        Flush(paragraphFormat);
        return inlines;
    }

    /// <summary>
    /// The formatting of a span: only what the span (or its automatic style) sets, ADR-0010. The
    /// paragraph style provides the base; a named character style stays a reference.
    /// </summary>
    private static TextRun FormatOf(TextStyle style, Length? inheritedFontSize)
    {
        // A numeric baseline shift is a percentage of the effective font size: the span's own
        // style may inherit it from a named character style or the paragraph.
        var fontSize = ParseLength(Attribute(style.Properties, Fo + "font-size"))
            ?? style.NamedFontSize
            ?? inheritedFontSize;
        return RunOf(ReadCharacterStyle("span", style.Properties, fontSize), style.NamedName);
    }


    private static TextRun Format(string text, TextRun format)
        => RunFormat.Of(format).ToRun(text, format.CharacterStyleName);

    private static double? ParsePt(string? value)
        => value is not null
            && value.EndsWith("pt", StringComparison.Ordinal)
            && double.TryParse(value.AsSpan(0, value.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var points)
            ? points
            : null;

    private static XElement? ReadXml(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part);
        if (entry is null)
        {
            return null;
        }

        using var stream = ReaderGuard.OpenBounded(entry);
        return ReaderGuard.LoadXml(stream).Root;
    }

    private sealed record SectionProps(
        PageSize PageSize,
        PageMargins Margins,
        PageOrientation Orientation,
        PageBorders? PageBorders = null);
}
