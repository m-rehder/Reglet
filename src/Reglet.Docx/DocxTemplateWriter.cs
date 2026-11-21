using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// writes a <see cref="PortableDocument"/> based on a DOCX template: the package of the
/// template is adopted, the <c>{{Name}}</c> placeholders in <c>word/document.xml</c> are
/// bound in the body and headers/footers, and for inserted images/lists media,
/// relationships and numbering are added.
/// </summary>
internal static class DocxTemplateWriter
{
    private static readonly XNamespace W = DocxConstants.WordprocessingML;
    private static readonly XNamespace WP = DocxConstants.DrawingWordprocessingNamespace;
    private static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string ContentTypesPart = "[Content_Types].xml";
    private const string DocumentRelsPart = "word/_rels/document.xml.rels";

    /// <summary>Namespaces of the WordprocessingML fragments.</summary>
    private static readonly (string Prefix, string Namespace)[] FragmentNamespaces =
    [
        ("w", DocxConstants.WordprocessingML),
        ("r", DocxConstants.Relationships),
        ("wp", DocxConstants.DrawingWordprocessingNamespace),
        ("a", DocxConstants.DrawingMainNamespace),
        ("pic", DocxConstants.DrawingPictureNamespace),
    ];

    public static void Build(
        PortableDocument document,
        DocumentTemplate template,
        Stream output,
        DocxWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            BuildCore(document, template, output, options, handler, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new DocumentValidationException("The DOCX template is not a valid OPC package.", exception);
        }
        catch (XmlException exception)
        {
            throw new DocumentValidationException("The DOCX template contains invalid XML.", exception);
        }
    }

    private static void BuildCore(
        PortableDocument document,
        DocumentTemplate template,
        Stream output,
        DocxWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        DocumentLimits.Enforce(document, options.Limits);
        cancellationToken.ThrowIfCancellationRequested();
        template.ValidateTypes();

        using var templateStream = new MemoryStream(template.RawData.ToArray(), writable: false);
        using var source = new ZipArchive(templateStream, ZipArchiveMode.Read, leaveOpen: true);

        var documentEntry = source.GetEntry(DocxConstants.DocumentPart)
            ?? throw new DocumentValidationException("The DOCX template contains no word/document.xml.");
        var documentXml = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(documentEntry));

        var bodyBlocks = document.Sections.SelectMany(section => section.Blocks).ToList();
        var bindings = new Dictionary<string, TemplateContent>(template.Placeholders, StringComparer.Ordinal);
        IReadOnlyList<IReadOnlyList<XElement>>? bodySections = null;
        var perSection = false;
        if (template.BodyPlaceholder is { } bodyName)
        {
            if (document.Sections.Count > 1)
            {
                bodySections = SplitBodySections(Body(documentXml));
                perSection = bodySections.Count == document.Sections.Count
                    && bodySections.All(section => ContainsPlaceholder(section, bodyName));
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
                    handler.Report("DOCX: multiple sections in the template are merged into one area.");
                }
            }
        }

        // Which blocks are actually produced? (images, lists)
        var insertedBlocks = new List<Block>();
        foreach (var content in bindings.Values)
        {
            insertedBlocks.AddRange(content.Blocks);
        }

        if (template.BodyPlaceholder is null || perSection)
        {
            insertedBlocks.AddRange(bodyBlocks);
        }

        var contentTypes = LoadOrCreate(source, ContentTypesPart, CT + "Types");
        var documentRels = LoadOrCreate(source, DocumentRelsPart, PR + "Relationships");
        XNamespace relationships = DocxConstants.Relationships;
        var bandIds = documentXml.Descendants().Where(node => node.Name == W + "headerReference" || node.Name == W + "footerReference")
            .Select(node => (string?)node.Attribute(relationships + "id")).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var bands = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var relationship in documentRels.Root!.Elements(PR + "Relationship"))
        {
            var type = (string?)relationship.Attribute("Type");
            if (!bandIds.Contains((string?)relationship.Attribute("Id")) || type is not (DocxConstants.HeaderRelationship or DocxConstants.FooterRelationship)) continue;
            var relationshipTarget = (string?)relationship.Attribute("Target");
            if ((string?)relationship.Attribute("TargetMode") == "External" || relationshipTarget is null
                || !Uri.TryCreate(new Uri("http://docx.local/word/document.xml"), relationshipTarget, out var uri)
                || uri.Host != "docx.local" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new DocumentValidationException("DOCX: invalid header or footer relationship.");
            var part = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (!bands.ContainsKey(part))
            {
                var entry = source.GetEntry(part) ?? throw new DocumentValidationException($"DOCX: header or footer part '{part}' is missing.");
                bands.Add(part, ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)));
            }
        }
        var nextRelationshipId = MaxRelationshipId(documentRels) + 1;
        var usedMediaNames = new HashSet<string>(source.Entries.Select(entry => entry.FullName), StringComparer.Ordinal);
        var media = RegisterImages(source, documentRels, contentTypes, insertedBlocks, options, ref nextRelationshipId, usedMediaNames, out var imageRelationships);
        var hyperlinkTargets = DocxPackageBuilder.EnumerateHyperlinks(insertedBlocks)
            .Concat(bindings.Values.SelectMany(content => DocxPackageBuilder.EnumerateHyperlinks(content.Inlines)));
        var hyperlinkRelationships = RegisterHyperlinks(documentRels, hyperlinkTargets, ref nextRelationshipId);

        var numberingEntry = source.GetEntry(DocxConstants.NumberingPart);
        var numbering = numberingEntry is null ? null : ReaderGuard.LoadXml(TemplatePackage.ReadEntry(numberingEntry));
        var (firstNumId, maxAbstractNumId) = NumberingIds(numbering);
        var hasLists = insertedBlocks.Any(AnyList);

        var footnotesEntry = source.GetEntry(DocxConstants.FootnotesPart);
        var footnotes = footnotesEntry is null ? null : ReaderGuard.LoadXml(TemplatePackage.ReadEntry(footnotesEntry));
        var endnotesEntry = source.GetEntry(DocxConstants.EndnotesPart);
        var endnotes = endnotesEntry is null ? null : ReaderGuard.LoadXml(TemplatePackage.ReadEntry(endnotesEntry));
        var commentsEntry = source.GetEntry("word/comments.xml");
        var comments = commentsEntry is null ? null : ReaderGuard.LoadXml(TemplatePackage.ReadEntry(commentsEntry));
        var safeFootnotes = SafeNoteIds(footnotes, "footnote");
        var safeEndnotes = SafeNoteIds(endnotes, "endnote");
        var safeComments = SafeCommentIds(comments);

        var context = new DocxWriteContext(
            imageRelationships,
            options.InvalidXmlCharacters,
            handler,
            firstNumId,
            DocxPackageBuilder.NextId(bands.Values.Append(documentXml).Max(MaxDrawingId), "drawing ID"),
            DocxPackageBuilder.NextId(bands.Values.Append(documentXml).Max(MaxBookmarkId), "bookmark ID"),
            hyperlinkRelationships: hyperlinkRelationships,
            numbering: new NumberingLookup(document.Numbering),
            firstSdtId: DocxPackageBuilder.NextId(bands.Values.Append(documentXml).Max(MaxSdtId), "content control ID"),
            firstRevisionId: DocxPackageBuilder.NextId(bands.Values.Append(documentXml).Max(MaxRevisionId), "tracked change ID"),
            firstFootnoteId: DocxPackageBuilder.NextId(MaxNoteId(footnotes, "footnote"), "footnote ID"),
            firstEndnoteId: DocxPackageBuilder.NextId(MaxNoteId(endnotes, "endnote"), "endnote ID"),
            firstCommentId: DocxPackageBuilder.NextId(MaxCommentId(comments), "comment ID"));
        context.BookmarkRanges = BookmarkScan.Collect(document).RangeNames;
        context.Styles = document.Styles;
        context.SourceStyles = document.SourceStyles ?? [];
        context.CharacterStyles = document.CharacterStyles;

        var binder = new DocxTemplateBinder(context, ContentWidthTwips(documentXml), handler, template.Conditions, template.Repeats, template.RegionData(), options.Limits, safeFootnotes, safeEndnotes, safeComments, document.Metadata.Language ?? TemplateLanguage(source));
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (perSection && template.BodyPlaceholder is { } sectionPlaceholder && bodySections is not null)
        {
            // Bind each section in a temporary container: the binder treats a container's
            // descendants as its content, so a section's paragraphs are bound even when they are
            // the direct children of the section. The elements move back into the body afterwards.
            var wrappers = new List<XElement>(bodySections.Count);
            for (var index = 0; index < bodySections.Count; index++)
            {
                var wrapper = new XElement(W + "body");
                wrapper.Add(bodySections[index]);
                var sectionBindings = new Dictionary<string, TemplateContent>(bindings, StringComparer.Ordinal)
                {
                    [sectionPlaceholder] = TemplateContent.FromBlocks(document.Sections[index].Blocks),
                };
                found.UnionWith(binder.Bind(wrapper, sectionBindings));
                wrappers.Add(wrapper);
            }

            var body = Body(documentXml);
            body.Elements().Remove();
            foreach (var wrapper in wrappers)
            {
                body.Add(wrapper.Nodes().ToList());
            }
        }
        else
        {
            found.UnionWith(binder.Bind(documentXml, bindings));
        }

        if (template.BodyPlaceholder is { } placeholder && !found.Contains(placeholder))
        {
            handler.Report($"DOCX: body placeholder '{{{{{placeholder}}}}}' was not found in the template; the content is appended.");
            binder.Append(documentXml, bodyBlocks);
        }
        else if (template.BodyPlaceholder is null && bodyBlocks.Count > 0)
        {
            binder.Append(documentXml, bodyBlocks);
        }

        var bandBindings = new Dictionary<string, TemplateContent>(template.Placeholders, StringComparer.Ordinal);
        var bandRelationships = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var (part, band) in bands.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = band.Root ?? throw new DocumentValidationException("DOCX: header or footer has no root element.");
            var bandText = string.Concat(root.Descendants(W + "t").Select(text => text.Value));
            var hasMergeField = root.Descendants(W + "fldSimple").Any(field => ((string?)field.Attribute(W + "instr"))?.Contains("MERGEFIELD", StringComparison.OrdinalIgnoreCase) == true)
                || string.Concat(root.Descendants(W + "instrText").Select(text => text.Value)).Contains("MERGEFIELD", StringComparison.OrdinalIgnoreCase);
            if (!bandText.Contains("{{", StringComparison.Ordinal) && !hasMergeField)
            {
                bands.Remove(part);
                continue;
            }
            var slash = part.LastIndexOf('/');
            var relsPart = (slash < 0 ? "" : part[..(slash + 1)]) + "_rels/" + part[(slash + 1)..] + ".rels";
            var rels = LoadOrCreate(source, relsPart, PR + "Relationships");
            var nextId = MaxRelationshipId(rels) + 1;
            var bandContents = bandBindings.Where(binding => bandText.Contains(DocumentTemplate.Marker(binding.Key), StringComparison.Ordinal))
                .Select(binding => binding.Value).ToList();
            var bandBlocks = bandContents.SelectMany(content => content.Blocks).ToList();
            media.AddRange(RegisterImages(source, rels, contentTypes, bandBlocks, options, ref nextId, usedMediaNames, out var bandImages));
            var bandTargets = bandContents.SelectMany(content => DocxPackageBuilder.EnumerateHyperlinks(content.Blocks)
                .Concat(DocxPackageBuilder.EnumerateHyperlinks(content.Inlines)));
            var bandHyperlinks = RegisterHyperlinks(rels, bandTargets, ref nextId);
            var baseUri = new Uri("http://docx.local/" + part);
            foreach (var relationship in rels.Root!.Elements(PR + "Relationship").Where(item => bandImages.Values.Contains((string?)item.Attribute("Id"))))
            {
                var relationshipTarget = (string)relationship.Attribute("Target")!;
                relationship.SetAttributeValue("Target", Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri("http://docx.local/word/" + relationshipTarget)).ToString()));
            }
            context.ImageRelationships = bandImages;
            context.HyperlinkRelationships = bandHyperlinks;
            context.BandName = root.Name == W + "hdr" ? "header" : "footer";
            found.UnionWith(binder.Bind(root, bandBindings));
            if (rels.Root!.HasElements) bandRelationships.Add(relsPart, rels);
        }

        ReportMissingBindings(template, found, handler);

        var noteBookmarkNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in new[] { documentXml, footnotes, endnotes }.Concat(bands.Values).OfType<XDocument>())
        {
            foreach (var bookmark in part.Descendants(W + "bookmarkStart"))
            {
                if ((string?)bookmark.Attribute(W + "name") is { } name)
                {
                    noteBookmarkNames.Add(name);
                }
            }
        }

        var nextNoteBookmarkId = new[] { documentXml, footnotes, endnotes }
            .Concat(bands.Values)
            .OfType<XDocument>()
            .Select(MaxBookmarkId)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var topLevelFootnotes = binder.NoteClones.Count(clone => !clone.Endnote);
        var topLevelEndnotes = binder.NoteClones.Count(clone => clone.Endnote);
        var bodyCommentClones = binder.CommentClones;
        var noteContext = new NoteCloneContext(footnotes, endnotes, comments, noteBookmarkNames, handler)
        {
            NextFootnoteId = MaxNoteId(footnotes, "footnote") + 1 + topLevelFootnotes,
            NextEndnoteId = MaxNoteId(endnotes, "endnote") + 1 + topLevelEndnotes,
            NextCommentId = MaxCommentId(comments) + 1 + bodyCommentClones.Count,
            NextBookmarkId = nextNoteBookmarkId,
        };
        foreach (var (sourceId, newId) in bodyCommentClones)
        {
            if (!ApplyCommentClone(noteContext, sourceId, newId))
            {
                continue;
            }
        }

        var footnotesChanged = ApplyNoteClones(noteContext, false, binder.NoteClones.Where(clone => !clone.Endnote));
        var endnotesChanged = ApplyNoteClones(noteContext, true, binder.NoteClones.Where(clone => clone.Endnote));
        var commentsChanged = noteContext.CommentsChanged;

        var stylesPart = AddMissingStyles(source, document, insertedBlocks, options.InvalidXmlCharacters);
        var numberingPart = UpdateNumbering(numbering, contentTypes, documentRels, hasLists, context.UsedNumbering, firstNumId, maxAbstractNumId, ref nextRelationshipId);

        // write package: adopt the template entries and replace the changed parts.
        using var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var documentRelsWritten = false;

        foreach (var entry in source.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Name.Length == 0
                || entry.FullName.StartsWith(DocxSignatureWriter.Folder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] data;
            if (bands.TryGetValue(entry.FullName, out var bandXml))
            {
                WriteEntry(target, entry.FullName, TemplatePackage.SaveXml(bandXml));
                continue;
            }
            if (bandRelationships.Remove(entry.FullName, out var bandRels))
            {
                WriteEntry(target, entry.FullName, TemplatePackage.SaveXml(bandRels));
                continue;
            }
            switch (entry.FullName)
            {
                case DocxConstants.DocumentPart:
                    data = TemplatePackage.SaveXml(documentXml);
                    break;
                case DocxConstants.StylesPart when stylesPart is not null:
                    data = TemplatePackage.SaveXml(stylesPart);
                    break;
                case DocxConstants.NumberingPart when numberingPart is not null:
                    data = TemplatePackage.SaveXml(numberingPart);
                    break;
                case ContentTypesPart:
                    contentTypes.Root?.Elements(CT + "Override")
                        .Where(element => ((string?)element.Attribute("PartName"))?.StartsWith("/" + DocxSignatureWriter.Folder, StringComparison.OrdinalIgnoreCase) == true)
                        .Remove();
                    data = TemplatePackage.SaveXml(contentTypes);
                    break;
                case "_rels/.rels" when source.Entries.Any(item => item.FullName.StartsWith(DocxSignatureWriter.Folder, StringComparison.OrdinalIgnoreCase)):
                    var packageRelationships = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry));
                    packageRelationships.Root?.Elements()
                        .Where(element => (string?)element.Attribute("Type") == DocxSignatureWriter.OriginRelationship)
                        .Remove();
                    data = TemplatePackage.SaveXml(packageRelationships);
                    handler.Report("DOCX: the signature of the template is removed; it does not cover the filled document.");
                    break;
                case DocumentRelsPart:
                    data = TemplatePackage.SaveXml(documentRels);
                    documentRelsWritten = true;
                    break;
                case DocxConstants.FootnotesPart when footnotesChanged && footnotes is not null:
                    data = TemplatePackage.SaveXml(footnotes);
                    break;
                case DocxConstants.EndnotesPart when endnotesChanged && endnotes is not null:
                    data = TemplatePackage.SaveXml(endnotes);
                    break;
                case "word/comments.xml" when commentsChanged && comments is not null:
                    data = TemplatePackage.SaveXml(comments);
                    break;
                default:
                    data = TemplatePackage.ReadEntry(entry);
                    break;
            }

            WriteEntry(target, entry.FullName, data);
        }

        // new parts: numbering (if the template had none) and media.
        if (numberingPart is not null && source.GetEntry(DocxConstants.NumberingPart) is null)
        {
            WriteEntry(target, DocxConstants.NumberingPart, TemplatePackage.SaveXml(numberingPart));
        }

        if (!documentRelsWritten && documentRels.Root!.HasElements)
        {
            WriteEntry(target, DocumentRelsPart, TemplatePackage.SaveXml(documentRels));
        }

        foreach (var (partName, data) in media)
        {
            WriteEntry(target, partName, data);
        }
        foreach (var (partName, rels) in bandRelationships)
        {
            WriteEntry(target, partName, TemplatePackage.SaveXml(rels));
        }
    }

    /// <summary>Registers inserted images as media parts and relationships.</summary>
    private static List<(string PartName, byte[] Data)> RegisterImages(
        ZipArchive source,
        XDocument documentRels,
        XDocument contentTypes,
        IReadOnlyList<Block> blocks,
        DocxWriteOptions options,
        ref int nextRelationshipId,
        HashSet<string> usedMediaNames,
        out IReadOnlyDictionary<ImageBlock, string> relationships)
    {
        var result = new List<(string, byte[])>();
        var map = new Dictionary<ImageBlock, string>();

        foreach (var image in DocxPackageBuilder.WithRasterFallbacks(DocxPackageBuilder.EnumerateImages(blocks)).Distinct())
        {
            ImageGuard.Check(image, options.Limits);

            var extension = image.MediaType == ImageMediaType.Svg ? "svg" : image.MediaType == ImageMediaType.Png ? "png" : "jpeg";
            var contentType = image.MediaType == ImageMediaType.Svg
                ? DocxConstants.SvgContentType
                : image.MediaType == ImageMediaType.Png ? DocxConstants.PngContentType : DocxConstants.JpegContentType;
            var partName = NextMediaName(usedMediaNames, extension);

            EnsureContentTypeDefault(contentTypes, extension, contentType);

            var relationshipId = $"rId{nextRelationshipId++}";
            documentRels.Root!.Add(new XElement(
                PR + "Relationship",
                new XAttribute("Id", relationshipId),
                new XAttribute("Type", DocxConstants.ImageRelationship),
                new XAttribute("Target", partName["word/".Length..])));

            map[image] = relationshipId;
            result.Add((partName, image.RawData.ToArray()));
        }

        relationships = map;
        return result;
    }

    private static string NextMediaName(HashSet<string> used, string extension)
    {
        for (var index = 1; ; index++)
        {
            var name = $"word/media/image{index}.{extension}";
            if (used.Add(name))
            {
                return name;
            }
        }
    }

    /// <summary>
    /// Registers the external hyperlinks of inserted block content as relationships of the part
    /// (body or band). A <c>#bookmark</c> target needs no relationship.
    /// </summary>
    private static Dictionary<string, string> RegisterHyperlinks(
        XDocument relationships,
        IEnumerable<string> targets,
        ref int nextRelationshipId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target.StartsWith('#') || result.ContainsKey(target))
            {
                continue;
            }

            var relationshipId = $"rId{nextRelationshipId++}";
            relationships.Root!.Add(new XElement(
                PR + "Relationship",
                new XAttribute("Id", relationshipId),
                new XAttribute("Type", DocxConstants.HyperlinkRelationship),
                new XAttribute("Target", target),
                new XAttribute("TargetMode", "External")));
            result[target] = relationshipId;
        }

        return result;
    }

    /// <summary>
    /// adds the numbering definitions if needed. Returns the
    /// numbering document or null if no lists occur.
    /// </summary>
    private static XDocument? UpdateNumbering(
        XDocument? numbering,
        XDocument contentTypes,
        XDocument documentRels,
        bool hasLists,
        IReadOnlyList<(NumberingDefinition Definition, int NumId)> definitions,
        int firstNumId,
        int maxAbstractNumId,
        ref int nextRelationshipId)
    {
        if (!hasLists)
        {
            return numbering;
        }

        if (numbering is null)
        {
            numbering = new XDocument(new XElement(W + "numbering"));
            EnsureContentTypeOverride(contentTypes, "/word/numbering.xml", DocxConstants.NumberingContentType);
            documentRels.Root!.Add(new XElement(
                PR + "Relationship",
                new XAttribute("Id", $"rId{nextRelationshipId++}"),
                new XAttribute("Type", DocxConstants.NumberingRelationship),
                new XAttribute("Target", "numbering.xml")));
        }

        AppendNumberingDefinitions(numbering, maxAbstractNumId + 1, firstNumId, definitions);
        return numbering;
    }

    /// <summary>
    /// adds one definition per used numbering definition. The <c>w:abstractNum</c> elements stand
    /// before all <c>w:num</c> elements (ECMA-376).
    /// </summary>
    private static void AppendNumberingDefinitions(
        XDocument numbering,
        int firstAbstractNumId,
        int firstNumId,
        IReadOnlyList<(NumberingDefinition Definition, int NumId)> definitions)
    {
        var root = numbering.Root
            ?? throw new DocumentValidationException("DOCX: numbering.xml without a root element.");

        var abstractElements = new List<XElement>();
        var numElements = new List<XElement>();

        for (var index = 0; index < definitions.Count; index++)
        {
            var abstractId = firstAbstractNumId + index;
            var definition = definitions[index].Definition;
            abstractElements.Add(FragmentElement(
                writer => DocxPackageBuilder.WriteAbstractNum(writer, abstractId, definition)));
            numElements.Add(FragmentElement(
                writer => DocxPackageBuilder.WriteNum(writer, definitions[index].NumId, abstractId)));
        }

        var firstNum = root.Elements(W + "num").FirstOrDefault();
        if (firstNum is not null)
        {
            foreach (var element in abstractElements)
            {
                firstNum.AddBeforeSelf(element);
            }
        }
        else
        {
            root.Add(abstractElements);
        }

        root.Add(numElements);
    }

    private static (int FirstNumId, int MaxAbstractNumId) NumberingIds(XDocument? numbering)
    {
        if (numbering is null)
        {
            return (1, -1);
        }

        var maxNumId = numbering.Descendants(W + "num")
            .Select(num => ParseInt(num.Attribute(W + "numId")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(0)
            .Max()!.Value;

        var maxAbstractId = numbering.Descendants(W + "abstractNum")
            .Select(entry => ParseInt(entry.Attribute(W + "abstractNumId")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(-1)
            .Max()!.Value;

        return (maxNumId + 1, maxAbstractId);
    }

    private static long ContentWidthTwips(XDocument document)
    {
        var section = document.Descendants(W + "sectPr").LastOrDefault();
        var pageSize = section?.Element(W + "pgSz");
        var margins = section?.Element(W + "pgMar");

        var width = ParseInt(pageSize?.Attribute(W + "w")?.Value) ?? 11906;
        var left = ParseInt(margins?.Attribute(W + "left")?.Value) ?? 1440;
        var right = ParseInt(margins?.Attribute(W + "right")?.Value) ?? 1440;
        return Math.Max(1, width - left - right);
    }

    /// <summary>
    /// The template's styles with the character styles of the model that it lacks, so a
    /// <c>w:rStyle</c> of an inserted run resolves; <c>null</c> when nothing is missing. A style the
    /// template defines stays the template's.
    /// </summary>
    private static XDocument? AddMissingStyles(
        ZipArchive source, PortableDocument document, IReadOnlyList<Block> insertedBlocks, InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (source.GetEntry(DocxConstants.StylesPart) is not { } entry
            || ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)) is not { Root: { } root } styles)
        {
            return null;
        }

        var existing = root.Elements(W + "style")
            .Select(style => (string?)style.Attribute(W + "styleId"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var tableStyles = TableScan.Enumerate(insertedBlocks).Select(table => table.Style)
            .OfType<TableStyle>().Select(style => style.Name);
        var missing = document.CharacterStyles.Select(style => style.Name).Concat(tableStyles)
            .Where(name => !existing.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        if (missing.Count == 0)
        {
            return null;
        }

        var styleDocument = document with { Sections = [new Section(insertedBlocks)] };
        root.Add(DocxPackageBuilder.RenderStyles(styleDocument, xmlBehavior).Elements(W + "style")
            .Where(style => (string?)style.Attribute(W + "type") is "character" or "table"
                && missing.Contains((string?)style.Attribute(W + "styleId") ?? string.Empty)));
        return styles;
    }

    /// <summary>The default language of the template's styles, if it states one.</summary>
    private static string? TemplateLanguage(ZipArchive source)
        => source.GetEntry(DocxConstants.StylesPart) is { } entry
            ? (string?)ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Descendants(W + "lang").FirstOrDefault()?.Attribute(W + "val")
            : null;

    private static int MaxDrawingId(XDocument document)
        => document.Descendants()
            .Where(element => element.Name == WP + "docPr" || element.Name.LocalName == "cNvPr")
            .Select(entry => ParseInt(entry.Attribute("id")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(0)
            .Max()!.Value;

    private static int MaxBookmarkId(XDocument document)
        => document.Descendants(W + "bookmarkStart").Concat(document.Descendants(W + "bookmarkEnd"))
            .Select(entry => ParseInt(entry.Attribute(W + "id")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(0)
            .Max()!.Value;

    private static int MaxSdtId(XDocument document)
        => document.Descendants(W + "sdtPr")
            .Select(properties => ParseInt(properties.Element(W + "id")?.Attribute(W + "val")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(0)
            .Max()!.Value;

    private static int MaxRevisionId(XDocument document)
        => document.Descendants()
            .Where(element => element.Name.Namespace == W
                && (element.Name.LocalName is "ins" or "del" or "moveFrom" or "moveTo"
                    || element.Name.LocalName.EndsWith("Change", StringComparison.Ordinal)))
            .Select(element => ParseInt(element.Attribute(W + "id")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(0)
            .Max()!.Value;

    private static int MaxNoteId(XDocument? notes, string localName)
        => notes?.Descendants(W + localName)
            .Select(note => ParseInt(note.Attribute(W + "id")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

    private static int MaxCommentId(XDocument? comments)
        => comments?.Descendants(W + "comment")
            .Select(comment => ParseInt(comment.Attribute(W + "id")?.Value))
            .Where(value => value is not null)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

    private static HashSet<int> SafeCommentIds(XDocument? comments)
        => comments?.Descendants(W + "comment")
            .Select(comment => ParseInt(comment.Attribute(W + "id")?.Value))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToHashSet() ?? new HashSet<int>();

    private static HashSet<int> SafeNoteIds(XDocument? notes, string localName)
    {
        var result = new HashSet<int>();
        if (notes is null)
        {
            return result;
        }

        foreach (var note in notes.Descendants(W + localName))
        {
            var id = ParseInt(note.Attribute(W + "id")?.Value);
            if (id is null)
            {
                continue;
            }

            var unsafeContent = note.Descendants().Any(node =>
                node.Name == W + "sectPr" || node.Name == W + "sdt"
                || node.Name == W + "ins" || node.Name == W + "del"
                || node.Name == W + "moveFrom" || node.Name == W + "moveTo");
            if (!unsafeContent)
            {
                result.Add(id.Value);
            }
        }

        return result;
    }

    private sealed class NoteCloneContext(
        XDocument? footnotes,
        XDocument? endnotes,
        XDocument? comments,
        HashSet<string> bookmarkNames,
        UnsupportedFeatureHandler handler)
    {
        public XDocument? Footnotes { get; } = footnotes;
        public XDocument? Endnotes { get; } = endnotes;
        public XDocument? Comments { get; } = comments;
        public HashSet<string> BookmarkNames { get; } = bookmarkNames;
        public UnsupportedFeatureHandler Handler { get; } = handler;
        public int NextFootnoteId { get; set; }
        public int NextEndnoteId { get; set; }
        public int NextCommentId { get; set; }
        public int NextBookmarkId { get; set; }
        public bool CommentsChanged { get; set; }
        public HashSet<(string Part, int Id)> Path { get; } = [];
    }

    private static bool ApplyNoteClones(
        NoteCloneContext context,
        bool endnote,
        IEnumerable<(bool Endnote, int SourceId, int NewId)> clones)
    {
        var notes = endnote ? context.Endnotes : context.Footnotes;
        var localName = endnote ? "endnote" : "footnote";
        if (notes?.Root is null)
        {
            return false;
        }

        var changed = false;
        foreach (var (_, sourceId, newId) in clones)
        {
            var source = FindNote(notes, localName, sourceId);
            if (source is null)
            {
                context.Handler.Report($"DOCX: the {localName} '{sourceId}' to clone is missing from the template.");
                continue;
            }

            if (!IsSafeNote(source))
            {
                context.Handler.Report($"DOCX: the {localName} '{sourceId}' contains content that cannot be duplicated; it remains shared.");
                continue;
            }

            DuplicateNote(context, source, localName, sourceId, newId, 0);
            changed = true;
        }

        return changed;
    }

    /// <summary>Copies a note, renames its bookmarks and duplicates nested notes recursively.</summary>
    private static void DuplicateNote(
        NoteCloneContext context,
        XElement source,
        string localName,
        int sourceId,
        int newId,
        int depth)
    {
        if (depth > 8)
        {
            context.Handler.Report($"DOCX: the nested notes below '{localName}' {sourceId} are too deep to duplicate.");
            return;
        }

        var notes = localName == "endnote" ? context.Endnotes : context.Footnotes;
        if (notes?.Root is null)
        {
            return;
        }

        var key = (localName, sourceId);
        if (!context.Path.Add(key))
        {
            context.Handler.Report($"DOCX: the {localName} '{sourceId}' references itself; it is not duplicated.");
            return;
        }

        try
        {
            var clone = new XElement(source);
            clone.SetAttributeValue(W + "id", newId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var nextBookmarkId = context.NextBookmarkId;
            RenameNoteBookmarks(clone, context.BookmarkNames, ref nextBookmarkId);
            context.NextBookmarkId = nextBookmarkId;

            var commentIds = new Dictionary<int, int>();
            foreach (var node in clone.Descendants()
                .Where(item => item.Name == W + "commentReference"
                    || item.Name == W + "commentRangeStart" || item.Name == W + "commentRangeEnd")
                .ToList())
            {
                var sourceCommentId = ParseInt(node.Attribute(W + "id")?.Value);
                if (sourceCommentId is null)
                {
                    continue;
                }

                if (!commentIds.TryGetValue(sourceCommentId.Value, out var newCommentId))
                {
                    if (FindComment(context.Comments, sourceCommentId.Value) is null)
                    {
                        context.Handler.Report($"DOCX: the comment '{sourceCommentId}' in a copied note is missing from the template.");
                        continue;
                    }

                    newCommentId = context.NextCommentId++;
                    ApplyCommentClone(context, sourceCommentId.Value, newCommentId);
                    commentIds[sourceCommentId.Value] = newCommentId;
                }

                node.SetAttributeValue(W + "id", newCommentId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var reference in clone.Descendants()
                .Where(node => node.Name == W + "footnoteReference" || node.Name == W + "endnoteReference")
                .ToList())
            {
                var nestedEndnote = reference.Name == W + "endnoteReference";
                var nestedLocal = nestedEndnote ? "endnote" : "footnote";
                var nestedNotes = nestedEndnote ? context.Endnotes : context.Footnotes;
                var nestedSourceId = ParseInt(reference.Attribute(W + "id")?.Value);
                if (nestedNotes is null || nestedSourceId is null)
                {
                    continue;
                }

                var nestedSource = FindNote(nestedNotes, nestedLocal, nestedSourceId.Value);
                if (nestedSource is null)
                {
                    context.Handler.Report($"DOCX: the nested {nestedLocal} '{nestedSourceId}' is missing from the template.");
                    continue;
                }

                if (!IsSafeNote(nestedSource))
                {
                    context.Handler.Report($"DOCX: the nested {nestedLocal} '{nestedSourceId}' contains content that cannot be duplicated; it remains shared.");
                    continue;
                }

                var nestedNewId = nestedEndnote ? context.NextEndnoteId++ : context.NextFootnoteId++;
                reference.SetAttributeValue(W + "id", nestedNewId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                DuplicateNote(context, nestedSource, nestedLocal, nestedSourceId.Value, nestedNewId, depth + 1);
            }

            notes.Root.Add(clone);
        }
        finally
        {
            context.Path.Remove(key);
        }
    }

    private static XElement? FindNote(XDocument? notes, string localName, int id)
        => notes?.Descendants(W + localName)
            .FirstOrDefault(note => ParseInt(note.Attribute(W + "id")?.Value) == id);

    private static XElement? FindComment(XDocument? comments, int id)
        => comments?.Descendants(W + "comment")
            .FirstOrDefault(comment => ParseInt(comment.Attribute(W + "id")?.Value) == id);

    private static bool ApplyCommentClone(NoteCloneContext context, int sourceId, int newId)
    {
        if (context.Comments?.Root is null)
        {
            context.Handler.Report($"DOCX: the comment '{sourceId}' to clone is missing from the template.");
            return false;
        }

        var source = FindComment(context.Comments, sourceId);
        if (source is null)
        {
            context.Handler.Report($"DOCX: the comment '{sourceId}' to clone is missing from the template.");
            return false;
        }

        var clone = new XElement(source);
        clone.SetAttributeValue(W + "id", newId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RemoveCommentParagraphIds(clone);
        var nextBookmarkId = context.NextBookmarkId;
        RenameNoteBookmarks(clone, context.BookmarkNames, ref nextBookmarkId);
        context.NextBookmarkId = nextBookmarkId;
        context.Comments.Root.Add(clone);
        context.CommentsChanged = true;
        return true;
    }

    /// <summary>Removes paragraph ids that commentsExtended/commentsIds would associate with the source.</summary>
    private static void RemoveCommentParagraphIds(XElement comment)
    {
        XNamespace word2010 = "http://schemas.microsoft.com/office/word/2010/wordml";
        XNamespace word2012 = "http://schemas.microsoft.com/office/word/2012/wordml";
        foreach (var paragraph in comment.Descendants(W + "p"))
        {
            paragraph.Attribute(word2010 + "paraId")?.Remove();
            paragraph.Attribute(word2010 + "textId")?.Remove();
            paragraph.Attribute(word2012 + "paraId")?.Remove();
        }
    }

    private static bool IsSafeNote(XElement note)
        => !note.Descendants().Any(node =>
            node.Name == W + "sectPr" || node.Name == W + "sdt"
            || node.Name == W + "ins" || node.Name == W + "del"
            || node.Name == W + "moveFrom" || node.Name == W + "moveTo");

    /// <summary>Gives the bookmarks of a copied note a fresh id and a unique name.</summary>
    private static void RenameNoteBookmarks(XElement clone, HashSet<string> bookmarkNames, ref int nextBookmarkId)
    {
        foreach (var start in clone.Descendants(W + "bookmarkStart").ToList())
        {
            var oldId = (string?)start.Attribute(W + "id");
            var oldName = (string?)start.Attribute(W + "name");
            if (oldId is null || string.IsNullOrEmpty(oldName))
            {
                continue;
            }

            var newName = oldName;
            var suffix = 2;
            while (!bookmarkNames.Add(newName))
            {
                newName = oldName + "_" + suffix++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var newId = nextBookmarkId++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.SetAttributeValue(W + "id", newId);
            start.SetAttributeValue(W + "name", newName);
            foreach (var end in clone.Descendants(W + "bookmarkEnd")
                .Where(end => (string?)end.Attribute(W + "id") == oldId))
            {
                end.SetAttributeValue(W + "id", newId);
            }

            foreach (var link in clone.Descendants(W + "hyperlink"))
            {
                if ((string?)link.Attribute(W + "anchor") == oldName)
                {
                    link.SetAttributeValue(W + "anchor", newName);
                }
            }
        }
    }

    private static int MaxRelationshipId(XDocument documentRels)
        => documentRels.Root?.Elements(PR + "Relationship")
            .Select(relationship => relationship.Attribute("Id")?.Value)
            .Where(id => id is not null && id.StartsWith("rId", StringComparison.Ordinal))
            .Select(id => ParseInt(id![3..]) ?? 0)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

    private static void EnsureContentTypeDefault(XDocument contentTypes, string extension, string contentType)
    {
        var root = contentTypes.Root
            ?? throw new DocumentValidationException("DOCX: template without a [Content_Types].xml root.");

        if (!root.Elements(CT + "Default")
            .Any(entry => string.Equals(entry.Attribute("Extension")?.Value, extension, StringComparison.OrdinalIgnoreCase)))
        {
            root.Add(new XElement(
                CT + "Default",
                new XAttribute("Extension", extension),
                new XAttribute("ContentType", contentType)));
        }
    }

    private static void EnsureContentTypeOverride(XDocument contentTypes, string partName, string contentType)
    {
        var root = contentTypes.Root
            ?? throw new DocumentValidationException("DOCX: template without a [Content_Types].xml root.");

        if (!root.Elements(CT + "Override")
            .Any(entry => string.Equals(entry.Attribute("PartName")?.Value, partName, StringComparison.Ordinal)))
        {
            root.Add(new XElement(
                CT + "Override",
                new XAttribute("PartName", partName),
                new XAttribute("ContentType", contentType)));
        }
    }

    private static bool AnyList(Block block) => block switch
    {
        ListBlock => true,
        TableBlock table => table.Rows.Any(row => row.Cells.Any(cell => cell.Blocks.Any(AnyList))),
        _ => false,
    };

    private static XDocument LoadOrCreate(ZipArchive source, string partName, XName rootName)
    {
        var entry = source.GetEntry(partName);
        return entry is null ? new XDocument(new XElement(rootName)) : ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry));
    }

    private static XElement FragmentElement(Action<XmlWriter> write)
        => XmlFragment.Write(write, FragmentNamespaces).OfType<XElement>().Single();

    private static XElement Body(XDocument document)
        => document.Root?.Element(W + "body")
            ?? throw new DocumentValidationException("The DOCX template contains no w:body.");

    /// <summary>
    /// Splits the body at its section breaks. A <c>w:sectPr</c> paragraph closes the section it
    /// belongs to; the final <c>w:sectPr</c> remains with the last section. Used to bind the body
    /// placeholder once per section instead of merging all model sections into one area.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<XElement>> SplitBodySections(XElement body)
    {
        var sections = new List<List<XElement>>();
        var current = new List<XElement>();
        foreach (var child in body.Elements())
        {
            if (child.Name == W + "sectPr" && child.Parent == body)
            {
                if (current.Count > 0)
                {
                    current.Add(child);
                }
                else if (sections.Count > 0)
                {
                    sections[^1].Add(child);
                }

                continue;
            }

            current.Add(child);
            if (child.Name == W + "p"
                && child.Element(W + "pPr")?.Element(W + "sectPr") is not null)
            {
                sections.Add(current);
                current = [];
            }
        }

        if (current.Count > 0)
        {
            sections.Add(current);
        }

        return sections;
    }

    /// <summary>Whether a section contains the marker of a placeholder in its text.</summary>
    private static bool ContainsPlaceholder(IReadOnlyList<XElement> section, string name)
        => string.Concat(section.SelectMany(element => element.Descendants(W + "t")).Select(text => text.Value))
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
                handler.Report($"DOCX: the bound placeholder '{{{{{name}}}}}' is not present in the template; its content is not written.");
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTimestamp;
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static int? ParseInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
}
