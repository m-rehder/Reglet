using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace Reglet;

/// <summary>
/// binds the <c>{{Name}}</c> placeholders in the loaded <c>word/document.xml</c> of a
/// DOCX template. Inline content replaces the placeholder text within its paragraph,
/// block content replaces the whole paragraph. Paragraph and run styles at the placeholder are adopted as
/// basis is adopted for the inserted content so that the template's styles are preserved.
/// </summary>
internal sealed class DocxTemplateBinder
{
    private static readonly XNamespace W = DocxConstants.WordprocessingML;
    private static readonly XNamespace Xml = XNamespace.Xml;
    private static readonly XNamespace WP = DocxConstants.DrawingWordprocessingNamespace;
    private static readonly Regex MergeField = new(
        "^\\s*MERGEFIELD\\s+(?:\"([^\"]+)\"|([^\\s\\\\]+))" +
        "(?:\\s+\\\\\\*\\s+(?<format>[A-Za-z]+)" +
        "|\\s+\\\\b\\s*(?:\"(?<before>[^\"]*)\"|(?<before>[^\\s\\\\\"]+))" +
        "|\\s+\\\\f\\s*(?:\"(?<after>[^\"]*)\"|(?<after>[^\\s\\\\\"]+))" +
        "|\\s+\\\\#\\s*(?:\"(?<number>[^\"]*)\"|(?<number>[^\\s\\\\\"]+))" +
        "|\\s+\\\\(?<flag>[vh]))*\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Word formatting switches that only protect the result's formatting.</summary>
    private static readonly HashSet<string> MergeFieldNoOps = new(StringComparer.Ordinal)
    {
        "MERGEFORMAT", "CHARFORMAT",
    };

    /// <summary>Word formatting switches that change the case of the result.</summary>
    private static readonly HashSet<string> MergeFieldCasing = new(StringComparer.Ordinal)
    {
        "UPPER", "LOWER", "CAPS", "FIRSTCAP",
    };

    /// <summary>A native Word mail-merge region marker (<c>MERGEFIELD TableStart:Name</c>).</summary>
    private static readonly Regex NativeRegion = new(
        "^\\s*MERGEFIELD\\s+(?<kind>TableStart|TableEnd)\\s*:\\s*(?:\"(?<name>[^\"]+)\"|(?<name>[^\\s\\\\]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Order of the child elements in <c>w:pPr</c> (CT_PPr) for merging.</summary>
    private static readonly string[] ParagraphPropertyOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr",
        "suppressLineNumbers", "pBdr", "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap",
        "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd",
        "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap",
        "jc", "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle",
        "rPr", "sectPr", "pPrChange",
    ];

    /// <summary>Order of the child elements in <c>w:rPr</c> (CT_RPr) for merging.</summary>
    private static readonly string[] RunPropertyOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline",
        "shadow", "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing",
        "w", "kern", "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText",
        "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath",
    ];

    /// <summary>Namespaces of the WordprocessingML fragments.</summary>
    private static readonly (string Prefix, string Namespace)[] FragmentNamespaces =
    [
        ("w", DocxConstants.WordprocessingML),
        ("r", DocxConstants.Relationships),
        ("wp", DocxConstants.DrawingWordprocessingNamespace),
        ("a", DocxConstants.DrawingMainNamespace),
        ("pic", DocxConstants.DrawingPictureNamespace),
    ];

    private readonly DocxWriteContext _context;
    private readonly long _contentWidth;
    private readonly UnsupportedFeatureHandler _handler;
    private readonly IReadOnlyDictionary<string, bool> _conditions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> _repeats;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> _regions;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly TemplateLiteralValues _literalValues = new();
    private readonly TemplateTextBudget _budget;
    private readonly string? _language;
    private readonly ResourceLimits _limits;
    private readonly HashSet<string> _bookmarkNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _duplicateBookmarkNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _bookmarkSuffixes = new(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlySet<int> _safeFootnoteIds;
    private readonly IReadOnlySet<int> _safeEndnoteIds;
    private readonly IReadOnlySet<int> _safeCommentIds;

    /// <summary>Notes that an additional clone needs: endnote flag, source id and new id.</summary>
    public List<(bool Endnote, int SourceId, int NewId)> NoteClones { get; } = [];

    /// <summary>Comments that an additional clone needs: source id and new id.</summary>
    public List<(int SourceId, int NewId)> CommentClones { get; } = [];

    public DocxTemplateBinder(DocxWriteContext context, long contentWidth, UnsupportedFeatureHandler handler, IReadOnlyDictionary<string, bool> conditions, IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> repeats, IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> regions, ResourceLimits limits, IReadOnlySet<int>? safeFootnoteIds = null, IReadOnlySet<int>? safeEndnoteIds = null, IReadOnlySet<int>? safeCommentIds = null, string? language = null)
    {
        _language = language;
        _context = context;
        _contentWidth = contentWidth;
        _handler = handler;
        _conditions = conditions;
        _repeats = repeats;
        _regions = regions;
        _limits = limits;
        _budget = new TemplateTextBudget(limits.MaxTemplateCharacters);
        _safeFootnoteIds = safeFootnoteIds ?? new HashSet<int>();
        _safeEndnoteIds = safeEndnoteIds ?? new HashSet<int>();
        _safeCommentIds = safeCommentIds ?? new HashSet<int>();
    }

    /// <summary>
    /// replaces all bound placeholders. Returns the names that were actually replaced.
    /// </summary>
    public IReadOnlySet<string> Bind(XDocument document, IReadOnlyDictionary<string, TemplateContent> bindings)
        => Bind(Body(document), bindings);

    public IReadOnlySet<string> Bind(XElement body, IReadOnlyDictionary<string, TemplateContent> bindings)
    {
        foreach (var bookmark in body.Descendants(W + "bookmarkStart"))
        {
            var name = (string?)bookmark.Attribute(W + "name");
            if (name is not null && !_bookmarkNames.Add(name))
            {
                _duplicateBookmarkNames.Add(name);
            }
        }
        var found = new HashSet<string>(StringComparer.Ordinal);
        JoinSplitPlaceholders(body);
        ConvertNativeRegions(body);
        if (_regions.Count > 0)
        {
            new TemplateRegionExpander(_regions, bindings, IsRegionElement, element => element.Name == W + "tc", RegionText, AllRegionText, UnsafeToClone, PrepareAdditionalClone,
                ProtectValue, _literalValues.RestoredLength, Report, _limits, region => !BookmarksPaired(region)).Expand(body);
        }
        ExpandRepeats(body);
        ExpandBlockConditions(body);
        RemoveEmptyTablesAndCloseCells(body);

        // snapshot: inserted blocks are not bound again.
        foreach (var paragraph in body.Descendants(W + "p").Where(paragraph => !TemplateRegionExpander.IsProtected(paragraph)).ToList())
        {
            var original = paragraph.DescendantsAndSelf().ToHashSet();
            BindParagraph(paragraph, bindings, found);
            if (paragraph.Parent is not null) BindMergeFields(paragraph, bindings, found, original);
        }

        foreach (var text in body.Descendants(W + "t").ToList())
        {
            var restored = _literalValues.Restore(text.Value);
            if (restored != text.Value)
            {
                // A value may hold line breaks and tabs, which w:t cannot carry.
                text.ReplaceWith(XmlFragment.Write(writer => DocxPackageBuilder.WriteText(writer, restored, _context.XmlBehavior), FragmentNamespaces));
            }
        }

        return found;
    }

    /// <summary>
    /// Converts native Word mail-merge region markers (<c>MERGEFIELD TableStart:Name</c> and
    /// <c>TableEnd:Name</c>) into the standalone <c>{{Repeat:Name}}</c>/<c>{{EndRepeat}}</c> markers
    /// the region expander understands. A marker in a table row becomes a temporary marker row
    /// around that row; a marker in a standalone paragraph replaces the field in its paragraph.
    /// </summary>
    private void ConvertNativeRegions(XElement body)
    {
        if (_regions.Count == 0)
        {
            return;
        }

        var fields = NativeFields(body).ToList();
        var balanced = fields.GroupBy(field => field.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(field => field.Start) == group.Count(field => !field.Start), StringComparer.Ordinal);
        foreach (var (name, start, paragraph, runs) in fields)
        {
            if (!HasRegionData(name))
            {
                Report($"DOCX: the native Word region '{name}' has no data; it is left unchanged.");
            }
            else if (!balanced[name])
            {
                Report($"DOCX: the native Word region '{name}' has no matching TableStart or TableEnd; it is left unchanged.");
            }
            else
            {
                ConvertNativeRegion(paragraph, name, start, runs);
            }
        }
    }

    /// <summary>The native region markers in the body, simple fields first.</summary>
    private IEnumerable<(string Name, bool Start, XElement Paragraph, IReadOnlyList<XElement> Runs)> NativeFields(XElement body)
    {
        foreach (var field in body.Descendants(W + "fldSimple"))
        {
            if (!TryNativeRegion((string?)field.Attribute(W + "instr"), out var name, out var start) || field.Parent is not XElement parent)
            {
                continue;
            }

            if (parent.Name != W + "p")
            {
                Report($"DOCX: the native Word region '{name}' is not inside a paragraph; it is left unchanged.");
                continue;
            }

            yield return (name, start, parent, [field]);
        }

        foreach (var paragraph in body.Descendants(W + "p"))
        {
            var elements = paragraph.Elements().ToList();
            for (var index = 0; index < elements.Count; index++)
            {
                if (!IsFieldMarker(elements[index], "begin"))
                {
                    continue;
                }

                var instruction = new System.Text.StringBuilder();
                var separated = false;
                var end = index + 1;
                for (; end < elements.Count; end++)
                {
                    var run = elements[end];
                    if (IsFieldMarker(run, "end"))
                    {
                        break;
                    }

                    if (IsFieldMarker(run, "separate") && !separated)
                    {
                        separated = true;
                        continue;
                    }

                    if (run.Name != W + "r"
                        || run.Elements().Any(child => child.Name != W + "rPr" && child.Name != (separated ? W + "t" : W + "instrText")))
                    {
                        break;
                    }

                    if (!separated)
                    {
                        foreach (var text in run.Elements(W + "instrText"))
                        {
                            instruction.Append(text.Value);
                        }
                    }
                }

                if (end < elements.Count && IsFieldMarker(elements[end], "end")
                    && TryNativeRegion(instruction.ToString(), out var name, out var start))
                {
                    yield return (name, start, paragraph, elements.Skip(index).Take(end - index + 1).ToList());
                }

                index = end;
            }
        }
    }

    private void ConvertNativeRegion(
        XElement paragraph,
        string name,
        bool start,
        IReadOnlyList<XElement> fieldRuns)
    {
        var marker = start ? $"{{{{Repeat:{name}}}}}" : "{{EndRepeat}}";
        var row = paragraph.Ancestors(W + "tr").FirstOrDefault();
        if (row is not null)
        {
            foreach (var run in fieldRuns)
            {
                run.Remove();
            }

            if (start)
            {
                row.AddBeforeSelf(CreateMarkerRow(marker));
            }
            else
            {
                row.AddAfterSelf(CreateMarkerRow(marker));
            }

            return;
        }

        var remaining = RemainingText(paragraph, fieldRuns);
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            Report($"DOCX: the native Word region '{name}' shares its paragraph with other text; it is left unchanged.");
            return;
        }

        foreach (var run in fieldRuns)
        {
            run.Remove();
        }

        var properties = paragraph.Element(W + "pPr");
        paragraph.RemoveNodes();
        if (properties is not null)
        {
            paragraph.Add(properties);
        }

        paragraph.Add(new XElement(W + "r", new XElement(W + "t", marker)));
    }

    private static string RemainingText(XElement paragraph, IReadOnlyList<XElement> fieldRuns)
    {
        var excluded = new HashSet<XElement>();
        foreach (var element in fieldRuns)
        {
            foreach (var text in element.DescendantsAndSelf(W + "t"))
            {
                excluded.Add(text);
            }
        }

        return string.Concat(paragraph.Descendants(W + "t")
            .Where(text => !excluded.Contains(text))
            .Select(text => text.Value));
    }

    private static XElement CreateMarkerRow(string marker)
        => new(W + "tr",
            new XElement(W + "tc",
                new XElement(W + "p",
                    new XElement(W + "r",
                        new XElement(W + "t", marker)))));

    private static bool TryNativeRegion(string? instruction, out string name, out bool start)
    {
        name = string.Empty;
        start = false;
        if (instruction is null)
        {
            return false;
        }

        var match = NativeRegion.Match(instruction);
        if (!match.Success)
        {
            return false;
        }

        start = match.Groups["kind"].Value.Equals("TableStart", StringComparison.OrdinalIgnoreCase);
        name = match.Groups["name"].Value.Trim();
        return name.Length > 0;
    }

    private bool HasRegionData(string name)
    {
        bool Contains(IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> groups)
        {
            foreach (var (key, rows) in groups)
            {
                if (string.Equals(key, name, StringComparison.Ordinal))
                {
                    return true;
                }

                foreach (var row in rows)
                {
                    if (Contains(row.Regions))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        return Contains(_regions);
    }

    private void BindMergeFields(XElement paragraph, IReadOnlyDictionary<string, TemplateContent> bindings, HashSet<string> found, HashSet<XElement> original)
    {
        foreach (var field in paragraph.Descendants(W + "fldSimple").ToList())
        {
            if (!original.Contains(field)) continue;
            if (field.Ancestors().Any(node => node.Name == W + "del" || node.Name == W + "moveFrom")) continue;
            if (field.Descendants().Any(node => node.Name == W + "fldSimple" || node.Name == W + "fldChar"))
            {
                if (((string?)field.Attribute(W + "instr"))?.Contains("MERGEFIELD", StringComparison.OrdinalIgnoreCase) == true)
                    Report(MergeFieldName((string?)field.Attribute(W + "instr")) is { } nestedName
                        ? $"DOCX: the MERGEFIELD '{nestedName}' contains another field and remains unchanged."
                        : "DOCX: a MERGEFIELD that contains another field remains unchanged.");
                continue;
            }
            if (!TryMergeField((string?)field.Attribute(W + "instr"), bindings, out var name, out var content)) continue;
            var baseProperties = field.Descendants(W + "r").FirstOrDefault()?.Element(W + "rPr");
            field.ReplaceWith(GenerateInlines(content.Inlines, baseProperties));
            found.Add(name);
        }

        var elements = paragraph.Elements().ToList();
        for (var i = 0; i < elements.Count; i++)
        {
            var begin = elements[i];
            if (!original.Contains(begin) || !IsFieldMarker(begin, "begin")) continue;
            var instruction = new System.Text.StringBuilder();
            XElement? resultProperties = null;
            var separated = false;
            var depth = 0;
            var end = i + 1;
            for (; end < elements.Count; end++)
            {
                var run = elements[end];
                if (!original.Contains(run)) break;
                if (IsFieldMarker(run, "begin"))
                {
                    depth++;
                    continue;
                }

                if (IsFieldMarker(run, "end"))
                {
                    if (depth == 0) break;
                    depth--;
                    continue;
                }

                if (depth > 0) continue;
                if (IsFieldMarker(run, "separate") && !separated) { separated = true; continue; }
                if (run.Name != W + "r" || run.Elements().Any(child => child.Name != W + "rPr" && child.Name != (separated ? W + "t" : W + "instrText"))) break;
                if (separated) resultProperties ??= run.Element(W + "rPr");
                else foreach (var text in run.Elements(W + "instrText")) instruction.Append(text.Value);
            }
            if (end >= elements.Count || !original.Contains(elements[end]) || !IsFieldMarker(elements[end], "end")) continue;
            if (TryMergeField(instruction.ToString(), bindings, out var name, out var content))
            {
                begin.AddBeforeSelf(GenerateInlines(content.Inlines, resultProperties));
                for (var index = i; index <= end; index++) elements[index].Remove();
                found.Add(name);
                i = end;
            }

            // A field that was not bound stays; a nested MERGEFIELD is visited by a later iteration.
        }

        var remainingInstruction = string.Concat(
            paragraph.Descendants(W + "instrText").Where(original.Contains).Select(text => text.Value));
        if (remainingInstruction.Contains("MERGEFIELD", StringComparison.OrdinalIgnoreCase))
        {
            Report(MergeFieldName(remainingInstruction) is { } remainingName
                ? $"DOCX: the MERGEFIELD '{remainingName}' remains unchanged because it is unbound or unsupported."
                : "DOCX: an unbound or unsupported MERGEFIELD remains unchanged.");
        }
    }

    private static bool IsFieldMarker(XElement run, string type)
        => run.Name == W + "r" && run.Elements().Count(child => child.Name != W + "rPr") == 1
            && (string?)run.Element(W + "fldChar")?.Attribute(W + "fldCharType") == type;

    private bool TryMergeField(string? instruction, IReadOnlyDictionary<string, TemplateContent> bindings, out string name, out TemplateContent content)
    {
        name = string.Empty;
        content = null!;
        if (instruction is null || !instruction.TrimStart().StartsWith("MERGEFIELD", StringComparison.OrdinalIgnoreCase)) return false;
        var match = MergeField.Match(instruction);
        if (!match.Success)
        {
            Report(MergeFieldName(instruction) is { } malformedName
                ? $"DOCX: the MERGEFIELD '{malformedName}' is malformed or uses an unsupported switch and remains unchanged."
                : "DOCX: a MERGEFIELD instruction is malformed or uses an unsupported switch and remains unchanged.");
            return false;
        }
        name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        var fieldName = name;
        var matches = bindings.Where(binding => string.Equals(binding.Key, fieldName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Value.Kind != TemplateContentKind.Inline)
        {
            Report($"DOCX: MERGEFIELD '{name}' has no unique inline binding and remains unchanged.");
            return false;
        }
        name = matches[0].Key;
        content = matches[0].Value;
        var formats = match.Groups["format"].Captures.Select(capture => capture.Value.ToUpperInvariant()).ToArray();
        var casing = formats.Where(MergeFieldCasing.Contains).ToArray();
        var numbers = match.Groups["number"].Captures.Select(capture => capture.Value).ToArray();
        var before = match.Groups["before"];
        var after = match.Groups["after"];
        var hasAffix = before.Success || after.Success;
        if (formats.Any(value => !MergeFieldNoOps.Contains(value) && !MergeFieldCasing.Contains(value))
            || casing.Length > 1
            || before.Captures.Count > 1 || after.Captures.Count > 1
            || numbers.Length > 1
            || hasAffix && (casing.Length > 0 || numbers.Length > 0)
            || casing.Length > 0 && numbers.Length > 0
            || (hasAffix || casing.Length > 0 || numbers.Length > 0) && content.Inlines.Any(inline => inline is not TextRun))
        {
            Report($"DOCX: MERGEFIELD '{name}' has unsupported formatting ({UnsupportedMergeFieldFormatting(match, content)}) and remains unchanged.");
            return false;
        }
        if (numbers.Length == 1)
        {
            if (!TryFormatMergeNumber(content, numbers[0], _language, out var formatted))
            {
                Report($"DOCX: MERGEFIELD '{name}' has a numeric picture '{numbers[0]}' that cannot be applied; it remains unchanged.");
                return false;
            }

            content = formatted;
        }
        if (casing.Length > 0)
        {
            content = TemplateContent.FromInlines(content.Inlines.Cast<TextRun>().Select(run =>
            {
                var text = casing[0] switch
                {
                    "UPPER" => run.Text.ToUpperInvariant(),
                    "CAPS" => FieldCasing.Caps(run.Text),
                    "LOWER" => run.Text.ToLowerInvariant(),
                    "FIRSTCAP" => FieldCasing.FirstCap(run.Text),
                    _ => run.Text,
                };
                return RunFormat.Of(run).ToRun(text, run.CharacterStyleName);
            }));
        }
        if (hasAffix && content.Inlines.Cast<TextRun>().Any(run => run.Text.Length > 0))
        {
            var inlines = new List<Inline>();
            if (before.Success) inlines.Add(new TextRun(before.Value));
            inlines.AddRange(content.Inlines);
            if (after.Success) inlines.Add(new TextRun(after.Value));
            content = TemplateContent.FromInlines(inlines);
        }
        return true;
    }

    /// <summary>Applies a Word <c>\\#</c> numeric picture to the text of an inline binding.</summary>
    private static bool TryFormatMergeNumber(TemplateContent content, string picture, string? language, out TemplateContent result)
    {
        result = content;
        var text = string.Concat(content.Inlines.OfType<TextRun>().Select(run => run.Text));
        if (!DocxMergeNumber.TryFormat(text, picture, language, out var formatted))
        {
            return false;
        }

        var baseRun = content.Inlines.OfType<TextRun>().FirstOrDefault();
        result = TemplateContent.FromInline(baseRun is null
            ? new TextRun(formatted)
            : RunFormat.Of(baseRun).ToRun(formatted, baseRun.CharacterStyleName));
        return true;
    }

    /// <summary>The field name of a <c>MERGEFIELD</c> instruction, for concrete diagnostics.</summary>
    private static string? MergeFieldName(string? instruction)
    {
        if (instruction is null)
        {
            return null;
        }

        var text = instruction.TrimStart();
        const string Keyword = "MERGEFIELD";
        if (!text.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        text = text[Keyword.Length..].TrimStart();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            return closing > 1 ? text[1..closing] : null;
        }

        var end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '\\')
        {
            end++;
        }

        return end > 0 ? text[..end] : null;
    }

    /// <summary>Names the concrete reason a <c>MERGEFIELD</c> combination is not supported.</summary>
    private static string UnsupportedMergeFieldFormatting(Match match, TemplateContent content)
    {
        var reasons = new List<string>();
        var switches = match.Groups["format"].Captures.Select(capture => capture.Value.ToUpperInvariant()).ToList();
        var unsupported = switches
            .Where(value => !MergeFieldNoOps.Contains(value) && !MergeFieldCasing.Contains(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unsupported.Count > 0)
        {
            reasons.Add("unsupported switch " + string.Join(", ", unsupported.Select(value => "\\* " + value)));
        }

        var casing = switches.Where(MergeFieldCasing.Contains).ToList();
        if (casing.Count > 1)
        {
            reasons.Add("conflicting casing switches");
        }

        var numbers = match.Groups["number"].Captures.Count;
        if (numbers > 1)
        {
            reasons.Add("repeated \\# picture");
        }

        var before = match.Groups["before"];
        var after = match.Groups["after"];
        var hasAffix = before.Success || after.Success;
        if (before.Captures.Count > 1 || after.Captures.Count > 1)
        {
            reasons.Add("repeated \\b/\\f text");
        }

        if (hasAffix && (casing.Count > 0 || numbers > 0))
        {
            reasons.Add("\\b/\\f combined with a casing or numeric switch");
        }

        if (casing.Count > 0 && numbers > 0)
        {
            reasons.Add("a casing switch combined with a numeric picture");
        }

        if ((hasAffix || casing.Count > 0 || numbers > 0) && content.Inlines.Any(inline => inline is not TextRun))
        {
            reasons.Add("a switch combined with formatted or non-text content");
        }

        return reasons.Count > 0 ? string.Join("; ", reasons) : "an unsupported combination";
    }

    private static bool IsRegionElement(XElement element) => element.Name == W + "p" || element.Name == W + "tr";

    private static IEnumerable<XText> RegionText(XElement element)
        => element.Descendants(W + "t")
            .Where(text => element.Name != W + "tr" || text.Ancestors(W + "tr").FirstOrDefault() == element)
            .SelectMany(text => text.Nodes().OfType<XText>());

    private static IEnumerable<XText> AllRegionText(XElement element)
        => element.Descendants(W + "t").SelectMany(text => text.Nodes().OfType<XText>());

    /// <summary>
    /// Whether a block or row must not be duplicated. Self-contained content is copied: a drawing
    /// (also a complex one), a field whose <c>w:fldChar</c> triple lies inside the clone, a
    /// hyperlink, a table, a bookmark range that closes inside the clone and tracked changes (with
    /// fresh revision ids) (OF-D03). A content control is cloned with a new <c>w:id</c>; data-bound
    /// content controls are reported. A note is cloned with its bookmarks, nested notes and
    /// comments; a comment outside a note is cloned with a fresh id. A section stays reported.
    /// </summary>
    private bool UnsafeToClone(XElement element, bool marker)
        => !FieldsComplete(element)
            || element.DescendantsAndSelf().Any(node =>
                node.Name == W + "sdt" && node.Element(W + "sdtPr")?.Element(W + "dataBinding") is not null
                || node.Name.Namespace == W && node.Name.LocalName is
                    "sectPr" or "pict" or "object"
                    or "altChunk" or "customXml" or "subDoc"
                || node.Attribute(XNamespace.Xml + "id") is not null
                || (node.Name == W + "bookmarkStart" || node.Name == W + "bookmarkEnd") && marker);

    /// <summary>
    /// Whether all bookmark ranges in the cloned content are complete and unique, so a clone can
    /// pair its starts and ends (also across paragraphs and table cells, OF-D03).
    /// </summary>
    private bool BookmarksPaired(IReadOnlyList<XElement> elements)
    {
        var starts = new Dictionary<string, string>(StringComparer.Ordinal);
        var ends = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            foreach (var node in element.DescendantsAndSelf())
            {
                if (node.Name == W + "bookmarkStart")
                {
                    var id = (string?)node.Attribute(W + "id");
                    var name = (string?)node.Attribute(W + "name");
                    if (id is null || string.IsNullOrEmpty(name)
                        || _duplicateBookmarkNames.Contains(name)
                        || !starts.TryAdd(id, name))
                    {
                        return false;
                    }
                }
                else if (node.Name == W + "bookmarkEnd")
                {
                    var id = (string?)node.Attribute(W + "id");
                    if (id is null || !ends.Add(id))
                    {
                        return false;
                    }
                }
            }
        }

        return starts.Count == ends.Count && starts.Keys.All(ends.Contains);
    }

    /// <summary>
    /// Whether every complex field (<c>w:fldChar begin … end</c>) begins and ends inside the element,
    /// so duplicating the element duplicates whole fields (OF-D03).
    /// </summary>
    private static bool FieldsComplete(XElement element)
    {
        var depth = 0;
        foreach (var marker in element.DescendantsAndSelf(W + "fldChar"))
        {
            switch ((string?)marker.Attribute(W + "fldCharType"))
            {
                case "begin":
                    depth++;
                    break;
                case "end":
                    depth--;
                    break;
            }

            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private void PrepareAdditionalClone(XElement clone)
    {
        RemoveParagraphIds(clone);
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in clone.DescendantsAndSelf(W + "bookmarkStart"))
        {
            var oldId = (string)start.Attribute(W + "id")!;
            var oldName = (string)start.Attribute(W + "name")!;
            var newName = UniqueBookmarkName(oldName);
            var id = _context.NextBookmarkId().ToString(System.Globalization.CultureInfo.InvariantCulture);
            clone.Descendants(W + "bookmarkEnd")
                .Single(end => (string?)end.Attribute(W + "id") == oldId).SetAttributeValue(W + "id", id);
            start.SetAttributeValue(W + "id", id);
            start.SetAttributeValue(W + "name", newName);
            renamed.Add(oldName, newName);
        }
        foreach (var link in clone.DescendantsAndSelf(W + "hyperlink"))
        {
            var anchor = (string?)link.Attribute(W + "anchor");
            if (anchor is not null && renamed.TryGetValue(anchor, out var name)) link.SetAttributeValue(W + "anchor", name);
        }
        foreach (var node in clone.DescendantsAndSelf()
            .Where(node => node.Name == WP + "docPr" || node.Name.LocalName == "cNvPr"))
        {
            node.SetAttributeValue("id", _context.NextDrawingId().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var contentControl in clone.DescendantsAndSelf(W + "sdt"))
        {
            var id = contentControl.Element(W + "sdtPr")?.Element(W + "id");
            if (id is not null)
            {
                id.SetAttributeValue(W + "val", _context.NextSdtId().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        foreach (var revision in clone.DescendantsAndSelf().Where(node => node.Name.Namespace == W
            && (node.Name.LocalName is "ins" or "del" or "moveFrom" or "moveTo"
                || node.Name.LocalName.EndsWith("Change", StringComparison.Ordinal))))
        {
            if (revision.Attribute(W + "id") is not null)
            {
                revision.SetAttributeValue(W + "id", _context.NextRevisionId().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        foreach (var reference in clone.DescendantsAndSelf())
        {
            if (reference.Name != W + "footnoteReference" && reference.Name != W + "endnoteReference")
            {
                continue;
            }

            var endnote = reference.Name == W + "endnoteReference";
            var id = (string?)reference.Attribute(W + "id");
            if (id is null || !int.TryParse(
                    id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sourceId))
            {
                continue;
            }

            var safe = endnote ? _safeEndnoteIds : _safeFootnoteIds;
            if (!safe.Contains(sourceId))
            {
                Report($"DOCX: the note '{sourceId}' in a repeated region contains content that cannot be duplicated; the reference remains shared.");
                continue;
            }

            var newId = endnote ? _context.NextEndnoteId() : _context.NextFootnoteId();
            reference.SetAttributeValue(W + "id", newId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            NoteClones.Add((endnote, sourceId, newId));
        }

        var commentIds = new Dictionary<int, int>();
        foreach (var node in clone.Descendants()
            .Where(item => item.Name == W + "commentReference"
                || item.Name == W + "commentRangeStart" || item.Name == W + "commentRangeEnd")
            .ToList())
        {
            var id = (string?)node.Attribute(W + "id");
            if (id is null || !int.TryParse(
                    id, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var sourceId))
            {
                continue;
            }

            if (!_safeCommentIds.Contains(sourceId))
            {
                Report($"DOCX: the comment '{sourceId}' in a repeated region cannot be duplicated; it remains shared.");
                continue;
            }

            if (!commentIds.TryGetValue(sourceId, out var newId))
            {
                newId = _context.NextCommentId();
                commentIds[sourceId] = newId;
                CommentClones.Add((sourceId, newId));
            }

            node.SetAttributeValue(W + "id", newId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private string UniqueBookmarkName(string original)
    {
        var number = _bookmarkSuffixes.GetValueOrDefault(original, 2);
        while (number < int.MaxValue)
        {
            var suffix = "_" + (number++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var length = Math.Min(original.Length, 40 - suffix.Length);
            if (length > 0 && char.IsHighSurrogate(original[length - 1])) length--;
            var candidate = original[..length] + suffix;
            if (_bookmarkNames.Add(candidate))
            {
                _bookmarkSuffixes[original] = number;
                return candidate;
            }
        }
        throw new DocumentValidationException("DOCX: no bookmark name is available.");
    }

    private static void RemoveParagraphIds(XElement clone)
    {
        XNamespace word2010 = "http://schemas.microsoft.com/office/word/2010/wordml";
        foreach (var paragraph in clone.DescendantsAndSelf(W + "p"))
        {
            paragraph.Attribute(word2010 + "paraId")?.Remove();
            paragraph.Attribute(word2010 + "textId")?.Remove();
        }
    }

    /// <summary>Joins placeholders that Word split over several runs, so rows and markers are found.</summary>
    private static void JoinSplitPlaceholders(XElement body)
    {
        foreach (var paragraph in body.Descendants(W + "p"))
        {
            var nodes = paragraph.Descendants(W + "t").ToList();
            if (nodes.Count < 2)
            {
                continue;
            }

            var values = nodes.Select(node => node.Value).ToArray();
            if (!TemplatePlaceholders.JoinSplitPlaceholders(values))
            {
                continue;
            }

            for (var index = 0; index < nodes.Count; index++)
            {
                if (nodes[index].Value != values[index])
                {
                    nodes[index].Value = values[index];
                    nodes[index].SetAttributeValue(Xml + "space", "preserve");
                }
            }
        }
    }

    /// <summary>Repeats the table row containing a row-repeat placeholder (phase AB, AB03).</summary>
    private void ExpandRepeats(XElement body)
    {
        foreach (var (placeholder, rows) in _repeats)
        {
            var marker = DocumentTemplate.Marker(placeholder);
            foreach (var row in body.Descendants(W + "tr").ToList())
            {
                if (TemplateRegionExpander.ContainsProtected(row))
                {
                    continue;
                }

                if (!row.Descendants(W + "t").Any(text => text.Ancestors(W + "tr").FirstOrDefault() == row
                    && text.Value.Contains(marker, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (rows.Count > 1 && (UnsafeToClone(row, false) || !BookmarksPaired([row])))
                {
                    Report($"DOCX: repeated row '{placeholder}' contains content that cannot safely be duplicated; it is left unchanged.");
                    TemplateRegionExpander.Protect(row);
                    continue;
                }

                var clones = rows.Select(data => CloneRow(row, marker, data)).ToList<XNode>();
                foreach (var clone in clones.Skip(1).OfType<XElement>())
                {
                    PrepareAdditionalClone(clone);
                }

                row.ReplaceWith(clones);
            }
        }
    }

    /// <summary>Makes a data value safe for XML and keeps it opaque until binding has finished.</summary>
    private string ProtectValue(string value) => _literalValues.Protect(XmlText.Clean(value, _context.XmlBehavior));

    private XElement CloneRow(XElement row, string marker, IReadOnlyDictionary<string, string?> data)
    {
        _budget.Charge(TemplateTextBudget.SizeOf(row));
        data = new Dictionary<string, string?>(data, StringComparer.Ordinal); // placeholder names are ordinal
        var clone = new XElement(row);
        foreach (var text in clone.Descendants(W + "t").ToList())
        {
            var before = _literalValues.RestoredLength(text.Value);
            text.Value = TemplatePlaceholders.ReplaceFields(text.Value, marker, data, ProtectValue);
            _budget.Charge(Math.Max(0, _literalValues.RestoredLength(text.Value) - before));
            text.SetAttributeValue(XNamespace.Xml + "space", "preserve");
        }

        return clone;
    }

    /// <summary>Appends blocks at the end of the body (before the closing <c>w:sectPr</c>).</summary>
    public void Append(XDocument document, IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        var body = Body(document);
        var generated = GenerateBlocks(blocks, baseParagraphProperties: null, baseRunProperties: null);
        if (generated.Count == 0)
        {
            return;
        }

        var sectionProperties = body.Elements(W + "sectPr").LastOrDefault();
        if (sectionProperties is not null)
        {
            foreach (var element in generated)
            {
                sectionProperties.AddBeforeSelf(element);
            }
        }
        else
        {
            body.Add(generated);
        }
    }

    private static XElement Body(XDocument document)
        => document.Root?.Element(W + "body")
            ?? throw new DocumentValidationException("The DOCX template contains no w:body.");

    /// <summary>Binds the placeholders of a paragraph; true if the paragraph was replaced.</summary>
    private void BindParagraph(
        XElement paragraph,
        IReadOnlyDictionary<string, TemplateContent> bindings,
        HashSet<string> found)
    {
        var textNodes = paragraph.Descendants(W + "t").ToList();
        if (textNodes.Count == 0)
        {
            return;
        }

        var cuts = new TemplateTextCuts(textNodes.Select(node => node.Value).ToList());
        var text = string.Concat(textNodes.Select(node => node.Value));
        ReportUnsetConditions(text);
        var (deletions, matches) = TemplatePlaceholders.Resolve(text, _conditions);
        if (deletions.Count == 0 && matches.Count == 0)
        {
            return;
        }

        // whole-paragraph placeholder: exactly one placeholder and otherwise only whitespace.
        if (deletions.Count == 0 && matches.Count == 1)
        {
            var (start, end, name) = matches[0];
            if (string.IsNullOrWhiteSpace(text[..start])
                && string.IsNullOrWhiteSpace(text[end..])
                && bindings.TryGetValue(name, out var content)
                && content.Kind == TemplateContentKind.Block)
            {
                ReplaceParagraph(paragraph, content.Blocks);
                found.Add(name);
                return;
            }
        }

        // Inline and conditional deletions from right to left so that the offsets remain valid.
        var operations = new List<(int Start, int End, string? Name)>();
        foreach (var (start, end) in deletions)
        {
            operations.Add((start, end, null));
        }

        foreach (var (start, end, name) in matches)
        {
            operations.Add((start, end, name));
        }

        operations.Sort((left, right) => right.Start.CompareTo(left.Start));

        foreach (var (start, end, name) in operations)
        {
            if (name is null)
            {
                ReplaceInline(paragraph, textNodes, cuts, start, end, Array.Empty<Inline>());
                continue;
            }

            if (!bindings.TryGetValue(name, out var content))
            {
                Report($"DOCX: placeholder '{{{{{name}}}}}' is unbound and remains.");
                continue;
            }

            if (content.Kind == TemplateContentKind.Block)
            {
                Report($"DOCX: placeholder '{{{{{name}}}}}' shares a paragraph with other text; block content is omitted.");
                continue;
            }

            ReplaceInline(paragraph, textNodes, cuts, start, end, content.Inlines);
            found.Add(name);
        }

        cuts.Apply((index, value) =>
        {
            textNodes[index].Value = value;
            if (NeedsSpacePreservation(value))
            {
                textNodes[index].SetAttributeValue(Xml + "space", "preserve"); // a cut can leave a space at the end
            }
        });
    }

    private void ReplaceParagraph(XElement paragraph, IReadOnlyList<Block> blocks)
    {
        var paragraphProperties = paragraph.Element(W + "pPr");
        var runProperties = paragraph.Descendants(W + "r").FirstOrDefault()?.Element(W + "rPr");
        var generated = GenerateBlocks(blocks, paragraphProperties, runProperties);

        // A paragraph with a section break (pPr/sectPr) is preserved; the content before it.
        if (paragraphProperties?.Element(W + "sectPr") is not null)
        {
            foreach (var element in generated)
            {
                element.Remove();
                paragraph.AddBeforeSelf(element);
            }

            foreach (var textNode in paragraph.Descendants(W + "t"))
            {
                textNode.Value = string.Empty;
            }

            Report("DOCX: a section break in the template paragraph is preserved; the content is placed before it.");
            return;
        }

        var parent = paragraph.Parent
            ?? throw new DocumentValidationException("DOCX: placeholder paragraph without a parent element.");

        foreach (var element in generated)
        {
            element.Remove();
            paragraph.AddBeforeSelf(element);
        }

        // A table cell must end with a paragraph.
        if (parent.Name == W + "tc" && generated.LastOrDefault()?.Name != W + "p")
        {
            paragraph.AddBeforeSelf(new XElement(W + "p"));
        }

        paragraph.Remove();
    }

    private void ReplaceInline(
        XElement paragraph,
        List<XElement> textNodes,
        TemplateTextCuts cuts,
        int start,
        int end,
        IReadOnlyList<Inline> inlines)
    {
        var (first, _, suffix) = cuts.Cut(start, end);
        var anchorRun = textNodes[first].Parent!;
        var baseRunProperties = anchorRun.Element(W + "rPr");

        XElement insertAfter = anchorRun;
        foreach (var run in GenerateInlines(inlines, baseRunProperties))
        {
            // Assigned nodes would be cloned by LINQ on insertion; detach them first.
            run.Remove();
            insertAfter.AddAfterSelf(run);
            insertAfter = run;
        }

        if (suffix.Length > 0)
        {
            insertAfter.AddAfterSelf(CreateTextRun(baseRunProperties, suffix));
        }
    }

    private List<XElement> GenerateInlines(IReadOnlyList<Inline> inlines, XElement? baseRunProperties)
    {
        var fragment = XmlFragment.Write(writer =>
        {
            foreach (var inline in inlines)
            {
                DocxPackageBuilder.WriteInline(writer, inline, _context);
            }
        }, FragmentNamespaces).OfType<XElement>().ToList();
        _budget.Charge(fragment.Sum(TemplateTextBudget.SizeOf));

        foreach (var run in fragment.Where(element => element.Name == W + "r"))
        {
            MergeProperties(run, baseRunProperties, RunPropertyOrder, "rPr", addFirst: true);
        }

        return fragment;
    }

    private List<XElement> GenerateBlocks(
        IReadOnlyList<Block> blocks,
        XElement? baseParagraphProperties,
        XElement? baseRunProperties)
    {
        var fragment = XmlFragment.Write(writer =>
        {
            foreach (var block in blocks)
            {
                DocxPackageBuilder.WriteBlock(writer, block, _contentWidth, _context);
            }
        }, FragmentNamespaces).OfType<XElement>().ToList();
        _budget.Charge(fragment.Sum(TemplateTextBudget.SizeOf));

        foreach (var element in fragment)
        {
            if (element.Name == W + "p")
            {
                MergeProperties(element, baseParagraphProperties, ParagraphPropertyOrder, "pPr", addFirst: true);
                if (baseRunProperties is not null)
                {
                    foreach (var run in element.Elements(W + "r"))
                    {
                        MergeProperties(run, baseRunProperties, RunPropertyOrder, "rPr", addFirst: true);
                    }
                }
            }
            else if (element.Name == W + "tbl" && baseParagraphProperties is not null)
            {
                // table cells do not inherit the template's paragraph style; only the border remains.
            }
        }

        return fragment;
    }

    private static XElement CreateTextRun(XElement? baseRunProperties, string text)
    {
        var run = new XElement(W + "r");
        if (baseRunProperties is not null)
        {
            run.Add(new XElement(baseRunProperties));
        }

        var textElement = new XElement(W + "t", text);
        if (NeedsSpacePreservation(text))
        {
            textElement.SetAttributeValue(Xml + "space", "preserve");
        }

        run.Add(textElement);
        return run;
    }

    private static bool NeedsSpacePreservation(string text)
        => text.Length > 0
            && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Any(character => character is '\t' or '\n' or '\r'));

    /// <summary>
    /// merges elements of a property group (<c>pPr</c>/<c>rPr</c>): the base
    /// supplies missing properties, the generated content wins, and the order follows
    /// dem Schema (CT_PPr/CT_RPr).
    /// </summary>
    private static void MergeProperties(
        XElement element,
        XElement? baseProperties,
        string[] order,
        string containerName,
        bool addFirst)
    {
        if (baseProperties is null)
        {
            return;
        }

        var container = element.Element(W + containerName);
        if (container is null)
        {
            container = new XElement(W + containerName);
            if (addFirst)
            {
                element.AddFirst(container);
            }
            else
            {
                element.Add(container);
            }
        }

        foreach (var property in baseProperties.Elements())
        {
            if (!container.Elements(property.Name).Any())
            {
                container.Add(new XElement(property));
            }
        }

        var ordered = container.Elements()
            .OrderBy(child => OrderOf(order, child.Name.LocalName))
            .ToList();
        container.RemoveNodes();
        container.Add(ordered);
    }

    private static int OrderOf(string[] order, string localName)
    {
        var index = Array.IndexOf(order, localName);
        return index < 0 ? int.MaxValue : index;
    }

    private void ExpandBlockConditions(XElement container)
        => TemplateBlockConditions.Apply(
            container.Elements().ToList(),
            TextOf,
            element => element.Name == W + "p",
            (element, text) =>
            {
                if (element.Name == W + "tbl")
                {
                    ExpandBlockConditionsInTable(element);
                }
                else if (element.Name == W + "p" && text == "{{EndIf}}")
                {
                    Report("DOCX: {{EndIf}} without a matching {{If}} is ignored.");
                }

                return false;
            },
            ReportUnclosedCondition,
            ConditionValue,
            mustStay: KeepSectionBreak);

    private void ExpandBlockConditionsInTable(XElement table)
        => TemplateBlockConditions.Apply(
            table.Elements(W + "tr").ToList(),
            TextOf,
            _ => true,
            (row, _) =>
            {
                foreach (var cell in row.Elements(W + "tc"))
                {
                    ExpandBlockConditions(cell);
                }

                return false;
            },
            ReportUnclosedCondition,
            ConditionValue);

    /// <summary>A section break inside a removed condition stays: its removal would merge two sections.</summary>
    private bool KeepSectionBreak(XElement element)
    {
        if (element.Element(W + "pPr")?.Element(W + "sectPr") is null)
        {
            return false;
        }

        element.Nodes().Where(node => node is not XElement { Name: var name } || name != W + "pPr").ToList().ForEach(node => node.Remove());
        Report("DOCX: a section break inside a removed condition is kept.");
        return true;
    }

    /// <summary>Removes tables without rows; a table cell has to end with a paragraph (ECMA-376).</summary>
    private void RemoveEmptyTablesAndCloseCells(XElement body)
    {
        var empty = body.Descendants(W + "tbl").Where(table => !table.Elements(W + "tr").Any()).ToHashSet();
        if (empty.Count > 0)
        {
            TemplateBlockConditions.Remove(empty);
            Report("DOCX: a table without rows after binding is removed.");
        }

        foreach (var cell in body.Descendants(W + "tc"))
        {
            if (cell.Elements().LastOrDefault()?.Name is var last && last != W + "p" && last != W + "sdt")
            {
                cell.Add(new XElement(W + "p"));
            }
        }
    }

    private void ReportUnclosedCondition(string condition)
        => Report($"DOCX: condition '{{{{If:{condition}}}}}' is not closed; the marker is ignored.");

    private bool ConditionValue(string condition)
    {
        if (_conditions.TryGetValue(condition, out var value))
        {
            return value;
        }

        if (_reported.Add("condition:" + condition))
        {
            Report("DOCX: condition '{{If:" + condition + "}}' is not set; false is used.");
        }

        return false;
    }

    private static string TextOf(XElement element)
        => string.Concat(element.Descendants(W + "t").Select(text => text.Value));

    private void ReportUnsetConditions(string text)
    {
        foreach (var (_, _, name) in TemplatePlaceholders.Find(text))
        {
            if (!name.StartsWith("If:", StringComparison.Ordinal)
                && !name.StartsWith("If ", StringComparison.Ordinal))
            {
                continue;
            }

            var condition = name[3..].Trim();
            if (condition.Length > 0
                && !_conditions.ContainsKey(condition)
                && _reported.Add("condition:" + condition))
            {
                Report("DOCX: condition '{{If:" + condition + "}}' is not set; false is used.");
            }
        }
    }

    private void Report(string message)
    {
        if (_reported.Add(message))
        {
            _handler.Report(message);
        }
    }
}
