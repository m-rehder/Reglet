using System.Text;

namespace Reglet;

/// <summary>
/// page-level operations on existing PDF files (phase AC): extract a page range or merge
/// several PDFs. The reachable object graph is copied and the page tree rebuilt; inherited
/// page attributes are pushed down. Outlines, forms, names and metadata are not carried over.
/// </summary>
public static class PdfPageOperations
{
    private static readonly string[] InheritableKeys = ["Resources", "MediaBox", "CropBox", "Rotate"];

    /// <summary>Extracts the 1-based page range <paramref name="fromPage"/>..<paramref name="toPage"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The range is invalid.</exception>
    /// <exception cref="DocumentValidationException">The PDF is invalid or the range exceeds the page count.</exception>
    public static void Extract(Stream source, int fromPage, int toPage, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThan(fromPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(toPage, fromPage);
        ReaderGuard.Run("PDF", () =>
        {
            ExtractRange(source, fromPage, toPage, destination);
            return 0;
        });
    }

    private static void ExtractRange(Stream source, int fromPage, int toPage, Stream destination)
    {
        var file = Load(source);
        var all = CollectPages(file);
        if (toPage > all.Count)
        {
            throw new DocumentValidationException($"The PDF has only {all.Count} pages.");
        }

        Write(destination, [(file, all.GetRange(fromPage - 1, toPage - fromPage + 1))], metadataSource: file, outlineSource: file);
    }

    /// <summary>Extracts the given 1-based pages (in the given order).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="source"/>, <paramref name="pages"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="DocumentValidationException">The PDF is invalid or a page is out of range.</exception>
    public static void Extract(Stream source, IReadOnlyList<int> pages, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(destination);
        ReaderGuard.Run("PDF", () =>
        {
            ExtractPages(source, pages, destination);
            return 0;
        });
    }

    private static void ExtractPages(Stream source, IReadOnlyList<int> pages, Stream destination)
    {
        var file = Load(source);
        var all = CollectPages(file);
        var selected = new List<(int Page, PdfDictionary Inherited)>(pages.Count);
        foreach (var page in pages)
        {
            if (page < 1 || page > all.Count)
            {
                throw new DocumentValidationException($"Page {page} is outside 1..{all.Count}.");
            }

            selected.Add(all[page - 1]);
        }

        Write(destination, [(file, selected)], metadataSource: file, outlineSource: file);
    }

    /// <summary>Merges the pages of all sources into one PDF, in the given order.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> or <paramref name="destination"/> is null.</exception>
    /// <exception cref="DocumentValidationException">A source PDF is invalid.</exception>
    public static void Merge(IEnumerable<Stream> sources, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);
        ReaderGuard.Run("PDF", () =>
        {
            MergeCore(sources, destination);
            return 0;
        });
    }

    private static void MergeCore(IEnumerable<Stream> sources, Stream destination)
    {
        var documents = new List<(PdfFile File, IReadOnlyList<(int Page, PdfDictionary Inherited)> Pages)>();
        foreach (var source in sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            var file = Load(source);
            documents.Add((file, CollectPages(file)));
        }

        Write(destination, documents, metadataSource: documents.Count > 0 ? documents[0].File : null,
            mergeOutlineSources: documents.Select(document => document.File).ToList());
    }

    private static PdfFile Load(Stream source)
    {
        var data = ReaderGuard.ReadAllBounded(source, "PDF");
        var file = PdfFile.Load(data);
        if (file.IsEncrypted)
        {
            throw new UnsupportedFeatureException(
                "Page operations do not support encrypted PDF files; decrypt the file with PdfRewriter first.");
        }

        return file;
    }

    /// <summary>Collects the page objects with their inherited attributes, top to bottom.</summary>
    internal static List<(int Page, PdfDictionary Inherited)> CollectPages(PdfFile file)
    {
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || catalog.Get("Pages") is not PdfReference pagesReference)
        {
            throw new DocumentValidationException("PDF catalog or page tree is missing.");
        }

        var pages = new List<(int, PdfDictionary)>();
        Walk(file, pagesReference, new Dictionary<string, PdfValue>(StringComparer.Ordinal), pages, new HashSet<int>());
        return pages;
    }

    private static void Walk(
        PdfFile file,
        PdfReference nodeReference,
        IReadOnlyDictionary<string, PdfValue> inherited,
        List<(int Page, PdfDictionary Inherited)> pages,
        HashSet<int> visited)
    {
        ReaderGuard.EnsureStack();
        if (!visited.Add(nodeReference.ObjectNumber) || file.Dictionary(nodeReference) is not { } node)
        {
            return;
        }

        var merged = new Dictionary<string, PdfValue>(inherited, StringComparer.Ordinal);
        foreach (var key in InheritableKeys)
        {
            if (node.Get(key) is { } value)
            {
                merged[key] = value;
            }
        }

        if (file.Array(node.Get("Kids")) is { } kids)
        {
            foreach (var kid in kids.Items)
            {
                if (kid is PdfReference child)
                {
                    Walk(file, child, merged, pages, visited);
                }
            }
        }
        else
        {
            pages.Add((nodeReference.ObjectNumber, new PdfDictionary(merged)));
        }
    }

    private static void Write(
        Stream destination,
        IReadOnlyList<(PdfFile File, IReadOnlyList<(int Page, PdfDictionary Inherited)> Pages)> documents,
        PdfFile? metadataSource = null,
        PdfFile? outlineSource = null,
        IReadOnlyList<PdfFile>? mergeOutlineSources = null)
    {
        // Buffered so that a failure leaves nothing in the caller's stream.
        using var buffer = new MemoryStream();
        var writer = new PdfFileWriter(buffer);
        var pagesObject = writer.ReserveObject();
        var catalogObject = writer.ReserveObject();
        var map = new Dictionary<(PdfFile, int), int>();
        var queue = new List<(PdfFile File, int NewNumber, PdfValue Value)>();
        var pageNumbers = new HashSet<int>();
        var newPages = new List<int>();
        var outlinePageMap = new Dictionary<int, int>();
        var mergedPageMap = new Dictionary<(PdfFile File, int Page), int>();

        const int Dropped = -1;

        // Every page of a source, also one without /Type: only the selected ones are copied.
        var sourcePages = documents.SelectMany(document => CollectPages(document.File).Select(page => (document.File, page.Page))).ToHashSet();
        var copiedAnnotations = new HashSet<(PdfFile File, int Number)>();

        int CopyReference(PdfFile file, int oldNumber)
        {
            if (map.TryGetValue((file, oldNumber), out var existing))
            {
                return existing;
            }

            if (sourcePages.Contains((file, oldNumber)))
            {
                map[(file, oldNumber)] = Dropped;
                return Dropped;
            }

            var value = file.GetObject(oldNumber);

            // References to pages that are not copied (e.g. a link or an annotation's /P to
            // another page) and to page tree nodes are dropped; following them would pull the
            // complete source document into the result.
            if (value is PdfDictionary { } dictionary && file.Name(dictionary.Get("Type")) is "Page" or "Pages")
            {
                map[(file, oldNumber)] = Dropped;
                return Dropped;
            }

            var newNumber = writer.ReserveObject();
            map[(file, oldNumber)] = newNumber;
            queue.Add((file, newNumber, value));
            return newNumber;
        }

        PdfValue Remap(PdfFile file, PdfValue value)
        {
            ReaderGuard.EnsureStack();
            return RemapCore(file, value);
        }

        PdfValue RemapReference(PdfFile file, PdfReference reference)
            => CopyReference(file, reference.ObjectNumber) is var number and not Dropped
                ? new PdfReference(number)
                : PdfValue.Null;

        PdfValue RemapCore(PdfFile file, PdfValue value) => value switch
        {
            PdfReference reference => RemapReference(file, reference),
            PdfArray array => new PdfArray(array.Items.Select(item => Remap(file, item)).ToList()),
            PdfDictionary dictionary => new PdfDictionary(dictionary.Entries
                .Where(pair => !LeavesCopiedAnnotations(file, dictionary, pair))
                .ToDictionary(pair => pair.Key, pair => Remap(file, pair.Value), StringComparer.Ordinal)),
            PdfStreamValue stream => new PdfStreamValue((PdfDictionary)Remap(file, stream.Dictionary), stream.Raw),
            _ => value,
        };

        // A form field or comment thread links annotations across pages and would carry their data along.
        bool LeavesCopiedAnnotations(PdfFile file, PdfDictionary dictionary, KeyValuePair<string, PdfValue> pair)
            => pair.Key is "Parent" or "Popup" or "IRT"
                && pair.Value is PdfReference target
                && dictionary.Get("Rect") is not null
                && dictionary.Get("Subtype") is not null
                && !copiedAnnotations.Contains((file, target.ObjectNumber));

        foreach (var (file, pages) in documents)
        {
            foreach (var (page, inherited) in pages)
            {
                if (file.Array(file.Dictionary(file.GetObject(page))?.Get("Annots")) is { } annotations)
                {
                    foreach (var annotation in annotations.Items.OfType<PdfReference>())
                    {
                        copiedAnnotations.Add((file, annotation.ObjectNumber));
                    }
                }

                var newNumber = writer.ReserveObject();
                pageNumbers.Add(newNumber);
                newPages.Add(newNumber);

                // References to a copied page (annotation /P, link destinations) point to its copy.
                map.TryAdd((file, page), newNumber);
                queue.Add((file, newNumber, BuildPage(file, page, inherited)));
                mergedPageMap[(file, page)] = newNumber;
                if (outlineSource is not null && ReferenceEquals(file, outlineSource))
                {
                    outlinePageMap[page] = newNumber;
                }
            }
        }

        // AF17: carry the metadata (Info dictionary and catalog XMP stream) of the reference source.
        int? infoObject = null;
        int? metadataObject = null;
        PdfValue? outputIntents = null;
        if (metadataSource is not null)
        {
            if (metadataSource.Trailer.Get("Info") is PdfReference infoReference)
            {
                infoObject = CopyReference(metadataSource, infoReference.ObjectNumber);
            }

            if (metadataSource.Dictionary(metadataSource.Trailer.Get("Root")) is { } sourceCatalog
                && sourceCatalog.Get("Metadata") is PdfReference metadataReference)
            {
                // A PDF/A claim survives only when every page comes from the same PDF/A part; the
                // output intent goes with it. Otherwise the metadata would promise too much.
                var part = PdfAIdentity.Part(metadataSource);
                if (part is null || documents.All(document => PdfAIdentity.Part(document.File) == part))
                {
                    metadataObject = CopyReference(metadataSource, metadataReference.ObjectNumber);
                    if (part is not null && sourceCatalog.Get("OutputIntents") is { } intents)
                    {
                        outputIntents = Remap(metadataSource, intents);
                    }
                }
            }
        }

        // AF17: carry the reference source's embedded-file name tree (filespecs + streams).
        List<(string Name, PdfValue Filespec)>? embeddedFiles = null;
        int? embeddedNamesObject = null;
        PdfValue? associatedFiles = null;
        if (outlineSource is { } embeddedSource)
        {
            embeddedFiles = CollectEmbeddedFiles(embeddedSource);
            if (embeddedFiles.Count > 0)
            {
                // PDF/A-3 (6.8) links every embedded file through the catalog's /AF array.
                if (embeddedSource.Dictionary(embeddedSource.Trailer.Get("Root"))?.Get("AF") is { } af)
                {
                    associatedFiles = Remap(embeddedSource, af);
                }

                embeddedNamesObject = writer.ReserveObject();
                for (var index = 0; index < embeddedFiles.Count; index++)
                {
                    embeddedFiles[index] = (embeddedFiles[index].Name, Remap(embeddedSource, embeddedFiles[index].Filespec));
                }
            }
        }

        for (var index = 0; index < queue.Count; index++)
        {
            var (file, newNumber, value) = queue[index];
            var remapped = Remap(file, value);
            if (remapped is PdfDictionary page && pageNumbers.Contains(newNumber))
            {
                var entries = new Dictionary<string, PdfValue>(page.Entries, StringComparer.Ordinal)
                {
                    ["Parent"] = new PdfReference(pagesObject),
                };
                remapped = new PdfDictionary(entries);
            }

            if (remapped is PdfStreamValue stream)
            {
                writer.WriteStreamObject(newNumber, stream.Raw, SerializeDictionary(stream.Dictionary));
            }
            else
            {
                writer.WriteObject(newNumber, tokens => WriteValue(tokens, remapped));
            }
        }

        writer.WriteObject(pagesObject, tokens => tokens.WriteDictionary(dictionary =>
        {
            dictionary.WriteName("Type");
            dictionary.WriteName("Pages");
            dictionary.WriteName("Count");
            dictionary.WriteInteger(newPages.Count);
            dictionary.WriteName("Kids");
            dictionary.WriteArray(array =>
            {
                foreach (var page in newPages)
                {
                    array.WriteReference(page);
                }
            });
        }));

        var outlineObject = outlineSource is not null
            ? WriteOutline(
                writer,
                FilterOutline(
                    ReadOutlineTree(outlineSource),
                    page => outlinePageMap.TryGetValue(page, out var newPage) ? newPage : null))
            : WriteMergedOutline(writer, mergeOutlineSources, mergedPageMap);
        var destNamesObject = outlineSource is null
            ? null
            : WriteNamedDestinations(writer, CollectNamedDestinations(outlineSource), outlinePageMap);

        if (embeddedFiles is { Count: > 0 } && embeddedNamesObject is { } embeddedObject)
        {
            writer.WriteObject(embeddedObject, tokens => tokens.WriteDictionary(dictionary =>
            {
                dictionary.WriteName("Names");
                dictionary.WriteArray(array =>
                {
                    foreach (var (name, filespec) in embeddedFiles)
                    {
                        array.WriteLiteralString(name);
                        WriteValue(array, filespec);
                    }
                });
            }));
        }

        writer.WriteObject(catalogObject, tokens => tokens.WriteDictionary(dictionary =>
        {
            dictionary.WriteName("Type");
            dictionary.WriteName("Catalog");
            dictionary.WriteName("Pages");
            dictionary.WriteReference(pagesObject);

            if (metadataObject is { } metadata)
            {
                dictionary.WriteName("Metadata");
                dictionary.WriteReference(metadata);
            }

            if (outputIntents is not null)
            {
                dictionary.WriteName("OutputIntents");
                WriteValue(dictionary, outputIntents);
            }

            if (associatedFiles is not null)
            {
                dictionary.WriteName("AF");
                WriteValue(dictionary, associatedFiles);
            }

            if (outlineObject is { } outline)
            {
                dictionary.WriteName("Outlines");
                dictionary.WriteReference(outline);
            }

            if (destNamesObject is not null || embeddedNamesObject is not null)
            {
                dictionary.WriteName("Names");
                dictionary.WriteDictionary(entries =>
                {
                    if (destNamesObject is { } dests)
                    {
                        entries.WriteName("Dests");
                        entries.WriteReference(dests);
                    }

                    if (embeddedNamesObject is { } embeddedNames)
                    {
                        entries.WriteName("EmbeddedFiles");
                        entries.WriteReference(embeddedNames);
                    }
                });
            }
        }));

        var (idOriginal, idChanging) = metadataSource is null ? (null, null) : PdfAIdentity.FileId(metadataSource);
        writer.Complete(catalogObject, infoObject, idOriginal, idChanging);
        buffer.Position = 0;
        buffer.CopyTo(destination);
    }

    private sealed record OutlineNode(string Title, int PageObject, double Y, IReadOnlyList<OutlineNode> Children);

    private sealed class OutlineEntry(int number, OutlineNode node, int parent)
    {
        public int Number { get; } = number;

        public OutlineNode Node { get; } = node;

        public int Parent { get; } = parent;

        public int? Prev { get; set; }

        public int? Next { get; set; }

        public int? First { get; set; }

        public int? Last { get; set; }

        public int Count { get; set; }
    }

    private static int? WriteOutline(PdfFileWriter writer, IReadOnlyList<OutlineNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return null;
        }

        var root = writer.ReserveObject();
        var entries = new Dictionary<int, OutlineEntry>();
        var (first, last, count) = ReserveOutline(nodes, root, writer, entries);

        foreach (var entry in entries.Values)
        {
            writer.WriteObject(entry.Number, tokens => tokens.WriteDictionary(dictionary =>
            {
                dictionary.WriteName("Title");
                dictionary.WriteLiteralString(entry.Node.Title);
                dictionary.WriteName("Parent");
                dictionary.WriteReference(entry.Parent);
                if (entry.Prev is { } previous)
                {
                    dictionary.WriteName("Prev");
                    dictionary.WriteReference(previous);
                }

                if (entry.Next is { } next)
                {
                    dictionary.WriteName("Next");
                    dictionary.WriteReference(next);
                }

                dictionary.WriteName("Dest");
                dictionary.WriteArray(destination =>
                {
                    destination.WriteReference(entry.Node.PageObject);
                    destination.WriteName("XYZ");
                    destination.WriteNumber(0);
                    destination.WriteNumber(entry.Node.Y);
                    destination.WriteNumber(0);
                });

                if (entry.First is { } childFirst && entry.Last is { } childLast)
                {
                    dictionary.WriteName("First");
                    dictionary.WriteReference(childFirst);
                    dictionary.WriteName("Last");
                    dictionary.WriteReference(childLast);
                    dictionary.WriteName("Count");
                    dictionary.WriteInteger(entry.Count);
                }
            }));
        }

        writer.WriteObject(root, tokens => tokens.WriteDictionary(dictionary =>
        {
            dictionary.WriteName("Type");
            dictionary.WriteName("Outlines");
            dictionary.WriteName("First");
            dictionary.WriteReference(first!.Value);
            dictionary.WriteName("Last");
            dictionary.WriteReference(last!.Value);
            dictionary.WriteName("Count");
            dictionary.WriteInteger(count);
        }));

        return root;
    }

    private static (int? First, int? Last, int Count) ReserveOutline(
        IReadOnlyList<OutlineNode> nodes,
        int parent,
        PdfFileWriter writer,
        Dictionary<int, OutlineEntry> entries)
    {
        ReaderGuard.EnsureStack();
        int? first = null;
        int? last = null;
        int? previous = null;
        var count = 0;
        foreach (var node in nodes)
        {
            var number = writer.ReserveObject();
            var entry = new OutlineEntry(number, node, parent) { Prev = previous };
            entries[number] = entry;
            if (previous is { } previousNumber)
            {
                entries[previousNumber].Next = number;
            }

            first ??= number;
            last = number;
            count++;

            if (node.Children.Count > 0)
            {
                var child = ReserveOutline(node.Children, number, writer, entries);
                entry.First = child.First;
                entry.Last = child.Last;
                entry.Count = child.Count;
                count += child.Count;
            }

            previous = number;
        }

        return (first, last, count);
    }

    private static IReadOnlyList<OutlineNode> FilterOutline(
        IReadOnlyList<OutlineNode> nodes,
        Func<int, int?> pageMap)
    {
        ReaderGuard.EnsureStack();
        var result = new List<OutlineNode>();
        foreach (var node in nodes)
        {
            var children = FilterOutline(node.Children, pageMap);
            if (node.PageObject >= 0 && pageMap(node.PageObject) is { } page)
            {
                result.Add(node with { PageObject = page, Children = children });
            }
            else
            {
                result.AddRange(children);
            }
        }

        return result;
    }

    private static IReadOnlyList<OutlineNode> ReadOutlineTree(PdfFile file)
    {
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || catalog.Get("Outlines") is not PdfReference outlineReference
            || file.Dictionary(outlineReference) is not { } outline)
        {
            return [];
        }

        return ReadOutlineSiblings(file, outline.Get("First"), new HashSet<int>());
    }

    private static List<OutlineNode> ReadOutlineSiblings(
        PdfFile file,
        PdfValue? value,
        HashSet<int> visited)
    {
        ReaderGuard.EnsureStack();
        var result = new List<OutlineNode>();
        var guard = 0;
        while (value is PdfReference reference && visited.Add(reference.ObjectNumber) && guard++ < 100_000)
        {
            if (file.Dictionary(reference) is not { } node)
            {
                break;
            }

            var destination = OutlineDestination(file, node);
            result.Add(new OutlineNode(
                OutlineTitle(node),
                destination?.PageObject ?? -1,
                destination?.Y ?? 0,
                ReadOutlineSiblings(file, node.Get("First"), visited)));
            value = node.Get("Next");
        }

        return result;
    }

    private static int? WriteMergedOutline(
        PdfFileWriter writer,
        IReadOnlyList<PdfFile>? sources,
        IReadOnlyDictionary<(PdfFile File, int Page), int> pageMap)
    {
        if (sources is null)
        {
            return null;
        }

        var nodes = new List<OutlineNode>();
        foreach (var source in sources)
        {
            nodes.AddRange(FilterOutline(
                ReadOutlineTree(source),
                page => pageMap.TryGetValue((source, page), out var newPage) ? newPage : null));
        }

        return WriteOutline(writer, nodes);
    }

    private static (int PageObject, double Y)? OutlineDestination(PdfFile file, PdfDictionary node)
    {
        var destination = node.Get("Dest");
        if (destination is null
            && file.Dictionary(node.Get("A")) is { } action
            && file.Name(action.Get("S")) == "GoTo")
        {
            destination = action.Get("D");
        }

        return PageDestination(file, destination);
    }

    private static (int PageObject, double Y)? PageDestination(PdfFile file, PdfValue? value)
    {
        if (file.Array(value) is not { Items.Count: > 0 } array
            || array.Items[0] is not PdfReference page)
        {
            return null;
        }

        var y = array.Items.Count > 3 ? file.Number(array.Items[3]) ?? 0 : 0;
        return (page.ObjectNumber, y);
    }

    private static List<(string Name, int PageObject, double Y)> CollectNamedDestinations(PdfFile file)
    {
        var items = new List<(string, int, double)>();
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || file.Dictionary(catalog.Get("Names")) is not { } names)
        {
            return items;
        }

        CollectNameTree(file, names.Get("Dests"), items, new HashSet<int>());
        return items;
    }

    private static void CollectNameTree(
        PdfFile file,
        PdfValue? value,
        List<(string Name, int PageObject, double Y)> items,
        HashSet<int> visited)
    {
        ReaderGuard.EnsureStack();
        if (file.Dictionary(value) is not { } tree)
        {
            return;
        }

        if (file.Array(tree.Get("Names")) is { } array)
        {
            for (var index = 0; index + 1 < array.Items.Count; index += 2)
            {
                if (array.Items[index] is PdfString name
                    && PageDestination(file, array.Items[index + 1]) is { } destination)
                {
                    items.Add((DecodeString(name.Bytes), destination.PageObject, destination.Y));
                }
            }
        }

        if (file.Array(tree.Get("Kids")) is { } kids)
        {
            foreach (var kid in kids.Items)
            {
                if (kid is PdfReference reference && visited.Add(reference.ObjectNumber))
                {
                    CollectNameTree(file, reference, items, visited);
                }
            }
        }
    }

    private static List<(string Name, PdfValue Filespec)> CollectEmbeddedFiles(PdfFile file)
    {
        var items = new List<(string, PdfValue)>();
        if (file.Dictionary(file.Trailer.Get("Root")) is not { } catalog
            || file.Dictionary(catalog.Get("Names")) is not { } names)
        {
            return items;
        }

        CollectFilespecTree(file, names.Get("EmbeddedFiles"), items, new HashSet<int>());
        return items;
    }

    private static void CollectFilespecTree(
        PdfFile file,
        PdfValue? value,
        List<(string Name, PdfValue Filespec)> items,
        HashSet<int> visited)
    {
        ReaderGuard.EnsureStack();
        if (file.Dictionary(value) is not { } tree)
        {
            return;
        }

        if (file.Array(tree.Get("Names")) is { } array)
        {
            for (var index = 0; index + 1 < array.Items.Count; index += 2)
            {
                if (array.Items[index] is PdfString name)
                {
                    items.Add((DecodeString(name.Bytes), array.Items[index + 1]));
                }
            }
        }

        if (file.Array(tree.Get("Kids")) is { } kids)
        {
            foreach (var kid in kids.Items)
            {
                if (kid is PdfReference reference && visited.Add(reference.ObjectNumber))
                {
                    CollectFilespecTree(file, reference, items, visited);
                }
            }
        }
    }

    private static int? WriteNamedDestinations(
        PdfFileWriter writer,
        IReadOnlyList<(string Name, int PageObject, double Y)> items,
        IReadOnlyDictionary<int, int> pageMap)
    {
        var kept = items.Where(item => pageMap.ContainsKey(item.PageObject)).ToList();
        if (kept.Count == 0)
        {
            return null;
        }

        var objectNumber = writer.ReserveObject();
        writer.WriteObject(objectNumber, tokens => tokens.WriteDictionary(dictionary =>
        {
            dictionary.WriteName("Names");
            dictionary.WriteArray(array =>
            {
                foreach (var (name, page, y) in kept)
                {
                    array.WriteLiteralString(name);
                    array.WriteArray(destination =>
                    {
                        destination.WriteReference(pageMap[page]);
                        destination.WriteName("XYZ");
                        destination.WriteNumber(0);
                        destination.WriteNumber(y);
                        destination.WriteNumber(0);
                    });
                }
            });
        }));

        return objectNumber;
    }

    private static string DecodeString(byte[] bytes) => PdfTextString.Decode(bytes);

    private static string OutlineTitle(PdfDictionary node)
        => node.Get("Title") is PdfString text ? DecodeString(text.Bytes) : string.Empty;

    private static PdfDictionary BuildPage(PdfFile file, int pageNumber, PdfDictionary inherited)
    {
        if (file.Dictionary(file.GetObject(pageNumber)) is not { } page)
        {
            throw new DocumentValidationException($"PDF page {pageNumber} is not a dictionary.");
        }

        var entries = new Dictionary<string, PdfValue>(page.Entries, StringComparer.Ordinal);
        foreach (var key in InheritableKeys)
        {
            if (!entries.ContainsKey(key) && inherited.Get(key) is { } value)
            {
                entries[key] = value;
            }
        }

        entries["Type"] = new PdfName("Page");
        entries.Remove("Parent");
        return new PdfDictionary(entries);
    }

    internal static string SerializeDictionary(PdfDictionary dictionary)
    {
        using var buffer = new MemoryStream();
        var tokens = new PdfTokenWriter(buffer);
        foreach (var (key, value) in dictionary.Entries)
        {
            if (key == "Length")
            {
                continue;
            }

            tokens.WriteParsedName(key);
            WriteValue(tokens, value);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    internal static void WriteValue(PdfTokenWriter writer, PdfValue value)
    {
        switch (value)
        {
            case PdfNull:
                writer.WriteNull();
                break;
            case PdfBoolean boolean:
                writer.WriteBoolean(boolean.Value);
                break;
            case PdfInteger integer:
                writer.WriteInteger(integer.Value);
                break;
            case PdfReal real:
                writer.WriteExactNumber(real.Value);
                break;
            case PdfString text:
                writer.WriteLiteralString(text.Bytes);
                break;
            case PdfName name:
                writer.WriteParsedName(name.Name);
                break;
            case PdfReference reference:
                writer.WriteReference(reference.ObjectNumber);
                break;
            case PdfArray array:
                writer.WriteArray(inner =>
                {
                    foreach (var item in array.Items)
                    {
                        WriteValue(inner, item);
                    }
                });
                break;
            case PdfDictionary dictionary:
                writer.WriteDictionary(inner =>
                {
                    foreach (var (key, item) in dictionary.Entries)
                    {
                        inner.WriteParsedName(key);
                        WriteValue(inner, item);
                    }
                });
                break;
            case PdfStreamValue stream:
                WriteValue(writer, stream.Dictionary);
                break;
        }
    }
}
