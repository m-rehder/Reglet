namespace Reglet;

/// <summary>
/// detects <c>{{Name}}</c> placeholders in contiguous text. The DOCX and ODT binders
/// use the same detection so that the syntax does not diverge.
/// </summary>
internal static class TemplatePlaceholders
{
    /// <summary>
    /// replaces the placeholders of one repeated row in a single pass: a value that itself
    /// contains <c>{{Name}}</c> stays literal. Unknown or null fields keep their placeholder.
    /// </summary>
    public static string ReplaceFields(string text, string rowMarker, IReadOnlyDictionary<string, string?> data, Func<string, string>? protect = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (rowMarker.Length > 0)
        {
            text = text.Replace(rowMarker, string.Empty, StringComparison.Ordinal);
        }
        var builder = new System.Text.StringBuilder(text.Length);
        var position = 0;
        foreach (var (start, end, name) in Find(text))
        {
            if (data.TryGetValue(name, out var value) && value is not null)
            {
                builder.Append(text, position, start - position).Append(protect is null ? value : protect(value));
                position = end;
            }
        }

        return builder.Append(text, position, text.Length - position).ToString();
    }

    /// <summary>
    /// returns all placeholders in <paramref name="text"/> as (start, end, name) in
    /// document order. Unclosed or empty markers are skipped.
    /// </summary>
    public static List<(int Start, int End, string Name)> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var matches = new List<(int, int, string)>();
        var index = 0;

        while (index < text.Length)
        {
            var open = text.IndexOf(DocumentTemplate.OpeningDelimiter, index, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var nameStart = open + DocumentTemplate.OpeningDelimiter.Length;
            var close = text.IndexOf(DocumentTemplate.ClosingDelimiter, nameStart, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var name = text[nameStart..close];
            if (name.Length > 0 && !name.Contains('{') && !name.Contains('}'))
            {
                matches.Add((open, close + DocumentTemplate.ClosingDelimiter.Length, name));
            }

            index = close + DocumentTemplate.ClosingDelimiter.Length;
        }

        return matches;
    }

    /// <summary>
    /// Moves every placeholder that is split over several text nodes (Word and LibreOffice
    /// split runs on spelling marks, revision ids or formatting changes) completely into the
    /// node where it starts. Text outside the placeholders stays in its node, so its formatting
    /// is preserved. Returns whether a value changed.
    /// </summary>
    public static bool JoinSplitPlaceholders(string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var text = string.Concat(values);
        var offsets = new int[values.Length];
        for (int index = 0, total = 0; index < values.Length; index++)
        {
            offsets[index] = total;
            total += values[index].Length;
        }

        // Later matches only change nodes behind the earlier ones, so the lookup can stay fixed.
        var nodes = Enumerable.Range(0, values.Length).Where(index => values[index].Length > 0).ToArray();
        var starts = nodes.Select(index => offsets[index]).ToArray();
        var changed = false;
        var matches = Find(text);
        for (var match = matches.Count - 1; match >= 0; match--)
        {
            var (start, end, _) = matches[match];
            var first = NodeAt(nodes, starts, start);
            var last = NodeAt(nodes, starts, end - 1);
            if (first == last)
            {
                continue;
            }

            var tail = values[last][(end - offsets[last])..];
            values[first] = values[first][..(start - offsets[first])] + text[start..end];
            for (var index = first + 1; index < last; index++)
            {
                values[index] = string.Empty;
            }

            values[last] = tail;
            changed = true;
        }

        return changed;

        static int NodeAt(int[] nodes, int[] starts, int position)
        {
            var index = Array.BinarySearch(starts, position);
            index = index < 0 ? ~index - 1 : index;
            return index < 0 ? 0 : nodes[index];
        }
    }

    /// <summary>
    /// resolves <c>{{If Name}}…{{EndIf}}</c> regions (phase AB, AB02) and the remaining
    /// placeholders. Returns the ranges to delete and the placeholders to bind (both as
    /// offsets into <paramref name="text"/>).
    /// </summary>
    public static (List<(int Start, int End)> Deletions, List<(int Start, int End, string Name)> Placeholders) Resolve(
        string text,
        IReadOnlyDictionary<string, bool>? conditions)
    {
        var matches = Find(text);
        var deletions = new List<(int, int)>();
        var placeholders = new List<(int, int, string)>();
        var stack = new List<(int Start, int MarkerEnd, bool Keep)>();
        var dropped = 0; // open regions whose content goes away

        foreach (var (start, end, name) in matches)
        {
            if (name.StartsWith("If:", StringComparison.Ordinal) || name.StartsWith("If ", StringComparison.Ordinal))
            {
                var conditionName = name[3..].Trim();
                var keep = conditions is not null && conditions.TryGetValue(conditionName, out var value) && value;
                stack.Add((start, end, keep));
                dropped += keep ? 0 : 1;
                continue;
            }

            if (name == "EndIf")
            {
                if (stack.Count == 0)
                {
                    deletions.Add((start, end));
                    continue;
                }

                var region = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                dropped -= region.Keep ? 0 : 1;
                if (!region.Keep)
                {
                    // An enclosing region that is removed as a whole already covers this one;
                    // overlapping deletions would shift the offsets of the outer one.
                    if (dropped == 0)
                    {
                        deletions.Add((region.Start, end));
                    }
                }
                else if (dropped == 0)
                {
                    deletions.Add((region.Start, region.MarkerEnd));
                    deletions.Add((start, end));
                }

                continue;
            }

            if (dropped == 0)
            {
                placeholders.Add((start, end, name));
            }
        }

        foreach (var region in stack)
        {
            deletions.Add((region.Start, region.MarkerEnd));
        }

        return (deletions, placeholders);
    }
    /// <summary>Whether a text is exactly an <c>{{If:Name}}</c> marker.</summary>
    public static bool TryIfMarker(string text, out string condition)
    {
        condition = string.Empty;
        if (text.Length < 6
            || !text.StartsWith("{{If", StringComparison.Ordinal)
            || !text.EndsWith("}}", StringComparison.Ordinal)
            || (text[4] != ':' && text[4] != ' '))
        {
            return false;
        }

        var marker = text[4..^2].TrimStart(':', ' ').Trim();

        // "{{If:a}}text{{EndIf}}" is a whole condition in one block, not a marker.
        if (marker.Length == 0 || marker.Contains('{') || marker.Contains('}'))
        {
            return false;
        }

        condition = marker;
        return true;
    }
}
