using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// binds the <c>{{Name}}</c> placeholders in the loaded <c>content.xml</c> of an ODT template.
/// inline content replaces the placeholder text within its paragraph, block content replaces
/// the whole paragraph. The paragraph style at the placeholder is applied to inserted paragraphs without their own
/// style is applied; inserting within a <c>text:span</c> preserves its text style.
/// </summary>
internal sealed class OdtTemplateBinder
{
    private static readonly XNamespace Office = OdtNamespaces.Office;
    private static readonly XNamespace Text = OdtNamespaces.Text;
    private static readonly XNamespace Table = OdtNamespaces.Table;
    private static readonly XNamespace Draw = OdtNamespaces.Draw;
    private static readonly XNamespace Svg = OdtNamespaces.Svg;
    private static readonly XNamespace XLink = OdtNamespaces.XLink;

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

    private readonly IReadOnlyDictionary<OdtTextStyleKey, string> _textStyles;
    private readonly OdtParagraphStyles _paragraphStyles;
    private readonly IReadOnlyDictionary<ImageBlock, OdtImageResource> _images;
    private readonly double _availableWidth;
    private readonly InvalidXmlCharacterBehavior _xmlBehavior;
    private readonly IReadOnlyDictionary<string, string> _renameMap;
    private readonly IReadOnlyDictionary<string, bool> _conditions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> _repeats;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> _regions;
    private readonly IReadOnlyList<NumberingDefinition> _numbering;
    private readonly UnsupportedFeatureHandler _handler;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly TemplateLiteralValues _literalValues = new();
    private readonly TemplateTextBudget _budget;
    private readonly ResourceLimits _limits;
    private readonly HashSet<string> _tableNames;
    private readonly HashSet<string> _bookmarkNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _duplicateBookmarkNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _bookmarkSuffixes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _noteIdSet = new(StringComparer.Ordinal);
    private readonly HashSet<string> _drawIdSet;
    private readonly XDocument _content;
    private readonly HashSet<string> _changeIdSet = new(StringComparer.Ordinal);

    private readonly OdtBlockState _state = new(0);
    private int _noteIds;
    private int _drawIds;
    private int _changeIds;

    public OdtTemplateBinder(
        XDocument content,
        IReadOnlyDictionary<OdtTextStyleKey, string> textStyles,
        OdtParagraphStyles paragraphStyles,
        IReadOnlyDictionary<ImageBlock, OdtImageResource> images,
        double availableWidth,
        InvalidXmlCharacterBehavior xmlBehavior,
        IReadOnlyDictionary<string, string> renameMap,
        IReadOnlyDictionary<string, bool> conditions,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> repeats,
        IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> regions,
        IReadOnlyList<NumberingDefinition> numbering,
        UnsupportedFeatureHandler handler,
        ResourceLimits limits)
    {
        _textStyles = textStyles;
        _paragraphStyles = paragraphStyles;
        _images = images;
        _availableWidth = availableWidth;
        _xmlBehavior = xmlBehavior;
        _renameMap = renameMap;
        _conditions = conditions;
        _repeats = repeats;
        _regions = regions;
        _numbering = numbering;
        _handler = handler;
        _limits = limits;
        _budget = new TemplateTextBudget(limits.MaxTemplateCharacters);
        _content = content;
        foreach (var region in content.Descendants(Text + "changed-region"))
        {
            if ((string?)region.Attribute(Text + "id") is { } id)
            {
                _changeIdSet.Add(id);
            }
        }

        _tableNames = content.Descendants(Table + "table")
            .Select(table => (string?)table.Attribute(Table + "name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal)!;
        _drawIdSet = content.Descendants()
            .Attributes(XNamespace.Xml + "id")
            .Select(attribute => attribute.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var bookmark in content.Descendants().Where(node => node.Name == Text + "bookmark" || node.Name == Text + "bookmark-start"))
        {
            var name = (string?)bookmark.Attribute(Text + "name");
            if (name is not null && !_bookmarkNames.Add(name)) _duplicateBookmarkNames.Add(name);
        }

        // A copied note gets a new text:id above the ids the template already uses (OF-D03).
        foreach (var note in content.Descendants(Text + "note"))
        {
            if (note.Attribute(Text + "id")?.Value is { } id)
            {
                _noteIdSet.Add(id);
            }
        }

        _noteIds = content.Descendants(Text + "note").Count() + 1;
    }

    /// <summary>Replaces all bound placeholders; returns the names actually replaced.</summary>
    public IReadOnlySet<string> Bind(XDocument content, IReadOnlyDictionary<string, TemplateContent> bindings)
        => Bind(Body(content), bindings);

    public IReadOnlySet<string> Bind(XElement body, IReadOnlyDictionary<string, TemplateContent> bindings)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        JoinSplitPlaceholders(body);
        if (_regions.Count > 0)
        {
            new TemplateRegionExpander(_regions, bindings, IsRegionElement, element => element.Name == Table + "table-cell", RegionText,
                element => element.DescendantNodes().OfType<XText>(), UnsafeToClone, PrepareAdditionalClone,
                ProtectValue, _literalValues.RestoredLength, Report, _limits).Expand(body);
        }
        ExpandRepeats(body);
        ExpandBlockConditions(body);
        var emptyTables = body.Descendants(Table + "table").Where(table => !table.Descendants(Table + "table-row").Any()).ToHashSet();
        if (emptyTables.Count > 0)
        {
            TemplateBlockConditions.Remove(emptyTables);
            Report("ODT: a table without rows after binding is removed.");
        }

        foreach (var paragraph in body.Descendants()
                     .Where(element => (element.Name == Text + "p" || element.Name == Text + "h") && !TemplateRegionExpander.IsProtected(element))
                     .ToList())
        {
            BindParagraph(paragraph, bindings, found);
        }

        foreach (var text in body.DescendantNodes().OfType<XText>().ToList())
        {
            var restored = _literalValues.Restore(text.Value);
            if (restored != text.Value)
            {
                // A value may hold line breaks, tabs and runs of spaces, which plain text cannot carry.
                text.ReplaceWith(XmlFragment.Write(writer => OdtContentWriter.WriteText(writer, restored, _xmlBehavior), FragmentNamespaces));
            }
        }

        return found;
    }

    private static bool IsRegionElement(XElement element)
        => element.Name == Text + "p" || element.Name == Text + "h" || element.Name == Table + "table-row";

    private static IEnumerable<XText> RegionText(XElement element)
        => element.DescendantNodes().OfType<XText>()
            .Where(text => element.Name != Table + "table-row" || text.Parent?.AncestorsAndSelf(Table + "table-row").FirstOrDefault() == element);

    private bool UnsafeToClone(XElement element, bool marker)
        => element.DescendantsAndSelf().Any(node => UnsafeNode(node, allowTables: false, marker));

    private bool UnsafeRowToClone(XElement element)
        => element.DescendantsAndSelf().Any(node => UnsafeNode(node, allowTables: true, marker: false));

    /// <summary>
    /// Whether an element must not be duplicated. Self-contained content is copied: a drawing
    /// (inline or anchored), a table (its name is made unique), an inline note (its body travels
    /// with it, only <c>text:id</c> is renumbered), a tracked-change range and a bookmark that
    /// closes inside the region (OF-D03). A section stays reported: its name and page setup must
    /// not be duplicated.
    /// </summary>
    private bool UnsafeNode(XElement node, bool allowTables, bool marker)
        => (node.Attribute(XNamespace.Xml + "id") is not null && node.Name.Namespace != Draw)
            || node.Name.Namespace == Draw && !IsSafeDrawingNode(node)
            || node.Name == Text + "section"
            || node.Name == Office + "annotation"
            || node.Name == Text + "bookmark" && (marker || string.IsNullOrEmpty((string?)node.Attribute(Text + "name"))
                || _duplicateBookmarkNames.Contains((string)node.Attribute(Text + "name")!))
            || node.Name.Namespace == Text && node.Name.LocalName is "bookmark-start" or "bookmark-end"
                or "reference-mark" or "reference-mark-start" or "reference-mark-end";

    private static bool IsSafeDrawingNode(XElement node)
    {
        if (node.Name == Draw + "image")
        {
            return node.Attribute(XLink + "href") is not null && !node.HasElements;
        }

        if (node.Name != Draw + "frame")
        {
            return true;
        }

        return node.Elements(Draw + "image").Count() <= 1;
    }

    private void PrepareAdditionalClone(XElement clone)
    {
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bookmark in clone.DescendantsAndSelf(Text + "bookmark"))
        {
            var oldName = (string)bookmark.Attribute(Text + "name")!;
            var number = _bookmarkSuffixes.GetValueOrDefault(oldName, 2);
            string name;
            do
            {
                if (number == int.MaxValue) throw new DocumentValidationException("ODT: no bookmark name is available.");
                name = oldName + "_" + number++;
            } while (!_bookmarkNames.Add(name));
            _bookmarkSuffixes[oldName] = number;
            bookmark.SetAttributeValue(Text + "name", name);
            renamed.Add(oldName, name);
        }
        foreach (var reference in clone.DescendantsAndSelf())
        {
            var attribute = reference.Name == Text + "bookmark-ref" ? reference.Attribute(Text + "ref-name")
                : reference.Name == Text + "a" ? reference.Attribute(XLink + "href") : null;
            if (attribute is null) continue;
            var isLink = reference.Name == Text + "a";
            if (isLink && !attribute.Value.StartsWith('#')) continue;
            var target = isLink ? attribute.Value[1..] : attribute.Value;
            if (renamed.TryGetValue(target, out var name)) attribute.Value = isLink ? "#" + name : name;
        }
        var renamedDrawIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in clone.DescendantsAndSelf().Where(element => element.Name.Namespace == Draw))
        {
            element.Attribute(Draw + "name")?.Remove();
            element.Attribute(Draw + "id")?.Remove();
            if (element.Attribute(XNamespace.Xml + "id") is { } id)
            {
                string name;
                do
                {
                    name = "draw" + _drawIds++.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                while (!_drawIdSet.Add(name));

                renamedDrawIds[id.Value] = name;
                id.Value = name;
            }
        }

        if (renamedDrawIds.Count > 0)
        {
            foreach (var reference in clone.DescendantsAndSelf())
            {
                foreach (var attributeName in new[] { Draw + "copy-of", Draw + "caption-id" })
                {
                    if (reference.Attribute(attributeName) is { } attribute
                        && renamedDrawIds.TryGetValue(attribute.Value, out var renamedId))
                    {
                        attribute.Value = renamedId;
                    }
                }
            }
        }

        // OF-D03: a tracked-change range gets a fresh region in the document's tracked changes.
        var changedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var marker in clone.Descendants()
            .Where(item => item.Name == Text + "change-start" || item.Name == Text + "change-end" || item.Name == Text + "change")
            .ToList())
        {
            var oldId = (string?)marker.Attribute(Text + "change-id");
            if (oldId is null)
            {
                continue;
            }

            if (!changedIds.TryGetValue(oldId, out var newId))
            {
                var region = _content.Descendants(Text + "changed-region")
                    .FirstOrDefault(item => (string?)item.Attribute(Text + "id") == oldId);
                if (region is null)
                {
                    Report($"ODT: the changed region '{oldId}' in a repeated region is missing; the marker remains shared.");
                    continue;
                }

                do
                {
                    newId = "ct" + _changeIds++.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                while (!_changeIdSet.Add(newId));

                var regionClone = new XElement(region);
                regionClone.SetAttributeValue(Text + "id", newId);
                if (!AppendChangedRegion(regionClone))
                {
                    continue;
                }

                changedIds[oldId] = newId;
            }

            marker.SetAttributeValue(Text + "change-id", newId);
        }

        // OF-D03: a table copies with a unique name, an inline note with a unique text:id.
        EnsureUniqueTableNames([clone]);
        foreach (var note in clone.DescendantsAndSelf(Text + "note"))
        {
            var endnote = (string?)note.Attribute(Text + "note-class") == "endnote";
            var prefix = endnote ? "end" : "ftn";
            string id;
            do
            {
                id = prefix + _noteIds++;
            } while (!_noteIdSet.Add(id));
            note.SetAttributeValue(Text + "id", id);
        }
    }

    /// <summary>Appends a copied changed region to the document's tracked changes.</summary>
    private bool AppendChangedRegion(XElement region)
    {
        var textBody = _content.Root?.Descendants(Office + "text").FirstOrDefault();
        if (textBody is null)
        {
            Report("ODT: a tracked-change region in a band cannot be duplicated.");
            return false;
        }

        var tracked = textBody.Element(Text + "tracked-changes");
        if (tracked is null)
        {
            tracked = new XElement(Text + "tracked-changes");
            textBody.Add(tracked);
        }

        tracked.Add(region);
        return true;
    }

    /// <summary>Joins placeholders that were split over several spans, so rows and markers are found.</summary>
    private static void JoinSplitPlaceholders(XElement body)
    {
        foreach (var paragraph in body.Descendants().Where(element => element.Name == Text + "p" || element.Name == Text + "h"))
        {
            var nodes = paragraph.DescendantNodes().OfType<XText>().ToList();
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
                nodes[index].Value = values[index];
            }
        }
    }

    /// <summary>Repeats the table row containing a row-repeat placeholder (phase AB, AB03).</summary>
    private void ExpandRepeats(XElement body)
    {
        foreach (var (placeholder, rows) in _repeats)
        {
            var marker = DocumentTemplate.Marker(placeholder);
            foreach (var row in body.Descendants(Table + "table-row").ToList())
            {
                if (TemplateRegionExpander.ContainsProtected(row))
                {
                    continue;
                }

                if (!row.DescendantNodes().OfType<XText>().Any(text => text.Parent?.AncestorsAndSelf(Table + "table-row").FirstOrDefault() == row
                    && text.Value.Contains(marker, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (rows.Count > 1 && UnsafeRowToClone(row))
                {
                    Report($"ODT: repeated row '{placeholder}' contains content that cannot safely be duplicated; it is left unchanged.");
                    TemplateRegionExpander.Protect(row);
                    continue;
                }

                var clones = rows.Select(data => CloneRow(row, marker, data)).ToList<XNode>();
                foreach (var clone in clones.Skip(1).OfType<XElement>())
                {
                    EnsureUniqueTableNames([clone]);
                    PrepareAdditionalClone(clone);
                }

                row.ReplaceWith(clones);
            }
        }
    }

    /// <summary>Makes a data value safe for XML and keeps it opaque until binding has finished.</summary>
    private string ProtectValue(string value) => _literalValues.Protect(XmlText.Clean(value, _xmlBehavior));

    private XElement CloneRow(XElement row, string marker, IReadOnlyDictionary<string, string?> data)
    {
        _budget.Charge(TemplateTextBudget.SizeOf(row));
        data = new Dictionary<string, string?>(data, StringComparer.Ordinal); // placeholder names are ordinal
        var clone = new XElement(row);
        foreach (var text in clone.DescendantNodes().OfType<XText>().ToList())
        {
            var before = _literalValues.RestoredLength(text.Value);
            text.Value = TemplatePlaceholders.ReplaceFields(text.Value, marker, data, ProtectValue);
            _budget.Charge(Math.Max(0, _literalValues.RestoredLength(text.Value) - before));
        }

        return clone;
    }

    /// <summary>Appends blocks at the end of the body.</summary>
    public void Append(XDocument content, IReadOnlyList<Block> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        var body = Body(content);
        var generated = GenerateBlocks(blocks);
        if (generated.Count == 0)
        {
            return;
        }

        foreach (var section in body.Elements(Text + "section").Reverse())
        {
            section.Add(generated);
            return;
        }

        body.Add(generated);
    }

    private static XElement Body(XDocument content)
        => content.Root?.Element(Office + "body")?.Element(Office + "text")
            ?? throw new DocumentValidationException("The ODT template contains no body (office:text).");

    private void BindParagraph(XElement paragraph, IReadOnlyDictionary<string, TemplateContent> bindings, HashSet<string> found)
    {
        var textNodes = paragraph.DescendantNodes().OfType<XText>().ToList();
        if (textNodes.Count == 0)
        {
            return;
        }

        var cuts = new TemplateTextCuts(textNodes.Select(node => node.Value).ToList());
        var tails = new List<(int Index, string Text)>();
        var text = string.Concat(textNodes.Select(node => node.Value));
        ReportUnsetConditions(text);
        var (deletions, matches) = TemplatePlaceholders.Resolve(text, _conditions);
        if (deletions.Count == 0 && matches.Count == 0)
        {
            return;
        }

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
                ReplaceInline(textNodes, cuts, tails, start, end, Array.Empty<Inline>());
                continue;
            }

            if (!bindings.TryGetValue(name, out var content))
            {
                Report($"ODT: placeholder '{{{{{name}}}}}' is unbound and remains.");
                continue;
            }

            if (content.Kind == TemplateContentKind.Block)
            {
                Report($"ODT: placeholder '{{{{{name}}}}}' shares a paragraph with other text; block content is omitted.");
                continue;
            }

            ReplaceInline(textNodes, cuts, tails, start, end, content.Inlines);
            found.Add(name);
        }

        cuts.Apply((index, value) => textNodes[index].Value = value);
        foreach (var (index, value) in tails)
        {
            textNodes[index].Value = value;
        }
    }

    private void ReplaceParagraph(XElement paragraph, IReadOnlyList<Block> blocks)
    {
        var baseStyle = (string?)paragraph.Attribute(Text + "style-name");
        var generated = GenerateBlocks(blocks);

        if (baseStyle is not null)
        {
            foreach (var element in generated)
            {
                if ((element.Name == Text + "p" || element.Name == Text + "h")
                    && element.Attribute(Text + "style-name") is null)
                {
                    element.SetAttributeValue(Text + "style-name", baseStyle);
                }
            }
        }

        var parent = paragraph.Parent
            ?? throw new DocumentValidationException("ODT: placeholder paragraph without a parent element.");

        if (parent.Name == Text + "list-item" && generated.Any(element => element.Name == Table + "table"))
        {
            ReplaceInListItem(paragraph, parent, generated);
            return;
        }

        foreach (var element in generated)
        {
            element.Remove();
            paragraph.AddBeforeSelf(element);
        }

        if (parent.Name == Table + "table-cell" && generated.LastOrDefault()?.Name != Text + "p")
        {
            paragraph.AddBeforeSelf(new XElement(Text + "p"));
        }

        paragraph.Remove();
    }

    /// <summary>
    /// ODF has no table in a list item (LibreOffice drops it), so the list ends before the table
    /// and continues after it.
    /// </summary>
    private void ReplaceInListItem(XElement paragraph, XElement item, List<XElement> generated)
    {
        var list = item.Parent!;
        var firstTable = generated.FindIndex(element => element.Name == Table + "table");
        foreach (var element in generated.Take(firstTable))
        {
            element.Remove();
            paragraph.AddBeforeSelf(element);
        }

        var rest = generated.Skip(firstTable).ToList();
        foreach (var element in rest)
        {
            element.Remove();
        }

        var outer = list.AncestorsAndSelf(Text + "list").Last();
        if (outer != list)
        {
            // shortcut: from a nested list the content moves behind the whole list; split each level if that order matters.
            Report("ODT: a table in a nested list item is moved behind the list.");
            paragraph.Remove();
            outer.AddAfterSelf(rest);
            return;
        }

        var tail = new XElement(list.Name, list.Attributes().Where(attribute => attribute.Name != XNamespace.Xml + "id"));
        tail.SetAttributeValue(Text + "continue-numbering", "true");
        var remainder = paragraph.NodesAfterSelf().ToList();
        if (remainder.Count > 0)
        {
            remainder.ForEach(node => node.Remove());
            tail.Add(new XElement(Text + "list-item", remainder));
        }

        var following = item.ElementsAfterSelf().ToList();
        following.ForEach(node => node.Remove());
        tail.Add(following);
        paragraph.Remove();
        if (!item.HasElements)
        {
            item.Remove();
        }

        if (tail.HasElements)
        {
            list.AddAfterSelf(tail);
        }

        list.AddAfterSelf(rest);
        if (!list.HasElements)
        {
            list.Remove();
        }
    }

    /// <summary>Generated notes and bookmarks must not take an id or name the template already uses.</summary>
    private void MakeNamesUnique(IEnumerable<XNode> fragment)
    {
        var elements = fragment.OfType<XElement>().SelectMany(element => element.DescendantsAndSelf()).ToList();
        foreach (var note in elements.Where(element => element.Name == Text + "note"))
        {
            if (note.Attribute(Text + "id")?.Value is { } current && !_noteIdSet.Add(current))
            {
                var prefix = (string?)note.Attribute(Text + "note-class") == "endnote" ? "end" : "ftn";
                string id;
                do
                {
                    id = prefix + _noteIds++;
                }
                while (!_noteIdSet.Add(id));

                note.SetAttributeValue(Text + "id", id);
            }
        }

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bookmark in elements.Where(element => element.Name == Text + "bookmark" || element.Name == Text + "bookmark-start"))
        {
            var oldName = (string?)bookmark.Attribute(Text + "name");
            if (oldName is null || _bookmarkNames.Add(oldName))
            {
                continue;
            }

            var number = _bookmarkSuffixes.GetValueOrDefault(oldName, 2);
            string name;
            do
            {
                name = oldName + "_" + number++;
            }
            while (!_bookmarkNames.Add(name));

            _bookmarkSuffixes[oldName] = number;
            renamed[oldName] = name;
            bookmark.SetAttributeValue(Text + "name", name);
            Report($"ODT: the bookmark '{oldName}' already exists in the template; the inserted one is named '{name}'.");
        }

        foreach (var reference in elements.Where(element => renamed.Count > 0))
        {
            var attribute = reference.Name == Text + "bookmark-end" ? reference.Attribute(Text + "name")
                : reference.Name == Text + "bookmark-ref" ? reference.Attribute(Text + "ref-name")
                : reference.Name == Text + "a" ? reference.Attribute(XLink + "href") : null;
            if (attribute is null)
            {
                continue;
            }

            var isLink = reference.Name == Text + "a" && attribute.Value.StartsWith('#');
            if (renamed.TryGetValue(isLink ? attribute.Value[1..] : attribute.Value, out var name))
            {
                attribute.Value = isLink ? "#" + name : name;
            }
        }
    }

    private void ReplaceInline(List<XText> textNodes, TemplateTextCuts cuts, List<(int Index, string Text)> tails, int start, int end, IReadOnlyList<Inline> inlines)
    {
        var (first, last, suffix) = cuts.Cut(start, end);

        XNode insertAfter = textNodes[first];
        foreach (var node in GenerateInlines(inlines))
        {
            // Assigned nodes would be cloned by LINQ on insertion; detach them first so that the
            // inserted nodes can be reused themselves.
            node.Remove();
            insertAfter.AddAfterSelf(node);
            insertAfter = node;
        }
        if (first != last)
        {
            // The last node carries the remainder; nodes in between become empty.
            tails.Add((last, suffix));
        }
        else if (suffix.Length > 0)
        {
            insertAfter.AddAfterSelf(new XText(suffix));
        }
    }

    private List<XElement> GenerateBlocks(IReadOnlyList<Block> blocks)
    {
        var fragment = XmlFragment.Write(writer =>
            OdtContentWriter.WriteTemplateBlocks(
                writer, blocks, _textStyles, _paragraphStyles, _images, _availableWidth, _xmlBehavior, _state,
                new NumberingLookup(_numbering)),
            FragmentNamespaces)
            .OfType<XElement>()
            .ToList();

        _budget.Charge(fragment.Sum(TemplateTextBudget.SizeOf));
        ApplyRenames(fragment);
        EnsureUniqueTableNames(fragment);
        MakeNamesUnique(fragment);
        return fragment;
    }

    private List<XNode> GenerateInlines(IReadOnlyList<Inline> inlines)
    {
        var fragment = XmlFragment.Write(
            writer => OdtContentWriter.WriteInlines(writer, inlines, _textStyles, _xmlBehavior, _paragraphStyles),
            FragmentNamespaces);
        _budget.Charge(fragment.Sum(node => node is XElement element ? TemplateTextBudget.SizeOf(element) : node is XText text ? text.Value.Length : 0L));
        ApplyRenames(fragment);
        MakeNamesUnique(fragment);
        return fragment;
    }

    private void ApplyRenames(IEnumerable<XNode> fragment)
    {
        if (_renameMap.Count == 0)
        {
            return;
        }

        foreach (var element in fragment.OfType<XElement>())
        {
            foreach (var attribute in element.DescendantsAndSelf().Attributes())
            {
                if (attribute.Name.LocalName == "style-name" && _renameMap.TryGetValue(attribute.Value, out var mapped))
                {
                    attribute.Value = mapped;
                }
            }
        }
    }

    private void EnsureUniqueTableNames(List<XElement> fragment)
    {
        foreach (var table in fragment.SelectMany(element => element.DescendantsAndSelf()))
        {
            if (table.Name != Table + "table")
            {
                continue;
            }

            if (table.Attribute(Table + "name") is not { } name)
            {
                continue;
            }

            if (_tableNames.Add(name.Value))
            {
                continue;
            }

            for (var index = 2; ; index++)
            {
                var candidate = name.Value + index;
                if (_tableNames.Add(candidate))
                {
                    name.Value = candidate;
                    break;
                }
            }
        }
    }

    private void ExpandBlockConditions(XElement container)
        => TemplateBlockConditions.Apply(
            container.Elements().ToList(),
            TextOf,
            element => element.Name == Text + "p" || element.Name == Text + "h",
            (element, text) =>
            {
                if (element.Name == Text + "section")
                {
                    ExpandBlockConditions(element);
                }
                else if (element.Name == Table + "table")
                {
                    ExpandBlockConditionsInTable(element);
                }
                else if (element.Name == Text + "list")
                {
                    ExpandBlockConditionsInList(element);
                    return !element.Elements(Text + "list-item").Any();
                }
                else if ((element.Name == Text + "p" || element.Name == Text + "h") && text == "{{EndIf}}")
                {
                    Report("ODT: {{EndIf}} without a matching {{If}} is ignored.");
                }

                return false;
            },
            ReportUnclosedCondition,
            ConditionValue);

    private void ExpandBlockConditionsInTable(XElement table)
        => TemplateBlockConditions.Apply(
            // Rows of nested tables are handled through their cell.
            table.Descendants(Table + "table-row").Where(row => row.Ancestors(Table + "table").First() == table).ToList(),
            TextOf,
            _ => true,
            (row, _) =>
            {
                foreach (var cell in row.Elements(Table + "table-cell"))
                {
                    ExpandBlockConditions(cell);
                }

                return false;
            },
            ReportUnclosedCondition,
            ConditionValue);

    private void ExpandBlockConditionsInList(XElement list)
    {
        var matched = TemplateBlockConditions.Apply(
            list.Elements(Text + "list-item").ToList(),
            TextOf,
            _ => true,
            (item, _) =>
            {
                ExpandBlockConditions(item);
                return false;
            },
            ReportUnclosedCondition,
            ConditionValue,
            item =>
            {
                RemoveMarkerParagraph(item);
                return !item.Elements().Any();
            });

        if (matched)
        {
            TemplateBlockConditions.Remove(list.Elements(Text + "list-item").Where(item => !item.Elements().Any()).ToHashSet());
        }
    }

    private static void RemoveMarkerParagraph(XElement item)
    {
        foreach (var paragraph in item.Elements(Text + "p").Concat(item.Elements(Text + "h")).ToList())
        {
            var text = paragraph.Value.Trim();
            if (text == "{{EndIf}}" || TemplatePlaceholders.TryIfMarker(text, out _))
            {
                paragraph.Remove();
            }
        }
    }

    private void ReportUnclosedCondition(string condition)
        => Report($"ODT: condition '{{{{If:{condition}}}}}' is not closed; the marker is ignored.");

    private bool ConditionValue(string condition)
    {
        if (_conditions.TryGetValue(condition, out var value))
        {
            return value;
        }

        if (_reported.Add("condition:" + condition))
        {
            Report("ODT: condition '{{If:" + condition + "}}' is not set; false is used.");
        }

        return false;
    }

    private static string TextOf(XElement element)
        => string.Concat(element.DescendantsAndSelf()
            .Where(node => node.Name == Text + "p" || node.Name == Text + "h")
            .Select(node => string.Concat(node.DescendantNodes().OfType<XText>().Select(text => text.Value))));

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
                Report("ODT: condition '{{If:" + condition + "}}' is not set; false is used.");
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
