using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// writes a <see cref="PortableDocument"/> based on an ODT template: the package of the
/// template is adopted, the <c>{{Name}}</c> placeholders in body and header/footer are
/// bound, and for inserted content automatic styles, images and
/// manifest entries are added. Named styles and page design of the
/// template are preserved.
/// </summary>
internal static class OdtTemplateWriter
{
    private static readonly XNamespace Office = OdtNamespaces.Office;
    private static readonly XNamespace Text = OdtNamespaces.Text;
    private static readonly XNamespace Style = OdtNamespaces.Style;
    private static readonly XNamespace Table = OdtNamespaces.Table;
    private static readonly XNamespace Manifest = OdtNamespaces.Manifest;
    private static readonly XNamespace Fo = OdtNamespaces.Fo;
    private static readonly XNamespace Draw = OdtNamespaces.Draw;
    private static readonly XNamespace Svg = OdtNamespaces.Svg;
    private static readonly XNamespace XLink = OdtNamespaces.XLink;
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ContentPart = "content.xml";
    private const string StylesPart = "styles.xml";
    private const string ManifestPart = "META-INF/manifest.xml";

    /// <summary>Namespaces of the ODF fragments.</summary>
    private static readonly (string Prefix, string Namespace)[] FragmentNamespaces =
    [
        ("office", OdtNamespaces.Office),
        ("text", OdtNamespaces.Text),
        ("table", OdtNamespaces.Table),
        ("style", OdtNamespaces.Style),
        ("fo", OdtNamespaces.Fo),
        ("draw", OdtNamespaces.Draw),
        ("svg", OdtNamespaces.Svg),
        ("xlink", OdtNamespaces.XLink),
    ];

    public static void Build(
        PortableDocument document,
        DocumentTemplate template,
        Stream output,
        OdtWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            BuildCore(document, template, output, options, handler, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new DocumentValidationException("The ODT template is not a valid ODF package.", exception);
        }
        catch (XmlException exception)
        {
            throw new DocumentValidationException("The ODT template contains invalid XML.", exception);
        }
    }

    private static void BuildCore(
        PortableDocument document,
        DocumentTemplate template,
        Stream output,
        OdtWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        DocumentLimits.Enforce(document, options.Limits);
        cancellationToken.ThrowIfCancellationRequested();
        template.ValidateTypes();

        using var templateStream = new MemoryStream(template.RawData.ToArray(), writable: false);
        using var source = new ZipArchive(templateStream, ZipArchiveMode.Read, leaveOpen: true);

        var contentEntry = source.GetEntry(ContentPart)
            ?? throw new DocumentValidationException("The ODT template contains no content.xml.");
        var content = TemplatePackage.LoadXml(TemplatePackage.ReadEntry(contentEntry));

        var bodyBlocks = document.Sections.SelectMany(section => section.Blocks).ToList();
        var bindings = new Dictionary<string, TemplateContent>(template.Placeholders, StringComparer.Ordinal);
        IReadOnlyList<XElement>? bodySections = null;
        var perSection = false;
        if (template.BodyPlaceholder is { } bodyName)
        {
            if (document.Sections.Count > 1)
            {
                var body = Body(content);
                var sections = body.Elements(Text + "section").ToList();
                perSection = sections.Count == document.Sections.Count
                    && sections.All(section => ContainsPlaceholder(section, bodyName));
                if (perSection)
                {
                    bodySections = sections;
                }
            }

            if (perSection)
            {
                // The body placeholder occurs once per template section; each occurrence receives
                // the blocks of the model section at the same position (S02/S03).
                bindings.Remove(bodyName);
            }
            else
            {
                bindings[bodyName] = TemplateContent.FromBlocks(bodyBlocks);
                if (document.Sections.Count > 1)
                {
                    handler.Report("ODT: multiple sections in the template are merged into one area.");
                }
            }
        }

        // All blocks that are produced (for styles, images, table names).
        var insertedBlocks = new List<Block>();
        foreach (var contentValue in bindings.Values)
        {
            if (contentValue.Kind == TemplateContentKind.Block)
            {
                insertedBlocks.AddRange(contentValue.Blocks);
            }
            else
            {
                insertedBlocks.Add(new Paragraph(contentValue.Inlines));
            }
        }

        if (template.BodyPlaceholder is null || perSection)
        {
            insertedBlocks.AddRange(bodyBlocks);
        }

        if (TableScan.Enumerate(insertedBlocks).Any(table => table.CornerRadius is { Points: > 0 }))
        {
            handler.Report("ODT: rounded table corners are not supported; the table is written with square corners.");
        }

        var stylesXml = source.GetEntry(StylesPart) is { } stylesEntry ? TemplatePackage.LoadXml(TemplatePackage.ReadEntry(stylesEntry)) : null;
        var stylesChanged = false;
        if (options.AutoHyphenation && stylesXml is not null)
        {
            OdtStylesWriter.EnsureHyphenation(stylesXml);
            stylesChanged = true;
        }

        var availableWidth = TemplateContentWidth(content, stylesXml);

        var additions = new List<(string PartName, string MediaType, byte[] Data)>();
        var images = RegisterImages(source, insertedBlocks, options, additions);

        var (textPrefix, paragraphPrefix) = ChoosePrefixes(content, stylesXml);
        var textStyles = OdtAutomaticStyles.Collect(
            insertedBlocks,
            textPrefix,
            characterStyles: document.CharacterStyles,
            defaultFontSize: document.Defaults.FontSize.Points);
        var paragraphStyles = OdtParagraphStyles.Collect(
            new[] { insertedBlocks },
            paragraphPrefix,
            bands: false,
            defaultFontSize: document.Defaults.FontSize.Points,
            styles: document.Styles,
            sourceStyles: document.SourceStyles);
        paragraphStyles.CharacterStyles = document.CharacterStyles;
        paragraphStyles.BookmarkRanges = BookmarkScan.Collect(document).RangeNames;
        var columnWidths = OdtContentWriter.CollectTemplateColumnWidths(insertedBlocks, availableWidth);
        var needsPageBreakStyle = insertedBlocks.Any(block => ContainsPageBreak(block));

        var styleFragment = XmlFragment.Write(
            writer => OdtContentWriter.WriteTemplateAutomaticStyles(
                writer, textStyles, paragraphStyles, columnWidths, needsPageBreakStyle, options.InvalidXmlCharacters,
                document.Numbering, images, ListScan.Collect(insertedBlocks)),
            FragmentNamespaces).OfType<XElement>().ToList();

        var renameMap = RenameCollidingStyles(content, stylesXml, styleFragment);
        ApplyStyleRenames(styleFragment, renameMap);
        AppendAutomaticStyles(content, styleFragment.Select(element => new XElement(element)).ToList());

        var binder = new OdtTemplateBinder(
            content, textStyles, paragraphStyles, images, availableWidth, options.InvalidXmlCharacters, renameMap,
            template.Conditions, template.Repeats, template.RegionData(), document.Numbering, handler, options.Limits);
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (perSection && template.BodyPlaceholder is { } sectionPlaceholder && bodySections is not null)
        {
            for (var index = 0; index < bodySections.Count; index++)
            {
                var sectionBindings = new Dictionary<string, TemplateContent>(bindings, StringComparer.Ordinal)
                {
                    [sectionPlaceholder] = TemplateContent.FromBlocks(document.Sections[index].Blocks),
                };
                found.UnionWith(binder.Bind(bodySections[index], sectionBindings));
            }
        }
        else
        {
            found.UnionWith(binder.Bind(content, bindings));
        }

        if (template.BodyPlaceholder is { } placeholder && !found.Contains(placeholder))
        {
            handler.Report($"ODT: body placeholder '{{{{{placeholder}}}}}' was not found in the template; the content is appended.");
            binder.Append(content, bodyBlocks);
        }
        else if (template.BodyPlaceholder is null && bodyBlocks.Count > 0)
        {
            binder.Append(content, bodyBlocks);
        }

        // The character styles of the model that the template lacks are added, so the span styles
        // that name them as parent resolve; a style the template defines stays the template's.
        if (stylesXml is not null && AddMissingCharacterStyles(stylesXml, document))
        {
            stylesChanged = true;
        }

        if (stylesXml is not null)
        {
            var bands = stylesXml.Descendants().Where(node => node.Name.Namespace == Style && node.Name.LocalName is
                "header" or "header-left" or "header-first" or "footer" or "footer-left" or "footer-first").ToList();
            var bandBindings = new Dictionary<string, TemplateContent>(template.Placeholders, StringComparer.Ordinal);
            var bandBinder = new OdtTemplateBinder(
                stylesXml, textStyles, paragraphStyles, images, availableWidth, options.InvalidXmlCharacters, renameMap,
                template.Conditions, template.Repeats, template.RegionData(), document.Numbering, handler, options.Limits);
            var modified = false;
            foreach (var band in bands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Concat(band.DescendantNodes().OfType<XText>().Select(text => text.Value)).Contains("{{", StringComparison.Ordinal)) continue;
                found.UnionWith(bandBinder.Bind(band, bandBindings));
                modified = true;
            }
            if (modified)
            {
                AppendAutomaticStyles(stylesXml, styleFragment);
                stylesChanged = true;
            }

            if (!stylesChanged) stylesXml = null;
        }

        ReportMissingBindings(template, found, handler);

        var manifestEntry = source.GetEntry(ManifestPart);
        var manifest = manifestEntry is null ? null : TemplatePackage.LoadXml(TemplatePackage.ReadEntry(manifestEntry));
        if (source.GetEntry(OdtSignatureWriter.SignaturePartName) is not null)
        {
            handler.Report("ODT: the signature of the template is removed; it does not cover the filled document.");
        }

        if (manifest is not null)
        {
            manifest.Root!.Elements().Where(element =>
                (string?)element.Attribute(Manifest + "full-path") is OdtSignatureWriter.SignaturePartName or ThumbnailPart or "Thumbnails/").Remove();
            foreach (var (partName, mediaType, _) in additions)
            {
                manifest.Root!.Add(new XElement(
                    Manifest + "file-entry",
                    new XAttribute(Manifest + "full-path", partName),
                    new XAttribute(Manifest + "media-type", mediaType)));
            }
        }

        WritePackage(source, output, contentEntry, content, stylesXml, manifestEntry, manifest, additions);
    }

    private static void WritePackage(
        ZipArchive source,
        Stream output,
        ZipArchiveEntry contentEntry,
        XDocument content,
        XDocument? styles,
        ZipArchiveEntry? manifestEntry,
        XDocument? manifest,
        IReadOnlyList<(string PartName, string MediaType, byte[] Data)> additions)
    {
        using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        // ODF package rule: mimetype MUST be the first entry and uncompressed.
        WriteEntry(target, "mimetype", Encoding.ASCII.GetBytes(OdtNamespaces.MimeType), CompressionLevel.NoCompression);

        foreach (var entry in source.Entries)
        {
            // A signature does not match the filled document, and the preview shows the empty template.
            if (entry.FullName == "mimetype" || entry.FullName.EndsWith('/') || entry.Name.Length == 0
                || entry.FullName == OdtSignatureWriter.SignaturePartName || entry.FullName == ThumbnailPart)
            {
                continue;
            }

            byte[] data;
            if (entry.FullName == ContentPart)
            {
                data = TemplatePackage.SaveXml(content);
            }
            else if (entry.FullName == StylesPart && styles is not null)
            {
                data = TemplatePackage.SaveXml(styles);
            }
            else if (entry.FullName == ManifestPart && manifest is not null)
            {
                data = TemplatePackage.SaveXml(manifest);
            }
            else
            {
                data = TemplatePackage.ReadEntry(entry);
            }

            WriteEntry(target, entry.FullName, data, CompressionLevel.Optimal);
        }

        foreach (var (partName, _, data) in additions)
        {
            WriteEntry(target, partName, data, CompressionLevel.Optimal);
        }
    }

    private static IReadOnlyDictionary<ImageBlock, OdtImageResource> RegisterImages(
        ZipArchive source,
        IReadOnlyList<Block> blocks,
        OdtWriteOptions options,
        List<(string PartName, string MediaType, byte[] Data)> additions)
    {
        var resources = new Dictionary<ImageBlock, OdtImageResource>();
        var used = new HashSet<string>(source.Entries.Select(entry => entry.FullName), StringComparer.Ordinal);

        foreach (var image in OdtImageResources.EnumerateImages(blocks).Distinct())
        {
            ImageGuard.Check(image, options.Limits);
            var (extension, mediaType) = OdtImageResources.Media(image.MediaType);
            var partName = NextPictureName(used, extension);

            resources[image] = new OdtImageResource(partName, mediaType);
            additions.Add((partName, mediaType, image.RawData.ToArray()));
        }

        return resources;
    }

    private const string ThumbnailPart = "Thumbnails/thumbnail.png";

    private static string NextPictureName(HashSet<string> used, string extension)
    {
        for (var index = 1; ; index++)
        {
            var name = $"Pictures/image{index}.{extension}";
            if (used.Add(name))
            {
                return name;
            }
        }
    }

    /// <summary>Chooses style prefixes that do not yet occur in the template.</summary>
    private static bool AddMissingCharacterStyles(XDocument styles, PortableDocument document)
    {
        var existing = styles.Descendants(Style + "style")
            .Where(element => (string?)element.Attribute(Style + "family") == "text")
            .Select(element => (string?)element.Attribute(Style + "name"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var missing = document.CharacterStyles.Where(style => !existing.Contains(style.Name)).ToList();
        if (missing.Count == 0)
        {
            return false;
        }

        var container = styles.Root?.Element(Office + "styles");
        if (container is null)
        {
            // ODF order: font-face-decls, styles, automatic-styles, master-styles.
            container = new XElement(Office + "styles");
            if (styles.Root?.Element(Office + "font-face-decls") is { } fonts)
            {
                fonts.AddAfterSelf(container);
            }
            else
            {
                styles.Root?.AddFirst(container);
            }
        }

        container.Add(XmlFragment.Write(
            writer => OdtStylesWriter.WriteNamedCharacterStyles(writer, missing, document.Defaults.FontSize.Points),
            FragmentNamespaces).OfType<XElement>());
        return true;
    }

    private static (string Text, string Paragraph) ChoosePrefixes(XDocument content, XDocument? styles)
    {
        var existing = ExistingStyleNames(content);
        if (styles is not null) existing.UnionWith(ExistingStyleNames(styles));

        for (var attempt = 0; ; attempt++)
        {
            var suffix = attempt == 0 ? "PD" : "PD" + attempt.ToString(CultureInfo.InvariantCulture);
            var textPrefix = suffix + "T";
            var paragraphPrefix = suffix + "P";
            if (!existing.Any(name => name.StartsWith(textPrefix, StringComparison.Ordinal))
                && !existing.Any(name => name.StartsWith(paragraphPrefix, StringComparison.Ordinal)))
            {
                return (textPrefix, paragraphPrefix);
            }
        }
    }

    /// <summary>
    /// renames generated styles whose name already occurs in the template, and returns the
    /// mapping (generated name → final name).
    /// </summary>
    private static IReadOnlyDictionary<string, string> RenameCollidingStyles(XDocument content, XDocument? styles, List<XElement> styleFragment)
    {
        var existing = ExistingStyleNames(content);
        if (styles is not null) existing.UnionWith(ExistingStyleNames(styles));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var element in styleFragment)
        {
            if (element.Attribute(Style + "name") is not { } name || !existing.Contains(name.Value))
            {
                continue;
            }

            for (var index = 2; ; index++)
            {
                var candidate = name.Value + "_PD" + (index == 2 ? string.Empty : index.ToString(CultureInfo.InvariantCulture));
                if (existing.Add(candidate))
                {
                    map[name.Value] = candidate;
                    name.Value = candidate;
                    break;
                }
            }
        }

        return map;
    }

    private static void ApplyStyleRenames(List<XElement> styleFragment, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0)
        {
            return;
        }

        foreach (var element in styleFragment)
        {
            foreach (var attribute in element.DescendantsAndSelf().Attributes())
            {
                if (attribute.Name.LocalName == "style-name" && map.TryGetValue(attribute.Value, out var mapped))
                {
                    attribute.Value = mapped;
                }
            }
        }
    }

    private static HashSet<string> ExistingStyleNames(XDocument content)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var automaticStyles = content.Root?.Element(Office + "automatic-styles");
        if (automaticStyles is not null)
        {
            foreach (var element in automaticStyles.Elements())
            {
                if (element.Attribute(Style + "name") is { } name)
                {
                    names.Add(name.Value);
                }
            }
        }

        return names;
    }

    private static void AppendAutomaticStyles(XDocument content, List<XElement> styleFragment)
    {
        var root = content.Root
            ?? throw new DocumentValidationException("ODT: content.xml without a root element.");
        var automaticStyles = root.Element(Office + "automatic-styles");
        if (automaticStyles is null)
        {
            automaticStyles = new XElement(Office + "automatic-styles");
            var body = root.Element(Office + "body") ?? root.Element(Office + "master-styles");
            if (body is not null)
            {
                body.AddBeforeSelf(automaticStyles);
            }
            else
            {
                root.Add(automaticStyles);
            }
        }

        automaticStyles.Add(styleFragment);
    }

    /// <summary>Available text width from the template's page template (fallback: A4 with 1 inch margin).</summary>
    private static double TemplateContentWidth(XDocument content, XDocument? styles)
    {
        foreach (var document in new[] { styles, content })
        {
            // The layout of the page style the text uses, not the first one in the file.
            var master = document?.Descendants(Style + "master-page")
                .OrderByDescending(page => (string?)page.Attribute(Style + "name") == "Standard")
                .FirstOrDefault();
            var layoutName = (string?)master?.Attribute(Style + "page-layout-name");
            var properties = (document?.Descendants(Style + "page-layout")
                    .FirstOrDefault(layout => (string?)layout.Attribute(Style + "name") == layoutName)
                    ?? document?.Descendants(Style + "page-layout").FirstOrDefault())
                ?.Element(Style + "page-layout-properties");
            if (properties is null)
            {
                continue;
            }

            var width = OdtDocumentReader.ParseLength(properties.Attribute(Fo + "page-width")?.Value)?.Points;
            var left = OdtDocumentReader.ParseLength(properties.Attribute(Fo + "margin-left")?.Value)?.Points ?? 0;
            var right = OdtDocumentReader.ParseLength(properties.Attribute(Fo + "margin-right")?.Value)?.Points ?? 0;
            if (width is > 0 and var pageWidth)
            {
                return Math.Max(1, pageWidth - left - right);
            }
        }

        return PageSize.A4.Width.Points - PageMargins.Normal.Left.Points - PageMargins.Normal.Right.Points;
    }

    private static bool ContainsPageBreak(Block block) => block switch
    {
        PageBreak => true,
        ListBlock list => list.Items.Any(item => item.Blocks.Any(ContainsPageBreak)),
        TableBlock table => table.Rows.Any(row => row.Cells.Any(cell => cell.Blocks.Any(ContainsPageBreak))),
        _ => false,
    };

    private static void WriteEntry(ZipArchive archive, string name, byte[] data, CompressionLevel compression)
    {
        var entry = archive.CreateEntry(name, compression);
        entry.LastWriteTime = FixedTimestamp;
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static XElement Body(XDocument content)
        => content.Root?.Element(Office + "body")?.Element(Office + "text")
            ?? throw new DocumentValidationException("The ODT template contains no body (office:text).");

    /// <summary>Whether a section contains the marker of a placeholder in its text.</summary>
    private static bool ContainsPlaceholder(XElement section, string name)
        => string.Concat(section.DescendantNodes().OfType<XText>().Select(text => text.Value))
            .Contains(DocumentTemplate.Marker(name), StringComparison.Ordinal);

    /// <summary>
    /// Reports bindings whose placeholder never occurred in the body or a band. Without this the
    /// caller's content would be dropped silently.
    /// </summary>
    private static void ReportMissingBindings(DocumentTemplate template, IReadOnlySet<string> found, UnsupportedFeatureHandler handler)
    {
        foreach (var name in template.Placeholders.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!found.Contains(name) && name != template.BodyPlaceholder)
            {
                handler.Report($"ODT: the bound placeholder '{{{{{name}}}}}' is not present in the template; its content is not written.");
            }
        }
    }
}
