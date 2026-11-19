using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// resolves <c>{{If:Name}}</c> and <c>{{EndIf}}</c> markers that stand alone in sibling elements.
/// The markers are paired once and the elements are removed together at the end, because removing
/// a single XML node walks its parent's child list.
/// </summary>
internal static class TemplateBlockConditions
{
    /// <summary>Walks the elements and removes the markers and every region whose condition is false.</summary>
    /// <param name="elements">The elements in document order.</param>
    /// <param name="textOf">The text of an element.</param>
    /// <param name="isMarker">Whether an element can itself be a marker.</param>
    /// <param name="visit">Handles any other element with its text; returns whether to remove it.</param>
    /// <param name="unclosed">Reports a marker without an end.</param>
    /// <param name="keep">The value of a condition.</param>
    /// <param name="consumeMarker">Clears a kept marker; returns whether its element goes away as well.</param>
    /// <param name="mustStay">Whether an element of a removed region has to stay in the document.</param>
    /// <returns>Whether a closed region was found.</returns>
    public static bool Apply(
        IReadOnlyList<XElement> elements,
        Func<XElement, string> textOf,
        Func<XElement, bool> isMarker,
        Func<XElement, string, bool> visit,
        Action<string> unclosed,
        Func<string, bool> keep,
        Func<XElement, bool>? consumeMarker = null,
        Func<XElement, bool>? mustStay = null)
    {
        var texts = elements.Select(element => textOf(element).Trim()).ToList();
        var ends = MatchEnds(texts);
        var removed = new HashSet<XElement>();
        var matched = false;
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            if (removed.Contains(element) || TemplateRegionExpander.IsProtected(element))
            {
                continue;
            }

            if (!isMarker(element) || !TemplatePlaceholders.TryIfMarker(texts[index], out var condition))
            {
                if (visit(element, texts[index]))
                {
                    removed.Add(element);
                }

                continue;
            }

            var end = ends[index];
            if (end < 0)
            {
                unclosed(condition);
                continue;
            }

            matched = true;
            if (!keep(condition))
            {
                for (var item = index; item <= end; item++)
                {
                    if (mustStay?.Invoke(elements[item]) != true)
                    {
                        removed.Add(elements[item]);
                    }
                }

                index = end;
                continue;
            }

            foreach (var marker in new[] { elements[index], elements[end] })
            {
                if (consumeMarker?.Invoke(marker) != false)
                {
                    removed.Add(marker);
                }
            }

            if (!removed.Contains(element) && visit(element, string.Empty))
            {
                removed.Add(element);
            }
        }

        Remove(removed);
        return matched;
    }

    /// <summary>Removes elements from their parents in one pass per parent.</summary>
    public static void Remove(IReadOnlySet<XElement> removed)
    {
        foreach (var parent in removed.Select(element => element.Parent).Distinct().ToList())
        {
            parent?.ReplaceNodes(parent.Nodes().Where(node => node is not XElement element || !removed.Contains(element)).ToList());
        }
    }

    /// <summary>The index of the <c>{{EndIf}}</c> for each <c>{{If}}</c>, or -1 when it is not closed.</summary>
    private static int[] MatchEnds(IReadOnlyList<string> texts)
    {
        var ends = new int[texts.Count];
        Array.Fill(ends, -1);
        var open = new Stack<int>();
        for (var index = 0; index < texts.Count; index++)
        {
            if (TemplatePlaceholders.TryIfMarker(texts[index], out _))
            {
                open.Push(index);
            }
            else if (texts[index] == "{{EndIf}}" && open.TryPop(out var start))
            {
                ends[start] = index;
            }
        }

        return ends;
    }
}
