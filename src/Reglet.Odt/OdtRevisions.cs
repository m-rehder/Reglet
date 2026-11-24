using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Tracked changes in ODT (ADR-0027). ODF keeps the changes in <c>text:tracked-changes</c> at the start of the
/// text and marks them in the text: an insertion by <c>text:change-start</c>/<c>text:change-end</c>, a deletion by
/// a <c>text:change</c> point (the deleted text lives in the region). The regions come first in the file, so a
/// plan over the body is made before anything is written; the writer then follows the plan for each paragraph.
/// </summary>
internal static class OdtRevisions
{
    /// <summary>One step in a paragraph: an inline, or the start, end or point of a changed region.</summary>
    internal abstract record Op;

    internal sealed record Content(Inline Inline) : Op;

    internal sealed record Start(string Name) : Op;

    internal sealed record End(string Name) : Op;

    internal sealed record Point(string Name) : Op;

    /// <summary>A changed region; a deletion carries the deleted inlines of its paragraph.</summary>
    internal sealed record Region(string Name, TrackedChange Change, List<Inline> Deleted);

    /// <summary>What the writer does with the paragraphs of the body, and the reports of the planning.</summary>
    internal sealed class Plan
    {
        public List<Region> Regions { get; } = [];

        /// <summary>The steps of every paragraph that contains a change or lies inside a deletion, by its inline list.</summary>
        public Dictionary<IReadOnlyList<Inline>, List<Op>> Paragraphs { get; } = new(ReferenceEqualityComparer.Instance);

        public List<string> Messages { get; } = [];
    }

    private sealed class Open(TrackedChange change, Region region)
    {
        public TrackedChange Change { get; } = change;

        public Region Region { get; set; } = region;
    }

    /// <summary>Plans the tracked changes of the body.</summary>
    public static Plan MakePlan(PortableDocument document)
    {
        var info = RevisionScan.Collect(document);
        var plan = new Plan();
        if (info.Elsewhere > 0)
        {
            plan.Messages.Add("ODT: a tracked change in a header, footer or note is not written.");
        }

        if (info.Body.Count == 0)
        {
            return plan;
        }

        var state = new State(plan, new HashSet<TrackedChange>(info.Body, ReferenceEqualityComparer.Instance), info.EndedIds);
        foreach (var section in document.Sections)
        {
            state.Blocks(section.Blocks);
        }

        return plan;
    }

    private sealed class State(Plan plan, HashSet<TrackedChange> body, IReadOnlySet<int> ended)
    {
        private readonly List<Open> _open = [];
        private readonly HashSet<int> _dropped = [];
        private int _next;

        public void Blocks(IReadOnlyList<Block> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        Paragraph(paragraph.Inlines);
                        break;
                    case Heading heading:
                        Paragraph(heading.Inlines);
                        break;
                    case ListBlock list:
                        foreach (var item in list.Items)
                        {
                            Blocks(item.Blocks);
                        }

                        break;
                    case TableBlock table:
                        foreach (var cell in table.Rows.SelectMany(row => row.Cells))
                        {
                            Blocks(cell.Blocks);
                        }

                        break;
                }
            }
        }

        private Region NewRegion(TrackedChange change)
        {
            var region = new Region("ct" + (++_next).ToString(CultureInfo.InvariantCulture), change, []);
            plan.Regions.Add(region);
            return region;
        }

        private static bool Touches(IReadOnlyList<Inline> inlines)
            => inlines.Any(inline => inline is TrackedChange or TrackedChangeEnd
                || inline is Hyperlink link && Touches(link.Inlines));

        private void Paragraph(IReadOnlyList<Inline> inlines)
        {
            var deleting = _open.Any(open => open.Change.Kind == TrackedChangeKind.Deletion);
            if (!deleting && !Touches(inlines))
            {
                return;
            }

            var flat = Flatten(inlines, deleting);
            var ops = new List<Op>();

            // A deletion that goes on from the previous paragraph is a region of its own here.
            foreach (var open in _open.Where(open => open.Change.Kind == TrackedChangeKind.Deletion))
            {
                open.Region = NewRegion(open.Change);
                ops.Add(new Point(open.Region.Name));
            }

            foreach (var inline in flat)
            {
                switch (inline)
                {
                    case TrackedChange change when !body.Contains(change) || !ended.Contains(change.Id):
                        if (body.Contains(change))
                        {
                            plan.Messages.Add($"ODT: the tracked change {change.Id} has no end; it is omitted.");
                            _dropped.Add(change.Id);
                        }

                        break;
                    case TrackedChange change when _open.Any(open => open.Change.Kind == TrackedChangeKind.Deletion):
                        plan.Messages.Add("ODT: a tracked change inside a deleted text is not written.");
                        _dropped.Add(change.Id);
                        break;
                    case TrackedChange change:
                    {
                        var region = NewRegion(change);
                        ops.Add(change.Kind == TrackedChangeKind.Insertion ? new Start(region.Name) : new Point(region.Name));
                        _open.Add(new Open(change, region));
                        break;
                    }

                    case TrackedChangeEnd end:
                    {
                        var index = _open.FindIndex(open => open.Change.Id == end.Id);
                        if (index >= 0)
                        {
                            if (_open[index].Change.Kind == TrackedChangeKind.Insertion)
                            {
                                ops.Add(new End(_open[index].Region.Name));
                            }

                            _open.RemoveAt(index);
                        }
                        else if (!_dropped.Remove(end.Id))
                        {
                            plan.Messages.Add($"ODT: the end of the tracked change {end.Id} has no start; it is omitted.");
                        }

                        break;
                    }

                    default:
                        if (_open.LastOrDefault(open => open.Change.Kind == TrackedChangeKind.Deletion) is { } deletion)
                        {
                            deletion.Region.Deleted.Add(inline);
                        }
                        else
                        {
                            ops.Add(new Content(inline));
                        }

                        break;
                }
            }

            plan.Paragraphs[inlines] = ops;
        }

        /// <summary>A hyperlink that holds a change, or lies inside a deletion, is replaced by its content (the link is dropped).</summary>
        private List<Inline> Flatten(IReadOnlyList<Inline> inlines, bool deleting)
        {
            var result = new List<Inline>();
            foreach (var inline in inlines)
            {
                if (inline is Hyperlink link && (deleting || Touches(link.Inlines)))
                {
                    plan.Messages.Add("ODT: a hyperlink inside or across a tracked change is written as text.");
                    result.AddRange(Flatten(link.Inlines, deleting));
                }
                else
                {
                    result.Add(inline);
                }
            }

            return result;
        }
    }

    /// <summary>Writes <c>text:tracked-changes</c>, if the body has changes.</summary>
    public static void WriteRegions(
        XmlWriter writer,
        Plan plan,
        Action<XmlWriter, IReadOnlyList<Inline>> writeInlines,
        InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (plan.Regions.Count == 0)
        {
            return;
        }

        writer.WriteStartElement("text", "tracked-changes", OdtNamespaces.Text);
        foreach (var region in plan.Regions)
        {
            writer.WriteStartElement("text", "changed-region", OdtNamespaces.Text);
            // ODF 1.2+ identifies the region by xml:id; text:id stays for older consumers (LibreOffice writes both).
            writer.WriteAttributeString("xml", "id", null, region.Name);
            writer.WriteAttributeString("text", "id", OdtNamespaces.Text, region.Name);
            var insertion = region.Change.Kind == TrackedChangeKind.Insertion;
            writer.WriteStartElement("text", insertion ? "insertion" : "deletion", OdtNamespaces.Text);
            writer.WriteStartElement("office", "change-info", OdtNamespaces.Office);
            writer.WriteStartElement("dc", "creator", OdtNamespaces.DublinCore);
            writer.WriteString(XmlText.Clean(region.Change.Author, xmlBehavior));
            writer.WriteEndElement();
            writer.WriteStartElement("dc", "date", OdtNamespaces.DublinCore);
            writer.WriteString(Timestamp(region.Change.Date));
            writer.WriteEndElement();
            writer.WriteEndElement();
            if (!insertion)
            {
                writer.WriteStartElement("text", "p", OdtNamespaces.Text);
                writeInlines(writer, region.Deleted);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>The date as LibreOffice writes it; an unknown date is zeros.</summary>
    internal static string Timestamp(DateTimeOffset? date)
        => date is { } value
            ? value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : "0000-00-00T00:00:00";

    /// <summary>A changed region read from <c>text:tracked-changes</c>.</summary>
    internal sealed record SourceRegion(TrackedChangeKind? Kind, string Author, DateTimeOffset? Date, XElement Element);

    /// <summary>The regions of <c>text:tracked-changes</c> by their id, and the model ids given to them while reading.</summary>
    internal sealed class Source
    {
        public static readonly Source Empty = new();

        public Dictionary<string, SourceRegion> Regions { get; } = new(StringComparer.Ordinal);

        /// <summary>The model id of an insertion region whose start was read, so its end finds it.</summary>
        public Dictionary<string, int> Started { get; } = new(StringComparer.Ordinal);

        public int Next { get; set; }
    }

    /// <summary>Reads the regions of <c>text:tracked-changes</c>.</summary>
    public static Source Read(XElement content)
    {
        XNamespace text = OdtNamespaces.Text;
        XNamespace office = OdtNamespaces.Office;
        XNamespace dc = OdtNamespaces.DublinCore;
        var source = new Source();
        foreach (var region in content.Descendants(text + "tracked-changes").Elements(text + "changed-region"))
        {
            var id = region.Attribute(text + "id")?.Value ?? region.Attribute(XNamespace.Xml + "id")?.Value;
            if (string.IsNullOrEmpty(id) || region.Elements().FirstOrDefault() is not { } kindElement)
            {
                continue;
            }

            TrackedChangeKind? kind = kindElement.Name == text + "insertion" ? TrackedChangeKind.Insertion
                : kindElement.Name == text + "deletion" ? TrackedChangeKind.Deletion
                : null;
            var info = kindElement.Element(office + "change-info");
            var author = info?.Element(dc + "creator")?.Value.Trim();
            source.Regions.TryAdd(id, new SourceRegion(
                kind,
                string.IsNullOrEmpty(author) ? "Unknown" : author,
                DateTimeOffset.TryParse(info?.Element(dc + "date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null,
                kindElement));
        }

        return source;
    }

    /// <summary>Writes one step of a paragraph that is not an inline.</summary>
    public static void WriteMarker(XmlWriter writer, Op op)
    {
        switch (op)
        {
            case Start start:
                writer.WriteStartElement("text", "change-start", OdtNamespaces.Text);
                writer.WriteAttributeString("text", "change-id", OdtNamespaces.Text, start.Name);
                writer.WriteEndElement();
                break;
            case End end:
                writer.WriteStartElement("text", "change-end", OdtNamespaces.Text);
                writer.WriteAttributeString("text", "change-id", OdtNamespaces.Text, end.Name);
                writer.WriteEndElement();
                break;
            case Point point:
                writer.WriteStartElement("text", "change", OdtNamespaces.Text);
                writer.WriteAttributeString("text", "change-id", OdtNamespaces.Text, point.Name);
                writer.WriteEndElement();
                break;
        }
    }
}
