namespace Reglet;

/// <summary>
/// Stage 3 of the PDF check (ADR-0044): every object parses and ends with <c>endobj</c>, every stream's /Length lands on
/// <c>endstream</c> and its data decodes; the page tree has the right types, /Parent links and /Count and no cycle;
/// every page has a valid /MediaBox and /Resources; content streams use known operators, balance q/Q and BT/ET and name
/// only fonts and XObjects their resources hold; fonts have their required entries; annotations have /Subtype and
/// /Rect. shortcut: form XObjects' own content and operand counts are not checked; add them when needed.
/// </summary>
internal static class PdfStructure
{
    private static readonly HashSet<string> Operators =
    [
        "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP", "EI",
        "EMC", "ET", "EX", "f", "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m", "M", "MP", "n",
        "q", "Q", "re", "RG", "rg", "ri", "s", "S", "SC", "sc", "SCN", "scn", "sh", "T*", "Tc", "Td", "TD", "Tf", "Tj",
        "TJ", "TL", "Tm", "Tr", "Ts", "Tw", "Tz", "v", "w", "W", "W*", "y", "'", "\"", "true", "false", "null",
    ];

    private static readonly HashSet<string> StandardFonts =
    [
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic", "Helvetica", "Helvetica-Bold", "Helvetica-Oblique",
        "Helvetica-BoldOblique", "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique", "Symbol", "ZapfDingbats",
    ];

    /// <summary>The deepest page tree that is walked; real files stay far below it, a deeper one would exhaust the stack.</summary>
    private const int MaxTreeDepth = 256;

    /// <summary>The bytes all decoded streams may add up to; beyond it the remaining streams are not decoded.</summary>
    private const long MaxDecodedTotal = 512L * 1024 * 1024;

    /// <summary>The decoding budget of one check, shared by the object and the content checks.</summary>
    private sealed class Budget
    {
        public long Remaining { get; set; } = MaxDecodedTotal;

        public bool Exhausted { get; set; }
    }

    public static void Check(byte[] data, PdfFile file, IReadOnlyDictionary<int, int> offsets, PdfDictionary trailer, IssueList issues, CancellationToken cancellationToken)
    {
        var budget = new Budget();
        foreach (var (number, offset) in offsets.OrderBy(entry => entry.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (number > 0)
            {
                CheckObject(data, file, number, offset, budget, issues);
            }
        }

        if (file.Dictionary(file.Dictionary(trailer.Get("Root"))?.Get("Pages")) is not { } pages)
        {
            issues.Error("catalog", null, "The catalog has no /Pages page tree.");
            return;
        }

        var pageNumber = 0;
        var visited = new HashSet<int>();
        var rootNumber = (file.Dictionary(trailer.Get("Root"))?.Get("Pages") as PdfReference)?.ObjectNumber ?? -1;
        visited.Add(rootNumber);
        Walk(file, pages, rootNumber, Inherited.Empty, visited, 0, ref pageNumber, budget, issues, cancellationToken);
        if (budget.Exhausted)
        {
            issues.Warning("[file]", null, $"The streams decode to more than {MaxDecodedTotal / (1024 * 1024)} MB; the rest were not decoded and checked.");
        }
    }

    /// <summary>Decodes a stream within the budget; null when the budget is spent.</summary>
    private static byte[]? Decode(PdfFile file, PdfStreamValue stream, Budget budget)
    {
        if (budget.Remaining <= 0)
        {
            budget.Exhausted = true;
            return null;
        }

        var decoded = file.DecodeStream(stream);
        budget.Remaining -= decoded.Length;
        return decoded;
    }

    private sealed record Inherited(PdfValue? MediaBox, PdfValue? Resources)
    {
        public static Inherited Empty { get; } = new(null, null);
    }

    /// <summary>Returns the number of pages below the node.</summary>
    private static int Walk(PdfFile file, PdfDictionary node, int nodeNumber, Inherited inherited, HashSet<int> visited, int depth, ref int pageNumber, Budget budget, IssueList issues, CancellationToken cancellationToken)
    {
        ReaderGuard.EnsureStack();
        if (depth > MaxTreeDepth)
        {
            issues.Error(nodeNumber > 0 ? $"object {nodeNumber}" : "page tree", null, $"The page tree is nested deeper than {MaxTreeDepth} levels; it is not walked further.");
            return 0;
        }

        var here = new Inherited(node.Get("MediaBox") ?? inherited.MediaBox, node.Get("Resources") ?? inherited.Resources);
        var part = nodeNumber > 0 ? $"object {nodeNumber}" : "page tree";
        if (file.Name(node.Get("Type")) != "Pages")
        {
            issues.Error(part, null, "A page tree node is not /Type /Pages.");
        }

        var leaves = 0;
        foreach (var kid in file.Array(node.Get("Kids"))?.Items ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kidNumber = (kid as PdfReference)?.ObjectNumber ?? -1;
            if (kidNumber > 0 && !visited.Add(kidNumber))
            {
                issues.Error(part, null, $"The page tree refers to object {kidNumber} twice (a cycle or a shared page).");
                continue;
            }

            if (file.Dictionary(kid) is not { } child)
            {
                issues.Error(part, null, "A /Kids entry is not a dictionary.");
                continue;
            }

            if (nodeNumber > 0 && (child.Get("Parent") as PdfReference)?.ObjectNumber != nodeNumber)
            {
                issues.Error(kidNumber > 0 ? $"object {kidNumber}" : part, null, $"/Parent does not point back to its page tree node {nodeNumber}.");
            }

            switch (file.Name(child.Get("Type")))
            {
                case "Pages":
                    leaves += Walk(file, child, kidNumber, here, visited, depth + 1, ref pageNumber, budget, issues, cancellationToken);
                    break;
                case "Page":
                    leaves++;
                    pageNumber++;
                    CheckPage(file, child, pageNumber, here, budget, issues);
                    break;
                default:
                    issues.Error(kidNumber > 0 ? $"object {kidNumber}" : part, null, "A /Kids entry is neither /Type /Page nor /Type /Pages.");
                    break;
            }
        }

        if (file.Resolve(node.Get("Count")) is not PdfInteger count || count.Value != leaves)
        {
            issues.Error(part, null, $"/Count is {(file.Resolve(node.Get("Count")) as PdfInteger)?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}, but the node holds {leaves} page(s).");
        }

        return leaves;
    }

    private static void CheckPage(PdfFile file, PdfDictionary page, int number, Inherited inherited, Budget budget, IssueList issues)
    {
        var part = $"page {number}";
        var box = file.Array(page.Get("MediaBox") ?? inherited.MediaBox);
        var values = box?.Items.Select(item => Number(file.Resolve(item))).ToList();
        if (values is not { Count: 4 } || values.Any(value => value is null) || values[2] == values[0] || values[3] == values[1])
        {
            issues.Error(part, null, "The page has no valid /MediaBox (four numbers enclosing an area), here or inherited.");
        }

        var resources = file.Dictionary(page.Get("Resources") ?? inherited.Resources);
        if (resources is null)
        {
            issues.Warning(part, null, "The page has no /Resources, here or inherited (most readers assume none).");
        }

        var fonts = file.Dictionary(resources?.Get("Font"));
        foreach (var (name, value) in fonts?.Entries ?? new Dictionary<string, PdfValue>())
        {
            CheckFont(file, name, file.Dictionary(value), part, issues);
        }

        var contents = page.Get("Contents");
        if (contents is not null)
        {
            var streams = file.Resolve(contents) is PdfArray array ? array.Items : [contents];
            var content = new List<byte>();
            foreach (var item in streams)
            {
                if (file.GetStream(item) is not { } stream)
                {
                    issues.Error(part, null, "/Contents is not a stream or an array of streams.");
                    return;
                }

                try
                {
                    if (Decode(file, stream, budget) is not { } decoded)
                    {
                        return;
                    }

                    content.AddRange(decoded);
                    content.Add((byte)'\n');
                }
                catch (Exception exception) when (exception is not IssueList.FullException and not OperationCanceledException)
                {
                    issues.Error(part, null, "A content stream cannot be decoded: " + exception.Message);
                    return;
                }
            }

            CheckContent([.. content], fonts, file.Dictionary(resources?.Get("XObject")), part, issues);
        }

        foreach (var annotation in file.Array(page.Get("Annots"))?.Items ?? [])
        {
            if (file.Dictionary(annotation) is not { } dictionary)
            {
                issues.Error(part, null, "An /Annots entry is not a dictionary.");
                continue;
            }

            if (file.Name(dictionary.Get("Subtype")) is null)
            {
                issues.Error(part, null, "An annotation has no /Subtype.");
            }

            if (file.Array(dictionary.Get("Rect")) is not { Items.Count: 4 } rect || rect.Items.Any(item => Number(file.Resolve(item)) is null))
            {
                issues.Error(part, null, $"An annotation ({file.Name(dictionary.Get("Subtype")) ?? "?"}) has no valid /Rect.");
            }
        }
    }

    private static void CheckFont(PdfFile file, string name, PdfDictionary? font, string part, IssueList issues)
    {
        if (font is null)
        {
            issues.Error(part, null, $"The font resource /{name} is not a dictionary.");
            return;
        }

        var subtype = file.Name(font.Get("Subtype"));
        var baseFont = file.Name(font.Get("BaseFont"));
        switch (subtype)
        {
            case "Type1" or "MMType1" or "TrueType":
                var standard = subtype == "Type1" && baseFont is not null && StandardFonts.Contains(baseFont);
                if (!standard)
                {
                    var first = Number(file.Resolve(font.Get("FirstChar")));
                    var last = Number(file.Resolve(font.Get("LastChar")));
                    var widths = file.Array(font.Get("Widths"));
                    if (first is null || last is null || widths is null)
                    {
                        issues.Error(part, null, $"The font /{name} ({baseFont}) lacks /FirstChar, /LastChar or /Widths.");
                    }
                    else if (widths.Items.Count != (int)(last.Value - first.Value) + 1)
                    {
                        issues.Error(part, null, $"The font /{name} ({baseFont}) has {widths.Items.Count} widths for the codes {first}–{last}.");
                    }

                    if (file.Dictionary(font.Get("FontDescriptor")) is null)
                    {
                        issues.Error(part, null, $"The font /{name} ({baseFont}) has no /FontDescriptor.");
                    }
                }

                break;
            case "Type0":
                if (file.Array(font.Get("DescendantFonts")) is not { Items.Count: 1 } descendants
                    || file.Dictionary(descendants.Items[0]) is not { } descendant)
                {
                    issues.Error(part, null, $"The composite font /{name} ({baseFont}) has no single /DescendantFonts entry.");
                }
                else if (file.Dictionary(descendant.Get("FontDescriptor")) is null)
                {
                    issues.Error(part, null, $"The descendant font of /{name} ({baseFont}) has no /FontDescriptor.");
                }

                if (font.Get("Encoding") is null)
                {
                    issues.Error(part, null, $"The composite font /{name} ({baseFont}) has no /Encoding.");
                }

                break;
            case "Type3":
                if (font.Get("CharProcs") is null || font.Get("FontMatrix") is null)
                {
                    issues.Error(part, null, $"The Type 3 font /{name} lacks /CharProcs or /FontMatrix.");
                }

                break;
            default:
                issues.Error(part, null, $"The font /{name} has the unknown /Subtype {subtype ?? "(none)"}.");
                break;
        }
    }

    /// <summary>Tokenises the page content: operators, q/Q and BT/ET nesting, and the names Tf and Do use.</summary>
    private static void CheckContent(byte[] content, PdfDictionary? fonts, PdfDictionary? xObjects, string part, IssueList issues)
    {
        var lexer = new PdfLexer(content);
        var operands = new List<PdfToken>();
        var saved = 0;
        var inText = false;
        var compatibility = 0;
        var reported = new HashSet<string>(StringComparer.Ordinal);
        void Report(string message)
        {
            if (reported.Add(message))
            {
                issues.Error(part, null, message);
            }
        }

        try
        {
            while (true)
            {
                var token = lexer.Next();
                if (token.Kind == PdfTokenKind.EndOfFile)
                {
                    break;
                }

                if (token.Kind != PdfTokenKind.Keyword)
                {
                    operands.Add(token);
                    continue;
                }

                var op = token.Text!;
                switch (op)
                {
                    case "ID":
                        // Inline image data runs to the EI that stands between white space.
                        var end = InlineImageEnd(content, token.End);
                        if (end < 0)
                        {
                            Report("An inline image has no EI.");
                            return;
                        }

                        lexer.Reset(end);
                        break;
                    case "q":
                        saved++;
                        break;
                    case "Q":
                        if (--saved < 0)
                        {
                            Report("Q restores a graphics state that q never saved.");
                            saved = 0;
                        }

                        break;
                    case "BT":
                        if (inText)
                        {
                            Report("BT opens a text object inside another one.");
                        }

                        inText = true;
                        break;
                    case "ET":
                        if (!inText)
                        {
                            Report("ET closes a text object that BT never opened.");
                        }

                        inText = false;
                        break;
                    case "BX":
                        compatibility++;
                        break;
                    case "EX":
                        compatibility = Math.Max(0, compatibility - 1);
                        break;
                    case "Tf" when operands.Count >= 2 && operands[^2].Kind == PdfTokenKind.Name:
                        var font = operands[^2].Text!;
                        if (fonts?.Get(font) is null)
                        {
                            Report($"Tf selects the font /{font}, which the page resources do not hold.");
                        }

                        break;
                    case "Do" when operands.Count >= 1 && operands[^1].Kind == PdfTokenKind.Name:
                        var xObject = operands[^1].Text!;
                        if (xObjects?.Get(xObject) is null)
                        {
                            Report($"Do paints the XObject /{xObject}, which the page resources do not hold.");
                        }

                        break;
                    default:
                        if (!Operators.Contains(op) && compatibility == 0)
                        {
                            Report($"The content uses the unknown operator '{op}'.");
                        }

                        break;
                }

                operands.Clear();
            }
        }
        catch (Exception exception) when (exception is not IssueList.FullException and not OperationCanceledException)
        {
            Report("The content stream cannot be tokenised: " + exception.Message);
            return;
        }

        if (inText)
        {
            Report("A text object (BT) is not closed with ET.");
        }

        if (saved > 0)
        {
            issues.Warning(part, null, $"{saved} graphics state(s) saved with q are not restored with Q.");
        }
    }

    private static int InlineImageEnd(byte[] content, int start)
    {
        for (var index = start; index + 2 < content.Length; index++)
        {
            if (content[index] == 'E' && content[index + 1] == 'I' && IsWhite(content[index - 1])
                && (index + 2 == content.Length || IsWhite(content[index + 2])))
            {
                return index + 2;
            }
        }

        return -1;
    }

    /// <summary>The object's syntax: header, value, endobj, and for a stream a /Length that lands on endstream.</summary>
    private static void CheckObject(byte[] data, PdfFile file, int number, int offset, Budget budget, IssueList issues)
    {
        var part = $"object {number}";
        try
        {
            var parser = new PdfObjectParser(new PdfLexer(data, offset));
            parser.Advance();
            parser.Advance();
            parser.Advance();
            var value = parser.ParseValue();
            if (value is PdfDictionary dictionary && parser.Peek(0) is { Kind: PdfTokenKind.Keyword, Text: "stream" })
            {
                var keyword = parser.Advance();
                var start = keyword.End + (keyword.End < data.Length && data[keyword.End] == '\r' ? 1 : 0);
                if (start < data.Length && data[start] == '\n')
                {
                    start++;
                }
                else
                {
                    issues.Warning(part, null, "The stream keyword is not followed by a line feed.");
                }

                if (Number(file.Resolve(dictionary.Get("Length"))) is not { } length || length < 0)
                {
                    issues.Error(part, null, "The stream has no valid /Length.");
                    return;
                }

                var after = start + (long)length;
                while (after < data.Length && IsWhite(data[after]))
                {
                    after++;
                }

                if (after + 9 > data.Length || !data.AsSpan((int)after, 9).SequenceEqual("endstream"u8))
                {
                    issues.Error(part, null, $"The stream's /Length {length} does not end at 'endstream'.");
                    return;
                }

                if (file.GetStream(new PdfReference(number)) is { } stream)
                {
                    try
                    {
                        Decode(file, stream, budget);
                    }
                    catch (Exception exception) when (exception is not IssueList.FullException and not OperationCanceledException)
                    {
                        issues.Error(part, null, "The stream data cannot be decoded: " + exception.Message);
                    }
                }

                return;
            }

            if (parser.Peek(0) is not { Kind: PdfTokenKind.Keyword, Text: "endobj" })
            {
                issues.Warning(part, null, "The object does not end with 'endobj'.");
            }
        }
        catch (Exception exception) when (exception is not IssueList.FullException and not OperationCanceledException)
        {
            issues.Error(part, null, "The object cannot be parsed: " + exception.Message);
        }
    }

    private static double? Number(PdfValue? value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => null,
    };

    private static bool IsWhite(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
}
