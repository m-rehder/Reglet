namespace Reglet;

/// <summary>
/// builds the tagged PDF structure tree (phase Q, Q01/Q02): <c>/StructTreeRoot</c> with a
/// <c>/Document</c> element and paragraphs, headings, tables and figures. The
/// marked-content sections of the content refer via <c>/MCID</c> and the parent tree to
/// the structure elements.
/// </summary>
/// <remarks>
/// containers (table, row, cell) carry child elements, content elements (paragraph,
/// heading, figure) carry an MCR reference to their marked-content sequence.
/// </remarks>
internal sealed class PdfStructureTree
{
    private sealed class Element
    {
        public required int Object { get; init; }

        public required string Role { get; init; }

        public string? Alt { get; init; }

        public required int Parent { get; init; }

        public required int Page { get; init; }

        public List<int> Children { get; } = [];

        public (int Page, int Mcid)? Content { get; set; }
    }

    private readonly PdfFileWriter _file;
    private readonly int _rootObject;
    private readonly Element _document;
    private readonly Dictionary<int, Element> _elements = [];
    private readonly Stack<Element> _stack = [];
    private readonly Dictionary<int, List<int>> _parentTree = [];
    private int _nextKey;

    public PdfStructureTree(PdfFileWriter file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        _rootObject = file.ReserveObject();
        var documentObject = file.ReserveObject();
        _document = new Element { Object = documentObject, Role = "Document", Parent = _rootObject, Page = 0 };
        _elements[documentObject] = _document;
        _stack.Push(_document);
    }

    public int RootObject => _rootObject;

    /// <summary>Starts a new page and returns its StructParents key.</summary>
    public int BeginPage()
    {
        var key = _nextKey++;
        _parentTree[key] = [];
        return key;
    }

    /// <summary>
    /// starts a content element (paragraph, heading, figure). Returns the MCID for the
    /// marked-content sequence.
    /// </summary>
    public int BeginContent(string role, string? alt, int pageObject, int structParents)
    {
        var parent = _stack.Peek();
        var element = new Element
        {
            Object = _file.ReserveObject(),
            Role = role,
            Alt = alt,
            Parent = parent.Object,
            Page = pageObject,
        };
        _elements[element.Object] = element;
        parent.Children.Add(element.Object);

        var mcid = _parentTree[structParents].Count;
        _parentTree[structParents].Add(element.Object);
        element.Content = (pageObject, mcid);
        _stack.Push(element);
        return mcid;
    }

    /// <summary>Starts a container element (table, row, cell).</summary>
    public void BeginContainer(string role, int pageObject)
    {
        var parent = _stack.Peek();
        var element = new Element
        {
            Object = _file.ReserveObject(),
            Role = role,
            Parent = parent.Object,
            Page = pageObject,
        };
        _elements[element.Object] = element;
        parent.Children.Add(element.Object);
        _stack.Push(element);
    }

    /// <summary>Ends the most recently started element.</summary>
    public void End()
    {
        if (_stack.Count > 1)
        {
            _stack.Pop();
        }
    }

    /// <summary>Writes structure elements, parent tree and root.</summary>
    public void Write()
    {
        foreach (var element in _elements.Values)
        {
            _file.WriteObject(element.Object, tokens => WriteElement(tokens, element));
        }

        var parentTreeObject = _file.ReserveObject();
        _file.WriteObject(parentTreeObject, tokens => WriteParentTree(tokens));

        _file.WriteObject(_rootObject, tokens => tokens.WriteDictionary(root =>
        {
            root.WriteName("Type");
            root.WriteName("StructTreeRoot");
            root.WriteName("K");
            root.WriteArray(kids => kids.WriteReference(_document.Object));
            root.WriteName("ParentTree");
            root.WriteReference(parentTreeObject);
            root.WriteName("ParentTreeNextKey");
            root.WriteInteger(_nextKey);
        }));
    }

    private static void WriteElement(PdfTokenWriter tokens, Element element)
    {
        tokens.WriteDictionary(dict =>
        {
            dict.WriteName("Type");
            dict.WriteName("StructElem");
            dict.WriteName("S");
            dict.WriteName(element.Role);
            dict.WriteName("P");
            dict.WriteReference(element.Parent);
            if (element.Page > 0)
            {
                dict.WriteName("Pg");
                dict.WriteReference(element.Page);
            }

            if (!string.IsNullOrEmpty(element.Alt))
            {
                dict.WriteName("Alt");
                dict.WriteLiteralString(element.Alt);
            }

            dict.WriteName("K");
            if (element.Content is { } content)
            {
                dict.WriteDictionary(k =>
                {
                    k.WriteName("Type");
                    k.WriteName("MCR");
                    k.WriteName("Pg");
                    k.WriteReference(content.Page);
                    k.WriteName("MCID");
                    k.WriteInteger(content.Mcid);
                });
            }
            else
            {
                dict.WriteArray(kids =>
                {
                    foreach (var child in element.Children)
                    {
                        kids.WriteReference(child);
                    }
                });
            }
        });
    }

    private void WriteParentTree(PdfTokenWriter tokens)
    {
        tokens.WriteDictionary(dict =>
        {
            dict.WriteName("Nums");
            dict.WriteArray(nums =>
            {
                foreach (var (key, elements) in _parentTree)
                {
                    nums.WriteInteger(key);
                    nums.WriteArray(entry =>
                    {
                        foreach (var element in elements)
                        {
                            entry.WriteReference(element);
                        }
                    });
                }
            });
        });
    }
}
