using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// builds the DOCX OPC package from a <see cref="PortableDocument"/>. The individual
/// components (numbering, images, header/footer) come in the following steps.
/// </summary>
internal static class DocxPackageBuilder
{
    /// <summary>The report for a note in a header or footer, which is written as text.</summary>
    private const string BandNoteMessage =
        "DOCX: a note in a header or footer is written as text; Word has no notes in a band "
        + "and LibreOffice cannot read a note reference in a header or footer part.";

    /// <summary>Header/footer variant in WordprocessingML.</summary>
    private enum DocxBand
    {
        Header,
        Footer,
        HeaderFirst,
        FooterFirst,
        HeaderEven,
        FooterEven,
    }

    public static void Build(
        PortableDocument document,
        Stream output,
        DocxWriteOptions options,
        UnsupportedFeatureHandler handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);

        cancellationToken.ThrowIfCancellationRequested();
        DocumentLimits.Enforce(document, options.Limits);

        // OF-C03: an internal link or a page reference whose bookmark is missing, or that points
        // into a header or footer, is written as it is but leads nowhere. Word shows an error for
        // it when the field is updated; the library says so in the write warnings instead.
        foreach (var reference in ReferenceScan.Find(document))
        {
            handler.Report($"DOCX: {reference.Message}");
        }

        using var package = new OpcPackageWriter(output, options.InvalidXmlCharacters);

        RegisterParts(package, document.Metadata.MacroProject is not null);
        RegisterRelationships(package);

        var customProperties = document.Metadata.CustomProperties;
        if (customProperties is { Count: > 0 })
        {
            package.AddOverride(DocxConstants.CustomPropertiesPart, DocxConstants.CustomPropertiesContentType);
            package.AddRelationship(
                string.Empty,
                new OpcRelationship("rId4", DocxConstants.CustomPropertiesRelationship, DocxConstants.CustomPropertiesPart));
        }

        var media = new MediaRegistry(package, options.Limits, cancellationToken, document.SourcePackage);
        var (bodyImages, nextRelationshipIndex) = RegisterImages(package, document, media);
        var hyperlinks = RegisterHyperlinks(package, document, ref nextRelationshipIndex);
        var headerFooters = RegisterHeadersFooters(package, document, media, ref nextRelationshipIndex);
        var xmlBehavior = options.InvalidXmlCharacters;
        var charts = RegisterCharts(package, document, xmlBehavior, ref nextRelationshipIndex);
        var embeddedObjects = DocxEmbeddedObjects.Register(package, document, ref nextRelationshipIndex);
        var evenAndOddHeaders = document.Sections.Any(section => section.EvenHeader is not null || section.EvenFooter is not null);

        // AF08: keep the source numbering definitions. The generated lists are placed above the
        // source maxima so they never collide with a preserved numId/abstractNumId.
        var sourceNumbering = ReadSourceNumbering(document.SourcePackage);
        var sourceDocument = ReadSourceDocument(document.SourcePackage);
        // OF-B08: the band parts the source sections reference, so that the blocks of a header or
        // footer that the model did not change keep their source XML.
        var sourceBands = ReadSourceBands(document, sourceDocument.Body);
        var firstNumId = sourceNumbering is { } numbering ? NextId(numbering.MaxNumId, "numId") : 1;
        var firstAbstractNumId = sourceNumbering is { } numberingIds ? NextId(numberingIds.MaxAbstractNumId, "abstractNumId") : 0;

        // AF06: generated drawing and bookmark ids start above the source maxima, so a preserved
        // source block never collides with a generated one. The band parts count too, because a
        // preserved band block can carry a drawing or a bookmark.
        var firstDrawingId = NextId(Math.Max(sourceDocument.MaxDrawingId, BandMaxDrawingId(sourceBands.Values)), "drawing ID");
        var firstBookmarkId = NextId(Math.Max(sourceDocument.MaxBookmarkId, BandMaxBookmarkId(sourceBands.Values)), "bookmark ID");
        var context = new DocxWriteContext(bodyImages, xmlBehavior, handler, firstNumId, firstDrawingId, firstBookmarkId, hyperlinkRelationships: hyperlinks, numbering: new NumberingLookup(document.Numbering));
        // AJ02: the general fields without a cached result are calculated (DOCPROPERTY,
        // DOCVARIABLE, IF, SEQ, ...); the result is written as the cached result.
        var bookmarks = BookmarkScan.Collect(document);
        context.ChartRelationships = charts;
        context.ObjectParts = embeddedObjects;
        context.BookmarkRanges = bookmarks.RangeNames;
        context.EditableRanges = options.EditableRanges?.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var name in context.EditableRanges.Where(name => !bookmarks.RangeNames.Contains(name)))
        {
            handler.Report($"DOCX: there is no bookmark range '{name}' to leave editable.");
        }
        context.Fields = FieldContext.FromMetadata(document.Metadata).WithBookmarks(bookmarks.Texts);
        context.CharacterStyles = document.CharacterStyles;
        context.Styles = document.Styles;
        context.SourceStyles = document.SourceStyles ?? [];
        // A note inside a header or footer stays text: WordprocessingML has no note there (see
        // NoteScan.Text), so band notes get no id and no place in the notes part.
        context.PrepareNotes(NoteScan.CollectBody(document));
        if (NoteScan.CollectBands(document).Count > 0)
        {
            handler.Report(BandNoteMessage);
        }

        context.PrepareHeadings(document);

        if (context.HasFootnotes)
        {
            package.AddOverride(DocxConstants.FootnotesPart, DocxConstants.FootnotesContentType);
            package.AddRelationship(
                DocxConstants.DocumentPart,
                new OpcRelationship($"rId{nextRelationshipIndex++}", DocxConstants.FootnotesRelationship, "footnotes.xml"));
        }

        var footnoteLinks = RegisterNoteHyperlinks(package, DocxConstants.FootnotesPart, context.Footnotes);
        var endnoteLinks = RegisterNoteHyperlinks(package, DocxConstants.EndnotesPart, context.Endnotes);

        if (context.HasEndnotes)
        {
            package.AddOverride(DocxConstants.EndnotesPart, DocxConstants.EndnotesContentType);
            package.AddRelationship(
                DocxConstants.DocumentPart,
                new OpcRelationship($"rId{nextRelationshipIndex++}", DocxConstants.EndnotesRelationship, "endnotes.xml"));
        }

        var revisionInfo = RevisionScan.Collect(document);
        DocxRevisions.Prepare(context, revisionInfo);
        if (revisionInfo.Elsewhere > 0)
        {
            handler.Report("DOCX: a tracked change in a header, footer or note is not written.");
        }

        var commentInfo = CommentScan.Collect(document);
        DocxComments.Prepare(context, commentInfo);
        if (commentInfo.Elsewhere > 0)
        {
            handler.Report("DOCX: a comment in a header, footer or note is not written; Word has no comments there.");
        }

        var needsExtendedComments = commentInfo.Body.Count > 0 && DocxComments.NeedsExtended(commentInfo.Body);
        if (commentInfo.Body.Count > 0)
        {
            package.AddOverride(DocxConstants.CommentsPart, DocxConstants.CommentsContentType);
            package.AddRelationship(
                DocxConstants.DocumentPart,
                new OpcRelationship($"rId{nextRelationshipIndex++}", DocxConstants.CommentsRelationship, "comments.xml"));
            if (needsExtendedComments)
            {
                package.AddOverride(DocxConstants.CommentsExtendedPart, DocxConstants.CommentsExtendedContentType);
                package.AddRelationship(
                    DocxConstants.DocumentPart,
                    new OpcRelationship($"rId{nextRelationshipIndex++}", DocxConstants.CommentsExtendedRelationship, "commentsExtended.xml"));
            }
        }

        // numbering.xml is written after document.xml and the headers/footers because
        // only then is it clear how many numbered lists need their own numbering.
        var preservedBlocks = ComputePreservedBlocks(document, sourceDocument.Body, package, ref nextRelationshipIndex, sourceNumbering is not null, SourceStylesPreserved(document), context.PreservedNoteIds);
        package.WriteXmlPart(DocxConstants.DocumentPart, writer => WriteDocument(writer, document, context, headerFooters, preservedBlocks, cancellationToken, options.Progress));

        foreach (var ((sectionIndex, band), resource) in headerFooters)
        {
            var section = document.Sections[sectionIndex];
            var isHeader = band is DocxBand.Header or DocxBand.HeaderFirst or DocxBand.HeaderEven;
            var preservedBandBlocks = ComputePreservedBandBlocks(
                document, sourceBands, sectionIndex, band, resource, package, sourceNumbering is not null, SourceStylesPreserved(document));
            package.WriteXmlPart(
                resource.PartName,
                writer => WriteHeaderFooter(writer, resource.Content, isHeader, ContentWidthTwips(section), resource.ImageRelationships, resource.HyperlinkRelationships, context, preservedBandBlocks));
        }

        if (context.HasFootnotes)
        {
            package.WriteXmlPart(DocxConstants.FootnotesPart, writer => WriteNotes(writer, context.Footnotes, isEndnote: false, context, footnoteLinks));
        }

        if (context.HasEndnotes)
        {
            package.WriteXmlPart(DocxConstants.EndnotesPart, writer => WriteNotes(writer, context.Endnotes, isEndnote: true, context, endnoteLinks));
        }

        if (commentInfo.Body.Count > 0)
        {
            package.WriteXmlPart(DocxConstants.CommentsPart, writer => DocxComments.WriteCommentsPart(writer, context, commentInfo.Body));
            if (needsExtendedComments)
            {
                package.WriteXmlPart(DocxConstants.CommentsExtendedPart, writer => DocxComments.WriteExtendedPart(writer, context, commentInfo.Body));
            }
        }

        WriteStylesPart(package, document, xmlBehavior, document.SourcePackage);
        WriteNumberingPart(package, context.UsedNumbering, firstAbstractNumId, sourceNumbering?.Root);
        package.WriteXmlPart(DocxConstants.CorePropertiesPart, writer => WriteCoreProperties(writer, document.Metadata, xmlBehavior));
        package.WriteXmlPart(DocxConstants.AppPropertiesPart, writer => WriteAppProperties(writer));
        if (customProperties is { Count: > 0 })
        {
            package.WriteXmlPart(
                DocxConstants.CustomPropertiesPart,
                writer => WriteCustomProperties(writer, customProperties, xmlBehavior));
        }
        if (document.Metadata.CustomXmlParts is { Count: > 0 } customXmlParts)
        {
            WriteCustomXmlParts(package, customXmlParts, ref nextRelationshipIndex);
        }
        if (document.Metadata.MacroProject is { } macroProject)
        {
            package.AddBinaryPart(DocxConstants.VbaProjectPart, macroProject.Data);
            package.AddOverride("/" + DocxConstants.VbaProjectPart, DocxConstants.VbaProjectContentType);
            package.AddRelationship(
                DocxConstants.DocumentPart,
                new OpcRelationship($"rId{nextRelationshipIndex++}", DocxConstants.VbaProjectRelationship, "vbaProject.bin"));
        }
        var fontsEmbedded = options.EmbeddedFonts is { } embeddedFonts
            && DocxFontEmbedding.Embed(package, document, embeddedFonts, ref nextRelationshipIndex, handler);
        package.WriteXmlPart(
            DocxConstants.SettingsPart,
            writer => WriteSettings(writer, evenAndOddHeaders, options.Protection, options.ProtectionPassword, document.Metadata.Variables, xmlBehavior, document.SourcePackage, options.AutoHyphenation, options.HyphenationZone, fontsEmbedded));

        // AF05: carry over source package parts the builder does not generate (comments, theme,
        // glossary, embeddings, unknown custom parts, …) so they survive a read→write round trip.
        PreserveSourceParts(package, document, ref nextRelationshipIndex, handler);
    }

    private static void PreserveSourceParts(OpcPackageWriter package, PortableDocument document, ref int nextRelationshipIndex, UnsupportedFeatureHandler handler)
    {
        if (document.SourcePackage is not { IsEmpty: false } sourcePackage)
        {
            return;
        }

        using var stream = new MemoryStream(sourcePackage.ToArray(), writable: false);
        using var source = new ZipArchive(stream, ZipArchiveMode.Read);

        // Only a DOCX source may be reused: the parts of an ODT would carry its whole original text along.
        if (source.GetEntry(DocxConstants.DocumentPart) is null)
        {
            return;
        }

        var contentTypes = SourceContentTypes.Read(source);
        var reachable = ReachableModelParts(package, source);
        var commentsModelled = ModelledCommentIds(sourcePackage.Span).Count > 0;
        var embeddingTargets = ReadSourceRelationships(source, DocxConstants.DocumentPart).Values
            .Where(r => r.Type is DocxEmbeddedObjects.OleObjectRelationship or DocxEmbeddedObjects.PackageRelationship)
            .Select(r => "word/" + r.Target)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelWritesComments = package.HasWritten(DocxConstants.CommentsPart);

        foreach (var entry in source.Entries)
        {
            var name = entry.FullName;
            if (name.Length == 0 || name.EndsWith('/') || package.HasWritten(name) || package.IsReserved(name))
            {
                continue;
            }

            // Headers, notes, media and charts come from the model; a source copy nothing points to any more
            // would keep removed content in the package.
            if (ModelPartOwner(name) is { } owner && !reachable.Contains(owner))
            {
                continue;
            }

            // The comments part is generated from the model (ADR-0026); a stale copy would keep deleted comments.
            if (commentsModelled && IsCommentsPart(name))
            {
                continue;
            }

            // The embedding parts of the document part are written from the model (ADR-0029); a stale copy would dangle.
            if (embeddingTargets.Contains(name))
            {
                continue;
            }

            // The VBA project is written from the model (ADR-0028); a stale copy would bring it back unasked.
            if (string.Equals(name, DocxConstants.VbaProjectPart, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "word/vbaData.xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A signature does not cover a changed document.
            if (name.StartsWith(DocxSignatureWriter.Folder, StringComparison.OrdinalIgnoreCase))
            {
                handler.Report("DOCX: the signature of the source document is removed; it does not cover the changed document.");
                continue;
            }

            package.AddBinaryPart(name, TemplatePackage.ReadEntry(entry));
            contentTypes.Register(package, name);
        }

        // Anchors the reader could not map still point at the source comments part; keep it reachable.
        if (!commentsModelled && !modelWritesComments)
        {
            foreach (var relationship in ReadSourceRelationships(source, DocxConstants.DocumentPart).Values
                .Where(relationship => relationship.Type.Contains("comments", StringComparison.OrdinalIgnoreCase)))
            {
                package.AddRelationship(
                    DocxConstants.DocumentPart,
                    new OpcRelationship($"rId{nextRelationshipIndex++}", relationship.Type, relationship.Target, relationship.TargetMode));
            }
        }
    }

    /// <summary>
    /// The ids of the source comments the reader maps into the model (ADR-0026); their anchors and their part
    /// are written from the model, not carried over.
    /// </summary>
    private static HashSet<string> ModelledCommentIds(ReadOnlySpan<byte> sourcePackage)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = new MemoryStream(sourcePackage.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.GetEntry(DocxConstants.CommentsPart) is not { } entry)
            {
                return ids;
            }

            XNamespace w = DocxConstants.WordprocessingML;
            foreach (var comment in ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Descendants(w + "comment"))
            {
                if (comment.Attribute(w + "id")?.Value is { } id
                    && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
        }

        return ids;
    }

    private static readonly System.Text.RegularExpressions.Regex ModelPartPattern = new(
        @"^word/(?:(?:header|footer)\d*\.xml|footnotes\.xml|endnotes\.xml|media/.+|charts/.+)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The part a source part belongs to when the model generates parts of its kind (header, footer, notes, media,
    /// chart, or the relationships part of one), else null.
    /// </summary>
    private static string? ModelPartOwner(string name)
    {
        var owner = name;
        var rels = name.LastIndexOf("_rels/", StringComparison.Ordinal);
        if (rels >= 0 && name.EndsWith(".rels", StringComparison.Ordinal))
        {
            owner = name[..rels] + name[(rels + "_rels/".Length)..^".rels".Length];
        }

        return ModelPartPattern.IsMatch(owner) ? owner : null;
    }

    /// <summary>
    /// The source parts of the model kinds that the written package still points to: directly through its
    /// relationships (e.g. a block kept verbatim) or through the source relationships of such a part.
    /// </summary>
    private static HashSet<string> ReachableModelParts(OpcPackageWriter package, ZipArchive source)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(package.InternalTargets());
        while (pending.Count > 0)
        {
            var part = pending.Pop();
            // A part the writer generated has its own new relationships; only a copied source part keeps the source ones.
            if (!reachable.Add(part) || package.HasWritten(part))
            {
                continue;
            }

            foreach (var relationship in ReadSourceRelationships(source, part).Values.Where(r => r.TargetMode != "External"))
            {
                pending.Push(OpcPackageWriter.ResolveTarget(part, relationship.Target));
            }
        }

        return reachable;
    }

    private static bool IsCommentsPart(string name)
        => name.StartsWith("word/comments", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>The content types of the source package, re-registered for preserved parts.</summary>
    private sealed class SourceContentTypes
    {
        private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase);

        public static SourceContentTypes Read(ZipArchive source)
        {
            var result = new SourceContentTypes();
            if (source.GetEntry("[Content_Types].xml") is not { } entry)
            {
                return result;
            }

            var xml = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry));
            foreach (var element in xml.Root?.Elements() ?? Enumerable.Empty<XElement>())
            {
                var partName = element.Attribute("PartName")?.Value;
                var extension = element.Attribute("Extension")?.Value;
                var contentType = element.Attribute("ContentType")?.Value;
                if (contentType is null)
                {
                    continue;
                }

                switch (element.Name.LocalName)
                {
                    case "Override" when partName is not null:
                        result._overrides[partName.TrimStart('/')] = contentType;
                        break;
                    case "Default" when extension is not null:
                        result._defaults[extension] = contentType;
                        break;
                }
            }

            return result;
        }

        public void Register(OpcPackageWriter package, string partName)
        {
            if (_overrides.TryGetValue(partName, out var contentType))
            {
                package.AddOverride("/" + partName, contentType);
                return;
            }

            var extension = Path.GetExtension(partName).TrimStart('.');
            if (extension.Length > 0 && _defaults.TryGetValue(extension, out var defaultContentType))
            {
                package.AddDefault(extension, defaultContentType);
            }
        }
    }

    /// <summary>
    /// Writes <c>settings.xml</c>. If the model carries a source package with a settings part,
    /// that part is adopted (AF08: compatibility flags, rsids and other settings stay stable) and
    /// only the elements this writer manages are merged in.
    /// </summary>
    /// <summary>
    /// Writes <c>styles.xml</c>. If the model neither adds named styles nor changes the document
    /// defaults, the source styles part is adopted (AF08: custom docDefaults, character/table/latent
    /// styles stay stable) and only the generated styles that are missing by <c>w:styleId</c> are
    /// merged in. Otherwise the part is regenerated.
    /// </summary>
    private static void WriteStylesPart(
        OpcPackageWriter package,
        PortableDocument document,
        InvalidXmlCharacterBehavior xmlBehavior,
        ReadOnlyMemory<byte>? sourcePackage)
    {
        // The source definitions stay only when the model did not change any of them; a changed
        // character style has to force the styles.xml to be regenerated.
        if ((document.Styles.Count == 0
                || NamedStyleComparer.Unchanged(document.Styles, document.SourceStyles))
            && NamedStyleComparer.CharacterStylesUnchanged(document.CharacterStyles, document.SourceCharacterStyles)
            && document.Defaults == document.SourceDefaults
            && ReadSourceStyles(sourcePackage) is { } sourceStyles)
        {
            MergeStyles(sourceStyles, RenderStyles(document, xmlBehavior));
            package.WriteXmlPart(DocxConstants.StylesPart, writer => sourceStyles.WriteTo(writer));
            return;
        }

        package.WriteXmlPart(
            DocxConstants.StylesPart,
            writer => WriteStyles(
                writer, document.Defaults, document.Metadata.Language, xmlBehavior,
                TableScan.CollectStyles(document), document.Styles, document.CharacterStyles));
    }

    internal static XElement RenderStyles(PortableDocument document, InvalidXmlCharacterBehavior xmlBehavior)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Document,
            Indent = false,
        }))
        {
            WriteStyles(
                writer, document.Defaults, document.Metadata.Language, xmlBehavior,
                TableScan.CollectStyles(document), document.Styles, document.CharacterStyles);
        }

        return XElement.Parse(builder.ToString());
    }

    private static XElement? ReadSourceStyles(ReadOnlyMemory<byte>? sourcePackage)
    {
        if (sourcePackage is not { IsEmpty: false } bytes)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var source = new ZipArchive(stream, ZipArchiveMode.Read);
        return source.GetEntry(DocxConstants.StylesPart) is { } entry
            ? ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Root
            : null;
    }

    private static void MergeStyles(XElement target, XElement generated)
    {
        XNamespace w = DocxConstants.WordprocessingML;
        var existing = target.Elements(w + "style")
            .Select(element => (string?)element.Attribute(w + "styleId"))
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var style in generated.Elements(w + "style").ToList())
        {
            var id = (string?)style.Attribute(w + "styleId");
            if (id is not null && existing.Add(id))
            {
                style.Remove();
                target.Add(style);
            }
        }
    }

    /// <summary>
    /// AF06: computes the source body elements that may be re-emitted verbatim for a changed
    /// body — top-level blocks that are still the exact instances the reader produced
    /// (reference equality) and whose XML references nothing the writer regenerates. Supports
    /// several sections and insertions or deletions in the block list.
    /// </summary>
    private static IReadOnlyDictionary<Block, XElement>? ComputePreservedBlocks(
        PortableDocument document,
        XElement? sourceBody,
        OpcPackageWriter package,
        ref int nextRelationshipIndex,
        bool numberingPreserved,
        bool stylesPreserved,
        IReadOnlySet<(bool Endnote, int Id)> preservedNoteIds)
    {
        if (document.SourcePackage is not { IsEmpty: false } bytes
            || document.SourceSections is not { Count: > 0 } sourceSections
            || sourceBody is null)
        {
            return null;
        }

        var modelledComments = ModelledCommentIds(bytes.Span);

        // Flatten across sections; section-break paragraphs and the body-level sectPr are
        // structural and regenerate from the model's sections.
        var sourceBlocks = sourceSections.SelectMany(section => section.Blocks).ToList();
        var currentBlocks = document.Sections.SelectMany(section => section.Blocks).ToList();
        if (sourceBlocks.Count == 0)
        {
            return null;
        }

        var sourceRelationships = new Dictionary<string, SourceRelationship>(StringComparer.Ordinal);
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var relsName = OpcPackageWriter.RelsPartName(DocxConstants.DocumentPart);
            if (archive.GetEntry(relsName) is { } relsEntry)
            {
                sourceRelationships = ReadSourceRelationships(ReaderGuard.LoadXml(TemplatePackage.ReadEntry(relsEntry)));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            return null;
        }

        XNamespace word = DocxConstants.WordprocessingML;
        var elements = sourceBody.Elements()
            .Where(element => (element.Name == word + "p" || element.Name == word + "tbl")
                && element.Element(word + "pPr")?.Element(word + "sectPr") is null)
            .ToList();
        if (elements.Count != sourceBlocks.Count)
        {
            return null;
        }

        var sourceIndex = new Dictionary<Block, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < sourceBlocks.Count; index++)
        {
            if (!sourceIndex.TryAdd(sourceBlocks[index], index))
            {
                sourceIndex[sourceBlocks[index]] = -1;
            }
        }

        var result = new Dictionary<Block, XElement>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<Block>(ReferenceEqualityComparer.Instance);
        foreach (var block in currentBlocks)
        {
            if (!seen.Add(block)
                || !sourceIndex.TryGetValue(block, out var index)
                || index < 0
                || !IsSelfContained(elements[index], numberingPreserved, stylesPreserved, modelledComments, preservedNoteIds)
                || !TryPreserveRelationships(elements[index], package, DocxConstants.DocumentPart, sourceRelationships, ref nextRelationshipIndex))
            {
                continue;
            }

            result[block] = elements[index];
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Whether a source block's XML references only things that survive the rewrite: relationship
    /// ids (images, hyperlinks) never do. Style references are safe when the source styles are
    /// adopted (AF08); numbering references are safe when the source numbering is preserved.
    /// </summary>
    private static bool IsSelfContained(
        XElement element,
        bool numberingPreserved,
        bool stylesPreserved,
        IReadOnlySet<string> modelledComments,
        IReadOnlySet<(bool Endnote, int Id)>? preservedNoteIds = null)
    {
        XNamespace w = DocxConstants.WordprocessingML;
        foreach (var node in element.DescendantsAndSelf())
        {
            if (node.Name == w + "pStyle" || node.Name == w + "rStyle" || node.Name == w + "tblStyle")
            {
                if (!stylesPreserved)
                {
                    return false;
                }
            }

            if (!numberingPreserved && node.Name == w + "numPr")
            {
                return false;
            }

            // Comments are modelled (ADR-0026): their anchors are written from the model, with the ids of
            // the comments part the writer generates.
            // An anchor whose comment the reader could not map stays preserved XML.
            if ((node.Name == w + "commentRangeStart" || node.Name == w + "commentRangeEnd" || node.Name == w + "commentReference")
                && node.Attribute(w + "id")?.Value is { } commentId
                && modelledComments.Contains(commentId))
            {
                return false;
            }

            // Tracked changes of the text are modelled (ADR-0027) and written from the model; a change of
            // the paragraph mark (w:ins in w:rPr) is not modelled and stays preserved.
            if ((node.Name == w + "ins" || node.Name == w + "del" || node.Name == w + "moveFrom" || node.Name == w + "moveTo")
                && node.Parent?.Name != w + "rPr")
            {
                return false;
            }

            // A note reference keeps its source id only when the writer could preserve that id.
            // Otherwise a preserved reference would point at the wrong note.
            if (node.Name == w + "footnoteReference" || node.Name == w + "endnoteReference")
            {
                var endnote = node.Name == w + "endnoteReference";
                if (preservedNoteIds is null
                    || !int.TryParse(node.Attribute(w + "id")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var noteId)
                    || !preservedNoteIds.Contains((endnote, noteId)))
                {
                    return false;
                }
            }

            // A bookmark range that crosses the block boundary loses its other half here (OF-C03).
            if ((node.Name == w + "bookmarkStart" || node.Name == w + "bookmarkEnd")
                && node.Attribute(w + "id")?.Value is { } bookmarkId
                && !SiblingBookmarkIds(element).Contains(bookmarkId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The ids of the bookmark starts and ends inside one block.</summary>
    private static HashSet<string> SiblingBookmarkIds(XElement element)
    {
        XNamespace w = DocxConstants.WordprocessingML;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in element.Descendants())
        {
            if (node.Name == w + "bookmarkStart" || node.Name == w + "bookmarkEnd")
            {
                if (node.Attribute(w + "id")?.Value is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private sealed record SourceDocumentInfo(XElement? Body, int MaxDrawingId, int MaxBookmarkId);

    private static SourceDocumentInfo ReadSourceDocument(ReadOnlyMemory<byte>? sourcePackage)
    {
        if (sourcePackage is not { IsEmpty: false } bytes)
        {
            return new SourceDocumentInfo(null, 0, 0);
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            if (archive.GetEntry(DocxConstants.DocumentPart) is not { } entry)
            {
                return new SourceDocumentInfo(null, 0, 0);
            }

            XNamespace w = DocxConstants.WordprocessingML;
            var document = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry));
            var body = document.Root?.Element(w + "body");
            return new SourceDocumentInfo(
                body,
                body is null ? 0 : MaxDrawingId(body),
                body is null ? 0 : MaxBookmarkId(body));
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            return new SourceDocumentInfo(null, 0, 0);
        }
    }

    /// <summary>
    /// a header/footer part of the source package with the relationships of that part, used to keep
    /// the band blocks that the model did not change (OF-B08).
    /// </summary>
    private sealed record SourceBand(
        string PartName,
        XElement Root,
        IReadOnlyDictionary<string, SourceRelationship> Relationships,
        int MaxDrawingId,
        int MaxBookmarkId);

    /// <summary>The band references of a <c>w:sectPr</c>, in the order the reader reads them.</summary>
    private static readonly (string Reference, string Type, bool IsHeader, DocxBand Band)[] BandReferences =
    [
        ("headerReference", "default", true, DocxBand.Header),
        ("footerReference", "default", false, DocxBand.Footer),
        ("headerReference", "first", true, DocxBand.HeaderFirst),
        ("footerReference", "first", false, DocxBand.FooterFirst),
        ("headerReference", "even", true, DocxBand.HeaderEven),
        ("footerReference", "even", false, DocxBand.FooterEven),
    ];

    /// <summary>
    /// Reads the band parts the sections of the source package reference, keyed by section index and
    /// band kind. The section properties are read from the source body in the same order the reader
    /// assigned them to its sections; when that count no longer matches the model, band preservation
    /// is skipped entirely.
    /// </summary>
    private static Dictionary<(int SectionIndex, DocxBand Band), SourceBand> ReadSourceBands(
        PortableDocument document,
        XElement? sourceBody)
    {
        var result = new Dictionary<(int, DocxBand), SourceBand>();
        if (document.SourcePackage is not { IsEmpty: false } bytes
            || document.SourceSections is not { Count: > 0 } sourceSections
            || sourceBody is null)
        {
            return result;
        }

        XNamespace w = DocxConstants.WordprocessingML;
        var sectionProperties = sourceBody.Elements()
            .Select(element => element.Name == w + "sectPr"
                ? element
                : element.Element(w + "pPr")?.Element(w + "sectPr"))
            .Where(element => element is not null)
            .ToList();
        if (sectionProperties.Count != sourceSections.Count)
        {
            return result;
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var targets = ReadSourceRelationshipTargets(archive, DocxConstants.DocumentPart);
            for (var sectionIndex = 0; sectionIndex < sectionProperties.Count; sectionIndex++)
            {
                foreach (var (reference, type, isHeader, band) in BandReferences)
                {
                    if (BandReference(sectionProperties[sectionIndex]!, reference, type) is not { } relationshipId
                        || !targets.TryGetValue(relationshipId, out var partName)
                        || ReadSourcePart(archive, partName) is not { } root
                        || root.Name != w + (isHeader ? "hdr" : "ftr"))
                    {
                        continue;
                    }

                    result[(sectionIndex, band)] = new SourceBand(
                        partName,
                        root,
                        ReadSourceRelationships(archive, partName),
                        MaxDrawingId(root),
                        MaxBookmarkId(root));
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or UriFormatException)
        {
            result.Clear();
        }

        return result;
    }

    /// <summary>
    /// OF-B08: the blocks of one band that may be re-emitted as their source XML — the blocks that
    /// are still the exact instances the reader produced and whose XML references nothing the writer
    /// regenerates. The rules are the ones of <see cref="ComputePreservedBlocks"/> for the body.
    /// </summary>
    private static IReadOnlyDictionary<Block, XElement>? ComputePreservedBandBlocks(
        PortableDocument document,
        Dictionary<(int SectionIndex, DocxBand Band), SourceBand> sourceBands,
        int sectionIndex,
        DocxBand band,
        HeaderFooterResource resource,
        OpcPackageWriter package,
        bool numberingPreserved,
        bool stylesPreserved)
    {
        if (!sourceBands.TryGetValue((sectionIndex, band), out var source)
            || document.SourceSections is not { } sourceSections
            || sectionIndex >= sourceSections.Count
            || SourceBandOf(sourceSections[sectionIndex], band) is not { } sourceBand
            || sourceBand.Blocks.Count == 0)
        {
            return null;
        }

        XNamespace word = DocxConstants.WordprocessingML;
        var elements = source.Root.Elements()
            .Where(element => element.Name == word + "p" || element.Name == word + "tbl")
            .ToList();
        if (elements.Count != sourceBand.Blocks.Count)
        {
            return null;
        }

        var sourceIndex = new Dictionary<Block, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < sourceBand.Blocks.Count; index++)
        {
            if (!sourceIndex.TryAdd(sourceBand.Blocks[index], index))
            {
                sourceIndex[sourceBand.Blocks[index]] = -1;
            }
        }

        var result = new Dictionary<Block, XElement>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<Block>(ReferenceEqualityComparer.Instance);
        var nextRelationshipIndex = resource.NextRelationshipIndex;
        foreach (var block in resource.Content.Blocks)
        {
            if (!seen.Add(block)
                || !sourceIndex.TryGetValue(block, out var index)
                || index < 0
                || !IsSelfContained(elements[index], numberingPreserved, stylesPreserved, document.SourcePackage is { } sourceBytes ? ModelledCommentIds(sourceBytes.Span) : new HashSet<string>())
                || !TryPreserveRelationships(
                    elements[index], package, resource.PartName, source.Relationships, ref nextRelationshipIndex))
            {
                continue;
            }

            result[block] = elements[index];
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>The band of a source section for one band kind.</summary>
    private static HeaderFooter? SourceBandOf(Section section, DocxBand band)
        => band switch
        {
            DocxBand.Header => section.Header,
            DocxBand.Footer => section.Footer,
            DocxBand.HeaderFirst => section.FirstHeader,
            DocxBand.FooterFirst => section.FirstFooter,
            DocxBand.HeaderEven => section.EvenHeader,
            DocxBand.FooterEven => section.EvenFooter,
            _ => null,
        };

    private static int BandMaxDrawingId(IEnumerable<SourceBand> bands)
        => bands.Select(band => band.MaxDrawingId).DefaultIfEmpty(0).Max();

    private static int BandMaxBookmarkId(IEnumerable<SourceBand> bands)
        => bands.Select(band => band.MaxBookmarkId).DefaultIfEmpty(0).Max();

    /// <summary>The relationship id of a band reference of a section property element.</summary>
    private static string? BandReference(XElement sectPr, string elementName, string type)
    {
        XNamespace w = DocxConstants.WordprocessingML;
        XNamespace r = DocxConstants.Relationships;
        foreach (var reference in sectPr.Elements(w + elementName))
        {
            if ((reference.Attribute(w + "type")?.Value ?? "default") == type)
            {
                return reference.Attribute(r + "id")?.Value;
            }
        }

        return null;
    }

    /// <summary>The internal targets of the relationships of a part, resolved to package part names.</summary>
    private static Dictionary<string, string> ReadSourceRelationshipTargets(ZipArchive archive, string sourcePart)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ReadSourcePartDocument(archive, OpcPackageWriter.RelsPartName(sourcePart)) is not { } relationships)
        {
            return result;
        }

        var baseUri = new Uri("http://package.local/" + sourcePart);
        foreach (var relationship in relationships.Root?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var id = relationship.Attribute("Id")?.Value;
            var target = relationship.Attribute("Target")?.Value;
            if (id is null || target is null || relationship.Attribute("TargetMode")?.Value == "External")
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, target, out var resolved)
                && resolved.Host == "package.local"
                && resolved.Query.Length == 0
                && resolved.Fragment.Length == 0)
            {
                result[id] = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
            }
        }

        return result;
    }

    /// <summary>The relationships of a source part, keyed by their id.</summary>
    private static IReadOnlyDictionary<string, SourceRelationship> ReadSourceRelationships(
        ZipArchive archive,
        string sourcePart)
        => ReadSourcePartDocument(archive, OpcPackageWriter.RelsPartName(sourcePart)) is { } relationships
            ? ReadSourceRelationships(relationships)
            : new Dictionary<string, SourceRelationship>(StringComparer.Ordinal);

    private static XDocument? ReadSourcePartDocument(ZipArchive archive, string partName)
    {
        if (archive.GetEntry(partName) is not { } entry)
        {
            return null;
        }

        try
        {
            return ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry));
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            return null;
        }
    }

    /// <summary>Reads the root element of a package part; null when it is missing or malformed.</summary>
    private static XElement? ReadSourcePart(ZipArchive archive, string partName)
        => ReadSourcePartDocument(archive, partName)?.Root;

    private static int MaxDrawingId(XElement root)
    {
        XNamespace wp = DocxConstants.DrawingWordprocessingNamespace;
        return root.Descendants(wp + "docPr")
            .Select(docPr => int.TryParse(
                docPr.Attribute("id")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static int MaxBookmarkId(XElement root)
    {
        XNamespace w = DocxConstants.WordprocessingML;
        return root.Descendants(w + "bookmarkStart").Concat(root.Descendants(w + "bookmarkEnd"))
            .Select(bookmark => int.TryParse(
                bookmark.Attribute(w + "id")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    internal static int NextId(int max, string what)
        => max == int.MaxValue
            ? throw new DocumentValidationException($"DOCX: the source {what} space is exhausted.")
            : max + 1;

    private static Dictionary<string, SourceRelationship> ReadSourceRelationships(XDocument relationships)
    {
        var result = new Dictionary<string, SourceRelationship>(StringComparer.Ordinal);
        foreach (var relationship in relationships.Root?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var id = relationship.Attribute("Id")?.Value;
            var type = relationship.Attribute("Type")?.Value;
            var target = relationship.Attribute("Target")?.Value;
            var targetMode = relationship.Attribute("TargetMode")?.Value;
            if (id is not null && type is not null && target is not null)
            {
                result[id] = new SourceRelationship(type, target, targetMode);
            }
        }

        return result;
    }

    /// <summary>
    /// Preserves the source relationships a kept block references: each <c>r:*</c> id is copied as
    /// a fresh relationship with a new id and rewritten in the element. Returns false when a
    /// referenced relationship is missing, so the block is regenerated instead.
    /// </summary>
    private static bool TryPreserveRelationships(
        XElement element,
        OpcPackageWriter package,
        string sourcePart,
        IReadOnlyDictionary<string, SourceRelationship> sourceRelationships,
        ref int nextRelationshipIndex)
    {
        XNamespace r = DocxConstants.Relationships;
        foreach (var attribute in element.DescendantsAndSelf().SelectMany(node => node.Attributes()).ToList())
        {
            if (attribute.Name.Namespace != r)
            {
                continue;
            }

            if (!sourceRelationships.TryGetValue(attribute.Value, out var relationship))
            {
                return false;
            }

            var relationshipId = $"rId{nextRelationshipIndex++}";
            package.AddRelationship(
                sourcePart,
                new OpcRelationship(relationshipId, relationship.Type, relationship.Target, relationship.TargetMode));
            attribute.Value = relationshipId;
        }

        return true;
    }

    private sealed record SourceRelationship(string Type, string Target, string? TargetMode);

    private static bool SourceStylesPreserved(PortableDocument document)
        => (document.Styles.Count == 0
            || NamedStyleComparer.Unchanged(document.Styles, document.SourceStyles))
            && NamedStyleComparer.CharacterStylesUnchanged(document.CharacterStyles, document.SourceCharacterStyles)
            && document.Defaults == document.SourceDefaults
            && ReadSourceStyles(document.SourcePackage) is not null;

    private static void WriteSettings(
        XmlWriter writer,
        bool evenAndOddHeaders,
        DocxProtection protection,
        string? password,
        IReadOnlyDictionary<string, string>? variables,
        InvalidXmlCharacterBehavior xmlBehavior,
        ReadOnlyMemory<byte>? sourcePackage,
        bool autoHyphenation = false,
        Length? hyphenationZone = null,
        bool embedFonts = false)
    {
        if (ReadSourceSettings(sourcePackage) is { } settings)
        {
            MergeSettings(settings, evenAndOddHeaders, protection, password, variables, xmlBehavior, autoHyphenation, hyphenationZone);
            settings.WriteTo(writer);
            return;
        }

        writer.WriteStartElement("w", "settings", DocxConstants.WordprocessingML);
        if (embedFonts)
        {
            // In CT_Settings w:embedTrueTypeFonts comes before w:documentProtection.
            writer.WriteStartElement("w", "embedTrueTypeFonts", DocxConstants.WordprocessingML);
            writer.WriteEndElement();
        }

        if (protection is not (DocxProtection.None or DocxProtection.Remove))
        {
            writer.WriteStartElement("w", "documentProtection", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "edit", ProtectionValue(protection));
            WriteWAttribute(writer, "enforcement", "1");
            if (password is { } passwordText)
            {
                // ISO/IEC 29500-1, 17.15.1.29: salted hash with an iteration count.
                var salt = DocxPasswordHash.CreateSalt();
                var (hashValue, saltValue) = DocxPasswordHash.Compute(passwordText, salt);
                WriteWAttribute(writer, "algorithmName", DocxPasswordHash.AlgorithmName);
                WriteWAttribute(writer, "hashValue", hashValue);
                WriteWAttribute(writer, "saltValue", saltValue);
                WriteWAttribute(writer, "spinCount", DocxPasswordHash.SpinCount.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndElement();
        }

        // BJ01: the automatic hyphenation of Word. In CT_Settings the elements come before
        // w:evenAndOddHeaders.
        if (autoHyphenation)
        {
            writer.WriteStartElement("w", "autoHyphenation", DocxConstants.WordprocessingML);
            writer.WriteEndElement();

            writer.WriteStartElement("w", "hyphenationZone", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", Twips(hyphenationZone ?? Length.FromInches(0.25)));
            writer.WriteEndElement();
        }

        if (evenAndOddHeaders)
        {
            writer.WriteStartElement("w", "evenAndOddHeaders", DocxConstants.WordprocessingML);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "compat", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "compatSetting", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "name", "compatibilityMode");
        WriteWAttribute(writer, "uri", DocxConstants.WordCompatibilityUri);
        WriteWAttribute(writer, "val", DocxConstants.CompatibilityMode);
        writer.WriteEndElement();
        writer.WriteEndElement();

        // Document variables (DOCVARIABLE) follow w:compat in CT_Settings.
        if (DocumentVariables(variables, xmlBehavior) is { } docVars)
        {
            docVars.WriteTo(writer);
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Inserts an element into the settings part at the position that CT_Settings requires
    /// (the element order of the schema is a sequence).
    /// </summary>
    private static void InsertSetting(XElement settings, XElement element)
    {
        // CT_Settings up to w:compat, in schema order; anything else comes later.
        string[] order =
        [
            "writeProtection", "view", "zoom", "removePersonalInformation", "removeDateAndTime",
            "doNotDisplayPageBoundaries", "displayBackgroundShape", "printPostScriptOverText",
            "printFractionalCharacterWidth", "printFormsData", "embedTrueTypeFonts", "embedSystemFonts",
            "saveSubsetFonts", "saveFormsData", "mirrorMargins", "alignBordersAndEdges",
            "bordersDoNotSurroundHeader", "bordersDoNotSurroundFooter", "gutterAtTop",
            "hideSpellingErrors", "hideGrammaticalErrors", "activeWritingStyle", "proofState",
            "formsDesign", "attachedTemplate", "linkStyles", "stylePaneFormatFilter",
            "stylePaneSortMethod", "documentType", "mailMerge", "revisionView", "trackRevisions",
            "doNotTrackMoves", "doNotTrackFormatting", "documentProtection", "autoFormatOverride",
            "styleLockTheme", "styleLockQFSet", "defaultTabStop", "autoHyphenation",
            "consecutiveHyphenLimit", "hyphenationZone", "doNotHyphenateCaps", "showEnvelope",
            "summaryLength", "clickAndTypeStyle", "defaultTableStyle", "evenAndOddHeaders",
            "bookFoldRevPrinting", "bookFoldPrinting", "bookFoldPrintingSheets",
            "drawingGridHorizontalSpacing", "drawingGridVerticalSpacing",
            "displayHorizontalDrawingGridEvery", "displayVerticalDrawingGridEvery",
            "doNotUseMarginsForDrawingGridOrigin", "drawingGridHorizontalOrigin",
            "drawingGridVerticalOrigin", "doNotShadeFormData", "noPunctuationKerning",
            "characterSpacingControl", "printTwoOnOne", "strictFirstAndLastChars",
            "noLineBreaksAfter", "noLineBreaksBefore", "savePreviewPicture",
            "doNotValidateAgainstSchema", "saveInvalidXml", "ignoreMixedContent",
            "alwaysShowPlaceholderText", "doNotDemarcateInvalidXml", "saveXmlDataOnly",
            "useXSLTWhenSaving", "saveThroughXslt", "showXMLTags", "alwaysMergeEmptyNamespace",
            "updateFields", "hdrShapeDefaults", "footnotePr", "endnotePr", "compat",
        ];

        var position = Array.IndexOf(order, element.Name.LocalName);
        var following = settings.Elements()
            .FirstOrDefault(child => Array.IndexOf(order, child.Name.LocalName) is var index && (index < 0 || index > position));

        if (following is null)
        {
            settings.Add(element);
            return;
        }

        following.AddBeforeSelf(element);
    }

    /// <summary>The <c>w:docVars</c> element for the document variables; null when there are none.</summary>
    private static XElement? DocumentVariables(IReadOnlyDictionary<string, string>? variables, InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (variables is not { Count: > 0 })
        {
            return null;
        }

        XNamespace w = DocxConstants.WordprocessingML;
        return new XElement(
            w + "docVars",
            variables.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => new XElement(
                w + "docVar",
                new XAttribute(w + "name", XmlText.Clean(entry.Key, xmlBehavior)),
                new XAttribute(w + "val", XmlText.Clean(entry.Value, xmlBehavior)))));
    }

    private static XElement? ReadSourceSettings(ReadOnlyMemory<byte>? sourcePackage)
    {
        if (sourcePackage is not { IsEmpty: false } bytes)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var source = new ZipArchive(stream, ZipArchiveMode.Read);
        return source.GetEntry(DocxConstants.SettingsPart) is { } entry
            ? ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Root
            : null;
    }

    internal static void MergeSettings(
        XElement settings,
        bool evenAndOddHeaders,
        DocxProtection protection,
        string? password,
        IReadOnlyDictionary<string, string>? variables,
        InvalidXmlCharacterBehavior xmlBehavior,
        bool autoHyphenation = false,
        Length? hyphenationZone = null)
    {
        XNamespace w = DocxConstants.WordprocessingML;
        var existingProtection = settings.Elements(w + "documentProtection").ToList();
        if (protection != DocxProtection.None)
        {
            foreach (var element in existingProtection)
            {
                element.Remove();
            }
        }

        if (protection is not (DocxProtection.None or DocxProtection.Remove))
        {
            var protectionElement = new XElement(
                w + "documentProtection",
                new XAttribute(w + "edit", ProtectionValue(protection)),
                new XAttribute(w + "enforcement", "1"));
            if (password is { } passwordText)
            {
                // ISO/IEC 29500-1, 17.15.1.29: salted hash with an iteration count.
                var salt = DocxPasswordHash.CreateSalt();
                var (hashValue, saltValue) = DocxPasswordHash.Compute(passwordText, salt);
                protectionElement.Add(new XAttribute(w + "algorithmName", DocxPasswordHash.AlgorithmName));
                protectionElement.Add(new XAttribute(w + "hashValue", hashValue));
                protectionElement.Add(new XAttribute(w + "saltValue", saltValue));
                protectionElement.Add(new XAttribute(w + "spinCount", DocxPasswordHash.SpinCount.ToString(CultureInfo.InvariantCulture)));
            }

            InsertSetting(settings, protectionElement);
        }

        if (autoHyphenation)
        {
            // BJ01: the automatic hyphenation of Word. The element is placed before the
            // elements that follow it in CT_Settings.
            settings.Elements(w + "autoHyphenation").Remove();
            settings.Elements(w + "hyphenationZone").Remove();
            InsertSetting(settings, new XElement(w + "autoHyphenation"));
            InsertSetting(settings, new XElement(
                w + "hyphenationZone",
                new XAttribute(w + "val", Twips(hyphenationZone ?? Length.FromInches(0.25)))));
        }

        if (evenAndOddHeaders && !settings.Elements(w + "evenAndOddHeaders").Any())
        {
            InsertSetting(settings, new XElement(w + "evenAndOddHeaders"));
        }

        if (!settings.Elements(w + "compat").Any())
        {
            InsertSetting(settings, new XElement(
                w + "compat",
                new XElement(
                    w + "compatSetting",
                    new XAttribute(w + "name", "compatibilityMode"),
                    new XAttribute(w + "uri", DocxConstants.WordCompatibilityUri),
                    new XAttribute(w + "val", DocxConstants.CompatibilityMode))));
        }

        // The model's variables replace the source's (the reader maps them into the model);
        // without variables in the model the source's stay untouched.
        if (variables is not null)
        {
            foreach (var element in settings.Elements(w + "docVars").ToList())
            {
                element.Remove();
            }

            if (DocumentVariables(variables, xmlBehavior) is { } docVars)
            {
                if (settings.Elements(w + "compat").LastOrDefault() is { } compat)
                {
                    compat.AddAfterSelf(docVars);
                }
                else
                {
                    settings.Add(docVars);
                }
            }
        }
    }

    private static string ProtectionValue(DocxProtection protection) => protection switch
    {
        DocxProtection.Comments => "comments",
        DocxProtection.TrackedChanges => "trackedChanges",
        DocxProtection.Forms => "forms",
        DocxProtection.Remove => throw new ArgumentOutOfRangeException(nameof(protection), protection, "Remove is not a protection mode."),
        _ => "readOnly",
    };

    private static void RegisterParts(OpcPackageWriter package, bool macroEnabled)
    {
        package.AddOverride(
            DocxConstants.DocumentPart,
            macroEnabled ? DocxConstants.MacroEnabledDocumentContentType : DocxConstants.DocumentContentType);
        package.AddOverride(DocxConstants.StylesPart, DocxConstants.StylesContentType);
        package.AddOverride(DocxConstants.NumberingPart, DocxConstants.NumberingContentType);
        package.AddOverride(DocxConstants.SettingsPart, DocxConstants.SettingsContentType);
        package.AddOverride(DocxConstants.CorePropertiesPart, DocxConstants.CorePropertiesContentType);
        package.AddOverride(DocxConstants.AppPropertiesPart, DocxConstants.AppPropertiesContentType);
    }

    private static void RegisterRelationships(OpcPackageWriter package)
    {
        package.AddRelationship(
            string.Empty,
            new OpcRelationship("rId1", DocxConstants.OfficeDocumentRelationship, DocxConstants.DocumentPart));
        package.AddRelationship(
            string.Empty,
            new OpcRelationship("rId2", DocxConstants.CorePropertiesRelationship, DocxConstants.CorePropertiesPart));
        package.AddRelationship(
            string.Empty,
            new OpcRelationship("rId3", DocxConstants.AppPropertiesRelationship, DocxConstants.AppPropertiesPart));

        package.AddRelationship(
            DocxConstants.DocumentPart,
            new OpcRelationship("rId1", DocxConstants.StylesRelationship, "styles.xml"));
        package.AddRelationship(
            DocxConstants.DocumentPart,
            new OpcRelationship("rId2", DocxConstants.NumberingRelationship, "numbering.xml"));
        package.AddRelationship(
            DocxConstants.DocumentPart,
            new OpcRelationship("rId3", DocxConstants.SettingsRelationship, "settings.xml"));
    }

    private static void WriteDocument(
        XmlWriter writer,
        PortableDocument document,
        DocxWriteContext context,
        IReadOnlyDictionary<(int SectionIndex, DocxBand Band), HeaderFooterResource> headerFooters,
        IReadOnlyDictionary<Block, XElement>? preservedBlocks,
        CancellationToken cancellationToken,
        IProgress<WriteProgress>? progress)
    {
        writer.WriteStartElement("w", "document", DocxConstants.WordprocessingML);
        DocxConstants.WriteCompatibilityNamespaces(writer);
        writer.WriteStartElement("w", "body", DocxConstants.WordprocessingML);

        for (var sectionIndex = 0; sectionIndex < document.Sections.Count; sectionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = document.Sections[sectionIndex];
            var availableWidth = ContentWidthTwips(section);
            if (!section.HeaderPushesBody)
            {
                context.Handler.Report("DOCX: a header that does not push the body is not supported; Word starts the body below a header taller than the top margin.");
            }

            foreach (var block in section.Blocks)
            {
                // AF06: re-emit an unmodified, self-contained source block verbatim (keeps
                // unmodelled XML); everything else is generated from the model.
                if (preservedBlocks is not null && preservedBlocks.TryGetValue(block, out var sourceElement))
                {
                    sourceElement.WriteTo(writer);
                }
                else
                {
                    WriteBlock(writer, block, availableWidth, context);
                }
            }

            progress?.Report(new WriteProgress("section", sectionIndex + 1, document.Sections.Count));

            if (sectionIndex < document.Sections.Count - 1)
            {
                writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
                writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
                WriteSectionProperties(writer, section, sectionIndex, headerFooters);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
        }

        WriteSectionProperties(writer, document.Sections[^1], document.Sections.Count - 1, headerFooters);

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static long ContentWidthTwips(Section section)
    {
        var landscape = section.Orientation == PageOrientation.Landscape;
        var width = landscape ? section.PageSize.Height : section.PageSize.Width;
        return Twips(width) - Twips(section.Margins.Left) - Twips(section.Margins.Right);
    }

    internal static void WriteBlock(
        XmlWriter writer,
        Block block,
        long availableWidth,
        DocxWriteContext context)
    {
        switch (block)
        {
            case Paragraph paragraph:
                WriteParagraph(writer, paragraph, context, compact: context.Spacing.IsCompact(paragraph));
                break;
            case Heading heading:
                WriteParagraph(writer, heading.Style, heading.Inlines, context);
                break;
            case ListBlock list:
                WriteListBlock(writer, list, availableWidth, context);
                break;
            case TableBlock table:
                WriteTableBlock(writer, table, availableWidth, context);
                break;
            case TableOfContents toc:
                WriteTableOfContents(writer, toc, context);
                break;
            case TableOfFigures figures:
                WriteTableOfFigures(writer, figures, context);
                break;
            case AlphabeticalIndex index:
                WriteAlphabeticalIndex(writer, index, context);
                break;
            case ImageBlock image:
                WriteImageBlock(writer, image, context);
                break;
            case DrawingShape shape:
                WriteDrawingShape(writer, shape, availableWidth, context);
                break;
            case FormField field:
                DocxFormControls.Write(writer, field, context);
                break;
            case Equation equation when context.BandName is null:
                DocxMath.Write(writer, equation);
                break;
            case Equation:
                context.Handler.Report("DOCX: an equation in a header or footer is not written.");
                break;
            case Diagram diagram when context.BandName is null:
                WriteDiagram(writer, diagram, availableWidth, context);
                break;
            case Diagram:
                context.Handler.Report("DOCX: a diagram in a header or footer is not written.");
                break;
            case Chart chart when context.BandName is null && context.ChartRelationships.TryGetValue(chart, out var chartRelationship):
                DocxCharts.WriteInline(writer, chart, chartRelationship, context.NextDrawingId(), context.XmlBehavior, availableWidth);
                break;
            case Chart:
                context.Handler.Report("DOCX: a chart in a header or footer is not written.");
                break;
            case EmbeddedObject embedded when context.BandName is null && context.ObjectParts.TryGetValue(embedded, out var objectParts):
                DocxEmbeddedObjects.Write(writer, embedded, objectParts, availableWidth, context.XmlBehavior);
                break;
            case EmbeddedObject:
                context.Handler.Report("DOCX: an embedded object outside the body is not written.");
                break;
            case ColumnBreak when context.BandName is { } band:
                context.Handler.Report($"DOCX: column breaks in the {band} are omitted.");
                break;
            case ColumnBreak:
                WriteColumnBreak(writer);
                break;
            case PageBreak when context.BandName is { } band:
                context.Handler.Report($"DOCX: page breaks in the {band} are omitted.");
                break;
            case PageBreak:
                WritePageBreak(writer);
                break;
        }
    }

    private const string WordprocessingShapeNamespace = DocxConstants.WordprocessingShapeNamespace;
    private const string WordprocessingGroupNamespace = DocxConstants.WordprocessingGroupNamespace;

    /// <summary>
    /// A shape or text box (ADR-0022): <c>wps:wsp</c> in an inline or anchored drawing. Word 2010 and
    /// newer read it directly; the text of a text box is written like a body paragraph.
    /// </summary>
    private static void WriteDrawingShape(
        XmlWriter writer, DrawingShape shape, long availableWidth, DocxWriteContext context)
    {
        var drawingId = context.NextDrawingId();
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "drawing", DocxConstants.WordprocessingML);

        var anchored = shape.Wrap != ImageWrap.None;
        writer.WriteStartElement("wp", anchored ? "anchor" : "inline", DocxConstants.DrawingWordprocessingNamespace);
        WriteImageDistances(writer);
        if (anchored)
        {
            writer.WriteAttributeString("simplePos", "0");
            writer.WriteAttributeString("relativeHeight", "251658240");
            writer.WriteAttributeString("behindDoc", shape.Wrap == ImageWrap.Behind ? "1" : "0");
            writer.WriteAttributeString("locked", "0");
            writer.WriteAttributeString("layoutInCell", "1");
            writer.WriteAttributeString("allowOverlap", "1");
            writer.WriteStartElement("wp", "simplePos", DocxConstants.DrawingWordprocessingNamespace);
            writer.WriteAttributeString("x", "0");
            writer.WriteAttributeString("y", "0");
            writer.WriteEndElement();
            WriteImagePositions(writer, FloatPlacement.Of(shape));
        }

        WriteImageExtent(writer, shape.Width, shape.Height);
        if (anchored)
        {
            writer.WriteStartElement("wp", "effectExtent", DocxConstants.DrawingWordprocessingNamespace);
            foreach (var side in new[] { "l", "t", "r", "b" })
            {
                writer.WriteAttributeString(side, "0");
            }

            writer.WriteEndElement();
            WriteImageWrap(writer, shape.Wrap);
        }

        writer.WriteStartElement("wp", "docPr", DocxConstants.DrawingWordprocessingNamespace);
        writer.WriteAttributeString("id", drawingId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", $"Shape {drawingId}");
        if (!string.IsNullOrEmpty(shape.AltText))
        {
            writer.WriteAttributeString("descr", XmlText.Clean(shape.AltText, context.XmlBehavior));
        }

        writer.WriteEndElement();

        writer.WriteStartElement("a", "graphic", DocxConstants.DrawingMainNamespace);
        writer.WriteStartElement("a", "graphicData", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("uri", WordprocessingShapeNamespace);
        WriteShapeBody(writer, shape, 0, 0, context);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>The <c>wps:wsp</c> of a shape at an offset (EMU) in its parent: the drawing itself or a group.</summary>
    private static void WriteShapeBody(XmlWriter writer, DrawingShape shape, long offsetX, long offsetY, DocxWriteContext context, double insetScale = 1)
    {
        writer.WriteStartElement("wps", "wsp", WordprocessingShapeNamespace);
        writer.WriteStartElement("wps", "cNvSpPr", WordprocessingShapeNamespace);
        if (shape.Kind == ShapeKind.TextBox)
        {
            writer.WriteAttributeString("txBox", "1");
        }

        writer.WriteEndElement();

        writer.WriteStartElement("wps", "spPr", WordprocessingShapeNamespace);
        writer.WriteStartElement("a", "xfrm", DocxConstants.DrawingMainNamespace);
        if (shape.Rotation != 0)
        {
            writer.WriteAttributeString("rot", ((long)Math.Round(shape.Rotation * 60000.0)).ToString(CultureInfo.InvariantCulture));
        }

        WriteDrawingPair(writer, "off", "x", "y", offsetX, offsetY);
        WriteDrawingPair(writer, "ext", "cx", "cy", Emu(shape.Width), Emu(shape.Height));
        writer.WriteEndElement();
        WriteShapeGeometry(writer, shape);
        WriteShapeColor(writer, "solidFill", shape.Fill);
        if (shape.Fill is null)
        {
            writer.WriteStartElement("a", "noFill", DocxConstants.DrawingMainNamespace);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("a", "ln", DocxConstants.DrawingMainNamespace);
        if (shape.Outline is { } outline)
        {
            writer.WriteAttributeString("w", Emu(shape.OutlineWidth).ToString(CultureInfo.InvariantCulture));
            WriteShapeColor(writer, "solidFill", outline);
            if (shape.Dash != ShapeDash.Solid)
            {
                writer.WriteStartElement("a", "prstDash", DocxConstants.DrawingMainNamespace);
                writer.WriteAttributeString("val", shape.Dash == ShapeDash.Dash ? "dash" : "sysDot");
                writer.WriteEndElement();
            }
        }
        else
        {
            writer.WriteStartElement("a", "noFill", DocxConstants.DrawingMainNamespace);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        if (shape.Content is { } content)
        {
            writer.WriteStartElement("wps", "txbx", WordprocessingShapeNamespace);
            writer.WriteStartElement("w", "txbxContent", DocxConstants.WordprocessingML);
            foreach (var block in content)
            {
                WriteBlock(writer, block, Math.Max(1, Twips(shape.Width)), context);
            }

            if (content.Count == 0)
            {
                // A text box needs at least one paragraph.
                writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteStartElement("wps", "bodyPr", WordprocessingShapeNamespace);
        if (insetScale < 1)
        {
            // A shrunk diagram shrinks the text margins too (Word's defaults: 0.1 in left and right, 0.05 in top and bottom).
            foreach (var (name, emu) in new[] { ("lIns", 91440), ("tIns", 45720), ("rIns", 91440), ("bIns", 45720) })
            {
                writer.WriteAttributeString(name, ((long)Math.Round(emu * insetScale, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    /// <summary>A diagram (ADR-0030): an inline group of the shapes it expands to, scaled down to the available width.</summary>
    private static void WriteDiagram(XmlWriter writer, Diagram diagram, long availableWidth, DocxWriteContext context)
    {
        var drawingId = context.NextDrawingId();
        var scale = Math.Min(1.0, Math.Max(1, availableWidth) / (double)Twips(diagram.Width));
        var extentX = (long)Math.Round(Emu(diagram.Width) * scale, MidpointRounding.AwayFromZero);
        var extentY = (long)Math.Round(Emu(diagram.Height) * scale, MidpointRounding.AwayFromZero);
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "drawing", DocxConstants.WordprocessingML);
        writer.WriteStartElement("wp", "inline", DocxConstants.DrawingWordprocessingNamespace);
        WriteImageDistances(writer);
        // wp:extent, not a:extent: Word rejects an inline drawing without its own extent.
        writer.WriteStartElement("wp", "extent", DocxConstants.DrawingWordprocessingNamespace);
        writer.WriteAttributeString("cx", extentX.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("cy", extentY.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteStartElement("wp", "docPr", DocxConstants.DrawingWordprocessingNamespace);
        writer.WriteAttributeString("id", drawingId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", $"Diagram {drawingId}");
        if (!string.IsNullOrEmpty(diagram.AltText))
        {
            writer.WriteAttributeString("descr", XmlText.Clean(diagram.AltText, context.XmlBehavior));
        }

        writer.WriteEndElement();
        writer.WriteStartElement("a", "graphic", DocxConstants.DrawingMainNamespace);
        writer.WriteStartElement("a", "graphicData", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("uri", WordprocessingGroupNamespace);
        writer.WriteStartElement("wpg", "wgp", WordprocessingGroupNamespace);
        writer.WriteStartElement("wpg", "cNvGrpSpPr", WordprocessingGroupNamespace);
        writer.WriteEndElement();
        writer.WriteStartElement("wpg", "grpSpPr", WordprocessingGroupNamespace);
        writer.WriteStartElement("a", "xfrm", DocxConstants.DrawingMainNamespace);
        WriteDrawingPair(writer, "off", "x", "y", 0, 0);
        WriteDrawingPair(writer, "ext", "cx", "cy", extentX, extentY);
        WriteDrawingPair(writer, "chOff", "x", "y", 0, 0);
        // The shapes themselves are scaled (text included); scaling only the group would leave the text at full size.
        WriteDrawingPair(writer, "chExt", "cx", "cy", extentX, extentY);
        writer.WriteEndElement();
        writer.WriteEndElement();
        foreach (var placed in DiagramLayout.Scale(diagram.Shapes, scale))
        {
            WriteShapeBody(writer, placed.Shape, Emu(placed.X), Emu(placed.Y), context, scale);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteDrawingPair(XmlWriter writer, string element, string first, string second, long a, long b)
    {
        writer.WriteStartElement("a", element, DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString(first, a.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString(second, b.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static void WriteShapeColor(XmlWriter writer, string element, Color? color)
    {
        if (color is not { } value)
        {
            return;
        }

        writer.WriteStartElement("a", element, DocxConstants.DrawingMainNamespace);
        writer.WriteStartElement("a", "srgbClr", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("val", value.ToString()[1..]);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteShapeGeometry(XmlWriter writer, DrawingShape shape)
    {
        if (shape.Path is not { } path)
        {
            var preset = shape.Kind switch
            {
                ShapeKind.Line => "line",
                ShapeKind.RoundRectangle => "roundRect",
                ShapeKind.Ellipse => "ellipse",
                _ => "rect",
            };
            writer.WriteStartElement("a", "prstGeom", DocxConstants.DrawingMainNamespace);
            writer.WriteAttributeString("prst", preset);
            writer.WriteStartElement("a", "avLst", DocxConstants.DrawingMainNamespace);
            writer.WriteEndElement();
            writer.WriteEndElement();
            return;
        }

        writer.WriteStartElement("a", "custGeom", DocxConstants.DrawingMainNamespace);
        foreach (var empty in new[] { "avLst", "gdLst", "ahLst", "cxnLst" })
        {
            writer.WriteStartElement("a", empty, DocxConstants.DrawingMainNamespace);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("a", "rect", DocxConstants.DrawingMainNamespace);
        foreach (var (name, value) in new[] { ("l", "0"), ("t", "0"), ("r", "r"), ("b", "b") })
        {
            writer.WriteAttributeString(name, value);
        }

        writer.WriteEndElement();
        writer.WriteStartElement("a", "pathLst", DocxConstants.DrawingMainNamespace);
        writer.WriteStartElement("a", "path", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("w", Emu(shape.Width).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("h", Emu(shape.Height).ToString(CultureInfo.InvariantCulture));
        foreach (var step in path)
        {
            switch (step.Command)
            {
                case ShapePathCommand.MoveTo:
                case ShapePathCommand.LineTo:
                    writer.WriteStartElement("a", step.Command == ShapePathCommand.MoveTo ? "moveTo" : "lnTo", DocxConstants.DrawingMainNamespace);
                    WritePathPoint(writer, step.X, step.Y);
                    writer.WriteEndElement();
                    break;
                case ShapePathCommand.CubicTo:
                    writer.WriteStartElement("a", "cubicBezTo", DocxConstants.DrawingMainNamespace);
                    WritePathPoint(writer, step.X1, step.Y1);
                    WritePathPoint(writer, step.X2, step.Y2);
                    WritePathPoint(writer, step.X, step.Y);
                    writer.WriteEndElement();
                    break;
                default:
                    writer.WriteStartElement("a", "close", DocxConstants.DrawingMainNamespace);
                    writer.WriteEndElement();
                    break;
            }
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WritePathPoint(XmlWriter writer, Length x, Length y)
    {
        writer.WriteStartElement("a", "pt", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("x", Emu(x).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("y", Emu(y).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static void WritePageBreak(XmlWriter writer)
    {
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "br", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", "page");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteColumnBreak(XmlWriter writer)
    {
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "br", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", "column");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteParagraph(
        XmlWriter writer,
        Paragraph paragraph,
        DocxWriteContext context,
        bool compact = false)
    {
        ParagraphStyleResolver.TryResolve(
            paragraph.Style,
            paragraph.SourceStyleBase,
            context.Styles,
            context.SourceStyles,
            out var effective,
            out var inheritedBase);
        WriteParagraph(writer, effective, paragraph.Inlines, context, compact: compact, inheritedBase: inheritedBase);
    }

    private static void WriteParagraph(
        XmlWriter writer,
        ParagraphStyle style,
        IReadOnlyList<Inline> inlines,
        DocxWriteContext context,
        (int Ilvl, int NumId)? numbering = null,
        bool compact = false,
        ParagraphStyle? inheritedBase = null)
    {
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        WriteParagraphProperties(writer, style, numbering, compact, inheritedBase);

        var previousParagraph = context.Paragraph;
        context.Paragraph = style;
        // A tracked change that runs on from the previous paragraph is opened again here (ADR-0027).
        DocxRevisions.Resume(writer, context);
        foreach (var inline in inlines)
        {
            WriteInline(writer, inline, context);
        }

        DocxRevisions.Suspend(writer, context);
        context.Paragraph = previousParagraph;
        writer.WriteEndElement();
    }

    /// <summary>
    /// the run formatting the runs of the paragraph inherit: its style when the paragraph references it
    /// by name (then Word applies the <c>w:rPr</c> of the style), otherwise nothing (ADR-0010).
    /// </summary>
    private static RunFormat RunBaseOf(DocxWriteContext context)
        => context.Paragraph is { StyleName.Length: > 0 } style && RunFormat.HasFormatting(style)
            ? RunFormat.Of(style)
            : RunFormat.Empty;

    private static void WriteListBlock(
        XmlWriter writer,
        ListBlock list,
        long availableWidth,
        DocxWriteContext context)
    {
        // Lists with the same numbering definition share their numbering and continue each other,
        // as in ODT and PDF.
        var numId = context.NumId(list);

        // The level follows the actual nesting; NestingLevel can increase it and selects the
        // level of the numbering definition.
        var level = Math.Max(list.NestingLevel, context.ListLevel + 1);
        var ilvl = Math.Clamp(level - 1, 0, 8);
        var parentLevel = context.ListLevel;
        context.ListLevel = level;
        using var spacing = context.Spacing.EnterList(list);

        foreach (var item in list.Items)
        {
            var wroteContent = false;

            foreach (var block in item.Blocks)
            {
                if (!wroteContent && block is Paragraph paragraph)
                {
                    WriteParagraph(
                        writer, paragraph.Style, paragraph.Inlines, context, (ilvl, numId),
                        compact: context.Spacing.IsCompact(paragraph));
                    wroteContent = true;
                }
                else if (block is Paragraph continuation)
                {
                    // Further paragraphs of the item line up with its text.
                    ParagraphStyleResolver.TryResolve(
                        continuation.Style, continuation.SourceStyleBase, context.Styles, context.SourceStyles, out var effective, out var inherited);
                    var indented = effective with { LeftIndent = Length.FromPoints((effective.LeftIndent?.Points ?? 0) + ((ilvl + 1) * 36.0)) };
                    WriteParagraph(writer, indented, continuation.Inlines, context, compact: context.Spacing.IsCompact(continuation), inheritedBase: inherited);
                }
                else
                {
                    WriteBlock(writer, block, availableWidth, context);
                }
            }

            if (!wroteContent)
            {
                WriteParagraph(writer, ParagraphStyle.Default, Array.Empty<Inline>(), context, (ilvl, numId), compact: true);
            }
        }

        context.ListLevel = parentLevel;
    }

    private static void WriteTableBlock(
        XmlWriter writer,
        TableBlock table,
        long availableWidth,
        DocxWriteContext context)
    {
        var columns = TableLayout.LogicalColumns(table);
        if (columns == 0)
        {
            return;
        }

        // OF-B04: a Word table has no rounded corners (a cell is a rectangle).
        if (table.CornerRadius is { Points: > 0 })
        {
            context.Handler.Report("DOCX: rounded table corners are not supported; the table is written with square corners.");
        }

        var availablePoints = availableWidth / 20.0;
        var resolved = TableLayout.ResolveColumnWidthsWithPercent(table, columns, availablePoints);
        var widths = new long[columns];
        for (var column = 0; column < columns; column++)
        {
            widths[column] = (long)Math.Round(resolved[column] * 20.0, MidpointRounding.AwayFromZero);
        }

        writer.WriteStartElement("w", "tbl", DocxConstants.WordprocessingML);

        writer.WriteStartElement("w", "tblPr", DocxConstants.WordprocessingML);
        if (table.Style is { } style)
        {
            writer.WriteStartElement("w", "tblStyle", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", style.Name);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "tblW", DocxConstants.WordprocessingML);
        // Percent columns need a percent table, or Word measures them against an automatic width.
        if ((table.ColumnWidthPercents is null ? table.WidthPercent : table.WidthPercent ?? 100) is { } percent)
        {
            WriteWAttribute(writer, "w", (long)Math.Round(percent * 50.0, MidpointRounding.AwayFromZero));
            WriteWAttribute(writer, "type", "pct");
        }
        else
        {
            WriteWAttribute(writer, "w", 0L);
            WriteWAttribute(writer, "type", "auto");
        }

        writer.WriteEndElement();
        if (table.LeftIndent is { } leftIndent)
        {
            writer.WriteStartElement("w", "tblInd", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "w", Twips(leftIndent));
            WriteWAttribute(writer, "type", "dxa");
            writer.WriteEndElement();
        }

        WriteTableBorders(writer, table.Border ?? table.Style?.Border);
        if (table.FixedColumnWidths)
        {
            writer.WriteStartElement("w", "tblLayout", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "type", "fixed");
            writer.WriteEndElement();
        }

        WriteTableCellMargins(writer, "tblCellMar", TableLayout.CellPadding(table, null));
        if (table.Style is not null)
        {
            // Only the areas the model applies: a first row counts as header when it is marked so.
            var firstRow = table.Rows[0].IsHeader;
            var lastRow = table.Style.LastRow is not null || table.Style.LastRowBackground is not null;
            var firstColumn = table.Style.FirstColumn is not null || table.Style.FirstColumnBackground is not null;
            var lastColumn = table.Style.LastColumn is not null || table.Style.LastColumnBackground is not null;
            var look = 0x0400 | (firstRow ? 0x20 : 0) | (lastRow ? 0x40 : 0) | (firstColumn ? 0x80 : 0) | (lastColumn ? 0x100 : 0);
            writer.WriteStartElement("w", "tblLook", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", look.ToString("X4", CultureInfo.InvariantCulture));
            WriteWAttribute(writer, "firstRow", firstRow ? "1" : "0");
            WriteWAttribute(writer, "lastRow", lastRow ? "1" : "0");
            WriteWAttribute(writer, "firstColumn", firstColumn ? "1" : "0");
            WriteWAttribute(writer, "lastColumn", lastColumn ? "1" : "0");
            WriteWAttribute(writer, "noHBand", "0");
            WriteWAttribute(writer, "noVBand", "1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        writer.WriteStartElement("w", "tblGrid", DocxConstants.WordprocessingML);
        foreach (var width in widths)
        {
            writer.WriteStartElement("w", "gridCol", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "w", width);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();

        var rowIndex = 0;
        var remaining = new int[widths.Length];
        var coveredSpans = new int[widths.Length];
        foreach (var row in table.Rows)
        {
            var conditional = row.IsHeader
                ? table.Style?.HeaderBackground
                : TableConditionalStyle.IsBanded(table, rowIndex) ? table.Style?.BandBackground : null;
            WriteTableRow(writer, row, widths, table, context, conditional, remaining, coveredSpans, isLastRow: rowIndex == table.Rows.Count - 1);
            rowIndex++;
        }

        writer.WriteEndElement();
    }

    private static void WriteTableOfContents(XmlWriter writer, TableOfContents toc, DocxWriteContext context)
    {
        if (!string.IsNullOrEmpty(toc.Title))
        {
            WriteParagraph(
                writer,
                ParagraphStyle.Default with { StyleName = "TOCHeading" },
                new Inline[] { new TextRun(toc.Title!) },
                context);
        }

        // TOC field so that Word fills in the page numbers on update.
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        WriteFieldRun(writer, "begin", null);
        WriteFieldRun(writer, null, $" TOC \\o \"1-{toc.MaxLevel}\" \\h \\z \\u ");
        WriteFieldRun(writer, "separate", null);
        writer.WriteEndElement();

        foreach (var (level, text) in context.Headings)
        {
            if (level > toc.MaxLevel)
            {
                continue;
            }

            writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "ind", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "left", (long)((level - 1) * 360));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "t", DocxConstants.WordprocessingML);
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(XmlText.Clean(text, context.XmlBehavior));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        WriteFieldRun(writer, "end", null);
        writer.WriteEndElement();
    }

    /// <summary>
    /// A table of figures: a <c>TOC \c</c> field over the captions of one label; the cached entries
    /// are the caption texts and the application fills in the page numbers on update.
    /// </summary>
    private static void WriteTableOfFigures(XmlWriter writer, TableOfFigures figures, DocxWriteContext context)
    {
        if (!string.IsNullOrEmpty(figures.Title))
        {
            WriteParagraph(
                writer,
                ParagraphStyle.Default with { StyleName = "TOCHeading" },
                new Inline[] { new TextRun(figures.Title!) },
                context);
        }

        var captions = context.Document is { } document ? FigureIndexScan.Captions(document, figures.Label) : [];
        WriteListField(
            writer, $" TOC \\h \\z \\c \"{figures.Label}\" ", captions.Select(caption => caption.Text), 0, context);
    }

    /// <summary>
    /// An alphabetical index: an <c>INDEX</c> field over the <c>XE</c> entries; the cached entries are
    /// the sorted entries and the application fills in the page numbers on update.
    /// </summary>
    private static void WriteAlphabeticalIndex(XmlWriter writer, AlphabeticalIndex index, DocxWriteContext context)
    {
        if (!string.IsNullOrEmpty(index.Title))
        {
            WriteParagraph(
                writer,
                ParagraphStyle.Default with { StyleName = "TOCHeading" },
                new Inline[] { new TextRun(index.Title!) },
                context);
        }

        var entries = context.Document is { } document ? FigureIndexScan.Entries(document) : [];
        // A main entry appears once, followed by its sub entries.
        var lines = new List<(string Text, int Level)>();
        string? previous = null;
        foreach (var (text, subText) in entries)
        {
            if (text != previous)
            {
                lines.Add((text, 0));
                previous = text;
            }

            if (subText is not null)
            {
                lines.Add((subText, 1));
            }
        }

        WriteListField(writer, " INDEX \\h \"A\" \\c \"1\" ", lines.Select(line => line.Text), 0, context, lines.Select(line => line.Level).ToList());
    }

    /// <summary>A field over several cached paragraphs (table of figures, index): begin, entries, end.</summary>
    private static void WriteListField(
        XmlWriter writer,
        string instruction,
        IEnumerable<string> entries,
        int baseIndent,
        DocxWriteContext context,
        IReadOnlyList<int>? levels = null)
    {
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        WriteFieldRun(writer, "begin", null);
        WriteFieldRun(writer, null, instruction);
        WriteFieldRun(writer, "separate", null);
        writer.WriteEndElement();

        var position = 0;
        foreach (var entry in entries)
        {
            var level = levels is null ? 0 : levels[position++];
            writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "ind", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "left", (long)(baseIndent + (level * 360)));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "t", DocxConstants.WordprocessingML);
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(XmlText.Clean(entry, context.XmlBehavior));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        WriteFieldRun(writer, "end", null);
        writer.WriteEndElement();
    }

    /// <summary>
    /// An index entry is a hidden <c>XE</c> field (Word shows it only with the formatting marks):
    /// the main entry and the sub entry are separated by a colon, a quotation mark, a backslash
    /// and a colon inside an entry are escaped.
    /// </summary>
    private static void WriteIndexEntry(XmlWriter writer, IndexEntry entry)
    {
        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(":", "\\:");
        var code = entry.SubText is null ? Escape(entry.Text) : Escape(entry.Text) + ":" + Escape(entry.SubText);
        foreach (var (type, instruction) in new (string?, string?)[] { ("begin", null), (null, $" XE \"{code}\" "), ("end", null) })
        {
            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
            WriteEmptyElement(writer, "vanish");
            writer.WriteEndElement();
            if (type is not null)
            {
                WriteFieldChar(writer, type);
            }
            else
            {
                writer.WriteStartElement("w", "instrText", DocxConstants.WordprocessingML);
                writer.WriteAttributeString("xml", "space", null, "preserve");
                writer.WriteString(instruction);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }
    }

    private static void WriteFieldRun(XmlWriter writer, string? fldCharType, string? instruction)
    {
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        if (fldCharType is not null)
        {
            writer.WriteStartElement("w", "fldChar", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "fldCharType", fldCharType);
            writer.WriteEndElement();
        }
        else if (instruction is not null)
        {
            writer.WriteStartElement("w", "instrText", DocxConstants.WordprocessingML);
            writer.WriteAttributeString("xml", "space", null, "preserve");
            writer.WriteString(instruction);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteTableBorders(XmlWriter writer, ParagraphBorder? border)
    {
        writer.WriteStartElement("w", "tblBorders", DocxConstants.WordprocessingML);
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            writer.WriteStartElement("w", side, DocxConstants.WordprocessingML);
            if (border is null)
            {
                WriteWAttribute(writer, "val", "single");
                WriteWAttribute(writer, "sz", (long)Math.Round(Typography.TableBorderWidth.Points * 8, MidpointRounding.AwayFromZero));
                WriteWAttribute(writer, "space", "0");
                WriteWAttribute(writer, "color", "auto");
            }
            else
            {
                WriteWAttribute(writer, "val", BorderValue(border.Style));
                WriteWAttribute(writer, "sz", DocxUnits.Scaled(border.Width.Points, 8));
                WriteWAttribute(writer, "space", "0");
                WriteWAttribute(writer, "color", HexColor(border.Color));
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteTableCellMargins(XmlWriter writer, string element, (Length Horizontal, Length Vertical) padding)
    {
        writer.WriteStartElement("w", element, DocxConstants.WordprocessingML);
        var vertical = Twips(padding.Vertical);
        var horizontal = Twips(padding.Horizontal);
        WriteCellMargin(writer, "top", vertical);
        WriteCellMargin(writer, "left", horizontal);
        WriteCellMargin(writer, "bottom", vertical);
        WriteCellMargin(writer, "right", horizontal);
        writer.WriteEndElement();
    }

    private static void WriteCellMargin(XmlWriter writer, string side, long twips)
    {
        writer.WriteStartElement("w", side, DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "w", twips);
        WriteWAttribute(writer, "type", "dxa");
        writer.WriteEndElement();
    }

    private static void WriteTableRow(
        XmlWriter writer,
        TableRow row,
        long[] widths,
        TableBlock table,
        DocxWriteContext context,
        Color? conditionalBackground,
        int[] remaining,
        int[] coveredSpans,
        bool isLastRow)
    {
        writer.WriteStartElement("w", "tr", DocxConstants.WordprocessingML);

        if (row.IsHeader || row.Height is not null || row.KeepTogether)
        {
            writer.WriteStartElement("w", "trPr", DocxConstants.WordprocessingML);
            if (row.KeepTogether)
            {
                WriteEmptyElement(writer, "cantSplit");
            }

            if (row.IsHeader)
            {
                WriteEmptyElement(writer, "tblHeader");
            }

            if (row.Height is { } height)
            {
                writer.WriteStartElement("w", "trHeight", DocxConstants.WordprocessingML);
                WriteWAttribute(writer, "val", Twips(height));
                WriteWAttribute(writer, "hRule", row.HeightRule == TableRowHeightRule.Exactly ? "exact" : "atLeast");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        var cellIndex = 0;
        var column = 0;
        while (column < widths.Length)
        {
            if (remaining[column] > 0)
            {
                // The continuation spans the same columns as the cell it continues.
                var covered = Math.Clamp(coveredSpans[column], 1, widths.Length - column);
                var coveredWidth = 0L;
                for (var offset = 0; offset < covered; offset++)
                {
                    remaining[column + offset] = Math.Max(0, remaining[column + offset] - 1);
                    coveredWidth += widths[column + offset];
                }

                WriteMergedCell(writer, coveredWidth, covered, widths.Sum(), table);
                column += covered;
                continue;
            }

            if (cellIndex >= row.Cells.Count)
            {
                column++;
                continue;
            }

            var cell = row.Cells[cellIndex++];
            var span = Math.Min(cell.ColumnSpan, Math.Max(1, widths.Length - column));
            var width = 0L;
            for (var index = 0; index < span; index++)
            {
                width += widths[Math.Min(column + index, widths.Length - 1)];
            }

            if (cell.RowSpan > 1)
            {
                coveredSpans[column] = span;
                for (var offset = 0; offset < span && column + offset < widths.Length; offset++)
                {
                    remaining[column + offset] = cell.RowSpan - 1;
                }
            }

            var styleBackground = conditionalBackground;
            if (!row.IsHeader && column == 0 && table.Style?.FirstColumnBackground is { } firstColumn)
            {
                styleBackground = firstColumn;
            }
            else if (!row.IsHeader && column + span - 1 == widths.Length - 1 && table.Style?.LastColumnBackground is { } lastColumn)
            {
                styleBackground = lastColumn;
            }

            if (isLastRow && !row.IsHeader && table.Style?.LastRowBackground is { } lastRowBackground)
            {
                styleBackground = lastRowBackground;
            }

            WriteTableCell(writer, cell, width, widths.Sum(), span, table, context, styleBackground, restart: cell.RowSpan > 1);
            column += span;
        }

        writer.WriteEndElement();
    }

    /// <summary>A cell's <c>w:tcW</c>: twips, or fiftieths of a percent of the table for percent columns.</summary>
    private static void WriteCellWidth(XmlWriter writer, long width, long tableWidth, TableBlock table)
    {
        writer.WriteStartElement("w", "tcW", DocxConstants.WordprocessingML);
        if (table.ColumnWidthPercents is not null && tableWidth > 0)
        {
            WriteWAttribute(writer, "w", (long)Math.Round(width * 5000.0 / tableWidth, MidpointRounding.AwayFromZero));
            WriteWAttribute(writer, "type", "pct");
        }
        else
        {
            WriteWAttribute(writer, "w", width);
            WriteWAttribute(writer, "type", "dxa");
        }

        writer.WriteEndElement();
    }

    private static void WriteMergedCell(XmlWriter writer, long width, int span, long tableWidth, TableBlock table)
    {
        writer.WriteStartElement("w", "tc", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "tcPr", DocxConstants.WordprocessingML);
        WriteCellWidth(writer, width, tableWidth, table);
        if (span > 1)
        {
            WriteValElement(writer, "gridSpan", span.ToString(CultureInfo.InvariantCulture));
        }

        WriteEmptyElement(writer, "vMerge");
        writer.WriteEndElement();
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteTableCell(
        XmlWriter writer,
        TableCell cell,
        long width,
        long tableWidth,
        int span,
        TableBlock table,
        DocxWriteContext context,
        Color? conditionalBackground,
        bool restart = false)
    {
        writer.WriteStartElement("w", "tc", DocxConstants.WordprocessingML);

        writer.WriteStartElement("w", "tcPr", DocxConstants.WordprocessingML);
        WriteCellWidth(writer, width, tableWidth, table);

        if (span > 1)
        {
            WriteValElement(writer, "gridSpan", span.ToString(CultureInfo.InvariantCulture));
        }

        if (restart)
        {
            writer.WriteStartElement("w", "vMerge", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", "restart");
            writer.WriteEndElement();
        }

        if (cell.Borders is { } cellBorders)
        {
            WriteCellBorders(writer, cellBorders);
        }

        if ((cell.Background ?? conditionalBackground) is { } background)
        {
            writer.WriteStartElement("w", "shd", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", "clear");
            WriteWAttribute(writer, "color", "auto");
            WriteWAttribute(writer, "fill", HexColor(background));
            writer.WriteEndElement();
        }

        if ((cell.Padding ?? cell.PaddingHorizontal ?? cell.PaddingVertical ?? table.CellPadding ?? table.CellPaddingHorizontal ?? table.CellPaddingVertical) is not null)
        {
            WriteTableCellMargins(writer, "tcMar", TableLayout.CellPadding(table, cell));
        }

        if (cell.VerticalAlignment != TableCellVerticalAlignment.Top)
        {
            WriteValElement(writer, "vAlign", cell.VerticalAlignment == TableCellVerticalAlignment.Center ? "center" : "bottom");
        }

        writer.WriteEndElement();

        // lists in cells start again at the top level; cells are compact.
        var parentLevel = context.ListLevel;
        context.ListLevel = 0;
        using var spacing = context.Spacing.EnterCompact();

        foreach (var block in cell.Blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    ParagraphStyleResolver.TryResolve(
                        paragraph.Style,
                        paragraph.SourceStyleBase,
                        context.Styles,
                        context.SourceStyles,
                        out var cellStyle,
                        out var cellBase);
                    WriteParagraph(
                        writer, TableLayout.CellParagraphStyle(cellStyle, cell), paragraph.Inlines, context,
                        compact: true, inheritedBase: cellBase);
                    break;
                case Heading heading:
                    WriteParagraph(writer, TableLayout.CellParagraphStyle(heading.Style, cell), heading.Inlines, context);
                    break;
                case Equation equation when context.BandName is null:
                    // LibreOffice drops an m:oMathPara that is the only content of a paragraph in a table cell; an empty
                    // run before it keeps the formula (Word shows the same either way).
                    DocxMath.Write(writer, equation, leadingEmptyRun: true);
                    break;
                default:
                    WriteBlock(writer, block, width, context);
                    break;
            }
        }

        context.ListLevel = parentLevel;

        // ECMA-376 requires that a cell ends with a paragraph (also after a
        // nested table or an empty list).
        if (!EndsWithParagraph(cell.Blocks, context.BandName is not null))
        {
            writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// writes one side of the page border (phase AJ, AJ05). A side without a border is
    /// written as "nil", as Word does.
    /// </summary>
    private static void WritePageBorderSide(XmlWriter writer, string side, ParagraphBorder? border, long space)
    {
        writer.WriteStartElement("w", side, DocxConstants.WordprocessingML);
        if (border is null || border.Style == BorderStyle.None)
        {
            WriteWAttribute(writer, "val", "nil");
        }
        else
        {
            WriteWAttribute(writer, "val", BorderValue(border.Style));
            WriteWAttribute(writer, "sz", DocxUnits.Scaled(border.Width.Points, 8));
            WriteWAttribute(writer, "space", space);
            WriteWAttribute(writer, "color", HexColor(border.Color));
        }

        writer.WriteEndElement();
    }

    private static void WriteCellBorders(XmlWriter writer, ParagraphBorders borders)
    {
        writer.WriteStartElement("w", "tcBorders", DocxConstants.WordprocessingML);
        WriteCellBorder(writer, "top", borders.Top);
        WriteCellBorder(writer, "left", borders.Left);
        WriteCellBorder(writer, "bottom", borders.Bottom);
        WriteCellBorder(writer, "right", borders.Right);
        writer.WriteEndElement();
    }

    private static void WriteCellBorder(XmlWriter writer, string side, ParagraphBorder? border)
    {
        if (border is null)
        {
            return;
        }

        writer.WriteStartElement("w", side, DocxConstants.WordprocessingML);
        if (border.Style == BorderStyle.None)
        {
            WriteWAttribute(writer, "val", "nil");
            writer.WriteEndElement();
            return;
        }
        WriteWAttribute(writer, "val", BorderValue(border.Style));
        WriteWAttribute(writer, "sz", DocxUnits.Scaled(border.Width.Points, 8));
        WriteWAttribute(writer, "space", "0");
        WriteWAttribute(writer, "color", HexColor(border.Color));
        writer.WriteEndElement();
    }

    private static void WriteParagraphProperties(
        XmlWriter writer,
        ParagraphStyle style,
        (int Ilvl, int NumId)? numbering = null,
        bool compact = false,
        ParagraphStyle? inheritedBase = null)
    {
        writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);

        if (!string.IsNullOrEmpty(style.StyleName))
        {
            WriteValElement(writer, "pStyle", style.StyleName);
        }

        // ADR-0011: a paragraph that references a named style states only its direct formatting;
        // the properties it inherits from the style are left to Word.
        if (style.KeepWithNext is { } keepWithNext
            && Overrides(keepWithNext, inheritedBase?.KeepWithNext))
        {
            WriteToggle(writer, "keepNext", keepWithNext);
        }

        if (style.KeepTogether is { } keepTogether
            && Overrides(keepTogether, inheritedBase?.KeepTogether))
        {
            WriteToggle(writer, "keepLines", keepTogether);
        }

        if (style.PageBreakBefore is { } pageBreakBefore
            && Overrides(pageBreakBefore, inheritedBase?.PageBreakBefore))
        {
            WriteToggle(writer, "pageBreakBefore", pageBreakBefore);
        }

        if (numbering is { } number)
        {
            writer.WriteStartElement("w", "numPr", DocxConstants.WordprocessingML);
            WriteValElement(writer, "ilvl", number.Ilvl.ToString(CultureInfo.InvariantCulture));
            WriteValElement(writer, "numId", number.NumId.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        if (style.Borders is { } borders && Overrides(style.Borders, inheritedBase?.Borders))
        {
            WriteParagraphBorders(writer, borders);
        }

        if (style.Background is { } background && Overrides(style.Background, inheritedBase?.Background))
        {
            WriteParagraphShading(writer, background);
        }

        if (style.TabStops is { } tabStops
            && (inheritedBase is null || !ParagraphStyleResolver.SameTabStops(style.TabStops, inheritedBase.TabStops)))
        {
            writer.WriteStartElement("w", "tabs", DocxConstants.WordprocessingML);
            foreach (var tab in tabStops)
            {
                writer.WriteStartElement("w", "tab", DocxConstants.WordprocessingML);
                WriteWAttribute(writer, "val", TabValue(tab.Alignment));
                WriteWAttribute(writer, "pos", Twips(tab.Position));
                WriteWAttribute(writer, "leader", TabLeaderValue(tab.Leader));
                writer.WriteEndElement();
            }

            ClearInheritedTabStops(writer, tabStops, inheritedBase?.TabStops);
            writer.WriteEndElement();
        }

        if (style.RightToLeft is { } rightToLeft
            && (inheritedBase is null || rightToLeft != inheritedBase.RightToLeft))
        {
            if (rightToLeft)
            {
                WriteEmptyElement(writer, "bidi");
            }
            else
            {
                WriteOffElement(writer, "bidi");
            }
        }

        // An unset spacing inherits; compact paragraphs (lists, cells, headers/footers) state an
        // explicit 0 so the built-in 6 pt after does not apply.
        var writeBefore = style.SpaceBefore is { } before
            && (inheritedBase is null || before != inheritedBase.SpaceBefore);
        var writeAfter = compact
            || (style.SpaceAfter is { } after
                && (inheritedBase is null || after != inheritedBase.SpaceAfter));
        var writeLine = style.LineSpacing is not null && Overrides(style.LineSpacing, inheritedBase?.LineSpacing);
        if (writeBefore || writeAfter || writeLine)
        {
            writer.WriteStartElement("w", "spacing", DocxConstants.WordprocessingML);
            if (writeBefore)
            {
                WriteWAttribute(writer, "before", Twips(style.SpaceBefore ?? Length.Zero));
            }

            if (writeAfter)
            {
                WriteWAttribute(writer, "after", Twips(style.SpaceAfter ?? Length.Zero));
            }

            if (writeLine && style.LineSpacing is { } lineSpacing)
            {
                switch (lineSpacing.Rule)
                {
                    case LineSpacingRule.Multiple:
                        // w:line="0" would mean automatic.
                        WriteWAttribute(writer, "line", Math.Max(1L, (long)Math.Round(lineSpacing.Factor * 240.0, MidpointRounding.AwayFromZero)));
                        WriteWAttribute(writer, "lineRule", "auto");
                        break;
                    case LineSpacingRule.AtLeast:
                        WriteWAttribute(writer, "line", Math.Max(1L, Twips(lineSpacing.Height)));
                        WriteWAttribute(writer, "lineRule", "atLeast");
                        break;
                    default:
                        WriteWAttribute(writer, "line", Math.Max(1L, Twips(lineSpacing.Height)));
                        WriteWAttribute(writer, "lineRule", "exact");
                        break;
                }
            }

            writer.WriteEndElement();
        }

        var writeLeft = style.LeftIndent is { } left && (inheritedBase is null || left != inheritedBase.LeftIndent);
        var writeRight = style.RightIndent is { } right && (inheritedBase is null || right != inheritedBase.RightIndent);
        var writeFirst = style.FirstLineIndent is { } first && (inheritedBase is null || first != inheritedBase.FirstLineIndent);
        if (writeLeft || writeRight || writeFirst)
        {
            writer.WriteStartElement("w", "ind", DocxConstants.WordprocessingML);
            if (style.LeftIndent is { } leftIndent && writeLeft)
            {
                WriteWAttribute(writer, "left", Twips(leftIndent));
            }

            if (style.RightIndent is { } rightIndent && writeRight)
            {
                WriteWAttribute(writer, "right", Twips(rightIndent));
            }

            if (style.FirstLineIndent is { } firstLineIndent && writeFirst)
            {
                // The schema has no negative first line: that is what w:hanging is for.
                WriteWAttribute(writer, firstLineIndent.Points < 0 ? "hanging" : "firstLine", Math.Abs(Twips(firstLineIndent)));
            }

            writer.WriteEndElement();
        }

        if (style.Alignment is { } alignment
            && (inheritedBase is null ? true : alignment != inheritedBase.Alignment))
        {
            WriteValElement(writer, "jc", AlignmentValue(alignment));
        }

        writer.WriteEndElement();
    }

    private static bool Overrides<T>(T value, object? inherited)
        => inherited is not T typed || !EqualityComparer<T>.Default.Equals(value, typed);

    /// <summary>
    /// Clears the inherited tab stops that the explicit set no longer contains, so a partial or
    /// empty set does not leave inherited positions behind (ADR-0018).
    /// </summary>
    private static void ClearInheritedTabStops(
        XmlWriter writer,
        IReadOnlyList<TabStop> tabStops,
        IReadOnlyList<TabStop>? inherited)
    {
        if (inherited is null)
        {
            return;
        }

        foreach (var tab in inherited)
        {
            if (ContainsTabStopAt(tabStops, tab.Position))
            {
                continue;
            }

            writer.WriteStartElement("w", "tab", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", "clear");
            WriteWAttribute(writer, "pos", Twips(tab.Position));
            writer.WriteEndElement();
        }
    }

    private static bool ContainsTabStopAt(IReadOnlyList<TabStop> tabStops, Length position)
    {
        foreach (var tab in tabStops)
        {
            if (tab.Position == position)
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteParagraphBorders(XmlWriter writer, ParagraphBorders borders)
    {
        writer.WriteStartElement("w", "pBdr", DocxConstants.WordprocessingML);
        // An empty set is an explicit "no borders": every side is cleared, including sides that
        // the inherited style may have (ADR-0016).
        var clearAll = !HasVisibleBorder(borders);
        WriteParagraphBorder(writer, "top", borders.Top, clearAll);
        WriteParagraphBorder(writer, "left", borders.Left, clearAll);
        WriteParagraphBorder(writer, "bottom", borders.Bottom, clearAll);
        WriteParagraphBorder(writer, "right", borders.Right, clearAll);
        writer.WriteEndElement();
    }

    private static bool HasVisibleBorder(ParagraphBorders borders)
        => borders.Top is { Style: not BorderStyle.None }
            || borders.Left is { Style: not BorderStyle.None }
            || borders.Bottom is { Style: not BorderStyle.None }
            || borders.Right is { Style: not BorderStyle.None };

    private static void WriteParagraphBorder(XmlWriter writer, string side, ParagraphBorder? border, bool clearAll = false)
    {
        if (border is null || border.Style == BorderStyle.None)
        {
            if (clearAll)
            {
                writer.WriteStartElement("w", side, DocxConstants.WordprocessingML);
                WriteWAttribute(writer, "val", "nil");
                writer.WriteEndElement();
            }

            return;
        }

        writer.WriteStartElement("w", side, DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", BorderValue(border.Style));
        WriteWAttribute(writer, "sz", DocxUnits.Scaled(border.Width.Points, 8));
        WriteWAttribute(writer, "space", 0L);
        WriteWAttribute(writer, "color", HexColor(border.Color));
        writer.WriteEndElement();
    }

    private static void WriteParagraphShading(XmlWriter writer, Color color)
    {
        writer.WriteStartElement("w", "shd", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", "clear");
        WriteWAttribute(writer, "color", "auto");
        WriteWAttribute(writer, "fill", HexColor(color));
        writer.WriteEndElement();
    }

    private static string BorderValue(BorderStyle style) => style switch
    {
        BorderStyle.Double => "double",
        BorderStyle.Dotted => "dotted",
        BorderStyle.Dashed => "dashed",
        _ => "single",
    };

    private static string TabValue(TabStopAlignment alignment) => alignment switch
    {
        TabStopAlignment.Center => "center",
        TabStopAlignment.Right => "right",
        TabStopAlignment.Decimal => "decimal",
        _ => "left",
    };

    private static string TabLeaderValue(TabStopLeader leader) => leader switch
    {
        TabStopLeader.Dot => "dot",
        TabStopLeader.Hyphen => "hyphen",
        TabStopLeader.Underscore => "underscore",
        _ => "none",
    };

    internal static void WriteInline(XmlWriter writer, Inline inline, DocxWriteContext context)
    {
        switch (inline)
        {
            case TextRun textRun:
                WriteTextRun(writer, textRun, context);
                break;
            case Hyperlink hyperlink:
                WriteHyperlink(writer, hyperlink, context);
                break;
            case Bookmark bookmark:
                WriteBookmark(writer, bookmark, context);
                break;
            case BookmarkEnd bookmarkEnd:
                WriteBookmarkEnd(writer, bookmarkEnd, context);
                break;
            case Comment comment:
                DocxComments.WriteStart(writer, comment, context);
                break;
            case CommentEnd commentEnd:
                DocxComments.WriteEnd(writer, commentEnd, context);
                break;
            case TrackedChange trackedChange:
                DocxRevisions.WriteStart(writer, trackedChange, context);
                break;
            case TrackedChangeEnd trackedChangeEnd:
                DocxRevisions.WriteEnd(writer, trackedChangeEnd, context);
                break;
            case Footnote footnote:
                WriteNoteReference(writer, footnote, context);
                break;
            case InlineImage inlineImage:
                WriteImageBlock(writer, inlineImage.Image, context, inLine: true);
                break;
            case PageNumberField:
                WriteField(writer, " PAGE ", result: null, context.XmlBehavior);
                break;
            case PageCountField:
                WriteField(writer, " NUMPAGES ", result: null, context.XmlBehavior);
                break;
            case DateField dateField:
                WriteField(
                    writer, " DATE ",
                    dateField.Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), context.XmlBehavior);
                break;
            case DocPropertyField docProperty:
                WriteField(writer, $" DOCPROPERTY {FieldArgument(docProperty.Name)} ", context.Fields?.Properties.GetValueOrDefault(docProperty.Name), context.XmlBehavior);
                break;
            case DocVariableField docVariable:
                WriteField(writer, $" DOCVARIABLE {FieldArgument(docVariable.Name)} ", context.Fields?.Variables.GetValueOrDefault(docVariable.Name), context.XmlBehavior);
                break;
            case IndexEntry entry:
                WriteIndexEntry(writer, entry);
                break;
            case AlignmentTab alignmentTab:
                writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
                writer.WriteStartElement("w", "ptab", DocxConstants.WordprocessingML);
                writer.WriteAttributeString("w", "relativeTo", DocxConstants.WordprocessingML,
                    alignmentTab.RelativeTo == AlignmentTabRelativeTo.Indent ? "indent" : "margin");
                writer.WriteAttributeString("w", "alignment", DocxConstants.WordprocessingML, alignmentTab.Alignment switch
                {
                    TabStopAlignment.Center => "center",
                    TabStopAlignment.Right => "right",
                    _ => "left",
                });
                writer.WriteAttributeString("w", "leader", DocxConstants.WordprocessingML, alignmentTab.Leader switch
                {
                    TabStopLeader.Dot => "dot",
                    TabStopLeader.Hyphen => "hyphen",
                    TabStopLeader.Underscore => "underscore",
                    _ => "none",
                });
                writer.WriteEndElement();
                writer.WriteEndElement();
                break;
            case PageRefField pageRef:
                WriteField(writer, $" PAGEREF {context.BookmarkName(pageRef.BookmarkName)} \\h ", result: null, context.XmlBehavior);
                break;
            case Field field:
            {
                // A general field code; the application calculates it. A field without a
                // cached result is calculated here, so that the document shows a value
                // (AJ02).
                var result = field.Result;
                if (result is null && context.Fields is { } fields)
                {
                    result = FieldEvaluator.Evaluate(field, fields);
                }

                WriteField(writer, $" {field.Instruction} ", result, context.XmlBehavior);
                break;
            }
        }
    }

    /// <summary>A field argument; a name with spaces or quotes needs quoting to stay one argument.</summary>
    private static string FieldArgument(string name)
        => name.Any(character => char.IsWhiteSpace(character) || character == '"')
            ? "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : name;

    /// <summary>
    /// writes the start of a bookmark. A bookmark without an explicit end in the model is a
    /// zero-width target and closes right here (OF-C03).
    /// </summary>
    private static void WriteBookmark(XmlWriter writer, Bookmark bookmark, DocxWriteContext context)
    {
        var id = context.NextBookmarkId();
        var idText = id.ToString(CultureInfo.InvariantCulture);
        writer.WriteStartElement("w", "bookmarkStart", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "id", idText);
        WriteWAttribute(writer, "name", context.BookmarkName(bookmark.Name));
        writer.WriteEndElement();

        if (context.BookmarkRanges.Contains(bookmark.Name))
        {
            context.OpenBookmark(bookmark.Name, id);
            if (context.EditableRanges.Contains(bookmark.Name))
            {
                writer.WriteStartElement("w", "permStart", DocxConstants.WordprocessingML);
                WriteWAttribute(writer, "id", context.OpenPermission(bookmark.Name));
                WriteWAttribute(writer, "edGrp", "everyone");
                writer.WriteEndElement();
            }

            return;
        }

        if (context.EditableRanges.Contains(bookmark.Name))
        {
            context.Handler.Report($"DOCX: the bookmark '{bookmark.Name}' has no end, so it cannot be an editable range.");
        }

        writer.WriteStartElement("w", "bookmarkEnd", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "id", idText);
        writer.WriteEndElement();
    }

    /// <summary>Writes the end of a bookmark range.</summary>
    private static void WriteBookmarkEnd(XmlWriter writer, BookmarkEnd end, DocxWriteContext context)
    {
        if (context.BookmarkId(end.Name) is not { } id)
        {
            context.Handler.Report($"DOCX: the end of the bookmark '{end.Name}' has no start; it is omitted.");
            return;
        }

        writer.WriteStartElement("w", "bookmarkEnd", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "id", id.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();

        if (context.PermissionId(end.Name) is { } permissionId)
        {
            writer.WriteStartElement("w", "permEnd", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "id", permissionId);
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// DOCX bookmark names start with a letter or an underscore (Word's hidden ones) and contain
    /// no spaces; other characters are replaced with an underscore.
    /// </summary>
    internal static string DocxBookmarkName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        if (builder.Length == 0 || !(char.IsLetter(builder[0]) || builder[0] == '_'))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private static void WriteNoteReference(XmlWriter writer, Footnote footnote, DocxWriteContext context)
    {
        if (context.BandName is not null)
        {
            // A band has no note mechanism: the text stays visible at the reference, and the note
            // body is not part of the notes part (see NoteScan.Text).
            var bandText = NoteScan.Text(footnote);
            if (bandText.Length > 0)
            {
                WriteTextRun(writer, new TextRun(bandText), context);
            }

            return;
        }

        var (id, kind, found) = context.NoteReference(footnote);
        if (!found)
        {
            context.Handler.Report("DOCX: a note reference without an assigned ID is omitted.");
            return;
        }

        var isEndnote = kind == NoteKind.Endnote;
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "rStyle", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", isEndnote ? "EndnoteReference" : "FootnoteReference");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("w", isEndnote ? "endnoteReference" : "footnoteReference", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "id", id.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteNotes(
        XmlWriter writer,
        IReadOnlyList<(int Id, Footnote Note)> notes,
        bool isEndnote,
        DocxWriteContext context,
        IReadOnlyDictionary<string, string> hyperlinks)
    {
        var bodyHyperlinks = context.HyperlinkRelationships;
        context.HyperlinkRelationships = hyperlinks;
        var root = isEndnote ? "endnotes" : "footnotes";
        var element = isEndnote ? "endnote" : "footnote";
        writer.WriteStartElement("w", root, DocxConstants.WordprocessingML);
        DocxConstants.WriteCompatibilityNamespaces(writer);

        WriteNoteSeparator(writer, element, "separator", -1);
        WriteNoteSeparator(writer, element, "continuationSeparator", 0);

        foreach (var (id, note) in notes)
        {
            writer.WriteStartElement("w", element, DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "id", id.ToString(CultureInfo.InvariantCulture));
            WriteNoteBlocks(writer, note, isEndnote, context);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        context.HyperlinkRelationships = bodyHyperlinks;
    }

    private static void WriteNoteSeparator(XmlWriter writer, string element, string type, int id)
    {
        writer.WriteStartElement("w", element, DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", type);
        WriteWAttribute(writer, "id", id.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", type, DocxConstants.WordprocessingML);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteNoteBlocks(XmlWriter writer, Footnote note, bool isEndnote, DocxWriteContext context)
    {
        var first = true;
        var wroteAny = false;
        foreach (var block in note.Blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    WriteNoteParagraph(writer, paragraph.Inlines, first, isEndnote, context);
                    first = false;
                    wroteAny = true;
                    break;
                case Heading heading:
                    WriteNoteParagraph(writer, heading.Inlines, first, isEndnote, context);
                    first = false;
                    wroteAny = true;
                    break;
                default:
                    context.Handler.Report($"DOCX: {block.GetType().Name} in a note is not rendered.");
                    break;
            }
        }

        if (!wroteAny)
        {
            WriteNoteParagraph(writer, [], first, isEndnote, context);
        }
    }

    private static void WriteNoteParagraph(
        XmlWriter writer,
        IReadOnlyList<Inline> inlines,
        bool first,
        bool isEndnote,
        DocxWriteContext context)
    {
        writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "pStyle", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", isEndnote ? "EndnoteText" : "FootnoteText");
        writer.WriteEndElement();
        writer.WriteEndElement();

        if (first)
        {
            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "rStyle", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", isEndnote ? "EndnoteReference" : "FootnoteReference");
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("w", isEndnote ? "endnoteRef" : "footnoteRef", DocxConstants.WordprocessingML);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        var previousNoteParagraph = context.Paragraph;
        context.Paragraph = null;
        foreach (var inline in inlines)
        {
            WriteInline(writer, inline, context);
        }

        context.Paragraph = previousNoteParagraph;
        writer.WriteEndElement();
    }

    private static bool IsInternalTarget(string target) => target.StartsWith('#') && target.Length > 1;

    private static void WriteHyperlink(XmlWriter writer, Hyperlink hyperlink, DocxWriteContext context)
    {
        // A tracked change cannot run across the hyperlink element: it is closed before and opened inside (ADR-0027).
        if (IsInternalTarget(hyperlink.Target))
        {
            DocxRevisions.Suspend(writer, context);
            writer.WriteStartElement("w", "hyperlink", DocxConstants.WordprocessingML);
            writer.WriteAttributeString(
                "w", "anchor", DocxConstants.WordprocessingML, context.BookmarkName(hyperlink.Target[1..]));
            DocxRevisions.Resume(writer, context);
            foreach (var inline in hyperlink.Inlines)
            {
                WriteInline(writer, inline, context);
            }

            DocxRevisions.Suspend(writer, context);
            writer.WriteEndElement();
            DocxRevisions.Resume(writer, context);
            return;
        }

        var linked = context.HyperlinkRelationships.TryGetValue(hyperlink.Target, out var relationshipId);
        if (linked)
        {
            DocxRevisions.Suspend(writer, context);
            writer.WriteStartElement("w", "hyperlink", DocxConstants.WordprocessingML);
            writer.WriteAttributeString("r", "id", DocxConstants.Relationships, relationshipId!);
            DocxRevisions.Resume(writer, context);
        }
        else
        {
            context.Handler.Report("DOCX: hyperlink is not linked (body only).");
        }

        foreach (var inline in hyperlink.Inlines)
        {
            WriteInline(writer, inline, context);
        }

        if (linked)
        {
            DocxRevisions.Suspend(writer, context);
            writer.WriteEndElement();
            DocxRevisions.Resume(writer, context);
        }
    }

    private static void WriteTextRun(XmlWriter writer, TextRun textRun, DocxWriteContext context)
    {
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        WriteRunProperties(writer, textRun, context);
        WriteText(writer, textRun.Text, context.XmlBehavior, DocxRevisions.InDeletion(context));
        writer.WriteEndElement();
    }

    /// <summary>
    /// writes the run: the character style stays a reference, the rest is the formatting the run shows
    /// next to what it inherits (ADR-0010).
    /// </summary>
    private static void WriteRunProperties(XmlWriter writer, TextRun run, DocxWriteContext context)
    {
        // The common case: neither the run nor its paragraph style formats anything.
        if (run.CharacterStyleName is null
            && !RunFormat.HasFormatting(run)
            && (context.Paragraph is null || !RunFormat.HasFormatting(context.Paragraph)))
        {
            return;
        }

        var characterStyle = RunFormatResolver.Find(context.CharacterStyles, run.CharacterStyleName);
        var format = RunFormat.Of(RunFormatResolver.Resolve(run, context.Paragraph, characterStyle))
            .Difference(RunBaseOf(context));
        if (format.IsEmpty && string.IsNullOrEmpty(run.CharacterStyleName))
        {
            return;
        }

        writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
        if (!string.IsNullOrEmpty(run.CharacterStyleName))
        {
            WriteValElement(writer, "rStyle", run.CharacterStyleName);
        }

        WriteRunFormat(writer, format, context.XmlBehavior, context.Handler);
        writer.WriteEndElement();
    }

    /// <summary>
    /// writes the run formatting (ADR-0010). A property the source does not set stays absent, so the
    /// inherited value applies; an explicit off is written as <c>w:val="0"</c> or
    /// <c>w:u w:val="none"</c>.
    /// </summary>
    private static void WriteRunFormat(XmlWriter writer, RunFormat format, InvalidXmlCharacterBehavior xmlBehavior, UnsupportedFeatureHandler? handler = null)
    {
        if (format.FontFamily is not null)
        {
            WriteFonts(writer, XmlText.Clean(FontFamilies.Resolve(format.FontFamily).Name, xmlBehavior));
        }

        WriteToggle(writer, "b", format.Bold);
        WriteToggle(writer, "i", format.Italic);
        WriteToggle(writer, "caps", format.AllCaps);
        WriteToggle(writer, "smallCaps", format.SmallCaps);

        switch (format.Strikethrough)
        {
            case TextStrikethrough.Single:
                WriteEmptyElement(writer, "strike");
                break;
            case TextStrikethrough.Double:
                WriteEmptyElement(writer, "dstrike");
                break;
            case TextStrikethrough.None:
                WriteOffElement(writer, "strike");
                WriteOffElement(writer, "dstrike");
                break;
        }

        if (format.Effects is { } effects)
        {
            // CT_RPr: outline, shadow, emboss, imprint follow the strikethrough.
            WriteToggle(writer, "outline", effects.HasFlag(TextEffects.Outline));
            WriteToggle(writer, "shadow", effects.HasFlag(TextEffects.Shadow));
            WriteToggle(writer, "emboss", effects.HasFlag(TextEffects.Emboss));
            WriteToggle(writer, "imprint", effects.HasFlag(TextEffects.Engrave));
        }

        WriteToggle(writer, "vanish", format.Hidden);

        if (format.Color is { } color)
        {
            WriteValElement(writer, "color", HexColor(color));
        }

        if (format.CharacterSpacing is { } characterSpacing && characterSpacing.Points != 0)
        {
            WriteValElement(writer, "spacing", Twips(characterSpacing).ToString(CultureInfo.InvariantCulture));
        }

        if (format.HorizontalScaling is { } scaling && Math.Abs(scaling - 100) > 1e-9)
        {
            WriteValElement(writer, "w", ((long)Math.Round(scaling, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));
        }

        if (format.Kerning is { } kerning)
        {
            // 0 means "kerning from every font size"; ODT's boolean enables kerning that way.
            WriteValElement(writer, "kern", HalfPoints(kerning).ToString(CultureInfo.InvariantCulture));
        }

        if (format.Position is { } position)
        {
            WriteValElement(writer, "position", HalfPoints(position).ToString(CultureInfo.InvariantCulture));
        }

        if (format.FontSize is { } fontSize)
        {
            var size = HalfPoints(fontSize).ToString(CultureInfo.InvariantCulture);
            WriteValElement(writer, "sz", size);
            WriteValElement(writer, "szCs", size);
        }

        // Word's marker knows 16 colours; any other highlight falls back to shading below.
        var highlightName = format.Highlight is { } highlight ? DocxHighlight.NameOf(highlight) : null;
        if (highlightName is not null)
        {
            WriteValElement(writer, "highlight", highlightName);
        }

        if (format.EffectiveUnderline is { } underline)
        {
            writer.WriteStartElement("w", "u", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", UnderlineValue(underline));
            if (format.UnderlineColor is { } underlineColor)
            {
                WriteWAttribute(writer, "color", HexColor(underlineColor));
            }

            writer.WriteEndElement();
        }

        if (format.Border is { } runBorder)
        {
            WriteParagraphBorder(writer, "bdr", runBorder);
        }

        var background = format.Background;
        if (format.Highlight is { } unnamed && highlightName is null)
        {
            handler?.ReportOnce(background is null
                ? $"DOCX: the highlight colour {HexColor(unnamed)} is not one of Word's 16 marker colours; it is written as shading."
                : $"DOCX: the highlight colour {HexColor(unnamed)} is not one of Word's 16 marker colours and the run has a background; the highlight is omitted.");
            background ??= unnamed;
        }

        if (background is { } fill)
        {
            writer.WriteStartElement("w", "shd", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", "clear");
            WriteWAttribute(writer, "color", "auto");
            WriteWAttribute(writer, "fill", HexColor(fill));
            writer.WriteEndElement();
        }

        if (format.Position is null && format.Baseline is { } baseline)
        {
            WriteValElement(
                writer,
                "vertAlign",
                baseline switch
                {
                    TextBaseline.Superscript => "superscript",
                    TextBaseline.Subscript => "subscript",
                    _ => "baseline",
                });
        }
    }

    /// <summary>
    /// writes a toggle: nothing when it is not set, <c>&lt;w:name/&gt;</c> when it is on and
    /// <c>&lt;w:name w:val="0"/&gt;</c> when an inherited toggle is switched off.
    /// </summary>
    private static void WriteToggle(XmlWriter writer, string name, bool? value)
    {
        if (value == true)
        {
            WriteEmptyElement(writer, name);
        }
        else if (value == false)
        {
            WriteOffElement(writer, name);
        }
    }

    /// <summary>Writes an explicit "off" of a boolean run property.</summary>
    private static void WriteOffElement(XmlWriter writer, string name)
    {
        writer.WriteStartElement("w", name, DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", "0");
        writer.WriteEndElement();
    }

    private static string UnderlineValue(TextUnderlineStyle style) => style switch
    {
        TextUnderlineStyle.None => "none",
        TextUnderlineStyle.Double => "double",
        TextUnderlineStyle.Dotted => "dotted",
        TextUnderlineStyle.Dashed => "dash",
        TextUnderlineStyle.Wave => "wave",
        _ => "single",
    };

    private static void WriteHeaderFooter(
        XmlWriter writer,
        HeaderFooter content,
        bool isHeader,
        long availableWidth,
        IReadOnlyDictionary<ImageBlock, string> imageRelationships,
        IReadOnlyDictionary<string, string> hyperlinkRelationships,
        DocxWriteContext context,
        IReadOnlyDictionary<Block, XElement>? preservedBlocks = null)
    {
        writer.WriteStartElement("w", isHeader ? "hdr" : "ftr", DocxConstants.WordprocessingML);
        DocxConstants.WriteCompatibilityNamespaces(writer);

        // images reference the media via the relationships of their own part;
        // page breaks cannot be represented in the header/footer and are reported.
        var bodyImages = context.ImageRelationships;
        var bodyHyperlinks = context.HyperlinkRelationships;
        context.BandName = isHeader ? "header" : "footer";
        context.ImageRelationships = imageRelationships;
        context.HyperlinkRelationships = hyperlinkRelationships;
        using (context.Spacing.EnterCompact())
        {
            foreach (var block in content.Blocks)
            {
                // OF-B08: a band block the model still holds as the reader's instance is re-emitted
                // as its source XML, so unmodelled XML inside it survives a change in another block.
                if (preservedBlocks is not null && preservedBlocks.TryGetValue(block, out var source))
                {
                    source.WriteTo(writer);
                }
                else
                {
                    WriteBlock(writer, block, availableWidth, context);
                }
            }
        }

        context.BandName = null;
        context.ImageRelationships = bodyImages;
        context.HyperlinkRelationships = bodyHyperlinks;

        // ECMA-376 requires that a header/footer ends with a paragraph (even if it
        // is empty or ends with a table); otherwise Word reports a repair.
        if (!EndsWithParagraph(content.Blocks, inBand: true))
        {
            writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// whether the last-written element of these blocks is a <c>w:p</c>. In the header/footer
    /// area, page breaks do nothing.
    /// </summary>
    private static bool EndsWithParagraph(IReadOnlyList<Block> blocks, bool inBand)
    {
        if (blocks.Count == 0)
        {
            return false;
        }

        return blocks[^1] switch
        {
            Paragraph or Heading => true,
            ImageBlock or DrawingShape or FormField or Diagram or Equation => true,
            PageBreak => !inBand,
            // An entry without a paragraph gets an empty numbered paragraph at the end.
            ListBlock { Items.Count: > 0 } list => !list.Items[^1].Blocks.Any(block => block is Paragraph)
                || EndsWithParagraph(list.Items[^1].Blocks, inBand),
            _ => false,
        };
    }

    private static void WriteField(XmlWriter writer, string instruction, string? result, InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        WriteFieldChar(writer, "begin");
        writer.WriteEndElement();

        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "instrText", DocxConstants.WordprocessingML);
        writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(instruction);
        writer.WriteEndElement();
        writer.WriteEndElement();

        if (result is not null)
        {
            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            WriteFieldChar(writer, "separate");
            writer.WriteEndElement();

            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            WriteText(writer, result, xmlBehavior);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        WriteFieldChar(writer, "end");
        writer.WriteEndElement();
    }

    private static void WriteFieldChar(XmlWriter writer, string type)
    {
        writer.WriteStartElement("w", "fldChar", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "fldCharType", type);
        writer.WriteEndElement();
    }

    private static void WriteEmptyElement(XmlWriter writer, string localName)
    {
        writer.WriteStartElement("w", localName, DocxConstants.WordprocessingML);
        writer.WriteEndElement();
    }

    private static string HexColor(Color color) => color.ToString().TrimStart('#');

    /// <summary>Writes run text; line breaks and tabs become <c>w:br</c> and <c>w:tab</c>, which <c>w:t</c> cannot carry.</summary>
    internal static void WriteText(XmlWriter writer, string text, InvalidXmlCharacterBehavior xmlBehavior, bool deleted = false)
    {
        text = XmlText.Clean(text, xmlBehavior);
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\t' or '\n' or '\r'))
            {
                continue;
            }

            if (index > start)
            {
                WriteTextElement(writer, text[start..index], deleted);
            }

            writer.WriteStartElement("w", text[index] == '\t' ? "tab" : "br", DocxConstants.WordprocessingML);
            writer.WriteEndElement();
            index += text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 1 : 0;
            start = index + 1;
        }

        if (start < text.Length || text.Length == 0)
        {
            WriteTextElement(writer, text[start..], deleted);
        }
    }

    private static void WriteTextElement(XmlWriter writer, string text, bool deleted)
    {
        // Deleted text is w:delText (ADR-0027).
        writer.WriteStartElement("w", deleted ? "delText" : "t", DocxConstants.WordprocessingML);
        if (NeedsSpacePreservation(text))
        {
            writer.WriteAttributeString("xml", "space", null, "preserve");
        }

        writer.WriteString(text);
        writer.WriteEndElement();
    }

    private static bool NeedsSpacePreservation(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]))
        {
            return true;
        }

        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) && char.IsWhiteSpace(text[i - 1]))
            {
                return true;
            }
        }

        return text.Any(character => character is '\t' or '\n' or '\r');
    }

    private static string AlignmentValue(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => "center",
        TextAlignment.Right => "right",
        TextAlignment.Justify => "both",
        _ => "left",
    };

    private static void WriteSectionProperties(
        XmlWriter writer,
        Section section,
        int sectionIndex,
        IReadOnlyDictionary<(int SectionIndex, DocxBand Band), HeaderFooterResource> headerFooters)
    {
        var landscape = section.Orientation == PageOrientation.Landscape;
        var width = landscape ? section.PageSize.Height : section.PageSize.Width;
        var height = landscape ? section.PageSize.Width : section.PageSize.Height;

        writer.WriteStartElement("w", "sectPr", DocxConstants.WordprocessingML);

        // The order follows the schema (CT_SectPr): the header and footer references come
        // first, then w:type, w:pgSz, w:pgMar, w:pgBorders, w:lnNumType, w:pgNumType,
        // w:cols and finally w:titlePg.

        WriteHeaderFooterReference(writer, headerFooters, sectionIndex, DocxBand.Header, "headerReference");
        WriteHeaderFooterReference(writer, headerFooters, sectionIndex, DocxBand.HeaderFirst, "headerReference");
        WriteHeaderFooterReference(writer, headerFooters, sectionIndex, DocxBand.HeaderEven, "headerReference");
        WriteHeaderFooterReference(writer, headerFooters, sectionIndex, DocxBand.Footer, "footerReference");
        WriteHeaderFooterReference(writer, headerFooters, sectionIndex, DocxBand.FooterFirst, "footerReference");
        WriteHeaderFooterReference(writer, headerFooters, sectionIndex, DocxBand.FooterEven, "footerReference");

        // The section break type (schema order: before pgSz).
        if (section.StartType != SectionStartType.NextPage)
        {
            writer.WriteStartElement("w", "type", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", section.StartType switch
            {
                SectionStartType.Continuous => "continuous",
                SectionStartType.OddPage => "oddPage",
                SectionStartType.EvenPage => "evenPage",
                _ => "nextPage",
            });
            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "pgSz", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "w", Twips(width));
        WriteWAttribute(writer, "h", Twips(height));
        if (landscape)
        {
            WriteWAttribute(writer, "orient", "landscape");
        }

        writer.WriteEndElement();

        writer.WriteStartElement("w", "pgMar", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "top", Twips(section.Margins.Top));
        WriteWAttribute(writer, "right", Twips(section.Margins.Right));
        WriteWAttribute(writer, "bottom", Twips(section.Margins.Bottom));
        WriteWAttribute(writer, "left", Twips(section.Margins.Left));
        WriteWAttribute(writer, "header", Twips(section.HeaderDistance ?? Typography.HeaderFooterDistance));
        WriteWAttribute(writer, "footer", Twips(section.FooterDistance ?? Typography.HeaderFooterDistance));
        WriteWAttribute(writer, "gutter", 0L);
        writer.WriteEndElement();

        // The page border (schema order: after pgMar, before lnNumType).
        if (section.PageBorders is { } pageBorders && !pageBorders.IsEmpty)
        {
            writer.WriteStartElement("w", "pgBorders", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "offsetFrom", "text");
            var space = Math.Clamp((long)Math.Round((pageBorders.Offset ?? Length.FromPoints(24)).Points, MidpointRounding.AwayFromZero), 0, 31);
            WritePageBorderSide(writer, "top", pageBorders.Top, space);
            WritePageBorderSide(writer, "left", pageBorders.Left, space);
            WritePageBorderSide(writer, "bottom", pageBorders.Bottom, space);
            WritePageBorderSide(writer, "right", pageBorders.Right, space);
            writer.WriteEndElement();
        }

        // The order of the elements follows the schema (CT_SectPr): lnNumType comes
        // before pgNumType and cols.
        if (section.LineNumbering is { } lineNumbering)
        {
            writer.WriteStartElement("w", "lnNumType", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "countBy", lineNumbering.CountBy.ToString(CultureInfo.InvariantCulture));
            WriteWAttribute(writer, "start", lineNumbering.Start.ToString(CultureInfo.InvariantCulture));
            WriteWAttribute(writer, "distance", Twips(lineNumbering.Distance).ToString(CultureInfo.InvariantCulture));
            WriteWAttribute(writer, "restart", lineNumbering.Restart switch
            {
                LineNumberRestart.Continuous => "continuous",
                LineNumberRestart.NewSection => "newSection",
                _ => "newPage",
            });
            writer.WriteEndElement();
        }

        if (section.PageNumberStart is { } pageNumberStart)
        {
            writer.WriteStartElement("w", "pgNumType", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "start", pageNumberStart.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        if (section.TextColumns is { } columns && columns > 1)
        {
            writer.WriteStartElement("w", "cols", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "num", (long)columns);
            writer.WriteEndElement();
        }

        // The title page (schema order: after w:cols).
        if (section.FirstHeader is not null || section.FirstFooter is not null)
        {
            WriteEmptyElement(writer, "titlePg");
        }

        writer.WriteEndElement();
    }

    private static void WriteHeaderFooterReference(
        XmlWriter writer,
        IReadOnlyDictionary<(int SectionIndex, DocxBand Band), HeaderFooterResource> headerFooters,
        int sectionIndex,
        DocxBand band,
        string elementName)
    {
        if (!headerFooters.TryGetValue((sectionIndex, band), out var resource))
        {
            return;
        }

        writer.WriteStartElement("w", elementName, DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", BandType(band));
        writer.WriteAttributeString("r", "id", DocxConstants.Relationships, resource.RelationshipId);
        writer.WriteEndElement();
    }

    private static string BandType(DocxBand band) => band switch
    {
        DocxBand.HeaderFirst or DocxBand.FooterFirst => "first",
        DocxBand.HeaderEven or DocxBand.FooterEven => "even",
        _ => "default",
    };

    /// <summary>
    /// writes one abstractNum and one num per used definition. ECMA-376 requires all abstractNum
    /// before the num elements. The ids start above the source maxima when a source numbering is
    /// preserved (AF08), so no id collides.
    /// </summary>
    private static void WriteNumbering(
        XmlWriter writer,
        IReadOnlyList<(NumberingDefinition Definition, int NumId)> definitions,
        int abstractNumBase)
    {
        writer.WriteStartElement("w", "numbering", DocxConstants.WordprocessingML);

        for (var index = 0; index < definitions.Count; index++)
        {
            WriteAbstractNum(writer, abstractNumBase + index, definitions[index].Definition);
        }

        for (var index = 0; index < definitions.Count; index++)
        {
            WriteNum(writer, definitions[index].NumId, abstractNumBase + index);
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Writes <c>numbering.xml</c>. If a source numbering part exists (AF08), its definitions are
    /// adopted and the generated definitions (with ids above the source maxima) are appended in
    /// schema order: all <c>w:abstractNum</c> before all <c>w:num</c>.
    /// </summary>
    private static void WriteNumberingPart(
        OpcPackageWriter package,
        IReadOnlyList<(NumberingDefinition Definition, int NumId)> definitions,
        int abstractNumBase,
        XElement? source)
    {
        if (source is null)
        {
            package.WriteXmlPart(
                DocxConstants.NumberingPart,
                writer => WriteNumbering(writer, definitions, abstractNumBase));
            return;
        }

        var generatedBuilder = new StringBuilder();
        using (var writer = XmlWriter.Create(generatedBuilder, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Document,
            Indent = false,
        }))
        {
            WriteNumbering(writer, definitions, abstractNumBase);
        }

        var generated = XElement.Parse(generatedBuilder.ToString());
        XNamespace w = DocxConstants.WordprocessingML;
        var merged = new XElement(source);

        var anchor = merged.Elements(w + "abstractNum").LastOrDefault();
        foreach (var abstractNum in generated.Elements(w + "abstractNum").ToList())
        {
            abstractNum.Remove();
            if (anchor is null)
            {
                merged.AddFirst(abstractNum);
            }
            else
            {
                anchor.AddAfterSelf(abstractNum);
            }

            anchor = abstractNum;
        }

        foreach (var num in generated.Elements(w + "num").ToList())
        {
            num.Remove();
            merged.Add(num);
        }

        package.WriteXmlPart(DocxConstants.NumberingPart, writer => merged.WriteTo(writer));
    }

    private static SourceNumbering? ReadSourceNumbering(ReadOnlyMemory<byte>? sourcePackage)
    {
        if (sourcePackage is not { IsEmpty: false } bytes)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var source = new ZipArchive(stream, ZipArchiveMode.Read);
            if (source.GetEntry(DocxConstants.DocumentPart) is null
                || source.GetEntry(DocxConstants.NumberingPart) is not { } entry)
            {
                return null;
            }

            var root = ReaderGuard.LoadXml(TemplatePackage.ReadEntry(entry)).Root;
            if (root is null)
            {
                return null;
            }

            XNamespace w = DocxConstants.WordprocessingML;
            var maxNumId = root.Elements(w + "num")
                .Select(element => SourceId(element.Attribute(w + "numId"))).DefaultIfEmpty(0).Max();
            var maxAbstractNumId = root.Elements(w + "abstractNum")
                .Select(element => SourceId(element.Attribute(w + "abstractNumId"))).DefaultIfEmpty(0).Max();
            return new SourceNumbering(root, maxNumId, maxAbstractNumId);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>An id of a source part; one that is not a number cannot collide with a generated id.</summary>
    private static int SourceId(XAttribute? attribute)
        => int.TryParse(attribute?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;

    private sealed record SourceNumbering(XElement Root, int MaxNumId, int MaxAbstractNumId);

    internal static void WriteAbstractNum(XmlWriter writer, int abstractNumId, NumberingDefinition definition)
    {
        writer.WriteStartElement("w", "abstractNum", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "abstractNumId", abstractNumId.ToString(CultureInfo.InvariantCulture));

        for (var level = 0; level < NumberingDefinition.MaxLevels; level++)
        {
            var spec = definition.Levels[level];
            var isBullet = spec.Kind == ListKind.Bullet;
            writer.WriteStartElement("w", "lvl", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "ilvl", level.ToString(CultureInfo.InvariantCulture));
            WriteValElement(writer, "start", isBullet ? "1" : spec.Start.ToString(CultureInfo.InvariantCulture));
            WriteValElement(writer, "numFmt", isBullet ? "bullet" : NumberFormatName(spec.Format));
            if (spec.Suffix != ListSuffix.Tab)
            {
                // CT_Lvl: w:suff comes before w:lvlText.
                WriteValElement(writer, "suff", spec.Suffix == ListSuffix.Space ? "space" : "nothing");
            }

            WriteValElement(writer, "lvlText", isBullet ? BulletText(level) : $"%{level + 1}.");
            WriteValElement(writer, "lvlJc", "left");

            writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "ind", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "left", (long)((level + 1) * 720));
            WriteWAttribute(writer, "hanging", 360L);
            writer.WriteEndElement();
            writer.WriteEndElement();

            // AJ04: the color and the weight of the number (the run properties of the level).
            if (spec.NumberColor is not null || spec.NumberBold)
            {
                writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
                if (spec.NumberBold)
                {
                    WriteEmptyElement(writer, "b");
                }

                if (spec.NumberColor is { } color)
                {
                    WriteValElement(writer, "color", HexColor(color));
                }

                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static string NumberFormatName(ListNumberFormat format) => format switch
    {
        ListNumberFormat.LowerRoman => "lowerRoman",
        ListNumberFormat.UpperRoman => "upperRoman",
        ListNumberFormat.LowerLetter => "lowerLetter",
        ListNumberFormat.UpperLetter => "upperLetter",
        _ => "decimal",
    };

    internal static void WriteNum(XmlWriter writer, int numId, int abstractNumId)
    {
        writer.WriteStartElement("w", "num", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "numId", numId.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartElement("w", "abstractNumId", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", abstractNumId.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static string BulletText(int level) => (level % 3) switch
    {
        0 => "\u2022",
        1 => "\u25E6",
        _ => "\u25AA",
    };

    /// <summary>An image under <c>word/media</c>; <see cref="Target"/> is relative to <c>word/</c>.</summary>
    private sealed record MediaPart(string PartName, string Target);

    /// <summary>
    /// stores each image exactly once under <c>word/media</c>, even if it appears in the body
    /// and occurs in several headers/footers.
    /// </summary>
    private sealed class MediaRegistry(OpcPackageWriter package, ResourceLimits limits, CancellationToken cancellationToken, ReadOnlyMemory<byte>? sourcePackage)
    {
        private readonly Dictionary<ImageBlock, MediaPart> _parts = new();
        private readonly Dictionary<string, MediaPart> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly (HashSet<string> Names, Dictionary<string, string> ByContent) _source = ReadSourceMedia(sourcePackage);
        private int _nextIndex;

        /// <summary>
        /// The media parts of the source. Blocks kept as source XML still point at these names, so
        /// an image with the same content reuses its name and any other image avoids them.
        /// </summary>
        private static (HashSet<string> Names, Dictionary<string, string> ByContent) ReadSourceMedia(ReadOnlyMemory<byte>? package)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byContent = new Dictionary<string, string>(StringComparer.Ordinal);
            if (package is not { IsEmpty: false } bytes)
            {
                return (names, byContent);
            }

            try
            {
                using var stream = new MemoryStream(bytes.ToArray(), writable: false);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(entry.FullName);
                    byContent.TryAdd(ContentKey(TemplatePackage.ReadEntry(entry), Path.GetExtension(entry.FullName)), entry.FullName);
                }
            }
            catch (InvalidDataException)
            {
                // A damaged source has no media to reuse; the names found so far stay reserved.
            }

            return (names, byContent);
        }

        private static string ContentKey(ReadOnlySpan<byte> data, string extension)
            => Convert.ToHexString(SHA256.HashData(data)) + extension.ToLowerInvariant();

        public MediaPart GetOrAdd(ImageBlock image)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_parts.TryGetValue(image, out var existing))
            {
                return existing;
            }

            ImageGuard.Check(image, limits);
            var (extension, contentType) = image.MediaType switch
            {
                ImageMediaType.Svg => ("svg", DocxConstants.SvgContentType),
                ImageMediaType.Png => ("png", DocxConstants.PngContentType),
                _ => ("jpeg", DocxConstants.JpegContentType),
            };
            if (_source.ByContent.TryGetValue(ContentKey(image.RawData.Span, "." + extension), out var sourceName))
            {
                if (_byName.TryGetValue(sourceName, out var written))
                {
                    _parts[image] = written;
                    return written;
                }

                return Add(image, sourceName, extension, contentType);
            }

            string name;
            do
            {
                name = $"word/media/image{++_nextIndex}.{extension}";
            }
            while (_source.Names.Contains(name));

            return Add(image, name, extension, contentType);
        }

        private MediaPart Add(ImageBlock image, string name, string extension, string contentType)
        {
            var part = new MediaPart(name, name["word/".Length..]);
            package.AddDefault(extension, contentType);
            package.AddBinaryPart(part.PartName, image.RawData);
            _parts[image] = part;
            _byName[name] = part;
            return part;
        }
    }

    /// <summary>A chart part and a relationship for every chart of the body (ADR-0025).</summary>
    private static Dictionary<Chart, string> RegisterCharts(
        OpcPackageWriter package, PortableDocument document, InvalidXmlCharacterBehavior xmlBehavior, ref int relationshipIndex)
    {
        var relationships = new Dictionary<Chart, string>(ReferenceEqualityComparer.Instance);
        var number = 0;
        foreach (var chart in document.Sections.SelectMany(section => BlockScan.Enumerate<Chart>(section.Blocks)))
        {
            var partName = $"word/charts/chart{++number}.xml";
            package.AddOverride("/" + partName, DocxCharts.ChartContentType);
            var relationshipId = $"rId{relationshipIndex++}";
            package.AddRelationship(DocxConstants.DocumentPart, new OpcRelationship(relationshipId, DocxCharts.ChartRelationship, $"charts/chart{number}.xml"));
            relationships[chart] = relationshipId;
            package.WriteXmlPart(partName, writer => DocxCharts.WritePart(writer, chart, xmlBehavior));
        }

        return relationships;
    }

    private static (Dictionary<ImageBlock, string> Relationships, int NextRelationshipIndex) RegisterImages(
        OpcPackageWriter package,
        PortableDocument document,
        MediaRegistry media)
    {
        var relationshipIndex = 4; // rId1 = styles, rId2 = numbering, rId3 = settings
        var relationships = RegisterImageRelationships(
            package,
            DocxConstants.DocumentPart,
            document.Sections.SelectMany(section => EnumerateImages(section.Blocks)),
            media,
            ref relationshipIndex);
        return (relationships, relationshipIndex);
    }

    /// <summary>Creates exactly one relationship to its media part for each image of a part.</summary>
    private static Dictionary<ImageBlock, string> RegisterImageRelationships(
        OpcPackageWriter package,
        string sourcePart,
        IEnumerable<ImageBlock> images,
        MediaRegistry media,
        ref int relationshipIndex)
    {
        var relationships = new Dictionary<ImageBlock, string>();
        foreach (var image in WithRasterFallbacks(images))
        {
            if (relationships.ContainsKey(image))
            {
                continue;
            }

            var part = media.GetOrAdd(image);
            var relationshipId = $"rId{relationshipIndex++}";
            package.AddRelationship(sourcePart, new OpcRelationship(relationshipId, DocxConstants.ImageRelationship, part.Target));
            relationships[image] = relationshipId;
        }

        return relationships;
    }

    internal static IEnumerable<ImageBlock> WithRasterFallbacks(IEnumerable<ImageBlock> images)
    {
        foreach (var image in images)
        {
            yield return image;
            if (image.RasterFallback is { } fallback) yield return fallback;
        }
    }

    private sealed record HeaderFooterResource(
        string PartName,
        string RelationshipId,
        HeaderFooter Content,
        IReadOnlyDictionary<ImageBlock, string> ImageRelationships,
        IReadOnlyDictionary<string, string> HyperlinkRelationships,
        int NextRelationshipIndex);

    private static Dictionary<(int SectionIndex, DocxBand Band), HeaderFooterResource> RegisterHeadersFooters(
        OpcPackageWriter package,
        PortableDocument document,
        MediaRegistry media,
        ref int relationshipIndex)
    {
        var resources = new Dictionary<(int, DocxBand), HeaderFooterResource>();
        var headerIndex = 0;
        var footerIndex = 0;
        var defaultHeaderSeen = false;
        var defaultFooterSeen = false;
        var emptyBand = new HeaderFooter(Array.Empty<Block>());

        // Word's even-page setting is document-wide: a section without an even band would show
        // nothing on its even pages instead of its default band.
        var evenAndOdd = document.Sections.Any(section => section.EvenHeader is not null || section.EvenFooter is not null);

        for (var sectionIndex = 0; sectionIndex < document.Sections.Count; sectionIndex++)
        {
            var section = document.Sections[sectionIndex];

            // A section without a headerReference inherits the previous section's header in Word
            // section. If an earlier section has one, this one gets an empty one.
            var header = section.Header ?? (defaultHeaderSeen ? emptyBand : null);
            defaultHeaderSeen |= section.Header is not null;
            RegisterBand(package, resources, media, sectionIndex, DocxBand.Header, "header", header, ref headerIndex, ref relationshipIndex);
            RegisterBand(package, resources, media, sectionIndex, DocxBand.HeaderFirst, "header", section.FirstHeader, ref headerIndex, ref relationshipIndex);
            RegisterBand(package, resources, media, sectionIndex, DocxBand.HeaderEven, "header", section.EvenHeader ?? (evenAndOdd ? header : null), ref headerIndex, ref relationshipIndex);

            var footer = section.Footer ?? (defaultFooterSeen ? emptyBand : null);
            defaultFooterSeen |= section.Footer is not null;
            RegisterBand(package, resources, media, sectionIndex, DocxBand.Footer, "footer", footer, ref footerIndex, ref relationshipIndex);
            RegisterBand(package, resources, media, sectionIndex, DocxBand.FooterFirst, "footer", section.FirstFooter, ref footerIndex, ref relationshipIndex);
            RegisterBand(package, resources, media, sectionIndex, DocxBand.FooterEven, "footer", section.EvenFooter ?? (evenAndOdd ? footer : null), ref footerIndex, ref relationshipIndex);
        }

        return resources;
    }

    private static void RegisterBand(
        OpcPackageWriter package,
        Dictionary<(int, DocxBand), HeaderFooterResource> resources,
        MediaRegistry media,
        int sectionIndex,
        DocxBand band,
        string elementName,
        HeaderFooter? content,
        ref int index,
        ref int relationshipIndex)
    {
        if (content is null)
        {
            return;
        }

        index++;
        var partName = $"word/{elementName}{index}.xml";
        package.AddOverride(
            partName,
            elementName == "header" ? DocxConstants.HeaderContentType : DocxConstants.FooterContentType);
        var relationshipId = $"rId{relationshipIndex++}";
        package.AddRelationship(
            DocxConstants.DocumentPart,
            new OpcRelationship(
                relationshipId,
                elementName == "header" ? DocxConstants.HeaderRelationship : DocxConstants.FooterRelationship,
                $"{elementName}{index}.xml"));
        var bandRelationshipIndex = 1;
        var images = RegisterImageRelationships(package, partName, EnumerateImages(content.Blocks), media, ref bandRelationshipIndex);
        var hyperlinks = RegisterHyperlinkRelationships(package, partName, EnumerateHyperlinks(content.Blocks), ref bandRelationshipIndex);
        resources[(sectionIndex, band)] = new HeaderFooterResource(partName, relationshipId, content, images, hyperlinks, bandRelationshipIndex);
    }

    private static Dictionary<string, string> RegisterHyperlinks(
        OpcPackageWriter package,
        PortableDocument document,
        ref int relationshipIndex)
    {
        return RegisterHyperlinkRelationships(package, DocxConstants.DocumentPart,
            document.Sections.SelectMany(section => EnumerateHyperlinks(section.Blocks)), ref relationshipIndex);
    }

    /// <summary>The links of the notes; the notes part has relationships of its own.</summary>
    private static Dictionary<string, string> RegisterNoteHyperlinks(
        OpcPackageWriter package, string partName, IReadOnlyList<(int Id, Footnote Note)> notes)
    {
        var relationshipIndex = 1;
        return RegisterHyperlinkRelationships(package, partName, notes.SelectMany(note => EnumerateHyperlinks(note.Note.Blocks)), ref relationshipIndex);
    }

    private static Dictionary<string, string> RegisterHyperlinkRelationships(
        OpcPackageWriter package, string partName, IEnumerable<string> targets, ref int relationshipIndex)
    {
        var relationships = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            // "#name" jumps to a bookmark (w:anchor) and needs no relationship.
            if (relationships.ContainsKey(target) || IsInternalTarget(target))
            {
                continue;
            }

            var relationshipId = $"rId{relationshipIndex++}";
            package.AddRelationship(partName, new OpcRelationship(relationshipId, DocxConstants.HyperlinkRelationship, target, "External"));
            relationships[target] = relationshipId;
        }

        return relationships;
    }

    internal static IEnumerable<string> EnumerateHyperlinks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    foreach (var target in EnumerateHyperlinks(paragraph.Inlines))
                    {
                        yield return target;
                    }

                    break;
                case Heading heading:
                    foreach (var target in EnumerateHyperlinks(heading.Inlines))
                    {
                        yield return target;
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        foreach (var target in EnumerateHyperlinks(item.Blocks))
                        {
                            yield return target;
                        }
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var target in EnumerateHyperlinks(cell.Blocks))
                            {
                                yield return target;
                            }
                        }
                    }

                    break;
            }
        }
    }

    internal static IEnumerable<string> EnumerateHyperlinks(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Hyperlink hyperlink:
                    yield return hyperlink.Target;
                    foreach (var target in EnumerateHyperlinks(hyperlink.Inlines))
                    {
                        yield return target;
                    }

                    break;
            }
        }
    }

    internal static IEnumerable<ImageBlock> EnumerateImages(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ImageBlock image:
                    yield return image;
                    break;
                case Paragraph paragraph:
                    foreach (var image in InlineImage.In(paragraph.Inlines))
                    {
                        yield return image;
                    }

                    break;
                case Heading heading:
                    foreach (var image in InlineImage.In(heading.Inlines))
                    {
                        yield return image;
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        foreach (var image in EnumerateImages(item.Blocks))
                        {
                            yield return image;
                        }
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var image in EnumerateImages(cell.Blocks))
                            {
                                yield return image;
                            }
                        }
                    }

                    break;
            }
        }
    }

    private static void WriteImageBlock(
        XmlWriter writer,
        ImageBlock image,
        DocxWriteContext context,
        bool inLine = false)
    {
        // inLine: an InlineImage, written as a run of the paragraph it stands in.
        var floating = !inLine && image.Wrap != ImageWrap.None;
        var isSvg = image.MediaType == ImageMediaType.Svg;
        if (isSvg && image.RasterFallback is null)
        {
            // The SVG is embedded as a media part and referenced through the
            // SVG extension of the picture; without a raster fallback older
            // Word versions show nothing.
            context.Handler.Report(
                "DOCX: SVG images are embedded without a raster fallback; Word 2016 or newer shows them, older versions may not.");
        }

        if (!context.ImageRelationships.TryGetValue(image, out var relationshipId))
        {
            // All images of a part are registered in advance; if one is missing, that is an error
            // in the writer and must not lead to silent data loss.
            throw new InvalidOperationException("DOCX: image without a registered relationship.");
        }

        string? fallbackRelationshipId = null;
        if (image.RasterFallback is { } fallback && !context.ImageRelationships.TryGetValue(fallback, out fallbackRelationshipId))
            throw new InvalidOperationException("DOCX: SVG raster fallback without a registered relationship.");

        if (!image.TakesAnchorLine && floating)
        {
            context.Handler.Report("DOCX: a floating image without an anchor line is written in a paragraph of its own; Word shows that paragraph as an empty line.");
        }

        var (width, height) = ImageLayout.Resolve(image);
        var drawingId = context.NextDrawingId();

        if (!inLine)
        {
            writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
            if (context.ListLevel > 0 && image.Wrap == ImageWrap.None)
            {
                // An image in a list item lines up with the text of the item.
                writer.WriteStartElement("w", "ind", DocxConstants.WordprocessingML);
                WriteWAttribute(writer, "left", Math.Min(context.ListLevel, 9) * 720L);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "drawing", DocxConstants.WordprocessingML);

        if (!floating)
        {
            writer.WriteStartElement("wp", "inline", DocxConstants.DrawingWordprocessingNamespace);
            WriteImageDistances(writer);
            WriteImageExtent(writer, width, height);
            WriteImageDocPr(writer, image, drawingId, context);
            WriteImageGraphic(writer, fallbackRelationshipId ?? relationshipId, drawingId, width, height, image.Crop, isSvg ? relationshipId : null);
            writer.WriteEndElement();
        }
        else
        {
            writer.WriteStartElement("wp", "anchor", DocxConstants.DrawingWordprocessingNamespace);
            WriteImageDistances(writer);
            writer.WriteAttributeString("simplePos", "0");
            writer.WriteAttributeString("relativeHeight", "251658240");
            writer.WriteAttributeString("behindDoc", image.Wrap == ImageWrap.Behind ? "1" : "0");
            writer.WriteAttributeString("locked", "0");
            writer.WriteAttributeString("layoutInCell", "1");
            writer.WriteAttributeString("allowOverlap", "1");

            writer.WriteStartElement("wp", "simplePos", DocxConstants.DrawingWordprocessingNamespace);
            writer.WriteAttributeString("x", "0");
            writer.WriteAttributeString("y", "0");
            writer.WriteEndElement();

            WriteImagePositions(writer, FloatPlacement.Of(image));
            WriteImageExtent(writer, width, height);

            writer.WriteStartElement("wp", "effectExtent", DocxConstants.DrawingWordprocessingNamespace);
            foreach (var side in new[] { "l", "t", "r", "b" })
            {
                writer.WriteAttributeString(side, "0");
            }

            writer.WriteEndElement();

            WriteImageWrap(writer, image.Wrap);

            WriteImageDocPr(writer, image, drawingId, context);
            WriteImageGraphic(writer, fallbackRelationshipId ?? relationshipId, drawingId, width, height, image.Crop, isSvg ? relationshipId : null);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        if (!inLine)
        {
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// writes the wrap mode of an anchored image (OF-B06). Word names them differently from the
    /// model: through wraps tightly, behind and in front need no wrap at all and differ in
    /// <c>behindDoc</c>. Without a contour Word wraps through the bounding box.
    /// </summary>
    private static void WriteImageWrap(XmlWriter writer, ImageWrap wrap)
    {
        switch (wrap)
        {
            case ImageWrap.Square:
                writer.WriteStartElement("wp", "wrapSquare", DocxConstants.DrawingWordprocessingNamespace);
                writer.WriteAttributeString("wrapText", "bothSides");
                writer.WriteEndElement();
                break;
            case ImageWrap.TopAndBottom:
                writer.WriteStartElement("wp", "wrapTopAndBottom", DocxConstants.DrawingWordprocessingNamespace);
                writer.WriteEndElement();
                break;
            case ImageWrap.Through:
            {
                writer.WriteStartElement("wp", "wrapThrough", DocxConstants.DrawingWordprocessingNamespace);
                writer.WriteAttributeString("wrapText", "bothSides");
                writer.WriteStartElement("wp", "wrapPolygon", DocxConstants.DrawingWordprocessingNamespace);
                writer.WriteAttributeString("edited", "0");
                foreach (var (x, y) in new[] { (0, 0), (21600, 0), (21600, 21600), (0, 21600), (0, 0) })
                {
                    writer.WriteStartElement(x == 0 && y == 0 ? "wp" : "wp", x == 0 && y == 0 ? "start" : "lineTo", DocxConstants.DrawingWordprocessingNamespace);
                    writer.WriteAttributeString("x", x.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("y", y.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
                break;
            }

            default:
                // Behind and InFront do not wrap; behindDoc decides the layer.
                writer.WriteStartElement("wp", "wrapNone", DocxConstants.DrawingWordprocessingNamespace);
                writer.WriteEndElement();
                break;
        }
    }

    private static void WriteImageDistances(XmlWriter writer)
    {
        foreach (var distance in new[] { "distT", "distB", "distL", "distR" })
        {
            writer.WriteAttributeString(distance, "0");
        }
    }

    private static void WriteImageExtent(XmlWriter writer, Length width, Length height)
    {
        writer.WriteStartElement("wp", "extent", DocxConstants.DrawingWordprocessingNamespace);
        writer.WriteAttributeString("cx", Emu(width).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("cy", Emu(height).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
    }

    private static void WriteImagePositions(XmlWriter writer, FloatPlacement placement)
    {
        WriteImagePosition(
            writer,
            "positionH",
            placement.HorizontalRelativeTo switch
            {
                HorizontalAnchor.Page => "page",
                HorizontalAnchor.Margin => "margin",
                HorizontalAnchor.Character => "character",
                _ => "column",
            },
            placement.HorizontalAlignment switch
            {
                HorizontalFloatAlignment.Left => "left",
                HorizontalFloatAlignment.Center => "center",
                HorizontalFloatAlignment.Right => "right",
                _ => null,
            },
            placement.OffsetX ?? Length.Zero);
        WriteImagePosition(
            writer,
            "positionV",
            placement.VerticalRelativeTo switch
            {
                VerticalAnchor.Page => "page",
                VerticalAnchor.Margin => "margin",
                VerticalAnchor.Line => "line",
                _ => "paragraph",
            },
            placement.VerticalAlignment switch
            {
                VerticalFloatAlignment.Top => "top",
                VerticalFloatAlignment.Center => "center",
                VerticalFloatAlignment.Bottom => "bottom",
                _ => null,
            },
            placement.OffsetY ?? Length.Zero);
    }

    /// <summary>A <c>wp:positionH</c>/<c>wp:positionV</c> with an alignment (<c>wp:align</c>) or an offset (<c>wp:posOffset</c>).</summary>
    private static void WriteImagePosition(XmlWriter writer, string element, string relativeFrom, string? align, Length offset)
    {
        writer.WriteStartElement("wp", element, DocxConstants.DrawingWordprocessingNamespace);
        writer.WriteAttributeString("relativeFrom", relativeFrom);
        if (align is not null)
        {
            writer.WriteElementString("wp", "align", DocxConstants.DrawingWordprocessingNamespace, align);
        }
        else
        {
            writer.WriteStartElement("wp", "posOffset", DocxConstants.DrawingWordprocessingNamespace);
            writer.WriteString(Emu(offset).ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteImageDocPr(XmlWriter writer, ImageBlock image, int drawingId, DocxWriteContext context)
    {
        writer.WriteStartElement("wp", "docPr", DocxConstants.DrawingWordprocessingNamespace);
        writer.WriteAttributeString("id", drawingId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", $"Image {drawingId}");
        if (!string.IsNullOrEmpty(image.AltText))
        {
            writer.WriteAttributeString("descr", XmlText.Clean(image.AltText, context.XmlBehavior));
        }

        writer.WriteEndElement();
    }

    private static void WriteImageGraphic(
        XmlWriter writer,
        string relationshipId,
        int drawingId,
        Length width,
        Length height,
        ImageCrop? crop,
        string? svgRelationshipId = null)
    {
        writer.WriteStartElement("a", "graphic", DocxConstants.DrawingMainNamespace);
        writer.WriteStartElement("a", "graphicData", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("uri", DocxConstants.DrawingPictureNamespace);

        writer.WriteStartElement("pic", "pic", DocxConstants.DrawingPictureNamespace);

        writer.WriteStartElement("pic", "nvPicPr", DocxConstants.DrawingPictureNamespace);
        writer.WriteStartElement("pic", "cNvPr", DocxConstants.DrawingPictureNamespace);
        writer.WriteAttributeString("id", drawingId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", $"Image {drawingId}");
        writer.WriteEndElement();
        writer.WriteStartElement("pic", "cNvPicPr", DocxConstants.DrawingPictureNamespace);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("pic", "blipFill", DocxConstants.DrawingPictureNamespace);
        writer.WriteStartElement("a", "blip", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("r", "embed", DocxConstants.Relationships, relationshipId);
        if (svgRelationshipId is not null)
        {
            // The SVG part is referenced a second time through the SVG
            // extension; that is how Word 2016 and newer find the vector data.
            writer.WriteStartElement("a", "extLst", DocxConstants.DrawingMainNamespace);
            writer.WriteStartElement("a", "ext", DocxConstants.DrawingMainNamespace);
            writer.WriteAttributeString("uri", DocxConstants.SvgDrawingExtensionUri);
            writer.WriteStartElement("asvg", "svgBlip", DocxConstants.SvgDrawingNamespace);
            writer.WriteAttributeString("r", "embed", DocxConstants.Relationships, svgRelationshipId);
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        if (crop is { } sourceRect && !sourceRect.IsEmpty)
        {
            writer.WriteStartElement("a", "srcRect", DocxConstants.DrawingMainNamespace);
            writer.WriteAttributeString("l", CropValue(sourceRect.Left));
            writer.WriteAttributeString("t", CropValue(sourceRect.Top));
            writer.WriteAttributeString("r", CropValue(sourceRect.Right));
            writer.WriteAttributeString("b", CropValue(sourceRect.Bottom));
            writer.WriteEndElement();
        }

        writer.WriteStartElement("a", "stretch", DocxConstants.DrawingMainNamespace);
        writer.WriteStartElement("a", "fillRect", DocxConstants.DrawingMainNamespace);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("pic", "spPr", DocxConstants.DrawingPictureNamespace);
        writer.WriteStartElement("a", "xfrm", DocxConstants.DrawingMainNamespace);
        writer.WriteStartElement("a", "off", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("x", "0");
        writer.WriteAttributeString("y", "0");
        writer.WriteEndElement();
        writer.WriteStartElement("a", "ext", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("cx", Emu(width).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("cy", Emu(height).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("a", "prstGeom", DocxConstants.DrawingMainNamespace);
        writer.WriteAttributeString("prst", "rect");
        writer.WriteStartElement("a", "avLst", DocxConstants.DrawingMainNamespace);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static long Emu(Length length) => DocxUnits.Emu(length.Points);

    /// <summary>Crop fraction in 1/1000 percent (Office Open XML srcRect).</summary>
    private static string CropValue(double fraction)
        => ((long)Math.Round(fraction * 100000.0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static void WriteStyles(XmlWriter writer, DocumentDefaults defaults, string? language, InvalidXmlCharacterBehavior xmlBehavior, IReadOnlyList<TableStyle> tableStyles, IReadOnlyList<ParagraphStyle> namedStyles, IReadOnlyList<CharacterStyle> characterStyles)
    {
        writer.WriteStartElement("w", "styles", DocxConstants.WordprocessingML);

        WriteDocDefaults(writer, defaults, language, xmlBehavior);

        // A style of the model replaces the built-in one of the same id: an id can exist only once.
        var ownIds = namedStyles.Select(style => style.StyleName ?? string.Empty)
            .Concat(characterStyles.Select(style => style.Name))
            .ToHashSet(StringComparer.Ordinal);

        if (!ownIds.Contains("Normal"))
        {
            writer.WriteStartElement("w", "style", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "type", "paragraph");
            WriteWAttribute(writer, "default", "1");
            WriteWAttribute(writer, "styleId", "Normal");
            WriteStyleName(writer, "Normal");
            writer.WriteEndElement();
        }

        for (var level = 1; level <= 6; level++)
        {
            if (!ownIds.Contains($"Heading{level}"))
            {
                WriteHeadingStyle(writer, level);
            }
        }

        WriteNoteStyles(writer, ownIds);

        foreach (var tableStyle in tableStyles)
        {
            WriteTableStyle(writer, tableStyle);
        }

        foreach (var characterStyle in characterStyles)
        {
            WriteCharacterStyle(writer, characterStyle, xmlBehavior);
        }

        foreach (var namedStyle in namedStyles)
        {
            if (string.IsNullOrEmpty(namedStyle.StyleName))
            {
                continue;
            }

            writer.WriteStartElement("w", "style", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "type", "paragraph");
            if (namedStyle.StyleName == "Normal")
            {
                WriteWAttribute(writer, "default", "1");
            }

            WriteWAttribute(writer, "styleId", namedStyle.StyleName);

            // Word recognizes a heading style by this name, which carries its outline level.
            WriteStyleName(writer, namedStyle.StyleName is { Length: 8 } id && id.StartsWith("Heading", StringComparison.Ordinal) && id[^1] is >= '1' and <= '6'
                ? "heading " + id[^1]
                : namedStyle.StyleName);
            WriteStyleLink(writer, namedStyle.LinkedStyleName);
            WriteParagraphProperties(writer, namedStyle with { StyleName = null });
            var format = RunFormat.Of(namedStyle);
            if (!format.IsEmpty)
            {
                writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
                WriteRunFormat(writer, format, xmlBehavior);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>writes a named character style (a reusable set of run formatting).</summary>
    private static void WriteCharacterStyle(
        XmlWriter writer, CharacterStyle style, InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("w", "style", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", "character");
        WriteWAttribute(writer, "styleId", style.Name);
        WriteStyleName(writer, style.Name);
        WriteStyleLink(writer, style.LinkedStyleName);

        var format = RunFormat.Of(style);
        if (!format.IsEmpty)
        {
            writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
            WriteRunFormat(writer, format, xmlBehavior);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteTableStyle(XmlWriter writer, TableStyle style)
    {
        writer.WriteStartElement("w", "style", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", "table");
        WriteWAttribute(writer, "styleId", style.Name);
        WriteStyleName(writer, style.Name);

        writer.WriteStartElement("w", "tblPr", DocxConstants.WordprocessingML);
        WriteTableBorders(writer, style.Border);
        WriteTableCellMargins(writer, "tblCellMar", (style.CellPadding ?? Typography.TableCellPaddingHorizontal, style.CellPadding ?? Typography.TableCellPaddingVertical));
        writer.WriteEndElement();

        // The conditional formatting of the areas: the background, the borders and the text.
        WriteTableStyleConditional(writer, "firstRow", style.HeaderBackground, style.HeaderRow);
        WriteTableStyleConditional(writer, "band1Horz", style.BandBackground, style.BandRow);
        WriteTableStyleConditional(writer, "lastRow", style.LastRowBackground, style.LastRow);
        WriteTableStyleConditional(writer, "firstCol", style.FirstColumnBackground, style.FirstColumn);
        WriteTableStyleConditional(writer, "lastCol", style.LastColumnBackground, style.LastColumn);

        writer.WriteEndElement();
    }

    private static void WriteTableStyleConditional(
        XmlWriter writer,
        string type,
        Color? background,
        TableStyleArea? area = null)
    {
        if (background is null && area is null)
        {
            return;
        }

        writer.WriteStartElement("w", "tblStylePr", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", type);

        // The order follows the schema (CT_TblStylePr): w:rPr before w:tcPr.
        if (area is { } areaStyle
            && (areaStyle.Bold is not null || areaStyle.Italic is not null || areaStyle.TextColor is not null))
        {
            writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
            if (areaStyle.Bold == true)
            {
                WriteEmptyElement(writer, "b");
            }

            if (areaStyle.Italic == true)
            {
                WriteEmptyElement(writer, "i");
            }

            if (areaStyle.TextColor is { } textColor)
            {
                WriteValElement(writer, "color", HexColor(textColor));
            }

            writer.WriteEndElement();
        }

        if (background is { } fill || area?.Border is not null)
        {
            writer.WriteStartElement("w", "tcPr", DocxConstants.WordprocessingML);

            if (area?.Border is { } border)
            {
                writer.WriteStartElement("w", "tcBorders", DocxConstants.WordprocessingML);
                WriteCellBorder(writer, "top", border);
                WriteCellBorder(writer, "left", border);
                WriteCellBorder(writer, "bottom", border);
                WriteCellBorder(writer, "right", border);
                writer.WriteEndElement();
            }

            if (background is { } fillColor)
            {
                writer.WriteStartElement("w", "shd", DocxConstants.WordprocessingML);
                WriteWAttribute(writer, "val", "clear");
                WriteWAttribute(writer, "color", "auto");
                WriteWAttribute(writer, "fill", HexColor(fillColor));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteNoteStyles(XmlWriter writer, IReadOnlySet<string> ownIds)
    {
        foreach (var id in new[] { "FootnoteReference", "EndnoteReference" }.Where(id => !ownIds.Contains(id)))
        {
            WriteNoteCharacterStyle(writer, id);
        }

        foreach (var id in new[] { "FootnoteText", "EndnoteText" }.Where(id => !ownIds.Contains(id)))
        {
            WriteNoteParagraphStyle(writer, id);
        }
    }

    private static void WriteNoteCharacterStyle(XmlWriter writer, string styleId)
    {
        writer.WriteStartElement("w", "style", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", "character");
        WriteWAttribute(writer, "styleId", styleId);
        WriteStyleName(writer, styleId);
        writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
        WriteValElement(writer, "vertAlign", "superscript");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteNoteParagraphStyle(XmlWriter writer, string styleId)
    {
        writer.WriteStartElement("w", "style", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", "paragraph");
        WriteWAttribute(writer, "styleId", styleId);
        WriteStyleName(writer, styleId);
        writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "spacing", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "after", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteDocDefaults(XmlWriter writer, DocumentDefaults defaults, string? language, InvalidXmlCharacterBehavior xmlBehavior)
    {
        var size = HalfPoints(defaults.FontSize).ToString(CultureInfo.InvariantCulture);

        writer.WriteStartElement("w", "docDefaults", DocxConstants.WordprocessingML);

        writer.WriteStartElement("w", "rPrDefault", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
        WriteFonts(writer, XmlText.Clean(FontFamilies.Resolve(defaults.FontFamily).Name, xmlBehavior));
        if (defaults.TextColor != Color.Black)
        {
            WriteValElement(writer, "color", HexColor(defaults.TextColor));
        }

        WriteValElement(writer, "sz", size);
        WriteValElement(writer, "szCs", size);
        if (!string.IsNullOrEmpty(language))
        {
            // Q03: language markup at the document level (screen readers, hyphenation).
            writer.WriteStartElement("w", "lang", DocxConstants.WordprocessingML);
            WriteWAttribute(writer, "val", language);
            WriteWAttribute(writer, "eastAsia", language);
            WriteWAttribute(writer, "bidi", language);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("w", "pPrDefault", DocxConstants.WordprocessingML);
        var paragraphDefaults = defaults.ParagraphStyle with { StyleName = null };
        if (paragraphDefaults.SpaceAfter is null)
        {
            paragraphDefaults = paragraphDefaults with { SpaceAfter = Typography.ParagraphSpaceAfter };
        }

        if (paragraphDefaults.LineSpacing is null)
        {
            paragraphDefaults = paragraphDefaults with { LineSpacing = LineSpacing.Multiple(1) };
        }

        WriteParagraphProperties(writer, paragraphDefaults);
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void WriteHeadingStyle(XmlWriter writer, int level)
    {
        var size = HalfPoints(Typography.HeadingSize((HeadingLevel)level)).ToString(CultureInfo.InvariantCulture);

        writer.WriteStartElement("w", "style", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "type", "paragraph");
        WriteWAttribute(writer, "styleId", $"Heading{level}");
        WriteStyleName(writer, $"heading {level}");

        writer.WriteStartElement("w", "basedOn", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", "Normal");
        writer.WriteEndElement();

        writer.WriteStartElement("w", "next", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", "Normal");
        writer.WriteEndElement();

        writer.WriteStartElement("w", "pPr", DocxConstants.WordprocessingML);
        writer.WriteStartElement("w", "keepNext", DocxConstants.WordprocessingML);
        writer.WriteEndElement();
        writer.WriteStartElement("w", "spacing", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "before", Twips(Typography.HeadingSpaceBefore));
        WriteWAttribute(writer, "after", Twips(Typography.HeadingSpaceAfter));
        writer.WriteEndElement();
        writer.WriteStartElement("w", "outlineLvl", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", (level - 1).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("w", "rPr", DocxConstants.WordprocessingML);
        writer.WriteElementString("w", "b", DocxConstants.WordprocessingML, string.Empty);
        WriteValElement(writer, "sz", size);
        WriteValElement(writer, "szCs", size);
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void WriteFonts(XmlWriter writer, string fontFamily)
    {
        writer.WriteStartElement("w", "rFonts", DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "ascii", fontFamily);
        WriteWAttribute(writer, "hAnsi", fontFamily);
        WriteWAttribute(writer, "cs", fontFamily);
        writer.WriteEndElement();
    }

    private static void WriteStyleName(XmlWriter writer, string name)
    {
        WriteValElement(writer, "name", name);
    }

    private static void WriteStyleLink(XmlWriter writer, string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            WriteValElement(writer, "link", name);
        }
    }

    private static void WriteValElement(XmlWriter writer, string localName, string value)
    {
        writer.WriteStartElement("w", localName, DocxConstants.WordprocessingML);
        WriteWAttribute(writer, "val", value);
        writer.WriteEndElement();
    }

    internal static void WriteCoreProperties(XmlWriter writer, DocumentMetadata metadata, InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("cp", "coreProperties", DocxConstants.CorePropertiesNamespace);

        WriteElementIfPresent(writer, "dc", "title", DocxConstants.DublinCoreNamespace, metadata.Title, xmlBehavior);
        WriteElementIfPresent(writer, "dc", "creator", DocxConstants.DublinCoreNamespace, metadata.Author, xmlBehavior);
        WriteElementIfPresent(writer, "dc", "subject", DocxConstants.DublinCoreNamespace, metadata.Subject, xmlBehavior);

        if (metadata.Keywords is { Count: > 0 })
        {
            writer.WriteElementString(
                "cp", "keywords", DocxConstants.CorePropertiesNamespace,
                XmlText.Clean(string.Join(", ", metadata.Keywords), xmlBehavior));
        }

        WriteElementIfPresent(writer, "dc", "language", DocxConstants.DublinCoreNamespace, metadata.Language, xmlBehavior);

        if (metadata.Created is { } created)
        {
            var value = created
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            WriteTimestamp(writer, "created", value);
            WriteTimestamp(writer, "modified", value);
        }

        writer.WriteEndElement();
    }

    private static void WriteTimestamp(XmlWriter writer, string localName, string value)
    {
        writer.WriteStartElement("dcterms", localName, DocxConstants.DublinCoreTermsNamespace);
        writer.WriteAttributeString(
            "xsi", "type", DocxConstants.XmlSchemaInstanceNamespace, "dcterms:W3CDTF");
        writer.WriteString(value);
        writer.WriteEndElement();
    }

    private static void WriteAppProperties(XmlWriter writer)
    {
        writer.WriteStartElement("Properties", DocxConstants.ExtendedPropertiesNamespace);
        writer.WriteElementString("Application", DocxConstants.ExtendedPropertiesNamespace, "Reglet");
        writer.WriteEndElement();
    }

    /// <summary>Writes each custom XML part with its properties part (<c>itemProps</c>) and the relationships.</summary>
    private static void WriteCustomXmlParts(
        OpcPackageWriter package,
        IReadOnlyList<CustomXmlPart> parts,
        ref int nextRelationshipIndex)
    {
        for (var index = 0; index < parts.Count; index++)
        {
            var number = index + 1;
            var item = $"customXml/item{number}.xml";
            var props = $"customXml/itemProps{number}.xml";
            var xml = parts[index].Xml;
            // A stable id from the content keeps the output deterministic.
            var itemId = parts[index].ItemId is { } given
                ? Guid.Parse(given)
                : new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(xml)));

            package.AddBinaryPart(item, System.Text.Encoding.UTF8.GetBytes(xml));
            package.AddOverride(props, DocxConstants.CustomXmlPropertiesContentType);
            package.WriteXmlPart(props, writer =>
            {
                writer.WriteStartElement("ds", "datastoreItem", DocxConstants.CustomXmlPropertiesNamespace);
                writer.WriteAttributeString("ds", "itemID", DocxConstants.CustomXmlPropertiesNamespace, itemId.ToString("B").ToUpperInvariant());
                writer.WriteEndElement();
            });
            package.AddRelationship(
                item,
                new OpcRelationship("rId1", DocxConstants.CustomXmlPropertiesRelationship, $"itemProps{number}.xml"));
            package.AddRelationship(
                DocxConstants.DocumentPart,
                new OpcRelationship($"rId{nextRelationshipIndex++}", DocxConstants.CustomXmlRelationship, "../" + item));
        }
    }

    private static void WriteCustomProperties(
        XmlWriter writer,
        IReadOnlyDictionary<string, string> properties,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("Properties", DocxConstants.CustomPropertiesNamespace);
        writer.WriteAttributeString("xmlns", "vt", null, DocxConstants.VariantTypesNamespace);

        var pid = 2;
        foreach (var (name, value) in properties.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            writer.WriteStartElement("property");
            writer.WriteAttributeString("fmtid", "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}");
            writer.WriteAttributeString("pid", pid.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("name", name);
            writer.WriteElementString("vt", "lpwstr", DocxConstants.VariantTypesNamespace, XmlText.Clean(value, xmlBehavior));
            writer.WriteEndElement();
            pid++;
        }

        writer.WriteEndElement();
    }

    private static void WriteElementIfPresent(
        XmlWriter writer,
        string prefix,
        string localName,
        string namespaceUri,
        string? value,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteElementString(prefix, localName, namespaceUri, XmlText.Clean(value, xmlBehavior));
        }
    }

    private static long Twips(Length length) => DocxUnits.Scaled(length.Points, 20);

    private static long HalfPoints(Length length) => DocxUnits.Scaled(length.Points, 2);

    private static void WriteWAttribute(XmlWriter writer, string localName, long value)
        => writer.WriteAttributeString(
            "w", localName, DocxConstants.WordprocessingML, value.ToString(CultureInfo.InvariantCulture));

    private static void WriteWAttribute(XmlWriter writer, string localName, string value)
        => writer.WriteAttributeString("w", localName, DocxConstants.WordprocessingML, value);
}
