using System.Xml.Linq;

namespace Reglet;

/// <summary>Expands standalone repeat markers within a common XML parent.</summary>
internal sealed class TemplateRegionExpander(
    IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> repeats,
    IReadOnlyDictionary<string, TemplateContent> bindings,
    Func<XElement, bool> candidate,
    Func<XElement, bool> tableCell,
    Func<XElement, IEnumerable<XText>> textNodes,
    Func<XElement, IEnumerable<XText>> cloneTextNodes,
    Func<XElement, bool, bool> unsafeToClone,
    Action<XElement> prepareAdditionalClone,
    Func<string, string> protect,
    Func<string, long> restoredLength,
    Action<string> report,
    ResourceLimits limits,
    Func<IReadOnlyList<XElement>, bool>? unsafeRegion = null,
    TemplateTextBudget? budget = null)
{
    private sealed class UnexpandedRegion { }

    private long _expandedBlocks;
    private long _expandedCells;
    private readonly HashSet<string> _knownRegionNames = CollectNames(repeats);
    private readonly Func<IReadOnlyList<XElement>, bool>? _unsafeRegion = unsafeRegion;
    private readonly TemplateTextBudget _budget = budget ?? new TemplateTextBudget(limits.MaxTemplateCharacters);

    public void Expand(XElement container) => Expand(container, repeats);

    private void Expand(XElement container, IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> scope)
    {
        ReaderGuard.EnsureStack();
        var position = 0;
        var elements = container.Elements().ToList();

        // Swapped in at the end: inserting or removing a single XML node walks the child list.
        var replaced = new Dictionary<XElement, IReadOnlyList<XElement>>();
        while (position < elements.Count)
        {
            var element = elements[position];
            if (IsProtected(element))
            {
                position++;
                continue;
            }

            var marker = Marker(element);
            if (marker is null || !marker.StartsWith("Repeat:", StringComparison.Ordinal))
            {
                if (marker is null)
                {
                    Expand(element, scope);
                }

                position++;
                continue;
            }

            var name = marker[7..].Trim();
            var end = FindEnd(elements, position);
            if (end < 0 || name.Length == 0 || !scope.TryGetValue(name, out var rows))
            {
                report($"Template: repeat region '{name}' is unclosed or has no data; it is left unchanged.");
                Preserve(elements, position, end < 0 ? elements.Count - 1 : end);
                position = end < 0 ? elements.Count : end + 1;
                continue;
            }

            var region = elements.Skip(position + 1).Take(end - position - 1).ToArray();
            if (unsafeToClone(element, true) || unsafeToClone(elements[end], true)
                || (rows.Count > 1 && (region.Any(item => unsafeToClone(item, false))
                    || _unsafeRegion?.Invoke(region) == true)))
            {
                report($"Template: repeat region '{name}' contains content that cannot safely be duplicated; it is left unchanged.");
                Preserve(elements, position, end);
                position = end + 1;
                continue;
            }

            var blockCount = region.Sum(element => (long)element.DescendantsAndSelf().Count(candidate));
            var cellCount = region.Sum(element => (long)element.DescendantsAndSelf().Count(tableCell));
            if (blockCount * rows.Count > limits.MaxBlocks - _expandedBlocks)
            {
                throw new DocumentValidationException($"Repeated template regions exceed the limit of {limits.MaxBlocks} blocks.");
            }

            if (cellCount * rows.Count > limits.MaxTableCells - _expandedCells)
            {
                throw new DocumentValidationException($"Repeated template regions exceed the limit of {limits.MaxTableCells} table cells.");
            }

            _expandedBlocks += blockCount * rows.Count;
            _expandedCells += cellCount * rows.Count;
            _budget.Charge(region.Sum(TemplateTextBudget.SizeOf) * rows.Count);
            var output = new List<XElement>();
            for (var rowIndex = 0; region.Length > 0 && rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];

                // Placeholder names compare ordinally, whatever comparer the data source used.
                var values = new Dictionary<string, string?>(row.Values, StringComparer.Ordinal);
                var clones = region.Select(source => new XElement(source)).ToArray();
                var wrapper = new XElement("region", clones);
                if (rowIndex > 0)
                {
                    prepareAdditionalClone(wrapper);
                }

                var nested = new Dictionary<string, IReadOnlyList<TemplateRegionRow>>(scope, StringComparer.Ordinal);
                foreach (var (childName, children) in row.Regions)
                {
                    nested[childName] = children;
                }

                Expand(wrapper, nested);
                foreach (var clone in wrapper.Elements().ToList())
                {
                    foreach (var text in cloneTextNodes(clone))
                    {
                        if (text.Parent is { } parent && IsProtected(parent))
                        {
                            continue;
                        }

                        var length = restoredLength(text.Value);
                        foreach (var (startIndex, endIndex, field) in TemplatePlaceholders.Find(text.Value))
                        {
                            if (values.TryGetValue(field, out var value) && value is not null)
                            {
                                length += (long)value.Length - (endIndex - startIndex);
                            }
                        }

                        if (length > limits.MaxTextLength)
                        {
                            throw new DocumentValidationException($"A repeated text run exceeds the limit of {limits.MaxTextLength} characters.");
                        }

                        _budget.Charge(Math.Max(0, length - restoredLength(text.Value)));
                        text.Value = TemplatePlaceholders.ReplaceFields(text.Value, string.Empty, values, protect);
                    }

                    clone.Remove();
                    output.Add(clone);
                }
            }

            replaced[element] = output;
            for (var index = position + 1; index <= end; index++)
            {
                replaced[elements[index]] = [];
            }

            position = end + 1;
        }

        if (replaced.Count > 0)
        {
            container.ReplaceNodes(container.Nodes()
                .SelectMany(node => node is XElement item && replaced.TryGetValue(item, out var clones) ? clones : (IEnumerable<XNode>)[node])
                .ToList());
        }
    }

    private static HashSet<string> CollectNames(IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> regions)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> groups)
        {
            ReaderGuard.EnsureStack();
            foreach (var (name, rows) in groups)
            {
                names.Add(name);
                foreach (var row in rows)
                {
                    Add(row.Regions);
                }
            }
        }

        Add(regions);
        return names;
    }

    public static bool IsProtected(XElement element)
        => element.AncestorsAndSelf().Any(node => node.Annotation<UnexpandedRegion>() is not null);

    public static bool ContainsProtected(XElement element)
        => IsProtected(element) || element.Descendants().Any(node => node.Annotation<UnexpandedRegion>() is not null);

    public static void Protect(XElement element) => element.AddAnnotation(new UnexpandedRegion());

    private static void Preserve(IReadOnlyList<XElement> elements, int start, int end)
    {
        for (var index = start; index <= end; index++)
        {
            Protect(elements[index]);
        }
    }

    private string? Marker(XElement element)
    {
        if (!candidate(element))
        {
            return null;
        }

        var text = string.Concat(textNodes(element).Select(node => node.Value)).Trim();
        if (!text.StartsWith("{{", StringComparison.Ordinal) || !text.EndsWith("}}", StringComparison.Ordinal))
        {
            return null;
        }

        var marker = text[2..^2];
        if (marker.Contains('{') || marker.Contains('}'))
        {
            return null;
        }

        if (marker.StartsWith("Repeat:", StringComparison.Ordinal) && !_knownRegionNames.Contains(marker[7..].Trim()) && bindings.ContainsKey(marker))
        {
            return null;
        }

        return marker == "EndRepeat" || marker.StartsWith("Repeat:", StringComparison.Ordinal) ? marker : null;
    }

    private int FindEnd(IReadOnlyList<XElement> elements, int start)
    {
        var depth = 0;
        for (var index = start + 1; index < elements.Count; index++)
        {
            var marker = Marker(elements[index]);
            if (marker?.StartsWith("Repeat:", StringComparison.Ordinal) == true)
            {
                depth++;
            }
            else if (marker == "EndRepeat")
            {
                if (depth-- == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }
}
