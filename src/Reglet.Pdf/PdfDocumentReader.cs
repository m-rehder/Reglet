using System.Globalization;
using System.Text;

namespace Reglet;

/// <summary>
/// extracts text from a PDF (subset) into the content model. Supports classic
/// xref tables, xref streams and object streams (PDF 1.5+) as well as filtered
/// Content-Streams (FlateDecode, LZWDecode, ASCII85Decode, ASCIIHexDecode,
/// RunLengthDecode including /DecodeParms predictor); text is decoded via the ToUnicode CMap
/// (Type0/Identity-H) or WinAnsi (simple fonts). Position and layout are
/// grouped into paragraphs, but not exactly preserved.
/// </summary>
public sealed partial class PdfDocumentReader : IDocumentReader
{
    private const int MaxPageTreeDepth = 256;

    /// <inheritdoc />
    public ReadResult Read(Stream input, CancellationToken cancellationToken = default)
        => Read(input, PdfReadOptions.Default, cancellationToken);

    /// <summary>
    /// Extracts text into the content model, opening an encrypted PDF with the password from
    /// <paramref name="options"/> (phase AD).
    /// </summary>
    public ReadResult Read(Stream input, PdfReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        return ReaderGuard.Run("PDF", () => ReadCore(input, options.Password, options.RecoverStructure, options.RecoverStructure && options.RecoverFormulas, cancellationToken));
    }

    /// <summary>Extracts the text of each page of a PDF (phase AC, AC05).</summary>
    /// <returns>The text of the pages in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public IReadOnlyList<string> ReadPageTexts(Stream input, CancellationToken cancellationToken = default)
        => ReadPageTexts(input, PdfReadOptions.Default, cancellationToken);

    /// <summary>Extracts the text of each page, opening an encrypted PDF with the given options.</summary>
    public IReadOnlyList<string> ReadPageTexts(Stream input, PdfReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        return ReaderGuard.Run("PDF", () => ReadPageTextsCore(input, options.Password, cancellationToken));
    }

    /// <summary>
    /// reads the non-link annotations of a PDF (OF-F01): sticky notes, highlights, stamps and free
    /// text boxes. A link annotation and an unknown subtype are ignored.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="DocumentValidationException">The PDF is invalid or encrypted with a wrong password.</exception>
    public IReadOnlyList<PdfAnnotation> ReadAnnotations(Stream input, CancellationToken cancellationToken = default)
        => ReadAnnotations(input, PdfReadOptions.Default, cancellationToken);

    /// <summary>Reads the non-link annotations, opening an encrypted PDF with the given options.</summary>
    public IReadOnlyList<PdfAnnotation> ReadAnnotations(Stream input, PdfReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        return ReaderGuard.Run("PDF", () => ReadAnnotationsCore(input, options.Password, cancellationToken));
    }

    private static IReadOnlyList<PdfAnnotation> ReadAnnotationsCore(Stream input, string? password, CancellationToken cancellationToken)
    {
        var data = ReaderGuard.ReadAllBounded(input, "PDF");
        var file = PdfFile.Load(data, password);
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || file.Dictionary(catalog.Get("Pages")) is not { } pagesRoot)
        {
            throw new DocumentValidationException("PDF catalog or page tree is missing.");
        }

        var pages = new List<PageEntry>();
        CollectPages(file, pagesRoot, new PageEntry(pagesRoot, null, null), pages, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), depth: 0);

        var result = new List<PdfAnnotation>();
        for (var index = 0; index < pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Array(pages[index].Page.Get("Annots")) is not { } annots)
            {
                continue;
            }

            foreach (var item in annots.Items)
            {
                if (file.Dictionary(item) is not { } annot || ReadAnnotation(file, annot, index + 1) is not { } annotation)
                {
                    continue;
                }

                result.Add(annotation);
            }
        }

        return result;
    }

    /// <summary>Reads one annotation dictionary; null for a link or an unknown subtype.</summary>
    private static PdfAnnotation? ReadAnnotation(PdfFile file, PdfDictionary annot, int page)
    {
        var kind = file.Name(annot.Get("Subtype")) switch
        {
            "Text" => PdfAnnotationKind.Text,
            "Highlight" => PdfAnnotationKind.Highlight,
            "Stamp" => PdfAnnotationKind.Stamp,
            "FreeText" => PdfAnnotationKind.FreeText,
            _ => (PdfAnnotationKind?)null,
        };
        if (kind is not { } annotationKind || file.Array(annot.Get("Rect")) is not { Items.Count: 4 } rect)
        {
            return null;
        }

        var numbers = rect.Items.Select(file.Number).ToList();
        if (numbers.Any(number => number is null))
        {
            return null;
        }

        var x1 = numbers[0]!.Value;
        var y1 = numbers[1]!.Value;
        var x2 = numbers[2]!.Value;
        var y2 = numbers[3]!.Value;

        return new PdfAnnotation
        {
            Kind = annotationKind,
            Page = page,
            X = Math.Min(x1, x2),
            Y = Math.Min(y1, y2),
            Width = Math.Max(Math.Abs(x2 - x1), 0.01),
            Height = Math.Max(Math.Abs(y2 - y1), 0.01),
            Content = ReadAnnotationText(file, annot.Get("Contents")),
            Author = ReadAnnotationText(file, annot.Get("T")),
            Subject = ReadAnnotationText(file, annot.Get("Subj")),
            Color = ReadAnnotationColor(file, annot.Get("C")),
            Icon = file.Name(annot.Get("Name")),
        };
    }

    /// <summary>Reads a text entry of an annotation (a PDF text string).</summary>
    private static string? ReadAnnotationText(PdfFile file, PdfValue? value)
        => file.Resolve(value) switch
        {
            PdfString text => PdfTextString.Decode(text.Bytes),
            _ => null,
        };

    /// <summary>Reads the colour array of an annotation (three components in 0–1).</summary>
    private static Color? ReadAnnotationColor(PdfFile file, PdfValue? value)
    {
        if (file.Array(value) is not { Items.Count: 3 } color)
        {
            return null;
        }

        var components = color.Items.Select(file.Number).ToList();
        if (components.Any(component => component is null))
        {
            return null;
        }

        static byte Channel(double? value) => (byte)Math.Clamp(Math.Round(value!.Value * 255), 0, 255);
        return Color.FromRgb(Channel(components[0]), Channel(components[1]), Channel(components[2]));
    }

    private static IReadOnlyList<string> ReadPageTextsCore(Stream input, string? password, CancellationToken cancellationToken)
    {
        var data = ReaderGuard.ReadAllBounded(input, "PDF");
        var file = PdfFile.Load(data, password);
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || file.Dictionary(catalog.Get("Pages")) is not { } pagesRoot)
        {
            throw new DocumentValidationException("PDF catalog or page tree is missing.");
        }

        var pages = new List<PageEntry>();
        CollectPages(file, pagesRoot, new PageEntry(pagesRoot, null, null), pages, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), depth: 0);

        var context = new ExtractionContext(file);
        var texts = new List<string>(pages.Count);
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            texts.Add(BuildPageText(context.Extract(page, out _)));
        }

        return texts;
    }

    private static string BuildPageText(List<PdfTextSegment> segments)
    {
        if (segments.Count == 0)
        {
            return string.Empty;
        }

        // Group the segments into lines by their baseline y (tolerance from the font size),
        // top to bottom and left to right within a line (phase AC, AC05).
        var lines = new List<List<PdfTextSegment>>();
        foreach (var segment in segments.OrderByDescending(item => item.Y))
        {
            var tolerance = Math.Max(1, segment.Size * 0.3);
            var line = lines.Count > 0 ? lines[^1] : null;
            if (line is not null && Math.Abs(line[0].Y - segment.Y) <= tolerance)
            {
                line.Add(segment);
            }
            else
            {
                lines.Add([segment]);
            }
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            var text = new StringBuilder();
            PdfTextSegment? previous = null;
            foreach (var segment in line.OrderBy(segment => segment.X))
            {
                if (previous is { } last && NeedsSpace(last, segment))
                {
                    text.Append(' ');
                }

                text.Append(segment.Text);
                previous = segment;
            }

            var lineText = text.ToString();
            builder.Append(IsReversibleRightToLeft(lineText) ? ReverseTextElements(lineText) : lineText);
        }

        return builder.ToString();
    }

    /// <summary>
    /// whether a line is strong right-to-left throughout, so its visual glyph order can be
    /// reversed into logical order. Latin letters, numbers and combining marks keep the line in
    /// visual order instead of corrupting them.
    /// </summary>
    private static bool IsReversibleRightToLeft(string text)
    {
        var strongRightToLeft = false;
        foreach (var rune in text.EnumerateRunes())
        {
            switch (BidiData.GetBidiClass(rune.Value))
            {
                case BidiClass.L:
                    return false;
                case BidiClass.R:
                case BidiClass.AL:
                    strongRightToLeft = true;
                    break;
            }
        }

        return strongRightToLeft;
    }

    /// <summary>
    /// Reverses text by grapheme clusters, keeping surrogate pairs intact. Numeric tokens are
    /// reversed back to their logical left-to-right order afterwards (a visual right-to-left line
    /// stores "123" as "321" after the reversal).
    /// </summary>
    private static string ReverseTextElements(string text)
    {
        var elements = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            elements.Add((string)enumerator.Current);
        }

        elements.Reverse();
        var reversed = string.Concat(elements);
        var builder = new StringBuilder(reversed.Length);
        for (var index = 0; index < reversed.Length;)
        {
            if (!IsNumberCharacter(reversed[index]))
            {
                builder.Append(reversed[index++]);
                continue;
            }

            var start = index;
            while (index < reversed.Length && IsNumberCharacter(reversed[index]))
            {
                index++;
            }

            for (var numberIndex = index - 1; numberIndex >= start; numberIndex--)
            {
                builder.Append(reversed[numberIndex]);
            }
        }

        return builder.ToString();

        static bool IsNumberCharacter(char value)
            => char.IsDigit(value) || value is '.' or ',' or '-' or '+' or '%';
    }

    private static ReadResult ReadCore(Stream input, string? password, bool recover, bool formulas, CancellationToken cancellationToken)
    {
        var data = ReaderGuard.ReadAllBounded(input, "PDF");
        var file = PdfFile.Load(data, password);
        var warnings = new List<ReadWarning>();
        var metadata = ReadMetadata(file);
        var numbering = new List<NumberingDefinition>();
        var sections = ReadPages(file, warnings, recover, formulas, numbering, cancellationToken);
        if (sections.Count == 0)
        {
            sections.Add(new Section(Array.Empty<Block>()));
        }

        return new ReadResult(new PortableDocument(metadata, sections) { Numbering = numbering }, warnings);
    }

    private static DocumentMetadata ReadMetadata(PdfFile file)
    {
        if (file.Dictionary(file.Trailer.Get("Info")) is not { } info)
        {
            return new DocumentMetadata();
        }

        string[]? keywords = null;
        var keywordText = PdfText(file, info.Get("Keywords"));
        if (!string.IsNullOrWhiteSpace(keywordText))
        {
            keywords = keywordText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        Dictionary<string, string>? customProperties = null;
        foreach (var (name, value) in info.Entries)
        {
            if (StandardInfoKeys.Contains(name) || PdfText(file, value) is not { } text)
            {
                continue;
            }

            customProperties ??= new Dictionary<string, string>(StringComparer.Ordinal);
            customProperties[name] = text;
        }

        return new DocumentMetadata
        {
            Title = PdfText(file, info.Get("Title")),
            Author = PdfText(file, info.Get("Author")),
            Subject = PdfText(file, info.Get("Subject")),
            Keywords = keywords,
            Created = ParsePdfDate(PdfText(file, info.Get("CreationDate"))),
            CustomProperties = customProperties,
        };
    }

    /// <summary>The standard keys of the PDF Info dictionary; other keys are custom properties.</summary>
    private static readonly HashSet<string> StandardInfoKeys = new(StringComparer.Ordinal)
    {
        "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped",
    };

    private static List<Section> ReadPages(PdfFile file, List<ReadWarning> warnings, bool recover, bool formulas, List<NumberingDefinition> numbering, CancellationToken cancellationToken)
    {
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || file.Dictionary(catalog.Get("Pages")) is not { } pagesRoot)
        {
            throw new DocumentValidationException("PDF catalog or page tree is missing.");
        }

        var pages = new List<PageEntry>();
        CollectPages(file, pagesRoot, new PageEntry(pagesRoot, null, null), pages, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance), depth: 0);

        var context = new ExtractionContext(file);
        var sections = new List<Section>();
        var pageLines = new List<List<Line>>();
        var pageSizes = new List<PageSize>();
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imageStreams = new List<PdfStreamValue>();
            bool unmapped;
            var segments = recover
                ? context.ExtractWithImages(page, out unmapped, out imageStreams)
                : context.Extract(page, out unmapped);
            if (unmapped && !warnings.Any(warning => warning.Message.Contains("ToUnicode", StringComparison.Ordinal)))
            {
                warnings.Add(new ReadWarning("PDF: characters without a ToUnicode mapping were replaced with U+FFFD."));
            }

            var mediaBox = ReadMediaBox(file, page.MediaBox);
            if (mediaBox is null && page.MediaBox is not null
                && !warnings.Any(warning => warning.Message.Contains("MediaBox", StringComparison.Ordinal)))
            {
                warnings.Add(new ReadWarning($"PDF: a page has an invalid /MediaBox (empty or larger than {MaxPageExtent} units); A4 is used."));
            }

            var size = mediaBox ?? PageSize.A4;
            if (recover && segments.Where(segment => !segment.Rule && !segment.Path).All(segment => segment.Image >= 0) && segments.Any(segment => !segment.Rule && !segment.Path)
                && !warnings.Any(warning => warning.Message.Contains("OCR", StringComparison.Ordinal)))
            {
                warnings.Add(new ReadWarning("PDF: a page consists of images only (a scan?); there is no OCR, so no text is recovered."));
            }

            if (recover)
            {
                if (segments.Any(segment => segment.Rotated)
                    && !warnings.Any(warning => warning.Message.Contains("rotated", StringComparison.Ordinal)))
                {
                    warnings.Add(new ReadWarning("PDF: rotated text (a margin stamp, a sideways label) was left out when recovering the structure."));
                }

                if (formulas)
                {
                    segments = ResolveOperatorShapes(segments);
                }

                var lines = BuildPageLines(segments.Where(segment => segment.Image < 0 && !segment.Rotated && !segment.Rule && !segment.Path).ToList(), size.Width.Points, warnings);
                var decoded = new Dictionary<int, ImageBlock?>();
                foreach (var segment in segments.Where(segment => segment.Image >= 0))
                {
                    if (!decoded.TryGetValue(segment.Image, out var image))
                    {
                        image = PdfImageRecovery.Decode(file, imageStreams[segment.Image], warnings);
                        decoded[segment.Image] = image;
                    }

                    if (image is not null)
                    {
                        lines.Add(new Line(segment.Y, segment.Size, [])
                        {
                            Segments = { segment },
                            Image = new ImageBlock(image.Data, image.MediaType)
                            {
                                Width = Length.FromPoints(Math.Max(1, segment.EndX - segment.X)),
                                Height = Length.FromPoints(Math.Max(1, segment.Size)),
                            },
                        });
                    }
                }

                // Text lines keep their order (a column page is left column, then right column); images go in by height.
                var ordered = lines.Where(line => line.Image is null).ToList();
                foreach (var picture in lines.Where(line => line.Image is not null).OrderByDescending(line => line.Y))
                {
                    var at = ordered.FindIndex(line => line.Image is null && line.Y < picture.Y);
                    var after = ordered.FindLastIndex(line => line.Image is not null);
                    ordered.Insert(at < 0 ? ordered.Count : Math.Max(at, after + 1), picture);
                }

                if (formulas)
                {
                    ordered = RecoverFormulas(
                        ordered,
                        segments.Where(segment => segment.Rule).ToList(),
                        segments.Where(segment => (segment.Path && segment.Text is "" or "|") || segment.Image >= 0).ToList(),
                        warnings);
                }

                pageLines.Add(ordered);
                pageSizes.Add(size);
                sections.Add(new Section(Array.Empty<Block>()) { PageSize = size, Margins = PageMargins.Normal });
                continue;
            }

            sections.Add(new Section(BuildBlocks(segments))
            {
                PageSize = size,
                Margins = PageMargins.Normal,
            });
        }

        if (recover)
        {
            var recovered = new StructureRecovery(pageLines, pageSizes, warnings, numbering, cancellationToken).Run();
            if (pageSizes.Distinct().Count() > 1)
            {
                warnings.Add(new ReadWarning("PDF: the pages have different sizes; the recovered document uses the size of the first page."));
            }

            // One flowing section: the page boundaries of the PDF are not page breaks of the document.
            sections.Clear();
            sections.Add(new Section(recovered)
            {
                PageSize = pageSizes.Count > 0 ? pageSizes[0] : PageSize.A4,
                Margins = PageMargins.Normal,
            });
        }

        return sections;
    }

    /// <summary>A page with its inheritable attributes (ISO 32000-1, 7.7.3.4) resolved.</summary>
    private sealed record PageEntry(PdfDictionary Page, PdfDictionary? Resources, PdfValue? MediaBox);

    private static void CollectPages(
        PdfFile file,
        PdfDictionary node,
        PageEntry inherited,
        List<PageEntry> pages,
        HashSet<PdfDictionary> visited,
        int depth)
    {
        // A page tree with a cycle or extreme depth would otherwise blow the stack
        // (a StackOverflowException would terminate the whole process).
        if (depth > MaxPageTreeDepth)
        {
            throw new DocumentValidationException("The PDF page tree is nested too deeply.");
        }

        if (!visited.Add(node))
        {
            throw new DocumentValidationException("The PDF page tree contains a cycle.");
        }

        var entry = new PageEntry(
            node,
            file.Dictionary(node.Get("Resources")) ?? inherited.Resources,
            node.Get("MediaBox") ?? inherited.MediaBox);

        if (file.Name(node.Get("Type")) == "Page" || node.Get("Kids") is null)
        {
            pages.Add(entry);
            return;
        }

        if (file.Array(node.Get("Kids")) is not { } kids)
        {
            return;
        }

        foreach (var kid in kids.Items)
        {
            if (file.Dictionary(kid) is { } child)
            {
                CollectPages(file, child, entry, pages, visited, depth + 1);
            }
        }
    }

    /// <summary>
    /// Per-document extraction state: font decoders are built once per font dictionary (a font
    /// is usually shared by all pages) and form XObjects are resolved from the resources.
    /// </summary>
    private sealed class ExtractionContext(PdfFile file)
    {
        private readonly Dictionary<PdfDictionary, PdfFontDecoder> _decoders = new(ReferenceEqualityComparer.Instance);

        public List<PdfTextSegment> Extract(PageEntry page, out bool unmapped)
            => PdfTextExtractor.Extract(
                ReadContents(file, page.Page),
                Fonts(page.Resources),
                Forms(page.Resources),
                out unmapped);

        private List<PdfStreamValue>? _images;
        private readonly Dictionary<PdfStreamValue, int> _imageIndex = new(ReferenceEqualityComparer.Instance);

        /// <summary>Looks image XObjects up by name in <paramref name="resources"/> and registers them in the page's image list.</summary>
        private Func<string, int?> ImageLookup(PdfDictionary? resources)
            => name =>
            {
                if (_images is null || resources is null
                    || file.Dictionary(resources.Get("XObject")) is not { } xobjects
                    || file.GetStream(xobjects.Get(name)) is not { } stream
                    || file.Name(stream.Dictionary.Get("Subtype")) != "Image")
                {
                    return null;
                }

                if (!_imageIndex.TryGetValue(stream, out var index))
                {
                    index = _images.Count;
                    _images.Add(stream);
                    _imageIndex[stream] = index;
                }

                return index;
            };

        /// <summary>Text segments and the image XObjects of the page, also those inside form XObjects (segments with Image >= 0 index <paramref name="images"/>).</summary>
        public List<PdfTextSegment> ExtractWithImages(PageEntry page, out bool unmapped, out List<PdfStreamValue> images)
        {
            _images = [];
            _imageIndex.Clear();
            images = _images;
            return PdfTextExtractor.Extract(
                ReadContents(file, page.Page),
                Fonts(page.Resources),
                Forms(page.Resources, withImages: true),
                out unmapped,
                ImageLookup(page.Resources));
        }

        private IReadOnlyDictionary<string, PdfFontDecoder> Fonts(PdfDictionary? resources)
        {
            var fonts = new Dictionary<string, PdfFontDecoder>(StringComparer.Ordinal);
            if (resources is null || file.Dictionary(resources.Get("Font")) is not { } fontDictionary)
            {
                return fonts;
            }

            foreach (var (name, value) in fontDictionary.Entries)
            {
                if (file.Dictionary(value) is { } font)
                {
                    if (!_decoders.TryGetValue(font, out var decoder))
                    {
                        decoder = PdfFontDecoder.Create(file, font);
                        _decoders[font] = decoder;
                    }

                    fonts[name] = decoder;
                }
            }

            return fonts;
        }

        private Func<string, PdfTextExtractor.FormXObject?> Forms(PdfDictionary? resources, bool withImages = false)
            => name =>
            {
                if (resources is null
                    || file.Dictionary(resources.Get("XObject")) is not { } xobjects
                    || file.GetStream(xobjects.Get(name)) is not { } stream
                    || file.Name(stream.Dictionary.Get("Subtype")) != "Form")
                {
                    return null;
                }

                // A form without own resources uses those of the page (PDF 1.1 behavior).
                var formResources = file.Dictionary(stream.Dictionary.Get("Resources")) ?? resources;
                var matrix = file.Array(stream.Dictionary.Get("Matrix"))?.Items
                    .Select(item => file.Number(item) ?? 0)
                    .ToArray() ?? [];
                return new PdfTextExtractor.FormXObject(
                    file.DecodeStream(stream),
                    Fonts(formResources),
                    Forms(formResources, withImages),
                    matrix,
                    withImages ? ImageLookup(formResources) : null);
            };
    }

    private static byte[] ReadContents(PdfFile file, PdfDictionary page)
    {
        var contents = page.Get("Contents");
        using var output = new MemoryStream();

        switch (file.Resolve(contents))
        {
            case PdfStreamValue stream:
                Write(output, file.DecodeStream(stream));
                break;
            case PdfArray array:
                foreach (var item in array.Items)
                {
                    if (file.GetStream(item) is { } itemStream)
                    {
                        Write(output, file.DecodeStream(itemStream));
                        output.WriteByte((byte)'\n');
                    }
                }

                break;
        }

        ReaderGuard.CheckDecodedLength(output.Length, "PDF page content");
        return output.ToArray();
    }

    private static void Write(Stream output, byte[] data) => output.Write(data, 0, data.Length);

    /// <summary>
    /// The largest page side a PDF may have (ISO 32000-1, annex C: 14,400 units). The structure recovery walks a page
    /// in steps of one point, so a side of hundreds of millions from a damaged file kept it busy indefinitely.
    /// </summary>
    private const double MaxPageExtent = 14_400;

    private static PageSize? ReadMediaBox(PdfFile file, PdfValue? mediaBox)
    {
        if (file.Array(mediaBox) is not { } box || box.Items.Count != 4)
        {
            return null;
        }

        var values = new double[4];
        for (var index = 0; index < 4; index++)
        {
            if (file.Number(box.Items[index]) is not { } number)
            {
                return null;
            }

            values[index] = number;
        }

        var width = Math.Abs(values[2] - values[0]);
        var height = Math.Abs(values[3] - values[1]);
        return width is > 0 and <= MaxPageExtent && height is > 0 and <= MaxPageExtent
            ? new PageSize(Length.FromPoints(width), Length.FromPoints(height))
            : null;
    }

    /// <summary>Groups text pieces into lines and lines into paragraphs (heuristic).</summary>
    private static List<Block> BuildBlocks(List<PdfTextSegment> segments)
        => BuildBlocks(BuildLines(segments));

    private static List<Line> BuildLines(List<PdfTextSegment> segments)
    {
        // Pieces on nearly the same height form a line; superscripts and subscripts (a little higher or lower, smaller)
        // belong to it and are put in horizontal order afterwards.
        var groups = new List<(double Y, double Size, List<PdfTextSegment> Members)>();
        foreach (var segment in segments.OrderByDescending(segment => segment.Y).ThenBy(segment => segment.X))
        {
            if (groups.Count > 0 && Math.Abs(groups[^1].Y - segment.Y) <= Math.Max(1, Math.Max(segment.Size, groups[^1].Size) * 0.5))
            {
                var group = groups[^1];
                group.Members.Add(segment);

                // The baseline of a line is the one of its largest piece, not of a superscript.
                groups[^1] = segment.Size > group.Size ? (segment.Y, segment.Size, group.Members) : group;
            }
            else
            {
                groups.Add((segment.Y, segment.Size, [segment]));
            }
        }

        var lines = new List<Line>();
        foreach (var (_, _, members) in groups)
        {
            var ordered = members.OrderBy(member => member.X).ToList(); // stable: ties keep the content order
            var main = ordered.MaxBy(member => member.Size)!;
            var line = new Line(main.Y, main.Size, [new TextRun(ordered[0].Text) { Bold = ordered[0].Bold, Italic = ordered[0].Italic }])
            {
                Last = ordered[0],
                Segments = { ordered[0] },
            };
            foreach (var segment in ordered.Skip(1))
            {
                if (NeedsSpace(line.Last, segment))
                {
                    line.Runs.Add(new TextRun(" "));
                }

                line.Runs.Add(new TextRun(segment.Text) { Bold = segment.Bold, Italic = segment.Italic });
                line.Last = segment;
                line.Segments.Add(segment);
            }

            lines.Add(line);
        }

        foreach (var line in lines)
        {
            var text = string.Concat(line.Runs.Select(run => run.Text));
            if (!IsReversibleRightToLeft(text))
            {
                continue;
            }

            line.Runs.Reverse();
            for (var index = 0; index < line.Runs.Count; index++)
            {
                var run = line.Runs[index];
                line.Runs[index] = RunFormat.Of(run).ToRun(ReverseTextElements(run.Text), run.CharacterStyleName);
            }
        }

        return lines;
    }

    private static List<Block> BuildBlocks(List<Line> lines)
    {
        var blocks = new List<Block>();
        var paragraph = new List<Inline>();
        Line? previous = null;

        foreach (var line in lines)
        {
            if (previous is { } last)
            {
                var delta = last.Y - line.Y;
                var limit = 1.3 * Math.Max(last.Size, line.Size);
                if (Math.Abs(last.Size - line.Size) > 0.75 || delta < 0 || delta > limit)
                {
                    blocks.Add(new Paragraph(paragraph));
                    paragraph = [];
                }
                else
                {
                    var previousText = string.Concat(last.Runs.Select(run => run.Text));
                    var nextText = string.Concat(line.Runs.Select(run => run.Text));
                    if (previousText.EndsWith('-') && nextText.Length > 0 && char.IsLetterOrDigit(nextText[0]))
                    {
                        RemoveTrailingHyphen(paragraph);
                    }
                    else if (!previousText.EndsWith(' ') && !nextText.StartsWith(' '))
                    {
                        paragraph.Add(new TextRun(" "));
                    }
                }
            }

            paragraph.AddRange(line.Runs);
            previous = line;
        }

        if (paragraph.Count > 0)
        {
            blocks.Add(new Paragraph(paragraph));
        }

        return blocks;
    }

    /// <summary>
    /// Removes a trailing hyphen from the last text run of a line. The next line continues the
    /// hyphenated word, so the extracted paragraph joins the two lines without a space.
    /// </summary>
    private static void RemoveTrailingHyphen(List<Inline> inlines)
    {
        for (var index = inlines.Count - 1; index >= 0; index--)
        {
            if (inlines[index] is not TextRun run)
            {
                continue;
            }

            var text = run.Text;
            if (text.Length == 0)
            {
                continue;
            }

            if (!text.EndsWith('-'))
            {
                return;
            }

            var trimmed = text[..^1].TrimEnd(' ');
            if (trimmed.Length == 0)
            {
                inlines.RemoveAt(index);
            }
            else
            {
                inlines[index] = RunFormat.Of(run).ToRun(trimmed, run.CharacterStyleName);
            }

            return;
        }
    }

    /// <summary>
    /// Whether a space separates two pieces of a line: a visible gap between the end of the
    /// previous piece and the start of the next one (or a jump backwards). Spaces already in the
    /// text are not doubled.
    /// </summary>
    private static bool NeedsSpace(PdfTextSegment previous, PdfTextSegment next)
    {
        if (previous.Text.EndsWith(' ') || next.Text.StartsWith(' '))
        {
            return false;
        }

        var gap = next.X - previous.EndX;
        var size = Math.Max(1, Math.Min(previous.Size, next.Size));
        return gap > size * 0.15 || next.X < previous.X - (size * 0.5);
    }

    private static string? PdfText(PdfFile file, PdfValue? value)
        => file.Resolve(value) is PdfString text ? PdfTextString.Decode(text.Bytes) : null;

    private static DateTimeOffset? ParsePdfDate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var text = value.StartsWith("D:", StringComparison.Ordinal) ? value[2..] : value;
        if (text.IndexOf('Z') is var utc and >= 0)
        {
            text = text[..utc];
        }

        // "+01'00'" (PDF) → "+01:00" (.NET); a trailing apostrophe is optional.
        text = text.TrimEnd('\'');
        text = text.Replace('\'', ':');
        string[] formats = ["yyyyMMddHHmmsszzz", "yyyyMMddHHmmzzz", "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMddHH", "yyyyMMdd", "yyyyMM", "yyyy"];

        return DateTimeOffset.TryParseExact(
            text,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var result)
            ? result
            : null;
    }

    private sealed class Line(double y, double size, List<TextRun> runs)
    {
        public double Y { get; } = y;

        public double Size { get; } = size;

        public List<TextRun> Runs { get; } = runs;

        public PdfTextSegment Last { get; set; }

        public List<PdfTextSegment> Segments { get; } = [];

        /// <summary>A block that stands for this line (an image, or a formula under ADR-0034).</summary>
        public Block? Image { get; init; }
    }
}
