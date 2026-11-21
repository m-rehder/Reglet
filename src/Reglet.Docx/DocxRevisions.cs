using System.Globalization;
using System.Xml;

namespace Reglet;

/// <summary>
/// Tracked changes in DOCX (ADR-0027): <c>w:ins</c> and <c>w:del</c> around the runs. Word has no element across
/// paragraphs or across a hyperlink, so the writer closes the open changes at such a border and opens them again
/// (with a fresh <c>w:id</c>) on the other side.
/// </summary>
internal static class DocxRevisions
{
    /// <summary>Remembers which changes of the body have an end.</summary>
    public static void Prepare(DocxWriteContext context, MarkScan.MarkInfo<TrackedChange> info)
    {
        context.RevisionBody.UnionWith(info.Body);
        context.RevisionEnded = info.EndedIds;
    }

    /// <summary>Writes the start of a tracked change.</summary>
    public static void WriteStart(XmlWriter writer, TrackedChange change, DocxWriteContext context)
    {
        if (!context.RevisionBody.Contains(change))
        {
            return;
        }

        if (!context.RevisionEnded.Contains(change.Id))
        {
            context.Handler.Report($"DOCX: the tracked change {change.Id} has no end; it is omitted.");
            return;
        }

        context.OpenChanges.Add(change);
        if (context.RevisionElementsOpen)
        {
            WriteOpen(writer, change, context);
        }
    }

    /// <summary>Writes the end of a tracked change.</summary>
    public static void WriteEnd(XmlWriter writer, TrackedChangeEnd end, DocxWriteContext context)
    {
        var index = context.OpenChanges.FindIndex(change => change.Id == end.Id);
        if (index < 0)
        {
            context.Handler.Report($"DOCX: the end of the tracked change {end.Id} has no start; it is omitted.");
            return;
        }

        if (!context.RevisionElementsOpen)
        {
            context.OpenChanges.RemoveAt(index);
            return;
        }

        // The elements above the one that ends are closed and opened again around the rest.
        for (var count = context.OpenChanges.Count - index; count > 0; count--)
        {
            writer.WriteEndElement();
        }

        context.OpenChanges.RemoveAt(index);
        for (var i = index; i < context.OpenChanges.Count; i++)
        {
            WriteOpen(writer, context.OpenChanges[i], context);
        }
    }

    /// <summary>Closes the open elements at a border (paragraph end, hyperlink); the changes stay open.</summary>
    public static void Suspend(XmlWriter writer, DocxWriteContext context)
    {
        if (!context.RevisionElementsOpen)
        {
            return;
        }

        for (var i = 0; i < context.OpenChanges.Count; i++)
        {
            writer.WriteEndElement();
        }

        context.RevisionElementsOpen = false;
    }

    /// <summary>Opens the elements of the changes that are still open, after a border.</summary>
    public static void Resume(XmlWriter writer, DocxWriteContext context)
    {
        if (context.RevisionElementsOpen)
        {
            return;
        }

        foreach (var change in context.OpenChanges)
        {
            WriteOpen(writer, change, context);
        }

        context.RevisionElementsOpen = true;
    }

    /// <summary>Whether the text being written is deleted (it uses <c>w:delText</c>).</summary>
    public static bool InDeletion(DocxWriteContext context)
        => context.OpenChanges.Any(change => change.Kind == TrackedChangeKind.Deletion);

    private static void WriteOpen(XmlWriter writer, TrackedChange change, DocxWriteContext context)
    {
        writer.WriteStartElement("w", change.Kind == TrackedChangeKind.Insertion ? "ins" : "del", DocxConstants.WordprocessingML);
        writer.WriteAttributeString("w", "id", DocxConstants.WordprocessingML, context.NextRevisionId().ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("w", "author", DocxConstants.WordprocessingML, XmlText.Clean(change.Author, context.XmlBehavior));
        if (change.Date is { } date)
        {
            writer.WriteAttributeString("w", "date", DocxConstants.WordprocessingML, date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
    }
}
