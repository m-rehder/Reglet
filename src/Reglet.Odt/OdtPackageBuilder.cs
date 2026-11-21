using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// builds the ODT package from a <see cref="PortableDocument"/>. The individual components
/// (content, styles, meta, settings, images) come in the following steps.
/// </summary>
internal static class OdtPackageBuilder
{
    public static void Build(
        PortableDocument document,
        Stream output,
        OdtWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        cancellationToken.ThrowIfCancellationRequested();
        DocumentLimits.Enforce(document, options.Limits);

        ReportUnsupported(document, handler);

        using var package = new OdtPackageWriter(output, options.Encryption);

        var preservedBlocks = ComputePreservedBlocks(
            document, SourceStylesPreserved(document), out var sourceStyles, out var coveredBlocks);

        // A block written from its source XML does not need a generated image part (AF12).
        var blocksWrittenFromSource = new HashSet<Block>(ReferenceEqualityComparer.Instance);
        if (preservedBlocks is not null)
        {
            blocksWrittenFromSource.UnionWith(preservedBlocks.Keys);
        }

        blocksWrittenFromSource.UnionWith(coveredBlocks);
        var images = OdtImageResources.Register(package, document, options.Limits, blocksWrittenFromSource, cancellationToken);
        var styleNames = sourceStyles.Elements.Select(element => (string?)element.Attribute(XName.Get("name", OdtNamespaces.Style)))
            .Where(name => name is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var resource in images.Values)
        {
            var baseName = resource.StyleName;
            var suffix = 2;
            while (!styleNames.Add(resource.StyleName)) resource.StyleName = baseName + "_" + suffix++;
        }
        var embeddedFonts = OdtEmbeddedFonts.Embed(package, document, options.EmbeddedFonts, options.InvalidXmlCharacters, handler);
        OdtContentWriter.Write(
            package,
            document,
            images,
            options.InvalidXmlCharacters,
            cancellationToken,
            options.Progress,
            preservedBlocks,
            sourceStyles.ParagraphStart,
            sourceStyles.TextStart,
            sourceStyles.TableNameStart,
            sourceStyles.CellStyleStart,
            sourceStyles.RowStyleStart,
            sourceStyles.TableStyleStart,
            sourceStyles.TableBreakStart,
            sourceStyles.Elements,
            coveredBlocks,
            embeddedFonts);

        // AF13: styles.xml is adopted when the model adds no named styles, the defaults and the
        // section layout/bands are unchanged; otherwise it is regenerated.
        var stylesFromSource = SourceStylesPreserved(document) && TryPreserveSourcePart(package, document, "styles.xml", options.AutoHyphenation);
        if (!stylesFromSource)
        {
            OdtStylesWriter.Write(package, document, images, options.InvalidXmlCharacters, options.AutoHyphenation, embeddedFonts);
        }

        OdtMetaWriter.Write(package, document, options.InvalidXmlCharacters);

        // AF13: settings.xml is independent of the model, so a source one is kept verbatim.
        if (!TryPreserveSourcePart(package, document, "settings.xml"))
        {
            OdtSettingsWriter.Write(package, document);
        }

        // AF11: carry over source package parts the builder does not generate (unknown custom parts, …)
        // byte-identically and list them in the regenerated manifest. Pictures and objects only while source
        // XML written again points to them (ADR-0036).
        var sourceXml = (preservedBlocks?.Values ?? []).Concat(sourceStyles.Elements);
        PreserveSourceParts(package, document, sourceXml, stylesFromSource, handler);
    }

    /// <summary>
    /// AF12/AF13: computes the source <c>content.xml</c> block elements that may be re-emitted
    /// verbatim for a changed body — top-level blocks that are still the reader's instances
    /// (reference equality) and whose references (styles, images, links) survive the rewrite.
    /// A paragraph with inline frames contributes its paragraph and image blocks together, so the
    /// source element is written once and the derived blocks are skipped. Supports several
    /// sections and insertions or deletions in the block list.
    /// </summary>
    private static IReadOnlyDictionary<Block, XElement>? ComputePreservedBlocks(
        PortableDocument document,
        bool stylesPreserved,
        out SourceStyles sourceStyles,
        out IReadOnlySet<Block> coveredBlocks)
    {
        sourceStyles = EmptyStyles;
        coveredBlocks = new HashSet<Block>(ReferenceEqualityComparer.Instance);
        if (document.SourcePackage is not { IsEmpty: false } bytes
            || document.SourceSections is not { Count: > 0 } sourceSections
            || document.SourceBlockOrigins is not { Count: > 0 } origins)
        {
            return null;
        }

        var sourceBlocks = sourceSections.SelectMany(section => section.Blocks).ToList();
        var currentBlocks = document.Sections.SelectMany(section => section.Blocks).ToList();
        if (sourceBlocks.Count == 0)
        {
            return null;
        }

        List<XElement> elements;
        SourceStyles styles;
        IReadOnlySet<string> sourceParts;
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.GetEntry("content.xml") is not { } entry)
            {
                return null;
            }

            XNamespace office = OdtNamespaces.Office;
            XNamespace text = OdtNamespaces.Text;
            var root = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Root;
            var body = root?.Element(office + "body")?.Element(office + "text");
            if (body is null)
            {
                return null;
            }

            var bodyElements = body.Elements().ToList();
            var sectionElements = bodyElements.Where(element => element.Name == text + "section").ToList();
            if (sectionElements.Count == sourceSections.Count
                && bodyElements.All(element => element.Name == text + "section" || element.Name == text + "user-field-decls"))
            {
                elements = sectionElements.SelectMany(element => element.Elements()).ToList();
            }
            else if (sourceSections.Count == 1)
            {
                var container = sectionElements.FirstOrDefault() ?? body;
                elements = container.Elements()
                    .Where(element => element.Name != text + "user-field-decls")
                    .ToList();
            }
            else
            {
                return null;
            }

            sourceParts = archive.Entries.Select(item => item.FullName).ToHashSet(StringComparer.Ordinal);
            styles = ReadStyleInfo(root?.Element(office + "automatic-styles"), elements, stylesPreserved);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException or DocumentValidationException)
        {
            return null;
        }

        // The blocks the reader produced from one source element: a paragraph with inline frames
        // yields the paragraph and the images hoisted from its frames.
        var sourceGroups = new Dictionary<int, List<Block>>();
        foreach (var block in sourceBlocks)
        {
            if (!origins.TryGetValue(block, out var origin) || origin < 0 || origin >= elements.Count)
            {
                continue;
            }

            if (!sourceGroups.TryGetValue(origin, out var group))
            {
                sourceGroups[origin] = group = [];
            }

            group.Add(block);
        }

        if (sourceGroups.Count == 0)
        {
            return null;
        }

        // The list styles the writer regenerates: one per numbering definition a list uses
        // (OF-B01). A preserved block may only reference one of them, otherwise its style
        // reference would dangle.
        var listStyles = new HashSet<string>(StringComparer.Ordinal);
        var numbering = new NumberingLookup(document.Numbering);
        foreach (var list in document.Sections.SelectMany(section => ListScan.Collect(section.Blocks)))
        {
            listStyles.Add(OdtListStyles.StyleName(numbering.Definition(list)));
        }

        var currentPositions = new Dictionary<Block, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < currentBlocks.Count; index++)
        {
            if (!currentPositions.TryAdd(currentBlocks[index], index))
            {
                currentPositions[currentBlocks[index]] = -1; // a block may not be written twice
            }
        }

        var result = new Dictionary<Block, XElement>(ReferenceEqualityComparer.Instance);
        var covered = new HashSet<Block>(ReferenceEqualityComparer.Instance);
        var handled = new HashSet<int>();
        foreach (var block in currentBlocks)
        {
            if (!origins.TryGetValue(block, out var origin)
                || origin < 0
                || origin >= elements.Count
                || !sourceGroups.TryGetValue(origin, out var group)
                || !handled.Add(origin))
            {
                continue;
            }

            // The whole group must still be present exactly once, contiguous and in source order;
            // otherwise the source element and the model disagree and both are regenerated.
            var positions = new int[group.Count];
            var complete = true;
            for (var index = 0; index < group.Count; index++)
            {
                if (!currentPositions.TryGetValue(group[index], out var position) || position < 0)
                {
                    complete = false;
                    break;
                }

                positions[index] = position;
            }

            if (!complete)
            {
                continue;
            }

            var first = positions[0];
            var ordered = true;
            for (var index = 1; index < positions.Length; index++)
            {
                if (positions[index] != first + index)
                {
                    ordered = false;
                    break;
                }
            }

            if (!ordered || !IsSelfContained(elements[origin], styles.Names, listStyles, sourceParts, stylesPreserved))
            {
                continue;
            }

            var primary = currentBlocks[first];
            result[primary] = elements[origin];
            for (var index = 1; index < positions.Length; index++)
            {
                covered.Add(currentBlocks[first + index]);
            }
        }

        if (result.Count == 0)
        {
            return null;
        }

        sourceStyles = styles;
        coveredBlocks = covered;
        return result;
    }

    /// <summary>
    /// Reads the source automatic paragraph (`P`)/text (`T`) styles that a kept block may
    /// reference: their elements are preserved and the generated names start above the maxima.
    /// </summary>
    private static SourceStyles ReadStyleInfo(XElement? automaticStyles, IEnumerable<XElement> elements, bool stylesPreserved)
    {
        XNamespace style = OdtNamespaces.Style;
        XNamespace table = OdtNamespaces.Table;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var styleElements = new List<XElement>();
        var paragraphStart = 0;
        var textStart = 0;
        var cellStyleStart = 0;
        var rowStyleStart = 0;
        var tableStyleStart = 0;
        var tableBreakStart = 0;
        if (automaticStyles is not null)
        {
            // A graphic style may inherit from a named style in styles.xml. Only then may a
            // preserved drawing reference it; otherwise the parent would be missing.
            var automaticNames = automaticStyles.Elements(style + "style")
                .Select(element => (string?)element.Attribute(style + "name"))
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            foreach (var element in automaticStyles.Elements(style + "style"))
            {
                var name = (string?)element.Attribute(style + "name");
                if (name is null)
                {
                    continue;
                }

                if (element.Attribute(style + "family")?.Value == "graphic")
                {
                    var parent = (string?)element.Attribute(style + "parent-style-name");
                    if (parent is null || stylesPreserved || automaticNames.Contains(parent))
                    {
                        names.Add(name);
                        styleElements.Add(element);
                    }

                    continue;
                }

                if (NumberedName(name, "P", out var paragraph))
                {
                    names.Add(name);
                    styleElements.Add(element);
                    paragraphStart = Math.Max(paragraphStart, paragraph);
                }
                else if (NumberedName(name, "T", out var text))
                {
                    names.Add(name);
                    styleElements.Add(element);
                    textStart = Math.Max(textStart, text);
                }
                else if (NumberedName(name, "TC", out var cell))
                {
                    names.Add(name);
                    styleElements.Add(element);
                    cellStyleStart = Math.Max(cellStyleStart, cell);
                }
                else if (NumberedName(name, "TR", out var row))
                {
                    names.Add(name);
                    styleElements.Add(element);
                    rowStyleStart = Math.Max(rowStyleStart, row);
                }
                else if (NumberedName(name, "TT", out var tableStyle))
                {
                    names.Add(name);
                    styleElements.Add(element);
                    tableStyleStart = Math.Max(tableStyleStart, tableStyle);
                }
                else if (NumberedName(name, "TB", out var tableBreak))
                {
                    names.Add(name);
                    styleElements.Add(element);
                    tableBreakStart = Math.Max(tableBreakStart, tableBreak);
                }
            }
        }

        // Generated table names (Table1, …) start above the source ones, so a preserved table keeps
        // its name without a duplicate.
        var tableNameStart = 0;
        foreach (var element in elements.SelectMany(item => item.DescendantsAndSelf(table + "table")))
        {
            if (NumberedName((string?)element.Attribute(table + "name") ?? string.Empty, "Table", out var number))
            {
                tableNameStart = Math.Max(tableNameStart, number);
            }
        }

        return new SourceStyles(paragraphStart, textStart, cellStyleStart, rowStyleStart, tableStyleStart, tableBreakStart, tableNameStart, names, styleElements);
    }

    private static bool NumberedName(string name, string prefix, out int number)
    {
        number = 0;
        return name.Length > prefix.Length
            && name.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsSelfContained(
        XElement element,
        IReadOnlySet<string> sourceStyleNames,
        IReadOnlySet<string> listStyles,
        IReadOnlySet<string> sourceParts,
        bool stylesPreserved)
    {
        XNamespace text = OdtNamespaces.Text;
        XNamespace table = OdtNamespaces.Table;
        XNamespace draw = OdtNamespaces.Draw;
        XNamespace xlink = OdtNamespaces.XLink;
        XNamespace svg = OdtNamespaces.Svg;
        foreach (var node in element.DescendantsAndSelf())
        {
            // Tracked changes are modelled (ADR-0027): the regions and their markers are written from the model.
            if (node.Name == text + "change-start" || node.Name == text + "change-end" || node.Name == text + "change")
            {
                return false;
            }

            foreach (var attribute in node.Attributes())
            {
                if (attribute.Name.Namespace == draw)
                {
                    // A graphic style name is a reference; the other drawing attributes (name,
                    // transform, layer, …) are data that survives verbatim.
                    if (attribute.Name.LocalName == "style-name"
                        && !sourceStyleNames.Contains(attribute.Value)
                        && !stylesPreserved)
                    {
                        return false;
                    }

                    continue;
                }

                if (attribute.Name.Namespace == xlink)
                {
                    // An image or linked part must be in the source package and carried over;
                    // an external URL is independent of the package. A '#bookmark' target is not
                    // preserved because it may point into a regenerated block.
                    if (attribute.Name.LocalName == "href" && !ReferenceSurvives(attribute.Value, sourceParts))
                    {
                        return false;
                    }

                    continue;
                }

                if (attribute.Name.Namespace == svg)
                {
                    // Geometry and text of a drawing (width, height, x, y, …) is data.
                    continue;
                }

                if (attribute.Name.Namespace == table)
                {
                    // Model-derived table attributes and the fixed column/cell styles are safe.
                    var tableLocal = attribute.Name.LocalName;
                    if (tableLocal == "name" && node.Name == table + "table")
                    {
                        continue;
                    }

                    if (tableLocal == "style-name" && node.Name == table + "table"
                        && sourceStyleNames.Contains(attribute.Value))
                    {
                        continue;
                    }

                    if (tableLocal == "style-name" && node.Name == table + "table-column" && IsColumnStyle(attribute.Value))
                    {
                        continue;
                    }

                    if (tableLocal == "style-name" && node.Name == table + "table-cell"
                        && (IsFixedCellStyle(attribute.Value) || sourceStyleNames.Contains(attribute.Value)))
                    {
                        continue;
                    }

                    if (tableLocal == "style-name" && node.Name == table + "table-row"
                        && sourceStyleNames.Contains(attribute.Value))
                    {
                        continue;
                    }

                    if ((tableLocal == "number-columns-spanned" || tableLocal == "number-rows-spanned")
                        && node.Name == table + "table-cell")
                    {
                        continue;
                    }

                    return false;
                }

                if (attribute.Name.Namespace != text)
                {
                    continue;
                }

                var local = attribute.Name.LocalName;

                // A drawing stores its anchor in the text namespace.
                if (local is "anchor-type" or "anchor-page-number")
                {
                    continue;
                }

                // A plain heading is safe: the writer always emits Heading1..Heading6.
                if (node.Name == text + "h" && local == "outline-level")
                {
                    continue;
                }

                // List styles are fixed names the writer always emits; the start value is model data.
                if (local == "style-name" && node.Name == text + "list" && listStyles.Contains(attribute.Value))
                {
                    continue;
                }

                if (local == "start-value" && node.Name == text + "list-item")
                {
                    continue;
                }

                // Automatic paragraph/text styles are preserved with the source names.
                if (local == "style-name"
                    && (node.Name == text + "p" || node.Name == text + "span" || node.Name == text + "h")
                    && (sourceStyleNames.Contains(attribute.Value)
                        || (node.Name == text + "h" && IsHeadingStyle(attribute.Value))))
                {
                    continue;
                }

                return false;
            }
        }

        return true;
    }

    /// <summary>The parts the writer generates itself; a kept block may not reference them.</summary>
    private static readonly HashSet<string> GeneratedParts = new(StringComparer.Ordinal)
    {
        "mimetype", "content.xml", "styles.xml", "meta.xml", "settings.xml", "META-INF/manifest.xml",
    };

    /// <summary>
    /// Whether a drawing or link reference survives the rewrite: an external URL always does, an
    /// embedded part only when the source package contains it and the writer does not replace it.
    /// </summary>
    private static bool ReferenceSurvives(string value, IReadOnlySet<string> sourceParts)
    {
        if (value.Length == 0)
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return true;
        }

        var path = Uri.UnescapeDataString(value);
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return sourceParts.Contains(path) && !GeneratedParts.Contains(path);
    }

    private static bool IsHeadingStyle(string name)
        => name is "Heading1" or "Heading2" or "Heading3" or "Heading4" or "Heading5" or "Heading6";

    private static bool IsColumnStyle(string name)
        => name.StartsWith("co", StringComparison.Ordinal) && name.EndsWith("cm", StringComparison.Ordinal);

    private static bool IsFixedCellStyle(string name)
        => name is "Cell" or "CellR" or "CellU" or "CellRU";

    private sealed record SourceStyles(
        int ParagraphStart,
        int TextStart,
        int CellStyleStart,
        int RowStyleStart,
        int TableStyleStart,
        int TableBreakStart,
        int TableNameStart,
        IReadOnlySet<string> Names,
        IReadOnlyList<XElement> Elements);

    private static readonly SourceStyles EmptyStyles = new(0, 0, 0, 0, 0, 0, 0, new HashSet<string>(), []);

    /// <summary>
    /// Whether the source styles part may be adopted: the model adds no named styles, the defaults
    /// are unchanged and the section layout/bands are unchanged (they are written into styles.xml).
    /// </summary>
    private static bool SourceStylesPreserved(PortableDocument document)
        => (document.Styles.Count == 0
            || NamedStyleComparer.Unchanged(document.Styles, document.SourceStyles))
            && NamedStyleComparer.CharacterStylesUnchanged(document.CharacterStyles, document.SourceCharacterStyles)
            && document.Defaults == document.SourceDefaults
            && SectionsLayoutUnchanged(document);

    private static bool SectionsLayoutUnchanged(PortableDocument document)
    {
        if (document.SourceSections is not { } source || source.Count != document.Sections.Count)
        {
            return false;
        }

        for (var index = 0; index < source.Count; index++)
        {
            var before = source[index];
            var after = document.Sections[index];
            if (before.PageSize != after.PageSize
                || before.Margins != after.Margins
                || before.Orientation != after.Orientation
                || before.TextColumns != after.TextColumns
                || before.PageNumberStart != after.PageNumberStart
                || !Equals(before.Header, after.Header)
                || !Equals(before.Footer, after.Footer)
                || !Equals(before.FirstHeader, after.FirstHeader)
                || !Equals(before.FirstFooter, after.FirstFooter)
                || !Equals(before.EvenHeader, after.EvenHeader)
                || !Equals(before.EvenFooter, after.EvenFooter))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes a source part verbatim if the source is an ODT and contains it.</summary>
    private static bool TryPreserveSourcePart(
        OdtPackageWriter package,
        PortableDocument document,
        string partName,
        bool autoHyphenation = false)
    {
        if (document.SourcePackage is not { IsEmpty: false } bytes)
        {
            return false;
        }

        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var source = new ZipArchive(stream, ZipArchiveMode.Read);
        if (source.GetEntry("content.xml") is null || source.GetEntry(partName) is not { } entry)
        {
            return false;
        }

        var data = TemplatePackage.ReadEntry(entry);
        if (autoHyphenation && partName == "styles.xml")
        {
            var styles = ReaderGuard.LoadXml(data);
            OdtStylesWriter.EnsureHyphenation(styles);
            data = TemplatePackage.SaveXml(styles);
        }

        var mediaTypes = ReadManifestMediaTypes(source);
        var mediaType = mediaTypes.TryGetValue(partName, out var value) ? value : OdtNamespaces.XmlContentType;
        package.AddBinaryPart(partName, mediaType, data);
        return true;
    }

    private static void PreserveSourceParts(
        OdtPackageWriter package,
        PortableDocument document,
        IEnumerable<XElement> sourceXml,
        bool stylesFromSource,
        UnsupportedFeatureHandler handler)
    {
        if (document.SourcePackage is not { IsEmpty: false } bytes)
        {
            return;
        }

        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var source = new ZipArchive(stream, ZipArchiveMode.Read);

        // Only a source package of this writer's own format may be reused.
        if (source.GetEntry("content.xml") is null)
        {
            return;
        }

        var mediaTypes = ReadManifestMediaTypes(source);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var xml = sourceXml.ToList();
        if (stylesFromSource && source.GetEntry("styles.xml") is { } stylesEntry)
        {
            xml.Add(ReaderGuard.LoadXml(TemplatePackage.ReadEntry(stylesEntry)).Root!);
        }

        foreach (var href in xml.SelectMany(element => element.DescendantsAndSelf())
            .Select(element => (string?)element.Attribute(XName.Get("href", OdtNamespaces.XLink)))
            .OfType<string>())
        {
            referenced.Add(PackagePart(href));
        }

        var macrosReported = false;
        foreach (var entry in source.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.EndsWith('/')
                || name is "mimetype" or "META-INF/manifest.xml"
                || package.HasPart(name))
            {
                continue;
            }

            // A signature does not cover the changed document.
            if (OdtPreservedPackageWriter.IsSignature(name))
            {
                handler.Report("ODT: the signature of the source document is removed; it does not cover the changed document.");
                continue;
            }

            // Macros are never carried into a changed document unasked (as in DOCX, ADR-0028).
            if (OdtPreservedPackageWriter.IsMacro(name))
            {
                if (!macrosReported)
                {
                    handler.Report("ODT: the macros of the source document are removed.");
                    macrosReported = true;
                }

                continue;
            }

            // Pictures and embedded objects come from the model; a source copy nothing points to any more would
            // keep removed content in the package (ADR-0036).
            if (IsModelPart(name) && !referenced.Any(target => name == target || name.StartsWith(target + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            var mediaType = mediaTypes.TryGetValue(name, out var value) ? value : "application/octet-stream";
            package.AddBinaryPart(name, mediaType, TemplatePackage.ReadEntry(entry));
        }
    }

    /// <summary>Pictures, object replacement images and embedded objects ("Object 1/content.xml").</summary>
    private static bool IsModelPart(string name)
        => name.StartsWith("Pictures/", StringComparison.Ordinal)
            || name.StartsWith("ObjectReplacements/", StringComparison.Ordinal)
            || name.StartsWith("Object ", StringComparison.Ordinal);

    /// <summary>The package path of a local link ("./Object 1" → "Object 1", "Pictures/a%20b.png" → "Pictures/a b.png").</summary>
    private static string PackagePart(string href)
    {
        var path = Uri.UnescapeDataString(href);
        return (path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path).TrimEnd('/');
    }

    private static Dictionary<string, string> ReadManifestMediaTypes(ZipArchive source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source.GetEntry("META-INF/manifest.xml") is not { } entry)
        {
            return result;
        }

        var manifest = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry));
        XNamespace ns = OdtNamespaces.Manifest;
        foreach (var fileEntry in manifest.Root?.Elements(ns + "file-entry") ?? Enumerable.Empty<XElement>())
        {
            var path = (string?)fileEntry.Attribute(ns + "full-path");
            var mediaType = (string?)fileEntry.Attribute(ns + "media-type");
            if (!string.IsNullOrEmpty(path) && mediaType is not null)
            {
                result[path] = mediaType;
            }
        }

        return result;
    }

    private static bool HasFormField(IReadOnlyList<Block> blocks)
        => blocks.Any(block => block is FormField
            || block is TableBlock table && table.Rows.Any(row => row.Cells.Any(cell => HasFormField(cell.Blocks))));

    private static void ReportUnsupported(PortableDocument document, UnsupportedFeatureHandler handler)
    {
        var sectionMessages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in OdtForms.Collect(document).Keys)
        {
            if (field.Required || field.FontSize is not null)
            {
                handler.Report($"ODT: 'Required' and 'FontSize' of the form field '{field.Name}' are not written (a form control has neither).");
            }
        }

        if (CommentScan.Collect(document).Elsewhere > 0)
        {
            handler.Report("ODT: a comment in a header, footer or note is not written.");
        }

        if (OdtEmbeddedObjects.HasUnstorable(document))
        {
            handler.Report("ODT: an embedded Open XML package object cannot be stored and is not written.");
        }

        foreach (var message in OdtRevisions.MakePlan(document).Messages.Distinct(StringComparer.Ordinal))
        {
            handler.Report(message);
        }

        for (var index = 0; index < document.Sections.Count; index++)
        {
            var section = document.Sections[index];
            foreach (var (band, label) in new[] { (section.Header, "header"), (section.Footer, "footer"),
                (section.FirstHeader, "first header"), (section.FirstFooter, "first footer"),
                (section.EvenHeader, "even header"), (section.EvenFooter, "even footer") })
            {
                if (band is not null && ContainsPageBreak(band.Blocks))
                {
                    handler.Report($"ODT: page breaks in the {label} are omitted.");
                }

                if (band is not null && OdtCharts.HasChart(band.Blocks))
                {
                    handler.Report($"ODT: a chart in the {label} is not written.");
                }

                if (band is not null && OdtEmbeddedObjects.Has(band.Blocks))
                {
                    handler.Report($"ODT: an embedded object in the {label} is not written.");
                }

                if (band is not null && OdtMath.Has(band.Blocks))
                {
                    handler.Report($"ODT: an equation in the {label} is not written.");
                }

                if (band is not null && HasFormField(band.Blocks))
                {
                    handler.Report($"ODT: form fields in the {label} are not written.");
                }
            }
        }

        // A page-number start in the first section needs a block that can carry the master page
        // (a paragraph or table style). Later sections carry it on their section break.
        if (document.Sections.Count > 0
            && document.Sections[0].PageNumberStart is not null
            && !OdtBreakPlan.Create(document).IsPageNumberStartApplied(1))
        {
            sectionMessages.Add("ODT: the starting page number of the first section is not rendered because it has no block that can carry it.");
        }

        foreach (var message in sectionMessages)
        {
            handler.Report(message);
        }

        // OF-B04: a table cell has no rounded corners in ODF.
        if (TableScan.Enumerate(document).Any(table => table.CornerRadius is { Points: > 0 }))
        {
            handler.Report("ODT: rounded table corners are not supported; the table is written with square corners.");
        }

        // ODF has one fo:background-color for text: a highlight and a shading cannot coexist.
        if (OdtAutomaticStyles.HasDistinctHighlightAndBackground(document))
        {
            handler.Report(
                "ODT: text highlight and text background share fo:background-color; the highlight wins and the background is not preserved.");
        }

        // A general field code without an ODF element keeps only its result as text (OF-C02).
        var fieldMessages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in FieldScan.Collect(document).Where(field => OdtFields.Map(field.Instruction) is null))
        {
            fieldMessages.Add($"ODT: the field '{field.Instruction}' is written as text; ODF has no element for it.");
        }

        foreach (var message in fieldMessages)
        {
            handler.Report(message);
        }

        var noteMessages = new HashSet<string>(StringComparer.Ordinal);
        // Only a band that is written from the model loses its note: an adopted source styles.xml
        // keeps the text:note of its master pages as it is.
        if (!SourceStylesPreserved(document) && NoteScan.CollectBands(document).Count > 0)
        {
            noteMessages.Add(
                "ODT: a note in a header or footer is written as text; LibreOffice shows no notes in a band.");
        }

        // A note of the body is written as text:note, which carries paragraphs and headings only.
        // The text of a band note is flattened instead, so only the pictures of such a note are
        // not drawn; the band note itself is reported below.
        foreach (var note in NoteScan.CollectBody(document))
        {
            foreach (var block in note.Blocks)
            {
                if (block is not (Paragraph or Heading))
                {
                    noteMessages.Add($"ODT: {block.GetType().Name} in a note is not rendered.");
                }
            }
        }

        foreach (var message in noteMessages)
        {
            handler.Report(message);
        }

        // OF-C03: a link or a page reference whose bookmark is missing or sits in a band is
        // written, but the jump leads nowhere; Word and LibreOffice show an error for it.
        foreach (var reference in ReferenceScan.Find(document))
        {
            handler.Report($"ODT: {reference.Message}");
        }
    }

    private static bool ContainsPageBreak(IEnumerable<Block> blocks)
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
}
