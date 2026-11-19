namespace Reglet;

/// <summary>
/// builds the PDF outline (bookmarks panel) from headings and named destinations from
/// <see cref="Bookmark"/> anchors (phase Z, Z03).
/// </summary>
/// <remarks>
/// The outline is hierarchical: a heading of a deeper level becomes a child of the last
/// heading of a shallower level. Named destinations are written as a flat name tree under
/// <c>/Names /Dests</c>.
/// </remarks>
internal sealed class PdfOutline
{
    private sealed class Node
    {
        public required int Object { get; init; }

        public required string Title { get; init; }

        public required int PageObject { get; init; }

        public required double Y { get; init; }

        public int? Parent { get; init; }

        public List<int> Children { get; } = [];
    }

    private readonly PdfFileWriter _file;
    private readonly List<Node> _top = [];
    private readonly Dictionary<int, Node> _nodes = [];
    private readonly Stack<(int Level, int Object)> _path = new();
    private readonly SortedDictionary<string, (int PageObject, double Y)> _named = new(StringComparer.Ordinal);
    private int? _rootObject;

    public PdfOutline(PdfFileWriter file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
    }

    /// <summary>Whether any heading was recorded.</summary>
    public bool HasOutline => _top.Count > 0;

    /// <summary>Whether any bookmark was recorded.</summary>
    public bool HasNamedDestinations => _named.Count > 0;

    /// <summary>The outline root object; only valid when <see cref="HasOutline"/> is true.</summary>
    public int RootObject => _rootObject
        ?? throw new InvalidOperationException("No outline entries were recorded.");

    /// <summary>Records a heading as an outline entry.</summary>
    public void AddHeading(string title, int level, int pageObject, double y)
    {
        _rootObject ??= _file.ReserveObject();

        while (_path.Count > 0 && _path.Peek().Level >= level)
        {
            _path.Pop();
        }

        var node = new Node
        {
            Object = _file.ReserveObject(),
            Title = title,
            PageObject = pageObject,
            Y = y,
            Parent = _path.Count > 0 ? _path.Peek().Object : null,
        };

        _nodes[node.Object] = node;
        if (node.Parent is { } parent)
        {
            _nodes[parent].Children.Add(node.Object);
        }
        else
        {
            _top.Add(node);
        }

        _path.Push((level, node.Object));
    }

    /// <summary>Records a named destination for a bookmark.</summary>
    public void AddBookmark(string name, int pageObject, double y) => _named[name] = (pageObject, y);

    /// <summary>Whether a named destination for the bookmark was already recorded.</summary>
    public bool ContainsBookmark(string name) => _named.ContainsKey(name);

    /// <summary>Writes the outline objects.</summary>
    public void Write()
    {
        if (!HasOutline)
        {
            return;
        }

        foreach (var node in _nodes.Values)
        {
            _file.WriteObject(node.Object, tokens => WriteNode(tokens, node));
        }

        _file.WriteObject(RootObject, tokens => tokens.WriteDictionary(root =>
        {
            root.WriteName("Type");
            root.WriteName("Outlines");
            root.WriteName("First");
            root.WriteReference(_top[0].Object);
            root.WriteName("Last");
            root.WriteReference(_top[^1].Object);
            root.WriteName("Count");
            root.WriteInteger(CountTop());
        }));
    }

    /// <summary>Writes the name tree of the bookmark destinations and returns its object number.</summary>
    public int WriteNames()
    {
        var objectNumber = _file.ReserveObject();
        _file.WriteObject(objectNumber, tokens => tokens.WriteDictionary(names =>
        {
            names.WriteName("Names");
            names.WriteArray(array =>
            {
                // Name tree keys must be sorted in lexical byte order (ISO 32000-1, 7.9.6);
                // viewers look them up by binary search.
                foreach (var (name, destination) in _named.OrderBy(pair => PdfTextString.Encode(pair.Key), ByteOrder.Instance))
                {
                    array.WriteLiteralString(name);
                    WriteDestination(array, destination.PageObject, destination.Y);
                }
            });
        }));
        return objectNumber;
    }

    private void WriteNode(PdfTokenWriter tokens, Node node)
    {
        var siblings = node.Parent is { } parent
            ? _nodes[parent].Children
            : _top.Select(top => top.Object).ToList();
        var index = siblings.IndexOf(node.Object);

        tokens.WriteDictionary(dict =>
        {
            dict.WriteName("Title");
            dict.WriteLiteralString(node.Title);
            dict.WriteName("Parent");
            dict.WriteReference(node.Parent ?? RootObject);
            if (index > 0)
            {
                dict.WriteName("Prev");
                dict.WriteReference(siblings[index - 1]);
            }

            if (index >= 0 && index < siblings.Count - 1)
            {
                dict.WriteName("Next");
                dict.WriteReference(siblings[index + 1]);
            }

            dict.WriteName("Dest");
            WriteDestination(dict, node.PageObject, node.Y);

            if (node.Children.Count > 0)
            {
                dict.WriteName("First");
                dict.WriteReference(node.Children[0]);
                dict.WriteName("Last");
                dict.WriteReference(node.Children[^1]);
                dict.WriteName("Count");
                dict.WriteInteger(CountOpen(node.Children));
            }
        });
    }

    private static void WriteDestination(PdfTokenWriter tokens, int pageObject, double y)
    {
        tokens.WriteArray(destination =>
        {
            destination.WriteReference(pageObject);
            destination.WriteName("XYZ");
            destination.WriteNumber(0);
            destination.WriteNumber(y);
            destination.WriteNumber(0);
        });
    }

    private int CountTop()
    {
        var total = 0;
        foreach (var node in _top)
        {
            total += 1 + CountOpen(node.Children);
        }

        return total;
    }

    private int CountOpen(List<int> nodes)
    {
        var total = 0;
        foreach (var objectNumber in nodes)
        {
            total += 1 + CountOpen(_nodes[objectNumber].Children);
        }

        return total;
    }
}

/// <summary>Compares byte arrays lexically (unsigned bytes, shorter prefix first).</summary>
internal sealed class ByteOrder : IComparer<byte[]>
{
    public static ByteOrder Instance { get; } = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}
