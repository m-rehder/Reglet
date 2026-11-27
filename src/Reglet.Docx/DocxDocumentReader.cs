using System.Globalization;
using System.Text;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// reads a DOCX package (subset) into the content model: paragraphs, headings, text with
/// formatting, (simple) lists, tables, page breaks, page number fields, sections
/// (page size/margins/orientation) and metadata from core/settings. Text in hyperlinks,
/// content controls and tracked insertions are read along, tabs and
/// line breaks as <c>\t</c>/<c>\n</c>. Layout is not preserved; unmappable
/// content (e.g. images, footnotes/endnotes, header/footer content, nested lists) is
/// reported instead of dropped silently.
/// </summary>
public sealed class DocxDocumentReader : IDocumentReader
{
    private static readonly XNamespace W = DocxConstants.WordprocessingML;
    private static readonly XNamespace Dc = DocxConstants.DublinCoreNamespace;
    private static readonly XNamespace Cp = DocxConstants.CorePropertiesNamespace;
    private static readonly XNamespace Dcterms = DocxConstants.DublinCoreTermsNamespace;
    private static readonly XNamespace R = DocxConstants.Relationships;
    private static readonly XNamespace A = DocxConstants.DrawingMainNamespace;
    private static readonly XNamespace WP = DocxConstants.DrawingWordprocessingNamespace;

    /// <summary>Elements whose children are read like runs of the surrounding paragraph.</summary>
    private static readonly HashSet<XName> RunContainers =
    [
        W + "hyperlink", W + "smartTag", W + "customXml", W + "dir", W + "bdo",
    ];

    /// <summary>Reads a DOCX from the stream into the content model.</summary>
    /// <param name="input">Source stream; it remains open after reading.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public ReadResult Read(Stream input, CancellationToken cancellationToken = default)
        => Read(input, DocxReadOptions.Default, cancellationToken);

    /// <summary>
    /// Reads a DOCX from the stream; an encrypted document is opened with the password of
    /// <paramref name="options"/> (OF-E01).
    /// </summary>
    /// <param name="input">Source stream; it remains open after reading.</param>
    /// <param name="options">Reader options (password).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="DocumentValidationException">The password is wrong or the package is damaged.</exception>
    public ReadResult Read(Stream input, DocxReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        return ReaderGuard.Run("DOCX", () =>
        {
            // The bytes are kept as the preserved source so that unmodelled package parts
            // survive a rewrite (phase AF, AF05). Own buffer, so the foreign stream stays open.
            var sourcePackage = ReaderGuard.ReadAllBounded(input, "DOCX");

            // OF-E01: an encrypted DOCX is a compound file; the package inside it is the source.
            if (DocxAgileEncryption.IsEncrypted(sourcePackage))
            {
                if (string.IsNullOrEmpty(options.Password))
                {
                    throw new DocumentValidationException("The DOCX is encrypted; a password is required.");
                }

                sourcePackage = DocxAgileEncryption.Decrypt(sourcePackage, options.Password);
            }

            using var zip = new ZipArchive(new MemoryStream(sourcePackage, writable: false), ZipArchiveMode.Read);
            var warnings = new List<ReadWarning>();
            var document = Parse(zip, warnings, sourcePackage, options.KeepMacros, cancellationToken);
            SourcePreservation.MarkPristine(document);
            return new ReadResult(document, warnings);
        });
    }

    private static PortableDocument Parse(
        ZipArchive zip,
        List<ReadWarning> warnings,
        ReadOnlyMemory<byte> sourcePackage,
        bool keepMacros,
        CancellationToken cancellationToken)
    {
        var metadata = ReadMetadata(zip) with { MacroProject = ReadMacroProject(zip, keepMacros, warnings) };
        var documentXml = ReadXml(zip, DocxConstants.DocumentPart)
            ?? throw new DocumentValidationException("The DOCX package contains no word/document.xml.");
        cancellationToken.ThrowIfCancellationRequested();

        var numbering = ReadNumbering(zip);
        var stylesPart = ReadXml(zip, DocxConstants.StylesPart);
        var paragraphLinks = ReadStyleLinks(stylesPart, "paragraph");
        var characterLinks = ReadStyleLinks(stylesPart, "character");
        var paragraphStyles = ResolveStyleFormats(paragraphLinks, link => link.Paragraph);
        var paragraphRunFormats = ResolveStyleFormats(paragraphLinks, link => link.Run);
        var characterStyles = ResolveStyleFormats(characterLinks, link => link.Run);
        var paragraphDefaults = ReadParagraphDefaults(stylesPart);
        var namedStyles = ReadNamedStyles(stylesPart, paragraphStyles, paragraphRunFormats);
        var resources = new DocxResources(zip, ReadRelationships(zip), ExternalRelationships: ReadExternalRelationships(zip))
        {
            HeadingStyles = ReadHeadingStyles(stylesPart),
            TableStyles = ReadTableStyles(stylesPart),
            ParagraphDefaults = paragraphDefaults,
            ParagraphStyleFormats = paragraphStyles,
            ParagraphStyleFormatsWithDefaults = WithParagraphDefaults(paragraphDefaults, paragraphStyles),
            ParagraphRunFormats = paragraphRunFormats,
            CharacterRunFormats = characterStyles,
            NamedStyleIds = namedStyles
                .Select(style => style.StyleName)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.Ordinal),
        };
        resources = resources with
        {
            Notes = ReadNotes(zip, numbering, resources, warnings),
            Comments = DocxComments.Read(
                ReadXml(zip, DocxConstants.CommentsPart),
                ReadXml(zip, DocxConstants.CommentsExtendedPart),
                message => Warn(warnings, message)),
        };
        var sections = ReadSections(documentXml, numbering, resources, warnings, cancellationToken);
        var namedCharacterStyles = ReadCharacterStyles(stylesPart, characterStyles);
        // A link is only kept when its target is in the model or a style the writer generates,
        // so a rewritten package never references a style it does not contain.
        var characterNames = namedCharacterStyles.Select(style => style.Name).ToHashSet(StringComparer.Ordinal);
        namedStyles = namedStyles
            .Select(style => style.LinkedStyleName is { } link
                && !characterNames.Contains(link)
                && !BuiltInStyleIds.Contains(link)
                    ? style with { LinkedStyleName = null }
                    : style)
            .ToList();
        return new PortableDocument(metadata, sections)
        {
            Defaults = ReadDocumentDefaults(stylesPart),
            Numbering = numbering.Values.ToList(),
            Styles = namedStyles,
            SourceStyles = namedStyles,
            CharacterStyles = namedCharacterStyles,
            SourceCharacterStyles = namedCharacterStyles,
            SourcePackage = sourcePackage,
            SourceHasUnmodelledContent = warnings.Count > 0,
            SourceUnmodelledWarnings = warnings.Select(warning => warning.Message).ToArray(),
        };
    }

    private sealed record DocxResources(
        ZipArchive Zip,
        IReadOnlyDictionary<string, string> Relationships,
        IReadOnlyDictionary<(bool Endnote, string Id), Footnote>? Notes = null,
        IReadOnlyDictionary<string, string>? ExternalRelationships = null)
    {
        private IReadOnlyDictionary<string, string>? _themeColors;

        /// <summary>The theme colors (<c>dk1</c>, <c>accent1</c> …) as RGB hex, from <c>word/theme/theme1.xml</c> over the Office defaults.</summary>
        public IReadOnlyDictionary<string, string> ThemeColors => _themeColors ??= ReadThemeColors(Zip);

        /// <summary>Paragraph style id → heading level 1–6 (from styles.xml).</summary>
        public IReadOnlyDictionary<string, int> HeadingStyles { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>The media budget of this read (shared by copies made with <c>with</c>).</summary>
        public ReaderGuard.MediaBudget Media { get; init; } = new();

        /// <summary>The chart point budget of this read (shared by copies made with <c>with</c>).</summary>
        public ReaderGuard.ChartBudget Charts { get; init; } = new();

        /// <summary>The table styles of the document (style id → style), from styles.xml.</summary>
        public IReadOnlyDictionary<string, TableStyle> TableStyles { get; init; } = new Dictionary<string, TableStyle>(StringComparer.Ordinal);

        /// <summary>The paragraph defaults (<c>w:docDefaults/w:pPrDefault</c>), the root of the inheritance.</summary>
        public XElement? ParagraphDefaults { get; init; }

        /// <summary>The effective paragraph formatting per style id (own properties over the inherited ones).</summary>
        public IReadOnlyDictionary<string, XElement?> ParagraphStyleFormats { get; init; } =
            new Dictionary<string, XElement?>(StringComparer.Ordinal);

        /// <summary>The style formatting with the paragraph defaults merged under it.</summary>
        public IReadOnlyDictionary<string, XElement?> ParagraphStyleFormatsWithDefaults { get; init; } =
            new Dictionary<string, XElement?>(StringComparer.Ordinal);

        /// <summary>The effective run formatting per paragraph style id (<c>w:style/w:rPr</c>).</summary>
        public IReadOnlyDictionary<string, XElement?> ParagraphRunFormats { get; init; } =
            new Dictionary<string, XElement?>(StringComparer.Ordinal);

        /// <summary>The effective run formatting per character style id (<c>w:style/w:rPr</c>).</summary>
        public IReadOnlyDictionary<string, XElement?> CharacterRunFormats { get; init; } =
            new Dictionary<string, XElement?>(StringComparer.Ordinal);

        /// <summary>
        /// the paragraph style ids that exist in the model (<see cref="PortableDocument.Styles"/>);
        /// a body paragraph keeps a reference only to one of them (ADR-0011).
        /// </summary>
        public IReadOnlySet<string> NamedStyleIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// the names of the bookmark starts by <c>w:bookmarkStart/w:id</c>, so a
        /// <c>w:bookmarkEnd</c> finds the name of its range (OF-C03). Filled while reading the body.
        /// </summary>
        public Dictionary<string, string> BookmarkNames { get; init; } = new(StringComparer.Ordinal);

        /// <summary>The comments of <c>word/comments.xml</c> by their <c>w:id</c> (ADR-0026).</summary>
        public IReadOnlyDictionary<string, DocxComments.Entry> Comments { get; init; } = new Dictionary<string, DocxComments.Entry>(StringComparer.Ordinal);

        /// <summary>The ids of the comments already placed in the text; a reference without a range start places a point comment.</summary>
        public HashSet<string> CommentsPlaced { get; init; } = new(StringComparer.Ordinal);

        /// <summary>The next model id for a tracked change (ADR-0027); every <c>w:ins</c>/<c>w:del</c> element is a change of its own.</summary>
        public int NextRevisionId { get; set; }
    }

    /// <summary>
    /// The document-wide run defaults (<c>w:docDefaults/w:rPrDefault</c>): the root of the run
    /// formatting that styles and runs inherit.
    /// </summary>
    private static DocumentDefaults ReadDocumentDefaults(XElement? styles)
    {
        var defaults = DocumentDefaults.Default;
        var rPr = styles?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
        if (rPr is not null)
        {
            var run = ReadTextRun(string.Empty, rPr);
            defaults = defaults with
            {
                FontFamily = run.FontFamily ?? defaults.FontFamily,
                FontSize = run.FontSize ?? defaults.FontSize,
                TextColor = run.Color ?? defaults.TextColor,
            };
        }

        var pPr = ReadParagraphDefaults(styles);
        if (pPr is not null)
        {
            defaults = defaults with { ParagraphStyle = ReadParagraphStyle(pPr) };
        }

        return defaults;
    }

    /// <summary>
    /// The paragraph defaults (<c>w:docDefaults/w:pPrDefault</c>) as the root of the paragraph
    /// inheritance. Values that already match the built-in typography are dropped, so they are
    /// not written onto every paragraph.
    /// </summary>
    private static XElement? ReadParagraphDefaults(XElement? styles)
    {
        if (styles?.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr") is not { } source)
        {
            return null;
        }

        var pPr = new XElement(source);
        if (pPr.Element(W + "spacing") is { } spacing)
        {
            if (spacing.Attribute(W + "after") is { } after
                && TryPoints(after.Value, out var afterPoints)
                && Math.Abs(afterPoints - Typography.ParagraphSpaceAfter.Points) < 1e-9)
            {
                after.Remove();
            }

            var single = spacing.Attribute(W + "lineRule")?.Value is null or "auto"
                && double.TryParse(
                    spacing.Attribute(W + "line")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips)
                && Math.Abs(twips - 240) < 1e-9;
            if (single)
            {
                spacing.Attribute(W + "line")?.Remove();
                spacing.Attribute(W + "lineRule")?.Remove();
            }

            if (!spacing.HasAttributes)
            {
                spacing.Remove();
            }
        }

        return pPr.HasElements ? pPr : null;
    }

    /// <summary>The style ids that the writer generates itself and that are therefore not read.</summary>
    private static readonly HashSet<string> BuiltInStyleIds = new(StringComparer.Ordinal)
    {
        "Normal",
        "Heading1", "Heading2", "Heading3", "Heading4", "Heading5", "Heading6",
        "FootnoteText", "EndnoteText",
        "FootnoteReference", "EndnoteReference",
    };

    /// <summary>
    /// The named paragraph styles of the document (phase AC, AC01). They are part of the model
    /// as <see cref="PortableDocument.Styles"/> and are written again by the writers. A style
    /// inherits the formatting of its parent style (<c>w:basedOn</c>).
    /// </summary>
    private static IReadOnlyList<ParagraphStyle> ReadNamedStyles(
        XElement? styles,
        IReadOnlyDictionary<string, XElement?> formats,
        IReadOnlyDictionary<string, XElement?> runFormats)
    {
        if (styles is null)
        {
            return [];
        }

        var result = new List<ParagraphStyle>();
        foreach (var style in styles.Descendants(W + "style"))
        {
            if ((string?)style.Attribute(W + "type") != "paragraph")
            {
                continue;
            }

            var id = (string?)style.Attribute(W + "styleId");
            if (id is null || BuiltInStyleIds.Contains(id))
            {
                continue;
            }

            var paragraph = ReadParagraphStyle(formats.GetValueOrDefault(id));
            if (runFormats.GetValueOrDefault(id) is { } rPr)
            {
                paragraph = RunFormat.Of(ReadTextRun(string.Empty, rPr)).ApplyTo(paragraph);
            }

            var link = (string?)style.Element(W + "link")?.Attribute(W + "val");
            result.Add(paragraph with
            {
                StyleName = id,
                LinkedStyleName = string.IsNullOrEmpty(link) ? null : link,
            });
        }

        return result;
    }

    /// <summary>
    /// The named character styles of the document. They are part of the model as
    /// <see cref="PortableDocument.CharacterStyles"/> and are written again by the writers.
    /// A style inherits the formatting of its parent style (<c>w:basedOn</c>).
    /// </summary>
    private static IReadOnlyList<CharacterStyle> ReadCharacterStyles(
        XElement? styles, IReadOnlyDictionary<string, XElement?> formats)
    {
        if (styles is null)
        {
            return [];
        }

        var result = new List<CharacterStyle>();
        foreach (var style in styles.Descendants(W + "style"))
        {
            if ((string?)style.Attribute(W + "type") != "character")
            {
                continue;
            }

            var id = (string?)style.Attribute(W + "styleId");
            if (id is null || BuiltInStyleIds.Contains(id))
            {
                continue;
            }

            var rPr = formats.GetValueOrDefault(id);
            if (rPr is null)
            {
                continue;
            }

            var run = ReadTextRun(string.Empty, rPr);
            var link = (string?)style.Element(W + "link")?.Attribute(W + "val");
            result.Add(new CharacterStyle(id)
            {
                LinkedStyleName = string.IsNullOrEmpty(link) ? null : link,
                Bold = run.Bold,
                Italic = run.Italic,
                AllCaps = run.AllCaps,
                SmallCaps = run.SmallCaps,
                Effects = run.Effects,
                Hidden = run.Hidden,
                Underline = run.Underline,
                UnderlineStyle = run.UnderlineStyle,
                UnderlineColor = run.UnderlineColor,
                Strikethrough = run.Strikethrough,
                Baseline = run.Baseline,
                Color = run.Color,
                Background = run.Background,
                Highlight = run.Highlight,
                FontSize = run.FontSize,
                FontFamily = run.FontFamily,
                Kerning = run.Kerning,
                CharacterSpacing = run.CharacterSpacing,
                HorizontalScaling = run.HorizontalScaling,
                Position = run.Position,
                Border = run.Border,
            });
        }

        return result;
    }

    /// <summary>The effective formatting of every style, resolved once per read.</summary>
    private static IReadOnlyDictionary<string, XElement?> ResolveStyleFormats(
        IReadOnlyDictionary<string, StyleLink> links, Func<StyleLink, XElement?> format)
    {
        // The effective formatting of a style: its own elements win over the inherited ones. DOCX
        // inheritance works per element, so the parent's elements are replaced by name. Each style
        // is resolved once over its w:basedOn chain (StyleChain), so a crafted long chain costs
        // linear time.
        return StyleChain.ResolveAll<XElement?>(
            links.Keys,
            id => links.TryGetValue(id, out var link) ? link.BasedOn : null,
            links.ContainsKey,
            null,
            (parent, id) =>
            {
                if (format(links[id]) is not { } own)
                {
                    return parent;
                }

                // A copy, because LINQ to XML moves elements and a style is the base of several others.
                if (parent is null)
                {
                    return new XElement(own);
                }

                var merged = new XElement(parent);
                Override(merged, own);
                return merged;
            });
    }

    /// <summary>Linkage of a style: the parent style and its direct formatting.</summary>
    private sealed record StyleLink(string? BasedOn, XElement? Paragraph, XElement? Run);

    /// <summary>The styles of one kind (<paramref name="type"/>), built-in ones included.</summary>
    private static IReadOnlyDictionary<string, StyleLink> ReadStyleLinks(XElement? styles, string type)
    {
        var result = new Dictionary<string, StyleLink>(StringComparer.Ordinal);
        if (styles is null)
        {
            return result;
        }

        foreach (var style in styles.Descendants(W + "style"))
        {
            if ((string?)style.Attribute(W + "type") != type
                || (string?)style.Attribute(W + "styleId") is not { Length: > 0 } id)
            {
                continue;
            }

            result[id] = new StyleLink(
                (string?)style.Element(W + "basedOn")?.Attribute(W + "val"),
                style.Element(W + "pPr"),
                style.Element(W + "rPr"));
        }

        return result;
    }

    /// <summary>
    /// Applies the formatting of a style over the inherited one: attributes win per attribute,
    /// container elements (borders with several sides) merge their children. A container with
    /// repeated child names is a list (tab stops) and replaces the inherited one.
    /// shortcut: tab stops inherited from a parent style are not merged.
    /// </summary>
    private static void Override(XElement target, XElement own)
    {
        foreach (var attribute in own.Attributes())
        {
            target.SetAttributeValue(attribute.Name, attribute.Value);
            DropOppositeIndent(target, attribute.Name);
        }

        foreach (var element in own.Elements())
        {
            // Tab stops are a set with clear entries, not a property tree; merge them so an
            // empty w:tabs can remove the inherited set (ADR-0018).
            if (element.Name == W + "tabs")
            {
                MergeTabStops(target, element);
                continue;
            }

            var existing = target.Element(element.Name);
            if (existing is not null && !HasRepeatedNames(existing) && !HasRepeatedNames(element))
            {
                Override(existing, element);
            }
            else
            {
                existing?.Remove();
                target.Add(new XElement(element));
            }
        }
    }

    /// <summary>
    /// Merges direct tab stops over the inherited ones: clear entries remove a position, other
    /// entries replace or add it, and an empty element removes the whole inherited set.
    /// </summary>
    private static void MergeTabStops(XElement target, XElement own)
    {
        var current = target.Element(W + "tabs");
        if (!own.HasElements)
        {
            current?.Remove();
            target.Add(new XElement(own));
            return;
        }

        if (current is null)
        {
            target.Add(new XElement(own));
            return;
        }

        foreach (var tab in own.Elements(W + "tab"))
        {
            var position = tab.Attribute(W + "pos")?.Value;
            foreach (var match in current.Elements(W + "tab")
                .Where(item => item.Attribute(W + "pos")?.Value == position)
                .ToList())
            {
                match.Remove();
            }

            if (tab.Attribute(W + "val")?.Value is not "clear")
            {
                current.Add(new XElement(tab));
            }
        }
    }

    /// <summary>
    /// Removes the twin of an indent attribute: <c>w:firstLine</c> and <c>w:hanging</c> describe
    /// the same indent, as do <c>w:left</c>/<c>w:start</c> and <c>w:right</c>/<c>w:end</c>.
    /// </summary>
    private static void DropOppositeIndent(XElement target, XName attribute)
    {
        if (attribute == W + "firstLine")
        {
            target.SetAttributeValue(W + "hanging", null);
        }
        else if (attribute == W + "hanging")
        {
            target.SetAttributeValue(W + "firstLine", null);
        }
        else if (attribute == W + "left")
        {
            target.SetAttributeValue(W + "start", null);
        }
        else if (attribute == W + "start")
        {
            target.SetAttributeValue(W + "left", null);
        }
        else if (attribute == W + "right")
        {
            target.SetAttributeValue(W + "end", null);
        }
        else if (attribute == W + "end")
        {
            target.SetAttributeValue(W + "right", null);
        }
    }

    /// <summary>Whether an element holds several children of the same name (a list).</summary>
    private static bool HasRepeatedNames(XElement element)
        => element.Elements().Select(child => child.Name).Distinct().Count() != element.Elements().Count();

    /// <summary>
    /// Heading levels of the paragraph styles. Localized Word documents use style ids like
    /// "berschrift1" or "Titre1"; the level therefore comes from the style's
    /// <c>w:outlineLvl</c> or its built-in name ("heading 1"), following <c>w:basedOn</c>.
    /// </summary>
    private static IReadOnlyDictionary<string, int> ReadHeadingStyles(XElement? styles)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (styles is null)
        {
            return result;
        }

        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var style in styles.Elements(W + "style"))
        {
            if (style.Attribute(W + "type")?.Value == "paragraph" && style.Attribute(W + "styleId")?.Value is { } id)
            {
                byId.TryAdd(id, style);
            }
        }

        // The level comes from the first style along the w:basedOn chain that has one; each style is
        // resolved once (StyleChain), so a crafted long chain costs linear time.
        var levels = StyleChain.ResolveAll<int?>(
            byId.Keys,
            id => byId.TryGetValue(id, out var style) ? style.Element(W + "basedOn")?.Attribute(W + "val")?.Value : null,
            byId.ContainsKey,
            null,
            (inherited, id) => HeadingLevelOf(byId[id]) ?? inherited);
        foreach (var (id, level) in levels)
        {
            if (level is { } value)
            {
                result[id] = value;
            }
        }

        return result;

        static int? HeadingLevelOf(XElement style)
        {
            var name = style.Element(W + "name")?.Attribute(W + "val")?.Value;
            if (name is not null
                && name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var named))
            {
                return named is >= 1 and <= 6 ? named : null;
            }

            return int.TryParse(
                    style.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var outline) && outline is >= 0 and <= 5
                ? outline + 1
                : null;
        }
    }

    /// <summary>Reads the internal relationships of a document part.</summary>
    private static IReadOnlyDictionary<string, string> ReadRelationships(ZipArchive zip, string partName = DocxConstants.DocumentPart)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadXml(zip, OpcPackageWriter.RelsPartName(partName)) is not { } relationships)
        {
            return result;
        }

        var baseUri = new Uri("http://package.local/" + partName);
        foreach (var relationship in relationships.Elements())
        {
            var id = relationship.Attribute("Id")?.Value;
            var target = relationship.Attribute("Target")?.Value;
            if (id is null || target is null || relationship.Attribute("TargetMode")?.Value == "External")
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, target, out var resolved) && resolved.Host == "package.local"
                && resolved.Query.Length == 0 && resolved.Fragment.Length == 0)
                result[id] = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
        }

        return result;
    }

    private static DocumentMetadata ReadMetadata(ZipArchive zip)
    {
        var core = ReadXml(zip, DocxConstants.CorePropertiesPart);
        string? language = null;

        if (core is not null)
        {
            // Our writer stores the language as dc:language in core.xml; that is the
            // preferred source (a readable property).
            language = core.Descendants(Dc + "language").FirstOrDefault()?.Value;
        }

        // Word itself usually stores the language in settings.xml as w:lang; as a fallback.
        var settings = ReadXml(zip, DocxConstants.SettingsPart);
        if (string.IsNullOrEmpty(language) && settings is not null)
        {
            language = settings.Descendants(W + "lang").FirstOrDefault()?.Attribute(W + "val")?.Value;
        }

        // Document variables (w:docVars) are the values of DOCVARIABLE fields.
        Dictionary<string, string>? variables = null;
        foreach (var docVar in settings?.Elements(W + "docVars").Elements(W + "docVar") ?? [])
        {
            if (docVar.Attribute(W + "name")?.Value is { Length: > 0 } name)
            {
                variables ??= new Dictionary<string, string>(StringComparer.Ordinal);
                variables.TryAdd(name, docVar.Attribute(W + "val")?.Value ?? string.Empty);
            }
        }

        var customXmlParts = ReadCustomXmlParts(zip);
        if (core is null)
        {
            return new DocumentMetadata { Language = language, Variables = variables, CustomXmlParts = customXmlParts };
        }

        string? ReadFirst(XName name) => core.Descendants(name).FirstOrDefault()?.Value;

        DateTimeOffset? created = null;
        if (DateTimeOffset.TryParse(
            ReadFirst(Dcterms + "created"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var createdValue))
        {
            created = createdValue;
        }

        string[]? keywords = null;
        var keywordText = ReadFirst(Cp + "keywords");
        if (!string.IsNullOrWhiteSpace(keywordText))
        {
            keywords = keywordText.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return new DocumentMetadata
        {
            Title = ReadFirst(Dc + "title"),
            Author = ReadFirst(Dc + "creator"),
            Subject = ReadFirst(Dc + "subject"),
            Keywords = keywords,
            Language = language,
            Created = created,
            Variables = variables,
            CustomXmlParts = customXmlParts,
        };
    }

    /// <summary>
    /// The VBA project the document part points to (ADR-0028): kept only on request, otherwise reported and dropped.
    /// </summary>
    private static MacroProject? ReadMacroProject(ZipArchive zip, bool keep, List<ReadWarning> warnings)
    {
        if (ReadXml(zip, OpcPackageWriter.RelsPartName(DocxConstants.DocumentPart)) is not { } relationships
            || relationships.Elements().FirstOrDefault(r => r.Attribute("Type")?.Value == DocxConstants.VbaProjectRelationship
                && r.Attribute("TargetMode")?.Value != "External") is not { } relationship
            || relationship.Attribute("Target")?.Value is not { } target
            || !Uri.TryCreate(new Uri("http://package.local/" + DocxConstants.DocumentPart), target, out var resolved)
            || resolved.Host != "package.local"
            || zip.GetEntry(Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'))) is not { Length: > 0 } entry)
        {
            return null;
        }

        if (!keep)
        {
            Warn(warnings, "DOCX: the macro project is not read; set KeepMacros to keep it.");
            return null;
        }

        using var content = ReaderGuard.OpenBounded(entry);
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return new MacroProject { Data = buffer.ToArray() };
    }

    /// <summary>The custom XML parts the document part points to (<c>customXml</c> relationships).</summary>
    private static List<CustomXmlPart>? ReadCustomXmlParts(ZipArchive zip)
    {
        if (ReadXml(zip, OpcPackageWriter.RelsPartName(DocxConstants.DocumentPart)) is not { } relationships)
        {
            return null;
        }

        var baseUri = new Uri("http://package.local/" + DocxConstants.DocumentPart);
        List<CustomXmlPart>? result = null;
        foreach (var relationship in relationships.Elements()
            .Where(r => r.Attribute("Type")?.Value == DocxConstants.CustomXmlRelationship
                && r.Attribute("TargetMode")?.Value != "External"))
        {
            if (relationship.Attribute("Target")?.Value is not { } target
                || !Uri.TryCreate(baseUri, target, out var resolved) || resolved.Host != "package.local")
            {
                continue;
            }

            var part = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
            var entry = zip.GetEntry(part);
            if (entry is null || entry.Length > 16 * 1024 * 1024)
            {
                continue;
            }

            string xml;
            using (var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8))
            {
                xml = reader.ReadToEnd();
            }

            string? itemId = null;
            var propsRelationship = ReadXml(zip, OpcPackageWriter.RelsPartName(part))?.Elements()
                .FirstOrDefault(r => r.Attribute("Type")?.Value == DocxConstants.CustomXmlPropertiesRelationship);
            if (propsRelationship?.Attribute("Target")?.Value is { } propsTarget
                && Uri.TryCreate(new Uri("http://package.local/" + part), propsTarget, out var propsUri)
                && ReadXml(zip, Uri.UnescapeDataString(propsUri.AbsolutePath.TrimStart('/'))) is { } props)
            {
                var id = props.Attributes().FirstOrDefault(a => a.Name.LocalName == "itemID")?.Value;
                itemId = Guid.TryParse(id, out var guid) ? guid.ToString("B").ToUpperInvariant() : null;
            }

            try
            {
                (result ??= []).Add(new CustomXmlPart { Xml = xml, ItemId = itemId });
            }
            catch (ArgumentException)
            {
                // Not well-formed or an empty part: skipped, the rest of the document stays readable.
            }
        }

        return result;
    }

    /// <summary>Reads a part's external relationships (hyperlink targets).</summary>
    private static IReadOnlyDictionary<string, string> ReadExternalRelationships(ZipArchive zip, string partName = DocxConstants.DocumentPart)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadXml(zip, OpcPackageWriter.RelsPartName(partName)) is not { } relationships)
        {
            return result;
        }

        foreach (var relationship in relationships.Elements())
        {
            if (relationship.Attribute("TargetMode")?.Value != "External")
            {
                continue;
            }

            var id = relationship.Attribute("Id")?.Value;
            var target = relationship.Attribute("Target")?.Value;
            if (id is not null && target is not null)
            {
                result[id] = target;
            }
        }

        return result;
    }

    /// <summary>Reads footnotes and endnotes into the model (AF18).</summary>
    private static Dictionary<(bool Endnote, string Id), Footnote> ReadNotes(
        ZipArchive zip,
        IReadOnlyDictionary<string, NumberingDefinition> numbering,
        DocxResources resources,
        List<ReadWarning> warnings)
    {
        var notes = new Dictionary<(bool Endnote, string Id), Footnote>();
        ReadNoteFile(zip, "word/footnotes.xml", W + "footnote", endnote: false, numbering, resources, warnings, notes);
        ReadNoteFile(zip, "word/endnotes.xml", W + "endnote", endnote: true, numbering, resources, warnings, notes);
        return notes;
    }

    private static void ReadNoteFile(
        ZipArchive zip,
        string path,
        XName elementName,
        bool endnote,
        IReadOnlyDictionary<string, NumberingDefinition> numbering,
        DocxResources resources,
        List<ReadWarning> warnings,
        Dictionary<(bool Endnote, string Id), Footnote> notes)
    {
        if (ReadXml(zip, path) is not { } root)
        {
            return;
        }

        foreach (var note in root.Elements(elementName))
        {
            var type = note.Attribute(W + "type")?.Value;
            if (type is "separator" or "continuationSeparator" or "continuationNotice")
            {
                continue;
            }

            if (note.Attribute(W + "id")?.Value is not { } id)
            {
                continue;
            }

            var blocks = new BlockCollector();
            foreach (var child in note.Elements())
            {
                ParseBodyElement(child, blocks, numbering, resources, warnings);
            }

            notes[(endnote, id)] = new Footnote(blocks.ToList())
            {
                Kind = endnote ? NoteKind.Endnote : NoteKind.Footnote,
                SourceId = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceId)
                    ? sourceId
                    : null,
            };
        }
    }

    private static IReadOnlyList<Section> ReadSections(
        XElement documentXml,
        IReadOnlyDictionary<string, NumberingDefinition> numbering,
        DocxResources resources,
        List<ReadWarning> warnings,
        CancellationToken cancellationToken)
    {
        var body = documentXml.Element(W + "body")
            ?? throw new DocumentValidationException("word/document.xml contains no w:body.");

        var sections = new List<Section>();
        var currentBlocks = new BlockCollector();
        SectionProps? pendingSectionProps = null;

        foreach (var child in body.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (child.Name == W + "sectPr")
            {
                pendingSectionProps = ParseSectionProperties(child);
                break; // The body-level sectPr comes last.
            }

            if (child.Name == W + "p" && child.Element(W + "pPr")?.Element(W + "sectPr") is { } boundarySectPr)
            {
                // The paragraph with the section end may itself carry text.
                ParseParagraph(child, currentBlocks, numbering, resources, warnings);
                sections.Add(BuildSection(currentBlocks.ToList(), ParseSectionProperties(boundarySectPr), numbering, resources, warnings));
                currentBlocks = new BlockCollector();
                continue;
            }

            if (!IsBodyClosingParagraph(child))
            {
                ParseBodyElement(child, currentBlocks, numbering, resources, warnings);
            }
        }

        sections.Add(BuildSection(currentBlocks.ToList(), pendingSectionProps, numbering, resources, warnings));
        return sections;
    }

    private static Section BuildSection(List<Block> blocks, SectionProps? props, IReadOnlyDictionary<string, NumberingDefinition> numbering, DocxResources resources, List<ReadWarning> warnings)
    {
        var section = new Section(blocks);
        if (props is { } p)
        {
            section = section with
            {
                PageSize = p.PageSize,
                Margins = p.Margins,
                Orientation = p.Orientation,
                Header = ReadBand(p.HeaderReference, numbering, resources, warnings),
                Footer = ReadBand(p.FooterReference, numbering, resources, warnings),
                FirstHeader = ReadBand(p.FirstHeaderReference, numbering, resources, warnings, keepEmpty: true),
                FirstFooter = ReadBand(p.FirstFooterReference, numbering, resources, warnings, keepEmpty: true),
                EvenHeader = ReadBand(p.EvenHeaderReference, numbering, resources, warnings),
                EvenFooter = ReadBand(p.EvenFooterReference, numbering, resources, warnings),
                TextColumns = p.TextColumns,
                StartType = p.StartType,
                PageBorders = p.PageBorders,
                HeaderDistance = p.HeaderDistance,
                FooterDistance = p.FooterDistance,
            };
        }

        return section;
    }

    /// <summary>Reads a header/footer part referenced by a sectPr relationship (phase AC).</summary>
    private static HeaderFooter? ReadBand(
        string? relationshipId,
        IReadOnlyDictionary<string, NumberingDefinition> numbering,
        DocxResources resources,
        List<ReadWarning> warnings,
        bool keepEmpty = false)
    {
        if (relationshipId is null || !resources.Relationships.TryGetValue(relationshipId, out var path))
        {
            return null;
        }

        if (ReadXml(resources.Zip, path) is not { } xml)
        {
            Warn(warnings, $"DOCX: the header/footer part '{path}' is missing.");
            return null;
        }

        var blocks = new BlockCollector();
        var bandResources = resources with
        {
            Relationships = ReadRelationships(resources.Zip, path),
            ExternalRelationships = ReadExternalRelationships(resources.Zip, path),
        };
        foreach (var child in xml.Elements())
        {
            if (!IsClosingParagraph(child))
            {
                ParseBodyElement(child, blocks, numbering, bandResources, warnings);
            }
        }

        var result = blocks.ToList();
        // An empty first-page band is kept: with w:titlePg it means "no band on page 1", while null would fall back to
        // the default band.
        return result.Count > 0 || keepEmpty ? new HeaderFooter(result) : null;
    }

    /// <summary>Whether a paragraph holds nothing but its properties, empty runs and marks without content.</summary>
    private static bool IsEmptyParagraph(XElement p)
        => p.Elements().All(child => child.Name.LocalName switch
        {
            "pPr" or "bookmarkStart" or "bookmarkEnd" or "proofErr" or "permStart" or "permEnd" => true,
            "r" => child.Elements().All(part => part.Name == W + "rPr" || part.Name == W + "lastRenderedPageBreak"
                || (part.Name == W + "t" && part.Value.Length == 0)),
            _ => false,
        });

    /// <summary>
    /// Whether a paragraph only closes a cell or a header/footer as the schema demands: the last element, empty, and
    /// either right after a table (also Reglet's paragraph without height) or the bare <c>w:p</c> of an empty cell or band.
    /// </summary>
    private static bool IsClosingParagraph(XElement element)
    {
        if (element.Name != W + "p" || element.ElementsAfterSelf().Any() || element.Elements().Any(child => child.Name != W + "pPr"))
        {
            return false;
        }

        var previous = element.ElementsBeforeSelf().LastOrDefault(sibling => sibling.Name != W + "tcPr");
        return previous?.Name == W + "tbl" || (previous is null && element.Element(W + "pPr") is null);
    }

    /// <summary>
    /// Whether a paragraph is the one Reglet writes where the body does not end with a paragraph (after a table or an
    /// index, <see cref="ClosingParagraph"/>): empty, not after a paragraph, only the body's sectPr after it, and nothing
    /// but that spacing. An empty paragraph Word wrote there in its own formatting stays a paragraph.
    /// </summary>
    private static bool IsBodyClosingParagraph(XElement element)
    {
        if (element.Name != W + "p" || element.ElementsBeforeSelf().LastOrDefault() is not { } previous || previous.Name == W + "p"
            || element.ElementsAfterSelf().Any(sibling => sibling.Name != W + "sectPr")
            || element.Elements().Any(child => child.Name != W + "pPr"))
        {
            return false;
        }

        var spacing = element.Element(W + "pPr")?.Elements().ToList() is [var only] ? only : null;
        return spacing?.Name == W + "spacing"
            && (string?)spacing.Attribute(W + "before") == "0" && (string?)spacing.Attribute(W + "after") == "0"
            && (string?)spacing.Attribute(W + "line") == ClosingParagraph.LineTwips.ToString(CultureInfo.InvariantCulture)
            && (string?)spacing.Attribute(W + "lineRule") == "exact";
    }

    /// <summary>Reads an element of the body (or a cell): paragraph, table or content control.</summary>
    private static void ParseBodyElement(
        XElement element,
        BlockCollector blocks,
        IReadOnlyDictionary<string, NumberingDefinition> numbering,
        DocxResources resources,
        List<ReadWarning> warnings)
    {
        ReaderGuard.EnsureStack();
        if (element.Name == W + "p")
        {
            ParseParagraph(element, blocks, numbering, resources, warnings);
        }
        else if (element.Name == W + "tbl")
        {
            blocks.Add(ReadTable(element, numbering, resources, warnings));
        }
        else if (element.Name == W + "sdt" && element.Element(W + "sdtContent") is { } content)
        {
            foreach (var child in content.Elements())
            {
                ParseBodyElement(child, blocks, numbering, resources, warnings);
            }
        }
        else if (element.Name == W + "customXml")
        {
            foreach (var child in element.Elements())
            {
                ParseBodyElement(child, blocks, numbering, resources, warnings);
            }
        }
    }

    /// <summary>
    /// parses a paragraph and appends it to <paramref name="blocks"/>: list paragraphs become
    /// to list items of the currently open list (or a new one), all others to
    /// paragraph/heading/page-break blocks. Hard page breaks split a
    /// normal paragraph into several paragraphs with a page break between them.
    /// </summary>
    private static void ParseParagraph(XElement p, BlockCollector blocks, IReadOnlyDictionary<string, NumberingDefinition> numbering, DocxResources resources, List<ReadWarning> warnings)
    {
        var pPr = p.Element(W + "pPr");
        var styleName = pPr?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        // The formatting of the referenced style counts for the paragraph; its own properties win.
        var effective = EffectiveParagraphProperties(pPr, styleName, resources);
        // ADR-0010: the run formatting of the paragraph style stays on the style, the runs carry
        // their own formatting only.
        var runFormat = RunFormatOf(styleName, resources);
        var reader = new InlineReader(resources, warnings, inlineImages: true);
        var segments = reader.Read(p);

        // A picture in the line of text stays there (InlineImage); a paragraph of pictures alone becomes image blocks,
        // unless the paragraph centres them or sets them right: an image block has no alignment of its own.
        var aligned = (string?)effective?.Element(W + "jc")?.Attribute(W + "val") is "center" or "right";
        var hasText = aligned || segments.SelectMany(segment => segment).Any(inline => inline is TextRun run
            ? !string.IsNullOrWhiteSpace(run.Text)
            : inline is not (InlineImage or Bookmark or BookmarkEnd or Comment or CommentEnd or TrackedChange or TrackedChangeEnd));
        if (!hasText && reader.InLine.Count > 0)
        {
            foreach (var segment in segments)
            {
                segment.RemoveAll(inline => inline is InlineImage);
            }
        }

        foreach (var image in reader.Images)
        {
            if (!hasText || image is not ImageBlock picture || !reader.InLine.Contains(picture))
            {
                blocks.Add(image);
            }
        }

        if (effective?.Element(W + "numPr") is { } numPr && ReadListKind(numPr, numbering, warnings) is { } list)
        {
            var inlines = Merge(segments, warnings);

            // A list item keeps the run formatting of its paragraph style; the list provides the
            // indent, so the paragraph formatting of the style is not applied (unchanged).
            var itemStyle = runFormat?.ApplyTo(ParagraphStyle.Default) ?? ParagraphStyle.Default;
            blocks.AddListItem(
                list.Definition,
                list.Level,
                inlines.Count > 0
                    ? new ListItem(new Paragraph(inlines) { Style = itemStyle })
                    : new ListItem(Array.Empty<Block>()));
            return;
        }

        if (HeadingLevel(styleName, pPr, resources) is { } level)
        {
            var style = RunFormatOf(styleName, resources)?.ApplyTo(ReadParagraphStyle(effective))
                ?? ReadParagraphStyle(effective);
            blocks.Add(new Heading((HeadingLevel)level, Merge(segments, warnings))
            {
                Style = style with { StyleName = styleName },
            });
            return;
        }

        var paragraphStyle = ReadParagraphStyle(effective);
        if (runFormat is not null)
        {
            paragraphStyle = runFormat.ApplyTo(paragraphStyle);
        }

        // ADR-0011: keep the formatting that came from the named style separately, so an edit of
        // the style definition after reading reaches the paragraph and the writer states only the
        // direct formatting.
        ParagraphStyle? sourceBase = null;
        if (styleName is not null)
        {
            var inherited = resources.ParagraphStyleFormatsWithDefaults.GetValueOrDefault(styleName)
                ?? resources.ParagraphDefaults;
            sourceBase = ReadParagraphStyle(inherited);
            if (runFormat is not null)
            {
                sourceBase = runFormat.ApplyTo(sourceBase);
            }

            if (resources.NamedStyleIds.Contains(styleName))
            {
                paragraphStyle = paragraphStyle with { StyleName = styleName };
            }
        }

        paragraphStyle = LiftMarkFont(paragraphStyle, segments, p);

        // An empty paragraph is a line of its own (its mark gives it height), unless it only carries a section boundary.
        // Only a paragraph empty in the file counts: one whose drawing, picture or object was not mapped stays away.
        var keepsEmpty = segments is [{ Count: 0 }] && pPr?.Element(W + "sectPr") is null && IsEmptyParagraph(p);
        for (var index = 0; index < segments.Count; index++)
        {
            // Empty pieces (e.g. a pure section boundary) are not adopted.
            if (segments[index].Count > 0 || keepsEmpty)
            {
                blocks.Add(new Paragraph(segments[index])
                {
                    Style = paragraphStyle,
                    SourceStyleBase = sourceBase,
                });
            }

            if (index < segments.Count - 1)
            {
                blocks.Add(new PageBreak());
            }
        }
    }

    /// <summary>
    /// A paragraph font Word stores on the mark and on every run (as Reglet writes <see cref="ParagraphStyle.FontFamily"/>
    /// and <see cref="ParagraphStyle.FontSize"/>, also on every field run since 1.7.2): when all text runs share the mark's
    /// font or size, or the paragraph has fields whose runs all carry it, it becomes the paragraph's again. Runs that carry
    /// exactly that value leave it to the paragraph, runs with their own keep theirs; a mark equal to the paragraph's font is
    /// dropped.
    /// </summary>
    private static ParagraphStyle LiftMarkFont(ParagraphStyle style, List<List<Inline>> segments, XElement paragraph)
    {
        // shortcut: runs inside hyperlinks keep the mark as it is; lift them too when such paragraphs need it.
        var inlines = segments.SelectMany(segment => segment).ToList();
        if (inlines.Any(inline => inline is Hyperlink) || (style.MarkFontFamily is null && style.MarkFontSize is null))
        {
            return style;
        }

        var runs = inlines.OfType<TextRun>().ToList();
        // The runs of the fields (fldChar/instrText and simple fields) show the formatting the fields have in the file.
        var fieldRuns = paragraph.Descendants(W + "r")
            .Where(run => run.Element(W + "fldChar") is not null || run.Element(W + "instrText") is not null
                || run.Ancestors(W + "fldSimple").Any())
            .Select(run => ReadTextRun(string.Empty, run.Element(W + "rPr")))
            .ToList();
        var family = style.MarkFontFamily;
        var size = style.MarkFontSize;
        var liftFamily = family is not null
            && (runs.All(run => run.FontFamily == family) || (fieldRuns.Count > 0 && fieldRuns.All(run => run.FontFamily == family)));
        var liftSize = size is not null
            && (runs.All(run => run.FontSize == size) || (fieldRuns.Count > 0 && fieldRuns.All(run => run.FontSize == size)));
        if (liftFamily || liftSize)
        {
            foreach (var segment in segments)
            {
                for (var index = 0; index < segment.Count; index++)
                {
                    if (segment[index] is TextRun run
                        && ((liftFamily && run.FontFamily == family) || (liftSize && run.FontSize == size)))
                    {
                        var format = RunFormat.Of(run);
                        segment[index] = (format with
                        {
                            FontFamily = liftFamily && run.FontFamily == family ? null : format.FontFamily,
                            FontSize = liftSize && run.FontSize == size ? null : format.FontSize,
                        }).ToRun(run.Text, run.CharacterStyleName);
                    }
                }
            }
        }

        return style with
        {
            FontFamily = liftFamily ? family : style.FontFamily,
            FontSize = liftSize ? size : style.FontSize,
            MarkFontFamily = liftFamily || family == style.FontFamily ? null : family,
            MarkFontSize = liftSize || size == style.FontSize ? null : size,
        };
    }

    /// <summary>
    /// The paragraph properties in effect: the resolved formatting of the referenced style over
    /// the document defaults, the paragraph's own properties over both.
    /// </summary>
    private static XElement? EffectiveParagraphProperties(XElement? pPr, string? styleName, DocxResources resources)
    {
        var inherited = styleName is null
            ? resources.ParagraphDefaults
            : resources.ParagraphStyleFormatsWithDefaults.GetValueOrDefault(styleName) ?? resources.ParagraphDefaults;
        if (inherited is null || pPr is null)
        {
            return inherited ?? pPr;
        }

        var merged = new XElement(inherited);
        Override(merged, pPr);
        return merged;
    }

    /// <summary>The style formatting with the paragraph defaults merged under it, resolved once per style.</summary>
    private static IReadOnlyDictionary<string, XElement?> WithParagraphDefaults(
        XElement? defaults,
        IReadOnlyDictionary<string, XElement?> formats)
    {
        if (defaults is null)
        {
            return formats;
        }

        var result = new Dictionary<string, XElement?>(StringComparer.Ordinal);
        foreach (var (id, format) in formats)
        {
            if (format is null)
            {
                result[id] = defaults;
                continue;
            }

            var merged = new XElement(defaults);
            Override(merged, format);
            result[id] = merged;
        }

        return result;
    }

    /// <summary>Heading level from the paragraph style (styles.xml, or the "HeadingN" id) or a direct outline level.</summary>
    private static int? HeadingLevel(string? styleName, XElement? pPr, DocxResources resources)
    {
        if (styleName is not null)
        {
            if (resources.HeadingStyles.TryGetValue(styleName, out var fromStyles))
            {
                return fromStyles;
            }

            if (styleName.StartsWith("Heading", StringComparison.Ordinal)
                && int.TryParse(styleName.AsSpan("Heading".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var level)
                && level is >= 1 and <= 6)
            {
                return level;
            }
        }

        return int.TryParse(
                pPr?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var outline) && outline is >= 0 and <= 5
            ? outline + 1
            : null;
    }

    /// <summary>Joins the pieces of a paragraph that cannot carry a page break.</summary>
    private static List<Inline> Merge(List<List<Inline>> segments, List<ReadWarning> warnings)
    {
        if (segments.Count > 1)
        {
            Warn(warnings, "DOCX: page breaks in headings and list items are not mapped when reading.");
        }

        return segments.SelectMany(segment => segment).ToList();
    }

    /// <summary>Adds a warning if the same message is not already present.</summary>
    private static void Warn(List<ReadWarning> warnings, string message)
    {
        if (!warnings.Any(warning => warning.Message == message))
        {
            warnings.Add(new ReadWarning(message));
        }
    }

    /// <summary>
    /// reads the inline content of a paragraph (text, formatting, page number field, tab,
    /// line break). Runs in hyperlinks, insertions, content controls and
    /// simple fields are read along; deleted text is not. The result is the
    /// pieces between hard page breaks.
    /// </summary>
    private sealed class InlineReader(DocxResources resources, List<ReadWarning> warnings, bool inlineImages = false)
    {
        private readonly List<List<Inline>> _segments = [[]];

        /// <summary>Images found while reading (block level; hoisted by the caller).</summary>
        public List<Block> Images { get; } = [];

        /// <summary>
        /// The <c>wp:inline</c> pictures, also kept at their place in the text as <see cref="InlineImage"/>; the caller keeps
        /// them there when the paragraph has text, otherwise it hoists them as blocks.
        /// </summary>
        public HashSet<ImageBlock> InLine { get; } = new(ReferenceEqualityComparer.Instance);

        // Open complex fields (fldChar begin … separate … end). Per field it is noted whether
        // it is a PAGE field and whether the cached result is currently being read.
        private readonly Stack<FieldState> _fields = new();

        public List<List<Inline>> Read(XElement paragraph)
        {
            ReadContainer(paragraph);
            return _segments;
        }

        private bool InMappedFieldResult => _fields.Any(open => open.Mapped && open.InResult);

        private void ReadHyperlink(XElement hyperlink)
        {
            var id = hyperlink.Attribute(R + "id")?.Value;
            string? target = null;
            if (id is not null && resources.ExternalRelationships is { } external && external.TryGetValue(id, out var externalTarget))
            {
                target = externalTarget;
            }
            else if (hyperlink.Attribute(W + "anchor")?.Value is { Length: > 0 } anchor)
            {
                // A jump to a bookmark in this document.
                target = "#" + anchor;
            }

            if (target is not null && !ReaderGuard.IsAllowedLinkTarget(target))
            {
                Warn(warnings, "DOCX: a hyperlink with a disallowed target is read as text.");
                target = null;
            }

            if (target is null)
            {
                // A missing relationship: read the text only.
                ReadContainer(hyperlink);
                return;
            }

            var inner = new InlineReader(resources, warnings);
            inner.ReadContainer(hyperlink);
            Images.AddRange(inner.Images);
            var inlines = inner._segments.SelectMany(segment => segment).ToList();
            if (inlines.Count > 0)
            {
                _segments[^1].Add(new Hyperlink(target, inlines));
            }
        }

        /// <summary>
        /// Reads the start or end of a commented range (ADR-0026). The comment is added at its start, a
        /// reply shares the anchor of its parent, and an end that follows its start directly leaves a point comment.
        /// </summary>
        private void ReadCommentAnchor(XElement anchor)
        {
            if (InMappedFieldResult)
            {
                return;
            }

            if (anchor.Attribute(W + "id")?.Value is not { } id || !resources.Comments.TryGetValue(id, out var entry))
            {
                Warn(warnings, "DOCX: a comment is not mapped when reading; its anchor is lost when the body changes.");
                return;
            }

            if (entry.IsReply)
            {
                return;
            }

            if (anchor.Name == W + "commentRangeStart")
            {
                resources.CommentsPlaced.Add(id);
                _segments[^1].Add(entry.Comment);
            }
            else if (_segments[^1] is not [.., Comment last] || last.Id != entry.Comment.Id)
            {
                _segments[^1].Add(new CommentEnd(entry.Comment.Id));
            }
        }

        /// <summary>
        /// Reads a tracked insertion or deletion (ADR-0027) as a marker pair around its content. A move is read as
        /// a deletion at the old place and an insertion at the new one, and reported.
        /// </summary>
        private void ReadTrackedChange(XElement change)
        {
            if (InMappedFieldResult)
            {
                ReadContainer(change);
                return;
            }

            var isMove = change.Name == W + "moveFrom" || change.Name == W + "moveTo";
            if (isMove)
            {
                Warn(warnings, "DOCX: a moved text is read as a deletion and an insertion.");
            }

            var kind = change.Name == W + "ins" || change.Name == W + "moveTo" ? TrackedChangeKind.Insertion : TrackedChangeKind.Deletion;
            var author = change.Attribute(W + "author")?.Value;
            var id = resources.NextRevisionId++;
            _segments[^1].Add(new TrackedChange(id, kind, string.IsNullOrWhiteSpace(author) ? "Unknown" : author)
            {
                Date = DateTimeOffset.TryParse(
                    change.Attribute(W + "date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date : null,
            });
            ReadContainer(change);
            _segments[^1].Add(new TrackedChangeEnd(id));
        }

        private void ReadContainer(XElement container)
        {
            ReaderGuard.EnsureStack();
            foreach (var child in container.Elements())
            {
                if (child.Name == W + "r")
                {
                    ReadRun(child);
                }
                else if (child.Name == W + "hyperlink")
                {
                    if (InMappedFieldResult)
                    {
                        ReadContainer(child);
                    }
                    else
                    {
                        ReadHyperlink(child);
                    }
                }
                else if (child.Name == W + "bookmarkStart"
                    && child.Attribute(W + "name")?.Value is { } bookmarkName
                    && !string.IsNullOrWhiteSpace(bookmarkName)
                    && !InMappedFieldResult)
                {
                    if (child.Attribute(W + "id")?.Value is { } bookmarkId)
                    {
                        resources.BookmarkNames[bookmarkId] = bookmarkName;
                    }

                    _segments[^1].Add(new Bookmark(bookmarkName));
                }
                else if (child.Name == W + "bookmarkEnd"
                    && !InMappedFieldResult
                    && child.Attribute(W + "id")?.Value is { } endId
                    && resources.BookmarkNames.TryGetValue(endId, out var endedName))
                {
                    // An end that follows its start directly is a zero-width target, not a range
                    // (OF-C03): Word writes every bookmark that way unless it spans text.
                    if (_segments[^1] is not [.., Bookmark last] || !string.Equals(last.Name, endedName, StringComparison.Ordinal))
                    {
                        _segments[^1].Add(new BookmarkEnd(endedName));
                    }
                }
                else if (child.Name == W + "commentRangeStart" || child.Name == W + "commentRangeEnd")
                {
                    ReadCommentAnchor(child);
                }
                else if (child.Name == W + "ins" || child.Name == W + "del" || child.Name == W + "moveFrom" || child.Name == W + "moveTo")
                {
                    ReadTrackedChange(child);
                }
                else if (child.Name == W + "moveFromRangeStart" || child.Name == W + "moveFromRangeEnd"
                    || child.Name == W + "moveToRangeStart" || child.Name == W + "moveToRangeEnd")
                {
                    // The range markers of a move are not needed: the text sits in moveFrom/moveTo.
                }
                else if (RunContainers.Contains(child.Name))
                {
                    ReadContainer(child);
                }
                else if (child.Name == W + "sdt" && child.Element(W + "sdtContent") is { } content)
                {
                    // ADR-0024: a text, drop-down or check box control becomes a form field (a block,
                    // hoisted before the paragraph like an image); another control is read as its text.
                    if (DocxFormControls.TryRead(child) is { } formField)
                    {
                        Images.Add(formField);
                    }
                    else
                    {
                        ReadContainer(content);
                    }
                }
                else if (child.Name == XName.Get("oMathPara", DocxMath.MathNamespace) || child.Name == XName.Get("oMath", DocxMath.MathNamespace))
                {
                    // ADR-0031: a display equation is a block, hoisted after the paragraph like an image. An equation
                    // inside a line of text is read as a display equation too (reported once).
                    if (DocxMath.Read(child, message => Warn(warnings, message)) is { } equation)
                    {
                        Images.Add(equation);
                        if (child.Name.LocalName == "oMath" && child.Parent?.Name == W + "p")
                        {
                            Warn(warnings, "DOCX: an equation inside a line of text is read as a display equation.");
                        }
                    }
                }
                else if (child.Name == W + "fldSimple")
                {
                    var instruction = child.Attribute(W + "instr")?.Value;
                    if (FieldFor(instruction) is { } inline)
                    {
                        if (!InMappedFieldResult)
                        {
                            // A general field keeps its code and its cached result.
                            _segments[^1].Add(inline is Field field ? new Field(field.Instruction, LimitedResult(child.Value)) : inline);
                        }
                    }
                    else
                    {
                        ReadContainer(child); // cached field result
                    }
                }
            }
        }

        private void ReadRun(XElement run)
        {
            var rPr = run.Element(W + "rPr");
            var text = new StringBuilder();

            void Flush()
            {
                if (text.Length > 0)
                {
                    _segments[^1].Add(ReadTextRun(text.ToString(), rPr));
                    text.Clear();
                }
            }

            foreach (var child in run.Elements())
            {
                var name = child.Name;
                if (name == W + "fldChar")
                {
                    Flush();
                    HandleFieldChar(child.Attribute(W + "fldCharType")?.Value);
                    continue;
                }

                if (name == W + "instrText" || name == W + "delInstrText")
                {
                    if (_fields.TryPeek(out var field) && !field.InResult)
                    {
                        field.Instruction.Append(child.Value);
                    }

                    continue;
                }

                if (InMappedFieldResult)
                {
                    // The cached result of a general field becomes its Result; the result of a typed
                    // field is already represented by the field inline.
                    var value = name == W + "t" || name == W + "delText" ? child.Value
                        : name == W + "tab" ? "\t"
                        : name == W + "cr" || name == W + "br" ? "\n"
                        : string.Empty;
                    foreach (var open in _fields.Where(open => open.General && open.InResult))
                    {
                        if (open.Result.Length + value.Length > ReaderGuard.MaxTextLength)
                        {
                            throw new DocumentValidationException("DOCX: the cached field result exceeds the text limit.");
                        }

                        open.Result.Append(value);
                    }

                    continue;
                }

                if (name == W + "t" || name == W + "delText")
                {
                    text.Append(child.Value);
                }
                else if (name == W + "tab")
                {
                    text.Append('\t');
                }
                else if (name == W + "ptab")
                {
                    var alignment = child.Attribute(W + "alignment")?.Value switch
                    {
                        "left" => TabStopAlignment.Left,
                        "center" => TabStopAlignment.Center,
                        "right" => TabStopAlignment.Right,
                        _ => (TabStopAlignment?)null,
                    };
                    if (alignment is null)
                    {
                        Warn(warnings, "DOCX: an alignment tab without a valid alignment is read as a tab.");
                        text.Append('\t');
                    }
                    else
                    {
                        Flush();
                        var leader = child.Attribute(W + "leader")?.Value switch
                        {
                            "dot" or "middleDot" => TabStopLeader.Dot,
                            "hyphen" => TabStopLeader.Hyphen,
                            "underscore" => TabStopLeader.Underscore,
                            _ => TabStopLeader.None,
                        };
                        var relativeTo = child.Attribute(W + "relativeTo")?.Value == "indent"
                            ? AlignmentTabRelativeTo.Indent : AlignmentTabRelativeTo.Margin;
                        _segments[^1].Add(new AlignmentTab(alignment.Value, leader, relativeTo));
                    }
                }
                else if (name == W + "cr")
                {
                    text.Append('\n');
                }
                else if (name == W + "br")
                {
                    if (child.Attribute(W + "type")?.Value == "page")
                    {
                        Flush();
                        _segments.Add([]);
                    }
                    else
                    {
                        text.Append('\n');
                    }
                }
                else if (name == W + "noBreakHyphen")
                {
                    text.Append('\u2011');
                }
                else if (name == W + "softHyphen")
                {
                    text.Append('\u00AD');
                }
                else if (name == W + "drawing")
                {
                    ReadDrawing(child);
                }
                else if (name == Mc + "AlternateContent")
                {
                    // Word writes shapes as a choice with a fallback; the choice is the real content.
                    foreach (var drawing in child.Element(Mc + "Choice")?.Elements(W + "drawing") ?? [])
                    {
                        ReadDrawing(drawing);
                    }
                }
                else if (name == W + "pict" || name == W + "object")
                {
                    if (!(name == W + "object" && ReadEmbeddedObject(child)))
                    {
                        Warn(warnings, EmbeddedObjectMessage(child));
                    }
                }
                else if (name == W + "commentReference"
                    && (InMappedFieldResult || child.Attribute(W + "id")?.Value is { } referenceId && resources.Comments.ContainsKey(referenceId)))
                {
                    // The marker after a commented range; the comment itself is read at its start. LibreOffice writes
                    // a comment without a range as a reference alone: that is a point comment.
                    if (!InMappedFieldResult
                        && resources.Comments[child.Attribute(W + "id")!.Value] is { IsReply: false } point
                        && resources.CommentsPlaced.Add(child.Attribute(W + "id")!.Value))
                    {
                        Flush();
                        _segments[^1].Add(point.Comment);
                    }
                }
                else if (name == W + "commentReference")
                {
                    Warn(warnings, "DOCX: a comment is not mapped when reading; its anchor is lost when the body changes.");
                }
                else if (name == W + "footnoteReference" || name == W + "endnoteReference")
                {
                    var endnote = name == W + "endnoteReference";
                    var id = child.Attribute(W + "id")?.Value;
                    if (id is not null
                        && resources.Notes is { } notes
                        && notes.TryGetValue((endnote, id), out var footnote))
                    {
                        _segments[^1].Add(footnote);
                    }
                    else
                    {
                        Warn(warnings, "DOCX: a footnote/endnote reference without content is not mapped when reading.");
                    }
                }
            }

            Flush();

            // A formatted but empty run is preserved (like an empty w:t).
            if (rPr is not null && run.Elements().All(child => child.Name == W + "rPr" || child.Name == W + "t")
                && !run.Elements(W + "t").Any(t => t.Value.Length > 0)
                && !InMappedFieldResult)
            {
                _segments[^1].Add(ReadTextRun(string.Empty, rPr));
            }
        }

        private void HandleFieldChar(string? type)
        {
            switch (type)
            {
                case "begin":
                    _fields.Push(new FieldState());
                    break;
                case "separate" when _fields.TryPeek(out var field):
                    FinishInstruction(field);
                    field.InResult = true;
                    break;
                case "end" when _fields.TryPop(out var field):
                    if (!field.InResult)
                    {
                        FinishInstruction(field); // a field without a cached result
                    }

                    // A general field gets its collected cached result at the end.
                    if (field.General && field.Location is var (segment, index)
                        && segment < _segments.Count && index < _segments[segment].Count)
                    {
                        _segments[segment][index] = new Field(
                            field.Instruction.ToString().Trim(), field.InResult ? field.Result.ToString() : null);
                    }

                    break;
            }
        }

        private void FinishInstruction(FieldState field)
        {
            if (field.InstructionDone)
            {
                return;
            }

            field.InstructionDone = true;
            var instruction = field.Instruction.ToString();
            if (FieldFor(instruction) is { } inline)
            {
                field.Mapped = true;
                field.General = inline is Field;
                // When called, the field itself is not yet in the result part (or already removed);
                // InMappedFieldResult therefore only affects enclosing fields.
                if (!InMappedFieldResult)
                {
                    field.Location = (_segments.Count - 1, _segments[^1].Count);
                    _segments[^1].Add(inline);
                }
            }
        }

        /// <summary>A cached field result within the text limit of a run.</summary>
        private static string LimitedResult(string result)
            => result.Length <= ReaderGuard.MaxTextLength
                ? result
                : throw new DocumentValidationException("DOCX: the cached field result exceeds the text limit.");

        /// <summary>
        /// Reads a <c>w:object</c> with an <c>o:OLEObject</c> into an <see cref="EmbeddedObject"/> block (ADR-0029).
        /// Returns false when the element holds no OLE object (the caller reports it); a problem with the object itself
        /// is reported here and the object is left out.
        /// </summary>
        private bool ReadEmbeddedObject(XElement element)
        {
            var ole = element.Descendants().FirstOrDefault(node => node.Name.LocalName == "OLEObject");
            if (ole is null)
            {
                return false;
            }

            var progId = ole.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "ProgID")?.Value ?? string.Empty;
            var label = progId.Length > 0 ? progId : "w:object";
            if (!resources.Relationships.TryGetValue((string?)ole.Attribute(R + "id") ?? string.Empty, out var dataPath)
                || resources.Zip.GetEntry(dataPath) is not { Length: > 0 } dataEntry)
            {
                Warn(warnings, $"DOCX: the embedded object '{label}' has no data part (a linked object?) and is not mapped when reading.");
                return true;
            }

            var shape = element.Descendants().FirstOrDefault(node => node.Name.LocalName == "shape");
            var style = (string?)shape?.Attribute("style");
            var width = DocxEmbeddedObjects.ParseStyleLength(style, "width") ?? TwipsLength((string?)element.Attribute(W + "dxaOrig"));
            var height = DocxEmbeddedObjects.ParseStyleLength(style, "height") ?? TwipsLength((string?)element.Attribute(W + "dyaOrig"));
            if (width is null || height is null)
            {
                Warn(warnings, $"DOCX: the embedded object '{label}' has no size and is not mapped when reading.");
                return true;
            }

            // Charged to the media budget: every reference becomes its own copy of the data.
            byte[] data;
            try
            {
                data = resources.Media.Read(dataEntry, ReaderGuard.MaxDecodedBytes);
            }
            catch (DocumentValidationException)
            {
                Warn(warnings, $"DOCX: the embedded object '{label}' exceeds the size limit and is not mapped.");
                return true;
            }

            ImageBlock? preview = null;
            var imageData = element.Descendants().FirstOrDefault(node => node.Name.LocalName == "imagedata");
            if (imageData?.Attribute(R + "id") is { } previewId)
            {
                if (resources.Relationships.TryGetValue(previewId.Value, out var previewPath)
                    && MediaTypeOf(previewPath) is ImageMediaType.Png or ImageMediaType.Jpeg
                    && resources.Zip.GetEntry(previewPath) is { } previewEntry)
                {
                    try
                    {
                        var bytes = resources.Media.Read(previewEntry, ResourceLimits.Default.MaxImageBytes);
                        preview = bytes.Length > 0 ? new ImageBlock(bytes, MediaTypeOf(previewPath)!.Value) : null;
                    }
                    catch (DocumentValidationException)
                    {
                        Warn(warnings, $"DOCX: the preview of the embedded object '{label}' exceeds the size limit and is dropped.");
                    }
                }
                else
                {
                    Warn(warnings, $"DOCX: the preview of the embedded object '{label}' is not a PNG or JPEG and is dropped.");
                }
            }

            var extension = Path.GetExtension(dataPath).TrimStart('.');
            var isOle = extension.Equals("bin", StringComparison.OrdinalIgnoreCase);
            try
            {
                Images.Add(new EmbeddedObject(data, isOle ? EmbeddedObjectKind.OleStorage : EmbeddedObjectKind.Package, width.Value, height.Value)
                {
                    ProgId = progId,
                    Preview = preview,
                    FileExtension = isOle || extension.Length == 0 ? "bin" : extension,
                });
            }
            catch (ArgumentException)
            {
                Warn(warnings, $"DOCX: the embedded object '{label}' cannot be mapped (its data part name is unusual).");
            }

            return true;
        }

        private static Length? TwipsLength(string? value)
            => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips) && twips > 0
                ? Length.FromPoints(twips / 20.0)
                : null;

        /// <summary>Names an embedded object by its OLE program id when one is present.</summary>
        private static string EmbeddedObjectMessage(XElement element)
        {
            var programId = element.Descendants()
                .Where(node => node.Name.LocalName == "OLEObject")
                .Select(node => node.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "ProgID")?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return programId is not null
                ? $"DOCX: the embedded object '{programId}' is not mapped when reading."
                : $"DOCX: the embedded object 'w:{element.Name.LocalName}' is not mapped when reading.";
        }

        private static Inline? FieldFor(string? instruction)
        {
            var parts = instruction?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts is null || parts.Length == 0)
            {
                return null;
            }

            return parts[0].ToUpperInvariant() switch
            {
                "PAGE" => new PageNumberField(),
                "NUMPAGES" => new PageCountField(),
                "DATE" or "CREATEDATE" or "SAVEDATE" => new DateField(),
                "DOCPROPERTY" when FirstArgument(instruction!, parts) is { } property => new DocPropertyField(property),
                "DOCVARIABLE" when FirstArgument(instruction!, parts) is { } variable => new DocVariableField(variable),
                "PAGEREF" when Argument(parts, 1) is { } bookmark => new PageRefField(bookmark),
                // Any other field keeps its code (and later its cached result), like the ODT reader.
                _ => new Field(instruction!.Trim()),
            };
        }

        /// <summary>The first argument of a field code; quoted, it may hold spaces and <c>\"</c>.</summary>
        private static string? FirstArgument(string instruction, string[] parts)
        {
            if (parts.Length < 2 || !parts[1].StartsWith('"'))
            {
                return Argument(parts, 1);
            }

            var builder = new StringBuilder();
            for (var index = instruction.IndexOf('"') + 1; index < instruction.Length && instruction[index] != '"'; index++)
            {
                index += instruction[index] == '\\' && index + 1 < instruction.Length ? 1 : 0;
                builder.Append(instruction[index]);
            }

            return builder.ToString();
        }

        private static string? Argument(string[] parts, int index)
            => parts.Length > index ? parts[index].Trim('"') : null;

        private static ImageMediaType? MediaTypeOf(string path)
            => Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => ImageMediaType.Png,
                ".jpg" or ".jpeg" => ImageMediaType.Jpeg,
                ".svg" => ImageMediaType.Svg,
                _ => null,
            };

        private static Length? Emu(string? value)
            => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var emu) && emu > 0
                ? Length.FromPoints(emu / 12700.0)
                : null;

        /// <summary>The anchor, alignment and offsets of <c>wp:positionH</c> and <c>wp:positionV</c>.</summary>
        private FloatPlacement ImagePlacement(XElement? anchor)
        {
            var horizontal = anchor?.Element(WP + "positionH");
            var vertical = anchor?.Element(WP + "positionV");
            HorizontalAnchor? horizontalAnchor = (string?)horizontal?.Attribute("relativeFrom") switch
            {
                null or "column" => HorizontalAnchor.Column,
                "page" => HorizontalAnchor.Page,
                "margin" => HorizontalAnchor.Margin,
                "character" => HorizontalAnchor.Character,
                _ => null,
            };
            VerticalAnchor? verticalAnchor = (string?)vertical?.Attribute("relativeFrom") switch
            {
                null or "paragraph" => VerticalAnchor.Paragraph,
                "page" => VerticalAnchor.Page,
                "margin" => VerticalAnchor.Margin,
                "line" => VerticalAnchor.Line,
                _ => null,
            };
            HorizontalFloatAlignment? horizontalAlignment = (string?)horizontal?.Element(WP + "align") switch
            {
                "left" => HorizontalFloatAlignment.Left,
                "center" => HorizontalFloatAlignment.Center,
                "right" => HorizontalFloatAlignment.Right,
                _ => null,
            };
            VerticalFloatAlignment? verticalAlignment = (string?)vertical?.Element(WP + "align") switch
            {
                "top" => VerticalFloatAlignment.Top,
                "center" => VerticalFloatAlignment.Center,
                "bottom" => VerticalFloatAlignment.Bottom,
                _ => null,
            };
            if (horizontalAnchor is null || verticalAnchor is null
                || (horizontal?.Element(WP + "align") is not null && horizontalAlignment is null)
                || (vertical?.Element(WP + "align") is not null && verticalAlignment is null))
                Warn(warnings, "DOCX: an image position relative to an inner, outer, left or right margin, or an inside/outside alignment, is approximated when reading.");
            return new FloatPlacement(
                horizontalAnchor ?? HorizontalAnchor.Column,
                verticalAnchor ?? VerticalAnchor.Paragraph,
                horizontalAlignment,
                verticalAlignment,
                PositionOffset(horizontal),
                PositionOffset(vertical));
        }

        private static Length? PositionOffset(XElement? position)
            => long.TryParse((string?)position?.Element(WP + "posOffset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? Length.FromPoints(value / 12700.0)
                : null;

        private ImageCrop? ImageCropOf(XElement drawing)
        {
            var rect = drawing.Descendants(A + "srcRect").FirstOrDefault();
            if (rect is null) return null;
            double? Fraction(string name)
            {
                var value = (string?)rect.Attribute(name);
                if (value is null) return 0;
                if (value.EndsWith('%') && double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var percentage) && double.IsFinite(percentage)) return percentage / 100;
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n / 100000.0 : null;
            }
            var left = Fraction("l"); var top = Fraction("t"); var right = Fraction("r"); var bottom = Fraction("b");
            if (left is >= 0 and <= 0.5 && top is >= 0 and <= 0.5 && right is >= 0 and <= 0.5 && bottom is >= 0 and <= 0.5
                && left + right < 1 && top + bottom < 1)
                return new ImageCrop(left.Value, top.Value, right.Value, bottom.Value);
            Warn(warnings, "DOCX: an image crop is outside the supported range and is not mapped when reading.");
            return null;
        }

        /// <summary>Reads a <c>w:drawing</c>: media part via the blip relationship, extent in EMU.</summary>
        private void ReadDrawing(XElement drawing)
        {
            if (drawing.Descendants(A + "graphicData").FirstOrDefault() is { } graphicData
                && (string?)graphicData.Attribute("uri") == DocxConstants.WordprocessingShapeNamespace)
            {
                if (graphicData.Element(Wps + "wsp") is { } wsp)
                {
                    ReadShape(drawing, wsp);
                }
                else
                {
                    Warn(warnings, "DOCX: a shape group or canvas is not mapped when reading.");
                }

                return;
            }

            // ADR-0025: a DrawingML chart.
            if (drawing.Descendants(A + "graphicData").FirstOrDefault() is { } chartData
                && (string?)chartData.Attribute("uri") == DocxCharts.ChartNamespace)
            {
                if (ReadChart(drawing, chartData) is { } chart)
                {
                    Images.Add(chart);
                }

                return;
            }

            var blip = drawing.Descendants(A + "blip").FirstOrDefault();
            var rasterEmbed = (string?)blip?.Attribute(R + "embed");
            var svgEmbed = (string?)blip?.Descendants(XName.Get("svgBlip", DocxConstants.SvgDrawingNamespace)).FirstOrDefault()?.Attribute(R + "embed");
            if (svgEmbed is not null && (!resources.Relationships.TryGetValue(svgEmbed, out var svgPath)
                || MediaTypeOf(svgPath) != ImageMediaType.Svg || resources.Zip.GetEntry(svgPath) is null))
            {
                Warn(warnings, "DOCX: the SVG image is missing; its raster fallback is used when available.");
                svgEmbed = null;
            }
            var embed = svgEmbed ?? rasterEmbed;
            if (embed is null || !resources.Relationships.TryGetValue(embed, out var path))
            {
                Warn(warnings, "DOCX: a drawing without a media part is not mapped when reading.");
                return;
            }

            var entry = resources.Zip.GetEntry(path);
            if (entry is null || MediaTypeOf(path) is not { } mediaType)
            {
                Warn(warnings, $"DOCX: the image part '{path}' cannot be mapped.");
                return;
            }

            // Bounded while inflating: a compressed image bomb must not occupy unbounded memory.
            // An oversized image is left out instead of failing the whole document.
            byte[] bytes;
            try
            {
                bytes = resources.Media.Read(entry, ResourceLimits.Default.MaxImageBytes);
            }
            catch (DocumentValidationException)
            {
                Warn(warnings, $"DOCX: the image part '{path}' exceeds the size limit and is not mapped.");
                return;
            }

            if (bytes.Length == 0)
            {
                Warn(warnings, $"DOCX: the image part '{path}' is empty.");
                return;
            }

            ImageBlock? fallback = null;
            if (mediaType == ImageMediaType.Svg && rasterEmbed is not null && rasterEmbed != svgEmbed)
            {
                if (resources.Relationships.TryGetValue(rasterEmbed, out var fallbackPath)
                    && resources.Zip.GetEntry(fallbackPath) is { } fallbackEntry
                    && MediaTypeOf(fallbackPath) is ImageMediaType.Png or ImageMediaType.Jpeg)
                {
                    try
                    {
                        var fallbackBytes = resources.Media.Read(fallbackEntry, ResourceLimits.Default.MaxImageBytes);
                        if (fallbackBytes.Length > 0) fallback = new ImageBlock(fallbackBytes, MediaTypeOf(fallbackPath)!.Value);
                    }
                    catch (DocumentValidationException)
                    {
                        Warn(warnings, "DOCX: an SVG raster fallback exceeds the size limit and is not mapped.");
                    }
                }
                if (fallback is null) Warn(warnings, "DOCX: an SVG raster fallback could not be mapped when reading.");
            }

            var extent = drawing.Descendants(WP + "extent").FirstOrDefault();
            var anchor = drawing.Element(WP + "anchor");
            var wrap = ReadWrap(anchor);
            var placement = ImagePlacement(anchor);
            var block = new ImageBlock(bytes, mediaType)
            {
                AltText = drawing.Descendants(WP + "docPr").FirstOrDefault()?.Attribute("descr")?.Value,
                Width = Emu(extent?.Attribute("cx")?.Value),
                Height = Emu(extent?.Attribute("cy")?.Value),
                Wrap = wrap,
                OffsetX = placement.OffsetX,
                OffsetY = placement.OffsetY,
                HorizontalRelativeTo = placement.HorizontalRelativeTo,
                VerticalRelativeTo = placement.VerticalRelativeTo,
                HorizontalAlignment = placement.HorizontalAlignment,
                VerticalAlignment = placement.VerticalAlignment,
                Crop = ImageCropOf(drawing),
                RasterFallback = fallback,
            };
            Images.Add(block);
            if (inlineImages && anchor is null && !InMappedFieldResult)
            {
                InLine.Add(block);
                _segments[^1].Add(new InlineImage(block));
            }
        }

        private Chart? ReadChart(XElement drawing, XElement graphicData)
        {
            var id = (string?)graphicData.Element(XName.Get("chart", DocxCharts.ChartNamespace))?.Attribute(R + "id");
            if (id is null || !resources.Relationships.TryGetValue(id, out var path) || resources.Zip.GetEntry(path) is not { } entry)
            {
                Warn(warnings, "DOCX: the chart part is missing and the chart is not mapped when reading.");
                return null;
            }

            XElement root;
            try
            {
                var bytes = resources.Media.Read(entry, ResourceLimits.Default.MaxImageBytes);
                root = ReaderGuard.LoadXml(bytes).Root!;
            }
            catch (DocumentValidationException)
            {
                Warn(warnings, $"DOCX: the chart part '{path}' exceeds the size limit and is not mapped.");
                return null;
            }
            catch (System.Xml.XmlException)
            {
                Warn(warnings, $"DOCX: the chart part '{path}' is not well-formed and is not mapped.");
                return null;
            }

            var extent = drawing.Descendants(WP + "extent").FirstOrDefault();
            var alt = drawing.Descendants(WP + "docPr").FirstOrDefault()?.Attribute("descr")?.Value;
            return DocxChartReader.Read(
                root, Emu(extent?.Attribute("cx")?.Value), Emu(extent?.Attribute("cy")?.Value),
                string.IsNullOrEmpty(alt) ? null : alt, resources.Charts, message => Warn(warnings, message));
        }

        /// <summary>The wrap mode of an anchored drawing; <see cref="ImageWrap.None"/> for an inline one.</summary>
        private ImageWrap ReadWrap(XElement? anchor)
        {
            var wrap = ImageWrap.None;
            if (anchor is not null)
            {
                if (anchor.Element(WP + "wrapSquare") is { } square)
                {
                    wrap = ImageWrap.Square;
                    if ((string?)square.Attribute("wrapText") is { } sides && sides != "bothSides")
                        Warn(warnings, "DOCX: one-sided image wrapping is approximated when reading.");
                }
                else if (anchor.Element(WP + "wrapTopAndBottom") is not null) wrap = ImageWrap.TopAndBottom;
                else if (anchor.Element(WP + "wrapThrough") is not null) wrap = ImageWrap.Through;
                else if (anchor.Element(WP + "wrapNone") is not null)
                {
                    // OF-B06: behind and in front differ only in the layer.
                    wrap = anchor.Attribute("behindDoc")?.Value is "1" or "true" ? ImageWrap.Behind : ImageWrap.InFront;
                }
                else
                {
                    wrap = ImageWrap.Square;
                    Warn(warnings, "DOCX: this image wrapping mode is approximated when reading.");
                }
            }
            return wrap;
        }

        private static readonly XNamespace Wps = DocxConstants.WordprocessingShapeNamespace;
        private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

        /// <summary>
        /// Reads a shape or text box (<c>wps:wsp</c>, ADR-0022): preset geometries and custom paths
        /// with a solid fill and outline. Anything else is reported and left out.
        /// </summary>
        private void ReadShape(XElement drawing, XElement wsp)
        {
            var spPr = wsp.Element(Wps + "spPr");
            var extent = drawing.Descendants(WP + "extent").FirstOrDefault();
            var width = ReadEmu(extent?.Attribute("cx")?.Value);
            var height = ReadEmu(extent?.Attribute("cy")?.Value);
            var anchor = drawing.Element(WP + "anchor");
            var content = wsp.Element(Wps + "txbx")?.Element(W + "txbxContent");
            var preset = spPr?.Element(A + "prstGeom")?.Attribute("prst")?.Value;
            var custom = spPr?.Element(A + "custGeom")?.Element(A + "pathLst")?.Element(A + "path");

            ShapeKind? kind = content is not null ? ShapeKind.TextBox : custom is not null ? ShapeKind.Freeform : preset switch
            {
                "rect" => ShapeKind.Rectangle,
                "roundRect" => ShapeKind.RoundRectangle,
                "ellipse" => ShapeKind.Ellipse,
                "line" or "straightConnector1" => ShapeKind.Line,
                _ => null,
            };
            if (kind is null || width is null || height is null)
            {
                Warn(warnings, $"DOCX: the shape geometry '{preset ?? "custom"}' is not mapped when reading.");
                return;
            }

            List<Block>? blocks = null;
            if (content is not null)
            {
                var collector = new BlockCollector();
                foreach (var child in content.Elements())
                {
                    ParseBodyElement(child, collector, new Dictionary<string, NumberingDefinition>(), resources, warnings);
                }

                var all = collector.ToList();
                blocks = all.Where(block => block is Paragraph or Heading).ToList();
                if (blocks.Count != all.Count)
                {
                    Warn(warnings, "DOCX: a text box holds only paragraphs when read; other content in it is not mapped.");
                }
            }

            List<ShapePathStep>? steps = null;
            if (kind == ShapeKind.Freeform)
            {
                steps = ReadPath(custom!, width.Value, height.Value);
                if (steps is null)
                {
                    Warn(warnings, "DOCX: a custom shape path is not mapped when reading.");
                    return;
                }
            }

            var style = wsp.Element(Wps + "style");
            var (fill, fillOk) = ReadShapeColor(spPr, "solidFill", spPr?.Element(A + "noFill") is not null);
            var line = spPr?.Element(A + "ln");
            var (outline, outlineOk) = ReadShapeColor(line, "solidFill", line?.Element(A + "noFill") is not null);
            // Without its own fill or line, a shape takes the theme color of its style (a:fillRef, a:lnRef).
            if (fill is null && fillOk && spPr?.Elements().Any(e => e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "blipFill" or "pattFill" or "grpFill") != true)
            {
                (fill, fillOk) = ReadStyleColor(style?.Element(A + "fillRef"));
            }

            if (outline is null && outlineOk && line?.Elements().Any(e => e.Name.LocalName is "noFill" or "solidFill" or "gradFill" or "pattFill") != true)
            {
                (outline, outlineOk) = ReadStyleColor(style?.Element(A + "lnRef"));
            }
            if (!fillOk || !outlineOk)
            {
                Warn(warnings, "DOCX: a shape color that is not a plain RGB value is not mapped when reading.");
            }

            var dash = line?.Element(A + "prstDash")?.Attribute("val")?.Value switch
            {
                "dash" or "sysDash" or "lgDash" or "dashDot" or "lgDashDot" => ShapeDash.Dash,
                "sysDot" or "dot" => ShapeDash.Dot,
                _ => ShapeDash.Solid,
            };
            var outlineWidth = long.TryParse(line?.Attribute("w")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineEmu) && lineEmu > 0
                ? Length.FromPoints(lineEmu / 12700.0) : Length.FromPoints(1);
            var rotation = long.TryParse(spPr?.Element(A + "xfrm")?.Attribute("rot")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rot)
                ? rot / 60000.0 : 0;
            var placement = ImagePlacement(anchor);
            try
            {
                Images.Add(new DrawingShape(kind.Value, width.Value, height.Value, blocks, steps)
                {
                    Fill = fill,
                    Outline = outline,
                    OutlineWidth = outlineWidth,
                    Dash = dash,
                    Rotation = rotation,
                    Wrap = ReadWrap(anchor),
                    OffsetX = placement.OffsetX,
                    OffsetY = placement.OffsetY,
                    HorizontalRelativeTo = placement.HorizontalRelativeTo,
                    VerticalRelativeTo = placement.VerticalRelativeTo,
                    HorizontalAlignment = placement.HorizontalAlignment,
                    VerticalAlignment = placement.VerticalAlignment,
                    AltText = drawing.Descendants(WP + "docPr").FirstOrDefault()?.Attribute("descr")?.Value,
                });
            }
            catch (ArgumentException)
            {
                Warn(warnings, "DOCX: a shape with an invalid size or path is not mapped when reading.");
            }
        }

        private static Length? ReadEmu(string? value)
            => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var emu) && emu >= 0
                ? Length.FromPoints(emu / 12700.0)
                : null;

        /// <summary>The RGB color below <paramref name="container"/>/<paramref name="fillName"/>; not ok for another kind of color.</summary>
        private (Color? Color, bool Ok) ReadShapeColor(XElement? container, string fillName, bool none)
        {
            var fill = container?.Element(A + fillName);
            return none || fill is null ? (null, true) : ReadColorElement(fill);
        }

        /// <summary>The color of a style reference (<c>a:fillRef idx="1"</c> with a color child); index 0 means none.</summary>
        private (Color? Color, bool Ok) ReadStyleColor(XElement? reference)
            => reference is null || reference.Attribute("idx")?.Value is null or "0" ? (null, true) : ReadColorElement(reference);

        /// <summary>An RGB (<c>srgbClr</c>) or theme color (<c>schemeClr</c>, <c>sysClr</c>) below <paramref name="holder"/>, with its shade, tint and luminance changes.</summary>
        private (Color? Color, bool Ok) ReadColorElement(XElement holder)
        {
            var srgb = holder.Element(A + "srgbClr");
            var scheme = holder.Element(A + "schemeClr");
            var system = holder.Element(A + "sysClr");
            var element = srgb ?? scheme ?? system;
            string? rgb;
            if (srgb is not null)
            {
                rgb = srgb.Attribute("val")?.Value;
            }
            else if (scheme is not null)
            {
                var name = scheme.Attribute("val")?.Value switch { "tx1" => "dk1", "bg1" => "lt1", "tx2" => "dk2", "bg2" => "lt2", var other => other };
                rgb = name is not null && resources.ThemeColors.TryGetValue(name, out var themed) ? themed : null;
            }
            else
            {
                rgb = system?.Attribute("lastClr")?.Value;
            }

            if (element is null || rgb is not { Length: 6 } || !rgb.All(char.IsAsciiHexDigit))
            {
                return (null, false);
            }

            var channels = new double[] { Convert.ToInt32(rgb[..2], 16), Convert.ToInt32(rgb[2..4], 16), Convert.ToInt32(rgb[4..], 16) };
            foreach (var change in element.Elements())
            {
                if (!double.TryParse(change.Attribute("val")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thousandths)
                    || !double.IsFinite(thousandths))
                {
                    continue;
                }

                var factor = Math.Clamp(thousandths / 100000.0, 0, 10);
                switch (change.Name.LocalName)
                {
                    case "shade":
                        channels = channels.Select(c => c * Math.Min(1, factor)).ToArray();
                        break;
                    case "tint":
                        channels = channels.Select(c => 255 - (255 - c) * Math.Min(1, factor)).ToArray();
                        break;
                    case "lumMod" or "lumOff":
                        var (hue, saturation, luminance) = ToHsl(channels);
                        luminance = change.Name.LocalName == "lumMod" ? luminance * factor : luminance + factor;
                        channels = FromHsl(hue, saturation, Math.Clamp(luminance, 0, 1));
                        break;
                }
            }

            return (Color.FromHex(string.Concat(channels.Select(c => ((int)Math.Round(Math.Clamp(c, 0, 255))).ToString("X2", CultureInfo.InvariantCulture)))), true);
        }

        private static (double Hue, double Saturation, double Luminance) ToHsl(double[] rgb)
        {
            double r = rgb[0] / 255, g = rgb[1] / 255, b = rgb[2] / 255;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
            if (d == 0)
            {
                return (0, 0, l);
            }

            var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            var h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
            return (h / 6, s, l);
        }

        private static double[] FromHsl(double h, double s, double l)
        {
            if (s == 0)
            {
                return [l * 255, l * 255, l * 255];
            }

            var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
            var p = (2 * l) - q;
            double Channel(double t)
            {
                t = (t + 1) % 1;
                return 255 * (t < 1.0 / 6 ? p + ((q - p) * 6 * t) : t < 0.5 ? q : t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6) : p);
            }

            return [Channel(h + (1.0 / 3)), Channel(h), Channel(h - (1.0 / 3))];
        }

        /// <summary>The steps of a custom path, scaled from the path's own size to the shape's size.</summary>
        private static List<ShapePathStep>? ReadPath(XElement path, Length width, Length height)
        {
            var pathWidth = long.TryParse(path.Attribute("w")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w > 0 ? w : 0;
            var pathHeight = long.TryParse(path.Attribute("h")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 0;
            if (pathWidth == 0 || pathHeight == 0)
            {
                return null;
            }

            double scaleX = width.Points / pathWidth, scaleY = height.Points / pathHeight;
            bool Point(XElement? pt, out Length x, out Length y)
            {
                x = y = default;
                if (pt is null
                    || !long.TryParse(pt.Attribute("x")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var px)
                    || !long.TryParse(pt.Attribute("y")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var py))
                {
                    return false;
                }

                x = Length.FromPoints(px * scaleX);
                y = Length.FromPoints(py * scaleY);
                return true;
            }

            var steps = new List<ShapePathStep>();
            foreach (var element in path.Elements())
            {
                var points = element.Elements(A + "pt").ToList();
                var name = element.Name.LocalName;
                Length x, y, x1, y1, x2, y2;
                if (name is "moveTo" or "lnTo" && points.Count == 1 && Point(points[0], out x, out y))
                {
                    steps.Add(new ShapePathStep(name == "moveTo" ? ShapePathCommand.MoveTo : ShapePathCommand.LineTo, x, y));
                }
                else if (name == "cubicBezTo" && points.Count == 3
                    && Point(points[0], out x1, out y1) && Point(points[1], out x2, out y2) && Point(points[2], out x, out y))
                {
                    steps.Add(new ShapePathStep(ShapePathCommand.CubicTo, x, y, x1, y1, x2, y2));
                }
                else if (name == "close")
                {
                    steps.Add(new ShapePathStep(ShapePathCommand.Close, Length.Zero, Length.Zero));
                }
                else
                {
                    return null;
                }
            }

            return steps.Count > 0 ? steps : null;
        }

        private sealed class FieldState
        {
            public StringBuilder Instruction { get; } = new();

            public bool InstructionDone { get; set; }

            public bool Mapped { get; set; }

            public bool InResult { get; set; }

            /// <summary>Whether the field is a general field whose cached result is collected.</summary>
            public bool General { get; set; }

            /// <summary>The collected cached result of a general field.</summary>
            public StringBuilder Result { get; } = new();

            /// <summary>Where the field inline sits (segment, index), so the end can set its result.</summary>
            public (int Segment, int Index)? Location { get; set; }
        }
    }

    /// <summary>
    /// The direct formatting of a run. ADR-0010: tri-state - a property the run does not set stays
    /// null, so the paragraph or character style applies; an explicit off (<c>w:b w:val="0"</c>) is
    /// false.
    /// </summary>
    private static TextRun ReadTextRun(string text, XElement? rPr) => new(text.Replace("\x0B", "\t"))
    {
        Bold = rPr?.Element(W + "b") is { } b ? IsOn(b) : null,
        Italic = rPr?.Element(W + "i") is { } i ? IsOn(i) : null,
        Underline = rPr?.Element(W + "u") is { } u
            ? (u.Attribute(W + "val")?.Value ?? "single") != "none"
            : null,
        UnderlineStyle = rPr?.Element(W + "u")?.Attribute(W + "val")?.Value switch
        {
            "double" => TextUnderlineStyle.Double,
            "dotted" => TextUnderlineStyle.Dotted,
            "dash" => TextUnderlineStyle.Dashed,
            "wave" => TextUnderlineStyle.Wave,
            _ => null,
        },
        UnderlineColor = ReadHexColor(rPr?.Element(W + "u")?.Attribute(W + "color")?.Value),
        Background = ReadHexColor(rPr?.Element(W + "shd")?.Attribute(W + "fill")?.Value),
        Highlight = DocxHighlight.ColorOf(rPr?.Element(W + "highlight")?.Attribute(W + "val")?.Value),
        CharacterSpacing = TryPoints(rPr?.Element(W + "spacing")?.Attribute(W + "val")?.Value, out var spacing)
            && double.IsFinite(spacing) ? Length.FromPoints(spacing) : null,
        Kerning = HalfPointLength(rPr?.Element(W + "kern")?.Attribute(W + "val")?.Value, nonnegative: true),
        Position = HalfPointLength(rPr?.Element(W + "position")?.Attribute(W + "val")?.Value, nonnegative: false),
        HorizontalScaling = double.TryParse(rPr?.Element(W + "w")?.Attribute(W + "val")?.Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var scaling) && double.IsFinite(scaling) && scaling > 0 ? scaling : null,
        AllCaps = rPr?.Element(W + "caps") is { } caps ? IsOn(caps) : null,
        SmallCaps = rPr?.Element(W + "smallCaps") is { } smallCaps ? IsOn(smallCaps) : null,
        Hidden = rPr?.Element(W + "vanish") is { } vanish ? IsOn(vanish) : null,
        Effects = EffectsOf(rPr),
        Border = ReadBorder(rPr?.Element(W + "bdr")),
        Strikethrough = StrikethroughOf(rPr),
        Baseline = BaselineOf(rPr),
        Color = rPr?.Element(W + "color")?.Attribute(W + "val") is { } colorVal
            && colorVal.Value.Length == 6
            && colorVal.Value.All(char.IsAsciiHexDigit)
                ? Color.FromHex(colorVal.Value)
                : null,
        FontSize = rPr?.Element(W + "sz")?.Attribute(W + "val") is { } szVal
            && double.TryParse(szVal.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var halfPoints)
            && halfPoints > 0
                ? Length.FromPoints(halfPoints / 2.0)
                : null,
        FontFamily = rPr?.Element(W + "rFonts")?.Attribute(W + "ascii") is { } ascii
            && !string.IsNullOrWhiteSpace(ascii.Value)
                ? ascii.Value
                : null,
        CharacterStyleName = rPr?.Element(W + "rStyle")?.Attribute(W + "val")?.Value,
    };

    private static Color? ReadHexColor(string? value)
        => value is { Length: 6 } && value.All(char.IsAsciiHexDigit) ? Color.FromHex(value) : null;

    private static Length? HalfPointLength(string? value, bool nonnegative)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var halfPoints)
            && (!nonnegative || halfPoints >= 0) ? Length.FromPoints(halfPoints / 2.0) : null;

    /// <summary>The strikethrough of a run; <c>w:strike</c>/<c>w:dstrike</c> with <c>val="0"</c> is off.</summary>
    private static TextStrikethrough? StrikethroughOf(XElement? rPr)
    {
        if (rPr?.Element(W + "strike") is { } strike)
        {
            return IsOn(strike) ? TextStrikethrough.Single : TextStrikethrough.None;
        }

        if (rPr?.Element(W + "dstrike") is { } doubleStrike)
        {
            return IsOn(doubleStrike) ? TextStrikethrough.Double : TextStrikethrough.None;
        }

        return null;
    }

    /// <summary>The vertical position of a run; <c>w:vertAlign w:val="baseline"</c> is off.</summary>
    private static TextBaseline? BaselineOf(XElement? rPr)
        => rPr?.Element(W + "vertAlign")?.Attribute(W + "val")?.Value switch
        {
            "superscript" => TextBaseline.Superscript,
            "subscript" => TextBaseline.Subscript,
            null => null,
            _ => TextBaseline.Normal,
        };

    private static bool IsOn(XElement? element)
        => element is not null
            && element.Attribute(W + "val")?.Value is not ("0" or "false" or "off" or "none");

    /// <summary>Reads the paragraph borders (phase AC, AC01).</summary>
    private static ParagraphBorders? ReadParagraphBorders(XElement? pBdr)
    {
        if (pBdr is null)
        {
            return null;
        }

        // A present w:pBdr is an explicit border state; all sides nil/none mean "no borders"
        // (ParagraphBorders.None) and must not inherit the borders of the style (ADR-0016).
        return new ParagraphBorders
        {
            Top = ReadBorder(pBdr.Element(W + "top")),
            Left = ReadBorder(pBdr.Element(W + "left")),
            Bottom = ReadBorder(pBdr.Element(W + "bottom")),
            Right = ReadBorder(pBdr.Element(W + "right")),
        };
    }

    /// <summary>
    /// Reads a table's border from the first outer side; <c>none</c> or <c>nil</c> on all four outer sides (and any inner
    /// line given) is <see cref="ParagraphBorder.None"/> (no grid), not a missing border (which falls back to the default grid).
    /// </summary>
    private static ParagraphBorder? ReadTableBorder(XElement? borders)
    {
        if (borders is null)
        {
            return null;
        }

        var sides = borders.Elements()
            .Where(element => element.Name.LocalName is "top" or "left" or "bottom" or "right" or "insideH" or "insideV" or "start" or "end")
            .ToList();
        // Only all four outer sides cleared mean no border; inner lines alone cleared keep the style's outer border.
        bool Cleared(string name, string? alternative = null) => sides.Any(side => side.Name.LocalName == name || side.Name.LocalName == alternative);
        if (Cleared("top") && Cleared("bottom") && Cleared("left", "start") && Cleared("right", "end")
            && sides.All(side => side.Attribute(W + "val")?.Value is "none" or "nil"))
        {
            return ParagraphBorder.None;
        }

        return ReadBorder(sides.FirstOrDefault(element => element.Name.LocalName is "top" or "left" or "bottom" or "right"));
    }

    /// <summary>Reads a single border side (the width is in eighths of a point).</summary>
    private static ParagraphBorder? ReadBorder(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var value = element.Attribute(W + "val")?.Value;
        if (value is "none" or "nil")
        {
            return null;
        }

        var style = value switch
        {
            "double" => BorderStyle.Double,
            "dotted" => BorderStyle.Dotted,
            "dashed" => BorderStyle.Dashed,
            _ => BorderStyle.Single,
        };

        var width = element.Attribute(W + "sz") is { } size
            && int.TryParse(size.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eighths)
            && eighths > 0
            ? Length.FromPoints(eighths / 8.0)
            : (Length?)null;
        var color = ParseHexColor(element.Attribute(W + "color")?.Value);
        return new ParagraphBorder(style, width, color);
    }

    /// <summary>Reads the paragraph shading (the fill color).</summary>
    private static Color? ReadShading(XElement? shd)
    {
        var fill = shd?.Attribute(W + "fill")?.Value;
        if (string.IsNullOrEmpty(fill) || fill == "auto")
        {
            return null;
        }

        return ParseHexColor(fill);
    }

    /// <summary>Reads the tab stops of a paragraph.</summary>
    private static IReadOnlyList<TabStop>? ReadTabStops(XElement? tabs)
    {
        if (tabs is null)
        {
            return null;
        }

        var stops = new List<TabStop>();
        foreach (var tab in tabs.Elements(W + "tab"))
        {
            // A clear entry removes an inherited stop; it is not a stop of the model (ADR-0018).
            if (tab.Attribute(W + "val")?.Value is "clear")
            {
                continue;
            }

            // OOXML allows a stop left of the margin; the model does not.
            if (!TryPoints(tab.Attribute(W + "pos")?.Value, out var position) || position < 0)
            {
                continue;
            }

            var alignment = tab.Attribute(W + "val")?.Value switch
            {
                "right" => TabStopAlignment.Right,
                "center" => TabStopAlignment.Center,
                "decimal" => TabStopAlignment.Decimal,
                _ => TabStopAlignment.Left,
            };
            var leader = tab.Attribute(W + "leader")?.Value switch
            {
                "dot" => TabStopLeader.Dot,
                "hyphen" => TabStopLeader.Hyphen,
                "underscore" => TabStopLeader.Underscore,
                _ => TabStopLeader.None,
            };
            stops.Add(new TabStop(Length.FromPoints(position), alignment, leader));
        }

        return stops;
    }

    /// <summary>A color from a hex value (for example "FF0000"); null on an invalid value.</summary>
    private static Color? ParseHexColor(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == "auto" || value.Length != 6)
        {
            return null;
        }

        try
        {
            return Color.FromHex(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The run formatting of a paragraph style (ADR-0010): the resolved <c>w:style/w:rPr</c> over
    /// <c>w:basedOn</c>, or null when the style sets none.
    /// </summary>
    private static RunFormat? RunFormatOf(string? styleName, DocxResources resources)
    {
        if (styleName is null || resources.ParagraphRunFormats.GetValueOrDefault(styleName) is not { } rPr)
        {
            return null;
        }

        var format = RunFormat.Of(ReadTextRun(string.Empty, rPr));
        return format.IsEmpty ? null : format;
    }

    private static ParagraphStyle ReadParagraphStyle(XElement? pPr)
    {
        if (pPr is null)
        {
            return ParagraphStyle.Default;
        }

        var style = ParagraphStyle.Default;

        // Break control and the writing direction (phase AC, AC01). A present element with an
        // explicit off stays false; an absent element inherits.
        if (pPr.Element(W + "keepNext") is { } keepNext)
        {
            style = style with { KeepWithNext = IsOn(keepNext) };
        }

        if (pPr.Element(W + "keepLines") is { } keepLines)
        {
            style = style with { KeepTogether = IsOn(keepLines) };
        }

        if (pPr.Element(W + "pageBreakBefore") is { } pageBreakBefore)
        {
            style = style with { PageBreakBefore = IsOn(pageBreakBefore) };
        }

        if (pPr.Element(W + "bidi") is { } bidi)
        {
            style = style with { RightToLeft = IsOn(bidi) };
        }

        if (ReadParagraphBorders(pPr.Element(W + "pBdr")) is { } borders)
        {
            style = style with { Borders = borders };
        }

        if (ReadShading(pPr.Element(W + "shd")) is { } shading)
        {
            style = style with { Background = shading };
        }

        if (ReadTabStops(pPr.Element(W + "tabs")) is { } tabStops)
        {
            style = style with { TabStops = tabStops };
        }

        if (pPr.Element(W + "jc")?.Attribute(W + "val") is { } jc)
        {
            style = style with { Alignment = jc.Value switch
            {
                "center" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                "both" => TextAlignment.Justify,
                _ => TextAlignment.Left,
            } };
        }

        // The paragraph mark: only its font and size are modelled (they set the height of an empty paragraph).
        if (pPr.Element(W + "rPr") is { } markProperties
            && ReadTextRun(string.Empty, markProperties) is { } mark
            && (mark.FontFamily is not null || mark.FontSize is not null))
        {
            style = style with { MarkFontFamily = mark.FontFamily, MarkFontSize = mark.FontSize };
        }

        if (pPr.Element(W + "spacing") is { } spacing)
        {
            // Word accepts negative spacing; the model does not, so it is clamped to 0.
            if (TryPoints(spacing.Attribute(W + "before")?.Value, out var before))
            {
                style = style with { SpaceBefore = Length.FromPoints(Math.Max(0, before)) };
            }

            if (TryPoints(spacing.Attribute(W + "after")?.Value, out var after))
            {
                style = style with { SpaceAfter = Length.FromPoints(Math.Max(0, after)) };
            }

            // The line spacing: "auto" is a multiple of 240 twips, the other rules a height.
            if (double.TryParse(
                    spacing.Attribute(W + "line")?.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var line)
                && double.IsFinite(line)
                && line > 0) // Word treats 0 as automatic; the model has no such value.
            {
                var lineSpacing = spacing.Attribute(W + "lineRule")?.Value switch
                {
                    "atLeast" => LineSpacing.AtLeast(Length.FromPoints(line / 20.0)),
                    "exact" => LineSpacing.Exactly(Length.FromPoints(line / 20.0)),
                    _ => LineSpacing.Multiple(line / 240.0),
                };
                style = style with { LineSpacing = lineSpacing };
            }
        }

        if (pPr.Element(W + "ind") is { } ind)
        {
            if (TryPoints((ind.Attribute(W + "left") ?? ind.Attribute(W + "start"))?.Value, out var left))
            {
                style = style with { LeftIndent = Length.FromPoints(left) };
            }

            if (TryPoints((ind.Attribute(W + "right") ?? ind.Attribute(W + "end"))?.Value, out var right))
            {
                style = style with { RightIndent = Length.FromPoints(right) };
            }

            if (TryPoints(ind.Attribute(W + "firstLine")?.Value, out var firstLine))
            {
                style = style with { FirstLineIndent = Length.FromPoints(firstLine) };
            }
            else if (TryPoints(ind.Attribute(W + "hanging")?.Value, out var hanging))
            {
                style = style with { FirstLineIndent = Length.FromPoints(-hanging) };
            }
        }

        return style;
    }

    /// <summary>Convert twips (1/20 pt) to points; false on an invalid value.</summary>
    private static bool TryPoints(string? value, out double points)
    {
        if (double.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips))
        {
            points = twips / 20.0;
            return true;
        }

        points = 0;
        return false;
    }

    /// <summary>
    /// The table styles of the document (phase AJ, AJ01). A table refers to its style by id
    /// (<c>w:tblStyle</c>); the definition comes from styles.xml.
    /// </summary>
    private static IReadOnlyDictionary<string, TableStyle> ReadTableStyles(XElement? styles)
    {
        var result = new Dictionary<string, TableStyle>(StringComparer.Ordinal);
        if (styles is null)
        {
            return result;
        }

        foreach (var style in styles.Descendants(W + "style"))
        {
            if ((string?)style.Attribute(W + "type") != "table")
            {
                continue;
            }

            var id = (string?)style.Attribute(W + "styleId");
            if (id is null)
            {
                continue;
            }

            var name = style.Element(W + "name")?.Attribute(W + "val")?.Value;
            var tblPr = style.Element(W + "tblPr");

            var border = ReadTableBorder(tblPr?.Element(W + "tblBorders"));

            var padding = ReadCellMargin(tblPr?.Element(W + "tblCellMar"));

            var tableStyle = new TableStyle(name is { Length: > 0 } ? name : id)
            {
                Border = border,
                CellPadding = padding,
                HeaderBackground = ReadConditionalBackground(style, "firstRow"),
                BandBackground = ReadConditionalBackground(style, "band1Horz"),
                LastRowBackground = ReadConditionalBackground(style, "lastRow"),
                FirstColumnBackground = ReadConditionalBackground(style, "firstCol"),
                LastColumnBackground = ReadConditionalBackground(style, "lastCol"),
                HeaderRow = ReadConditionalArea(style, "firstRow"),
                BandRow = ReadConditionalArea(style, "band1Horz"),
                LastRow = ReadConditionalArea(style, "lastRow"),
                FirstColumn = ReadConditionalArea(style, "firstCol"),
                LastColumn = ReadConditionalArea(style, "lastCol"),
            };

            result[id] = tableStyle;
        }

        return result;
    }

    /// <summary>The background of a conditional table format (the fill of the shading).</summary>
    private static Color? ReadConditionalBackground(XElement style, string type)
    {
        var conditional = style.Elements(W + "tblStylePr")
            .FirstOrDefault(element => (string?)element.Attribute(W + "type") == type);
        if (conditional is null)
        {
            return null;
        }

        // The shading lives in w:tcPr (ISO/IEC 29500-1); older files of this library wrote
        // it directly under w:tblStylePr.
        var shading = conditional.Element(W + "tcPr")?.Element(W + "shd") ?? conditional.Element(W + "shd");
        return shading is null ? null : ReadShading(shading);
    }

    /// <summary>The borders and the text formatting of a conditional table format.</summary>
    private static TableStyleArea? ReadConditionalArea(XElement style, string type)
    {
        var conditional = style.Elements(W + "tblStylePr")
            .FirstOrDefault(element => (string?)element.Attribute(W + "type") == type);
        if (conditional is null)
        {
            return null;
        }

        var borders = conditional.Element(W + "tcPr")?.Element(W + "tcBorders")
            ?? conditional.Element(W + "tcBorders");
        var border = borders is null
            ? null
            : ReadBorder(borders.Elements().FirstOrDefault(element =>
                element.Name.LocalName is "top" or "left" or "bottom" or "right"));

        var rPr = conditional.Element(W + "rPr");
        var bold = rPr?.Element(W + "b") is not null ? IsOn(rPr.Element(W + "b")) : (bool?)null;
        var italic = rPr?.Element(W + "i") is not null ? IsOn(rPr.Element(W + "i")) : (bool?)null;
        var color = ParseHexColor(rPr?.Element(W + "color")?.Attribute(W + "val")?.Value);

        if (border is null && bold is null && italic is null && color is null)
        {
            return null;
        }

        return new TableStyleArea
        {
            Border = border,
            Bold = bold,
            Italic = italic,
            TextColor = color,
        };
    }

    /// <summary>Reads the cell margins of a table (the horizontal margin in twips).</summary>
    private static Length? ReadCellMargin(XElement? margins) => ReadCellMargins(margins).Horizontal;

    /// <summary>The left (horizontal) and top (vertical) cell margin; null where it is absent or invalid.</summary>
    private static (Length? Horizontal, Length? Vertical) ReadCellMargins(XElement? margins)
    {
        static Length? Side(XElement? side, long minimum)
            => side?.Attribute(W + "w")?.Value is { } value
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips)
                && twips >= minimum
                    ? Length.FromPoints(twips / 20.0)
                    : null;

        return margins is null
            ? (null, null)
            : (Side(margins.Element(W + "left") ?? margins.Element(W + "start"), 1), Side(margins.Element(W + "top"), 0));
    }

    /// <summary>
    /// Reads a table (phase AJ, AJ01): the style, the borders, the cell padding, the width in
    /// percent, the column widths, the row heights and the cell properties (spans, background,
    /// vertical alignment and borders).
    /// </summary>
    private static TableBlock ReadTable(XElement tbl, IReadOnlyDictionary<string, NumberingDefinition> numbering, DocxResources resources, List<ReadWarning> warnings)
    {
        var tblPr = tbl.Element(W + "tblPr");
        var styleId = tblPr?.Element(W + "tblStyle")?.Attribute(W + "val")?.Value;
        var style = styleId is not null && resources.TableStyles.TryGetValue(styleId, out var tableStyle)
            ? tableStyle
            : null;

        var border = ReadTableBorder(tblPr?.Element(W + "tblBorders"));

        var (horizontalPadding, verticalPadding) = ReadCellMargins(tblPr?.Element(W + "tblCellMar"));
        var uniformPadding = horizontalPadding == verticalPadding;
        var leftIndent = tblPr?.Element(W + "tblInd") is { } tblInd
            && (string?)tblInd.Attribute(W + "type") is null or "dxa"
            && long.TryParse(tblInd.Attribute(W + "w")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var indentTwips)
            && indentTwips != 0
                ? Length.FromPoints(indentTwips / 20.0)
                : (Length?)null;

        var fixedLayout = (string?)tblPr?.Element(W + "tblLayout")?.Attribute(W + "type") == "fixed";
        double? widthPercent = null;
        if (tblPr?.Element(W + "tblW") is { } width
            && (string?)width.Attribute(W + "type") == "pct"
            && width.Attribute(W + "w")?.Value is { } percentValue
            && double.TryParse(percentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            widthPercent = Math.Clamp(percent / 50.0, 0.01, TableBlock.MaxWidthPercent);
        }

        var columnWidths = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol")
            .Select(element => element.Attribute(W + "w")?.Value)
            .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips) && twips > 0
                ? Length.FromPoints(twips / 20.0)
                : (Length?)null)
            .Where(width => width is not null)
            .Select(width => width!.Value)
            .ToList();

        var rowElements = tbl.Elements(W + "tr").ToList();
        // Every cell width "auto" outside a fixed layout: Word sizes the columns by their content, the grid is an estimate.
        var cellWidths = rowElements.SelectMany(tr => tr.Elements(W + "tc")).Select(tc => tc.Element(W + "tcPr")?.Element(W + "tcW")).ToList();
        var autoFit = !fixedLayout && widthPercent is null && cellWidths.Count > 0
            && cellWidths.All(width => (string?)width?.Attribute(W + "type") == "auto");
        if (autoFit)
        {
            columnWidths = null;
        }

        // What a cell gets from the table when it has no margins of its own.
        var tablePadding = (
            Horizontal: horizontalPadding ?? style?.CellPadding ?? Typography.TableCellPaddingHorizontal,
            Vertical: verticalPadding ?? style?.CellPadding ?? Typography.TableCellPaddingVertical);
        var parsedRows = new List<List<ParsedCell>>();
        foreach (var tr in rowElements)
        {
            parsedRows.Add(ReadTableCells(tr, numbering, resources, warnings));
        }

        var rows = new List<TableRow>();
        for (var rowIndex = 0; rowIndex < rowElements.Count; rowIndex++)
        {
            var cells = new List<TableCell>();
            foreach (var parsed in parsedRows[rowIndex])
            {
                if (parsed.VerticalMerge == VerticalMerge.Continue)
                {
                    continue; // the cell is covered by the row span of the cell above
                }

                var rowSpan = parsed.VerticalMerge == VerticalMerge.Restart
                    ? CountRowSpan(parsedRows, rowIndex, parsed.GridStart)
                    : 1;

                cells.Add(WithOwnPadding(parsed.Cell, parsed.Margins, tablePadding) with { RowSpan = Math.Min(rowSpan, TableCell.MaxSpan) });
            }

            var height = ReadRowHeight(rowElements[rowIndex]);
            rows.Add(new TableRow(cells)
            {
                IsHeader = height.IsHeader,
                Height = height.Height,
                HeightRule = height.Rule,
                KeepTogether = rowElements[rowIndex].Element(W + "trPr")?.Element(W + "cantSplit") is { } cantSplit && IsOn(cantSplit),
            });
        }

        return new TableBlock(rows)
        {
            Style = style,
            Border = border,
            CellPadding = uniformPadding ? horizontalPadding : null,
            CellPaddingHorizontal = uniformPadding ? null : horizontalPadding,
            CellPaddingVertical = uniformPadding ? null : verticalPadding,
            LeftIndent = leftIndent,
            WidthPercent = widthPercent,
            ColumnWidths = columnWidths is { Count: > 0 } ? columnWidths : null,
            FixedColumnWidths = fixedLayout && columnWidths is { Count: > 0 },
            AutoFit = autoFit,
            ColumnWidthPercents = ReadColumnPercents(rowElements.FirstOrDefault(), columnWidths?.Count ?? 0),
        };
    }

    /// <summary>
    /// The column percents of a table whose first row gives every column a <c>w:tcW w:type="pct"</c> of its own (no
    /// spans); null otherwise, so the grid widths apply.
    /// </summary>
    private static IReadOnlyList<double>? ReadColumnPercents(XElement? firstRow, int columns)
    {
        var percents = new List<double>();
        foreach (var tcPr in firstRow?.Elements(W + "tc").Select(tc => tc.Element(W + "tcPr")) ?? [])
        {
            if (tcPr?.Element(W + "gridSpan") is not null
                || tcPr?.Element(W + "tcW") is not { } width
                || (string?)width.Attribute(W + "type") != "pct"
                || !double.TryParse(width.Attribute(W + "w")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fiftieths)
                || !double.IsFinite(fiftieths) || fiftieths <= 0)
            {
                return null;
            }

            percents.Add(fiftieths / 50.0);
        }

        // Rounded fiftieths can sum a little past 100; the writers stretch the shares anyway.
        var total = percents.Sum();
        return percents.Count == columns && columns > 0
            ? total > 100 ? percents.Select(percent => percent * 100 / total).ToList() : percents
            : null;
    }

    /// <summary>The number of rows a vertically merged cell covers.</summary>
    private static int CountRowSpan(List<List<ParsedCell>> rows, int rowIndex, int gridStart)
    {
        var span = 1;
        for (var index = rowIndex + 1; index < rows.Count; index++)
        {
            var continuation = rows[index].FirstOrDefault(cell =>
                cell.GridStart == gridStart && cell.VerticalMerge == VerticalMerge.Continue);
            if (continuation is null)
            {
                break;
            }

            span++;
        }

        return span;
    }

    /// <summary>The height of a table row.</summary>
    private static (Length? Height, TableRowHeightRule Rule, bool IsHeader) ReadRowHeight(XElement tr)
    {
        var trPr = tr.Element(W + "trPr");
        var isHeader = trPr?.Element(W + "tblHeader") is not null;

        Length? height = null;
        var rule = TableRowHeightRule.Auto;
        if (trPr?.Element(W + "trHeight") is { } trHeight)
        {
            if (trHeight.Attribute(W + "val")?.Value is { } value
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips)
                && twips > 0)
            {
                height = Length.FromPoints(twips / 20.0);
            }

            rule = (trHeight.Attribute(W + "hRule")?.Value) switch
            {
                "exact" => TableRowHeightRule.Exactly,
                "atLeast" => TableRowHeightRule.AtLeast,
                _ => TableRowHeightRule.Auto,
            };
        }

        return (height, rule, isHeader);
    }

    /// <summary>The vertical merge of a cell (the row span).</summary>
    private enum VerticalMerge
    {
        None,
        Restart,
        Continue,
    }

    /// <summary>A cell of a table with its position in the grid.</summary>
    private sealed record ParsedCell(TableCell Cell, int GridStart, VerticalMerge VerticalMerge, (Length? Horizontal, Length? Vertical) Margins = default);

    private static List<ParsedCell> ReadTableCells(XElement tr, IReadOnlyDictionary<string, NumberingDefinition> numbering, DocxResources resources, List<ReadWarning> warnings)
    {
        var cells = new List<ParsedCell>();
        var column = 0;
        foreach (var tc in tr.Elements(W + "tc"))
        {
            var cellBlocks = new BlockCollector();
            foreach (var cellChild in tc.Elements())
            {
                if (IsClosingParagraph(cellChild))
                {
                    continue;
                }

                ParseBodyElement(cellChild, cellBlocks, numbering, resources, warnings);
            }

            var tcPr = tc.Element(W + "tcPr");
            var columnSpan = tcPr?.Element(W + "gridSpan")?.Attribute(W + "val")?.Value is { } spanValue
                && int.TryParse(spanValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var span) && span > 1
                    ? Math.Min(span, TableCell.MaxSpan)
                    : 1;

            // In OOXML a vMerge without a value continues the merge; "restart" starts it.
            var verticalMerge = tcPr?.Element(W + "vMerge") is { } vMerge
                ? (vMerge.Attribute(W + "val")?.Value ?? "continue") == "restart"
                    ? VerticalMerge.Restart
                    : VerticalMerge.Continue
                : VerticalMerge.None;

            var alignment = tcPr?.Element(W + "vAlign")?.Attribute(W + "val")?.Value switch
            {
                "center" => TableCellVerticalAlignment.Center,
                "bottom" => TableCellVerticalAlignment.Bottom,
                _ => TableCellVerticalAlignment.Top,
            };

            var borders = ReadCellBorders(tcPr?.Element(W + "tcBorders"));

            var cell = new TableCell(cellBlocks.ToList())
            {
                ColumnSpan = columnSpan,
                VerticalAlignment = alignment,
                Background = ReadShading(tcPr?.Element(W + "shd")),
                Borders = borders,
            };

            cells.Add(new ParsedCell(cell, column, verticalMerge, ReadCellOwnMargins(tcPr?.Element(W + "tcMar"))));
            column += columnSpan;
        }

        return cells;
    }

    /// <summary>The left (horizontal) and top (vertical) margin of one cell (<c>w:tcMar</c>); 0 is a valid margin here.</summary>
    private static (Length? Horizontal, Length? Vertical) ReadCellOwnMargins(XElement? margins)
    {
        static Length? Side(XElement? side)
            => side?.Attribute(W + "w")?.Value is { } value
                && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips)
                && twips >= 0 && twips <= 31680
                && (string?)side.Attribute(W + "type") is null or "dxa"
                    ? Length.FromPoints(twips / 20.0)
                    : null;

        return margins is null ? (null, null) : (Side(margins.Element(W + "left") ?? margins.Element(W + "start")), Side(margins.Element(W + "top")));
    }

    /// <summary>
    /// The padding of a cell from its own margins: a side equal to what the table gives (its own margins, its style's or
    /// Word's default) is left to the table, so a table whose writer repeats its padding on every cell reads back as it
    /// was; equal axes become <see cref="TableCell.Padding"/>.
    /// </summary>
    private static TableCell WithOwnPadding(TableCell cell, (Length? Horizontal, Length? Vertical) margins, (Length Horizontal, Length Vertical) table)
    {
        var horizontal = margins.Horizontal == table.Horizontal ? null : margins.Horizontal;
        var vertical = margins.Vertical == table.Vertical ? null : margins.Vertical;
        if (horizontal is null && vertical is null)
        {
            return cell;
        }

        return horizontal == vertical
            ? cell with { Padding = horizontal }
            : cell with { PaddingHorizontal = horizontal, PaddingVertical = vertical };
    }

    /// <summary>Reads the borders of a table cell (per side).</summary>
    private static ParagraphBorders? ReadCellBorders(XElement? borders)
    {
        if (borders is null)
        {
            return null;
        }

        var top = ReadCellBorder(borders.Element(W + "top"));
        var left = ReadCellBorder(borders.Element(W + "left")) ?? ReadCellBorder(borders.Element(W + "start"));
        var bottom = ReadCellBorder(borders.Element(W + "bottom"));
        var right = ReadCellBorder(borders.Element(W + "right")) ?? ReadCellBorder(borders.Element(W + "end"));

        if (top is null && left is null && bottom is null && right is null)
        {
            return null;
        }

        return new ParagraphBorders { Top = top, Left = left, Bottom = bottom, Right = right };
    }

    private static ParagraphBorder? ReadCellBorder(XElement? element)
        => (string?)element?.Attribute(W + "val") is "none" or "nil"
            ? new ParagraphBorder(BorderStyle.None)
            : ReadBorder(element);

    private static (NumberingDefinition Definition, int Level)? ReadListKind(
        XElement numPr,
        IReadOnlyDictionary<string, NumberingDefinition> numbering,
        List<ReadWarning> warnings)
    {
        var numId = numPr.Element(W + "numId")?.Attribute(W + "val")?.Value;

        // numId 0 removes the numbering (e.g. inherited from the paragraph style).
        if (numId == "0")
        {
            return null;
        }

        var level = 0;
        if (numPr.Element(W + "ilvl")?.Attribute(W + "val") is { } ilvl
            && int.TryParse(ilvl.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0)
        {
            level = parsed;
        }

        if (numId is null)
        {
            return null;
        }

        if (numbering.TryGetValue(numId, out var definition))
        {
            return (definition, level);
        }

        // A dangling id is not a list: the paragraph is read without a marker and reported.
        warnings.Add(new ReadWarning($"DOCX: the numbering id '{numId}' has no definition; the list is read as text."));
        return null;
    }

    private static IReadOnlyDictionary<string, NumberingDefinition> ReadNumbering(ZipArchive zip)
    {
        var numbering = ReadXml(zip, DocxConstants.NumberingPart);
        if (numbering is null)
        {
            return new Dictionary<string, NumberingDefinition>();
        }

        // abstractNumId → levels. Every level has its own format, start value and formatting of
        // the number (AJ04); the nesting depth selects the level.
        var levelsByAbstractId = new Dictionary<string, IReadOnlyList<NumberingLevel>>(StringComparer.Ordinal);
        foreach (var entry in numbering.Elements(W + "abstractNum"))
        {
            // Word uses nine levels; a malformed definition with more keeps the first nine.
            var levels = entry.Elements(W + "lvl").Take(NumberingDefinition.MaxLevels).Select(ReadNumberingLevel).ToList();
            if (levels.Count == 0)
            {
                levels.Add(new NumberingLevel());
            }

            levelsByAbstractId[(string?)entry.Attribute(W + "abstractNumId") ?? string.Empty] = levels;
        }

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var result = new Dictionary<string, NumberingDefinition>(StringComparer.Ordinal);
        var fallback = 0;
        foreach (var num in numbering.Elements(W + "num"))
        {
            var numId = num.Attribute(W + "numId")?.Value ?? string.Empty;

            // Duplicate numId entries: the first wins instead of aborting the read.
            if (result.ContainsKey(numId))
            {
                continue;
            }

            var abstractId = num.Element(W + "abstractNumId")?.Attribute(W + "val")?.Value ?? string.Empty;
            var levels = levelsByAbstractId.TryGetValue(abstractId, out var abstractLevels)
                ? new List<NumberingLevel>(abstractLevels)
                : [new NumberingLevel()];
            ApplyLevelOverrides(num, levels);

            var name = "Numbering" + numId;
            if (!NumberingDefinition.IsNcName(name) || !usedNames.Add(name))
            {
                do
                {
                    name = string.Create(CultureInfo.InvariantCulture, $"Numbering{++fallback}");
                }
                while (!usedNames.Add(name));
            }

            result[numId] = new NumberingDefinition(name, levels);
        }

        return result;
    }

    /// <summary>Reads one <c>w:lvl</c> of a numbering definition.</summary>
    /// <summary>The text effects of a run: null when none of the four properties is present.</summary>
    private static TextEffects? EffectsOf(XElement? rPr)
    {
        if (rPr is null)
        {
            return null;
        }

        var found = false;
        var effects = TextEffects.None;
        foreach (var (name, flag) in new[]
        {
            ("outline", TextEffects.Outline), ("shadow", TextEffects.Shadow),
            ("emboss", TextEffects.Emboss), ("imprint", TextEffects.Engrave),
        })
        {
            if (rPr.Element(W + name) is { } element)
            {
                found = true;
                if (IsOn(element))
                {
                    effects |= flag;
                }
            }
        }

        return found ? effects : null;
    }

    private static NumberingLevel ReadNumberingLevel(XElement level)
    {
        var numberFormat = level.Element(W + "numFmt")?.Attribute(W + "val")?.Value;
        var isBullet = numberFormat == "bullet";

        // AJ04: the color and the weight of the number (the run properties of the level).
        var levelProperties = level.Element(W + "rPr");
        return new NumberingLevel
        {
            Kind = isBullet ? ListKind.Bullet : ListKind.Number,
            Format = isBullet ? ListNumberFormat.Decimal : NumberFormatOf(numberFormat),
            Start = NumberOf(level.Element(W + "start")),
            Suffix = level.Element(W + "suff")?.Attribute(W + "val")?.Value switch
            {
                "space" => ListSuffix.Space,
                "nothing" => ListSuffix.Nothing,
                _ => ListSuffix.Tab,
            },
            NumberColor = ParseHexColor(levelProperties?.Element(W + "color")?.Attribute(W + "val")?.Value),
            NumberBold = levelProperties?.Element(W + "b") is not null && IsOn(levelProperties.Element(W + "b")),
        };
    }

    /// <summary>
    /// applies <c>w:lvlOverride</c> of a <c>w:num</c>: a restarted level (<c>w:startOverride</c>)
    /// or a fully overridden level (<c>w:lvl</c>), so a list can restart without a new definition.
    /// </summary>
    private static void ApplyLevelOverrides(XElement num, List<NumberingLevel> levels)
    {
        foreach (var over in num.Elements(W + "lvlOverride"))
        {
            if (over.Attribute(W + "ilvl")?.Value is not { } ilvlText
                || !int.TryParse(ilvlText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ilvl)
                || ilvl is < 0 or >= NumberingDefinition.MaxLevels)
            {
                continue;
            }

            var level = over.Element(W + "lvl");
            var start = over.Element(W + "startOverride");
            if (level is null && start is null)
            {
                continue;
            }

            while (levels.Count <= ilvl)
            {
                levels.Add(levels[^1]);
            }

            levels[ilvl] = level is null
                ? levels[ilvl] with { Start = NumberOf(start) }
                : ReadNumberingLevel(level);
        }
    }

    /// <summary>An optional integer value element (<c>w:start</c>, <c>w:startOverride</c>).</summary>
    private static int NumberOf(XElement? element)
        => element?.Attribute(W + "val")?.Value is { } text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= 1
                ? value
                : 1;

    /// <summary>The number format of a list level (phase AC, AC01).</summary>
    private static ListNumberFormat NumberFormatOf(string? value) => value switch
    {
        "upperRoman" => ListNumberFormat.UpperRoman,
        "lowerRoman" => ListNumberFormat.LowerRoman,
        "upperLetter" => ListNumberFormat.UpperLetter,
        "lowerLetter" => ListNumberFormat.LowerLetter,
        _ => ListNumberFormat.Decimal,
    };

    private static SectionProps ParseSectionProperties(XElement sectPr)
    {
        var pgSz = sectPr.Element(W + "pgSz");
        var width = ParseTwips(pgSz?.Attribute(W + "w")?.Value, PageSize.A4.Width.Points);
        var height = ParseTwips(pgSz?.Attribute(W + "h")?.Value, PageSize.A4.Height.Points);
        var orient = pgSz?.Attribute(W + "orient")?.Value;

        // Word allows negative top/bottom margins (text may overlap the header); the model does
        // not, so they are clamped. A missing or invalid page size falls back to A4.
        var pgMar = sectPr.Element(W + "pgMar");
        var margin = new PageMargins(
            Length.FromPoints(Math.Abs(ParseTwips(pgMar?.Attribute(W + "top")?.Value, 72))),
            Length.FromPoints(Math.Max(0, ParseTwips(pgMar?.Attribute(W + "right")?.Value, 72))),
            Length.FromPoints(Math.Abs(ParseTwips(pgMar?.Attribute(W + "bottom")?.Value, 72))),
            Length.FromPoints(Math.Max(0, ParseTwips(pgMar?.Attribute(W + "left")?.Value, 72))));
        if (width <= 0 || height <= 0)
        {
            (width, height) = (PageSize.A4.Width.Points, PageSize.A4.Height.Points);
        }

        var titlePage = sectPr.Element(W + "titlePg") is { } titlePg && IsOn(titlePg);
        return new SectionProps(
            new PageSize(Length.FromPoints(width), Length.FromPoints(height)),
            margin,
            orient == "landscape" ? PageOrientation.Landscape : PageOrientation.Portrait,
            BandReference(sectPr, "headerReference", "default"),
            BandReference(sectPr, "footerReference", "default"),
            // Word uses the first-page bands only with w:titlePg.
            titlePage ? BandReference(sectPr, "headerReference", "first") : null,
            titlePage ? BandReference(sectPr, "footerReference", "first") : null,
            BandReference(sectPr, "headerReference", "even"),
            BandReference(sectPr, "footerReference", "even"),
            ReadColumns(sectPr),
            ReadStartType(sectPr),
            ReadPageBorders(sectPr),
            BandDistance(pgMar, "header"),
            BandDistance(pgMar, "footer"));
    }

    /// <summary>The header or footer distance of <c>w:pgMar</c>; null for a missing value or Word's default 0.5 in.</summary>
    private static Length? BandDistance(XElement? pgMar, string name)
        => pgMar?.Attribute(W + name)?.Value is { } value
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips)
            && twips >= 0
            && twips != 720
                ? Length.FromPoints(twips / 20.0)
                : null;

    /// <summary>Reads the page border of a section (<c>w:pgBorders</c>, phase AJ, AJ05).</summary>
    private static PageBorders? ReadPageBorders(XElement sectPr)
    {
        var borders = sectPr.Element(W + "pgBorders");
        if (borders is null)
        {
            return null;
        }

        var top = ReadBorder(borders.Element(W + "top"));
        var left = ReadBorder(borders.Element(W + "left"));
        var bottom = ReadBorder(borders.Element(W + "bottom"));
        var right = ReadBorder(borders.Element(W + "right"));
        if (top is null && left is null && bottom is null && right is null)
        {
            return null;
        }

        var space = borders.Element(W + "top")?.Attribute(W + "space")?.Value
            ?? borders.Element(W + "left")?.Attribute(W + "space")?.Value;

        return new PageBorders
        {
            Top = top,
            Left = left,
            Bottom = bottom,
            Right = right,
            Offset = int.TryParse(space, NumberStyles.Integer, CultureInfo.InvariantCulture, out var points)
                ? Length.FromPoints(points)
                : null,
        };
    }

    /// <summary>Reads the break type of a section (<c>w:type</c>).</summary>
    private static SectionStartType ReadStartType(XElement sectPr)
        => sectPr.Element(W + "type")?.Attribute(W + "val")?.Value switch
        {
            "continuous" => SectionStartType.Continuous,
            "oddPage" => SectionStartType.OddPage,
            "evenPage" => SectionStartType.EvenPage,
            _ => SectionStartType.NextPage,
        };

    /// <summary>Reads the number of text columns of a section (phase AC, AC01).</summary>
    private static int? ReadColumns(XElement sectPr)
    {
        var columns = sectPr.Element(W + "cols")?.Attribute(W + "num")?.Value;
        if (!int.TryParse(columns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            return null;
        }

        return count > 1 ? count : null;
    }

    /// <summary>The relationship id of a header/footer reference of the given type.</summary>
    private static string? BandReference(XElement sectPr, string elementName, string type)
    {
        foreach (var reference in sectPr.Elements(W + elementName))
        {
            if ((reference.Attribute(W + "type")?.Value ?? "default") == type)
            {
                return reference.Attribute(R + "id")?.Value;
            }
        }

        return null;
    }

    /// <summary>Twips to points; fallback if no valid value is present.</summary>
    private static double ParseTwips(string? value, double fallbackPoints)
        => double.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var twips)
            ? twips / 20.0
            : fallbackPoints;

    /// <summary>The colors of the Office theme; the theme part of the file overrides them.</summary>
    private static Dictionary<string, string> ReadThemeColors(ZipArchive zip)
    {
        var colors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dk1"] = "000000", ["lt1"] = "FFFFFF", ["dk2"] = "44546A", ["lt2"] = "E7E6E6",
            ["accent1"] = "4472C4", ["accent2"] = "ED7D31", ["accent3"] = "A5A5A5", ["accent4"] = "FFC000",
            ["accent5"] = "5B9BD5", ["accent6"] = "70AD47", ["hlink"] = "0563C1", ["folHlink"] = "954F72",
        };
        try
        {
            var scheme = ReadXml(zip, "word/theme/theme1.xml")?.Descendants(A + "clrScheme").FirstOrDefault();
            foreach (var entry in scheme?.Elements() ?? [])
            {
                var rgb = entry.Element(A + "srgbClr")?.Attribute("val")?.Value ?? entry.Element(A + "sysClr")?.Attribute("lastClr")?.Value;
                if (rgb is { Length: 6 } && rgb.All(char.IsAsciiHexDigit))
                {
                    colors[entry.Name.LocalName] = rgb.ToUpperInvariant();
                }
            }
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException or IOException)
        {
            // A broken theme part: the Office defaults stay.
        }

        return colors;
    }

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
        string? HeaderReference,
        string? FooterReference,
        string? FirstHeaderReference,
        string? FirstFooterReference,
        string? EvenHeaderReference,
        string? EvenFooterReference,
        int? TextColumns,
        SectionStartType StartType,
        PageBorders? PageBorders = null,
        Length? HeaderDistance = null,
        Length? FooterDistance = null);

    /// <summary>
    /// collects blocks and combines consecutive list items of the same numbering into one
    /// list together without rebuilding the list for each item. The level of an item
    /// (<c>w:ilvl</c>) opens a nested list inside the item of the level above.
    /// </summary>
    private sealed class BlockCollector
    {
        private readonly List<Block> _blocks = [];

        /// <summary>The open lists by nesting position.</summary>
        private readonly List<OpenList> _open = [];

        /// <summary>An open list: its items, its nesting position and its level in the numbering.</summary>
        private sealed class OpenList
        {
            public OpenList(NumberingDefinition definition, int level, int sourceLevel)
            {
                Definition = definition;
                Level = level;
                SourceLevel = sourceLevel;
            }

            public NumberingDefinition Definition { get; }

            /// <summary>The nesting position (0 = outermost open list).</summary>
            public int Level { get; }

            /// <summary>The level from <c>w:ilvl</c>, which selects the numbering definition.</summary>
            public int SourceLevel { get; }

            /// <summary>The items of the list; the blocks of an item grow while the nested lists are read.</summary>
            public List<OpenItem> Items { get; } = [];
        }

        /// <summary>An item of an open list; its blocks grow while the nested lists are read.</summary>
        private sealed class OpenItem
        {
            public OpenItem(IReadOnlyList<Block> blocks)
            {
                Blocks = [.. blocks];
            }

            public List<Block> Blocks { get; }
        }

        public void Add(Block block)
        {
            CloseLevels(0);
            _blocks.Add(block);
        }

        /// <summary>
        /// Adds a list item. The level is the depth of the item inside the numbering; the items of
        /// a deeper level become a nested list inside the last item of the level above.
        /// </summary>
        public void AddListItem(NumberingDefinition definition, int level, ListItem item)
        {
            // A level deeper than the open ones has no parent: it becomes the next level.
            var target = Math.Min(level, _open.Count);

            // The levels below the item end here.
            CloseLevels(target + 1);

            if (_open.Count > target
                && _open[target] is { } open
                && open.Definition.Name == definition.Name)
            {
                open.Items.Add(new OpenItem(item.Blocks));
                return;
            }

            // A different numbering at the level: the list of the level ends here.
            CloseLevels(target);

            var list = new OpenList(definition, target, level);
            list.Items.Add(new OpenItem(item.Blocks));
            _open.Add(list);
        }

        public List<Block> ToList()
        {
            CloseLevels(0);
            return _blocks;
        }

        /// <summary>
        /// Closes the levels from the given one on: their lists become nested lists inside the
        /// last item of the level above, the outermost one a block of its own.
        /// </summary>
        private void CloseLevels(int from)
        {
            while (_open.Count > from)
            {
                var list = _open[^1];
                _open.RemoveAt(_open.Count - 1);

                var items = list.Items.Select(item => new ListItem(item.Blocks)).ToList();
                var block = new ListBlock(list.Definition.Name, items)
                {
                    // w:ilvl selects the level; keep it as the minimum nesting depth.
                    NestingLevel = Math.Clamp(list.SourceLevel + 1, 1, ListBlock.MaxNestingLevel),
                };

                if (list.Level == 0 || _open.Count == 0)
                {
                    _blocks.Add(block);
                    continue;
                }

                // The nested list belongs to the last item of the list above.
                var parent = _open[^1];
                if (parent.Items.Count == 0)
                {
                    _blocks.Add(block);
                    continue;
                }

                parent.Items[^1].Blocks.Add(block);
            }
        }
    }
}
