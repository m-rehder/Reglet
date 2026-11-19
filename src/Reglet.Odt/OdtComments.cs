using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Comments in ODT (ADR-0026): <c>office:annotation</c> at the start of the range and <c>office:annotation-end</c>
/// at its end; a reply is an annotation that names its parent in <c>loext:parent-name</c>, the done flag is
/// <c>loext:resolved</c> (LibreOffice 7.x writes and reads both).
/// </summary>
internal static class OdtComments
{
    private const string LibreOfficeExtension = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    private static readonly XNamespace Office = OdtNamespaces.Office;
    private static readonly XNamespace Text = OdtNamespaces.Text;
    private static readonly XNamespace Dc = OdtNamespaces.DublinCore;
    private static readonly XNamespace Meta = OdtNamespaces.Meta;
    private static readonly XNamespace Loext = LibreOfficeExtension;

    /// <summary>A comment read from ODT; a reply shares the anchor of its parent and adds none.</summary>
    internal sealed record Entry(Comment Comment, bool IsReply);

    /// <summary>The comments of a document by their <c>office:annotation</c> element and by their name.</summary>
    internal sealed record Comments(
        IReadOnlyDictionary<XElement, Entry> ByElement,
        IReadOnlyDictionary<string, Entry> ByName)
    {
        internal static readonly Comments Empty = new(
            new Dictionary<XElement, Entry>(), new Dictionary<string, Entry>(StringComparer.Ordinal));
    }

    /// <summary>Gives every comment of the body its annotation name (a comment first, then its replies).</summary>
    public static void Prepare(OdtParagraphStyles styles, CommentScan.CommentInfo info)
    {
        // Not the "__Annotation__N_hash" names of LibreOffice, so they cannot clash with preserved source annotations.
        var next = 0;
        foreach (var comment in info.Body)
        {
            styles.CommentNames[comment] = "__Annotation__pd" + next++.ToString(CultureInfo.InvariantCulture);
            foreach (var reply in comment.Replies)
            {
                styles.CommentNames[reply] = "__Annotation__pd" + next++.ToString(CultureInfo.InvariantCulture);
            }
        }

        styles.CommentEnded = info.EndedIds;
    }

    /// <summary>Writes the start of a commented range (a point comment has no end).</summary>
    public static void WriteStart(XmlWriter writer, Comment comment, OdtParagraphStyles styles, InvalidXmlCharacterBehavior xmlBehavior)
    {
        if (!styles.CommentNames.TryGetValue(comment, out var name))
        {
            return;
        }

        // The order LibreOffice writes: the replies, then the comment they belong to.
        foreach (var reply in comment.Replies)
        {
            WriteAnnotation(writer, reply, styles.CommentNames[reply], name, xmlBehavior);
        }

        WriteAnnotation(writer, comment, name, parentName: null, xmlBehavior);
        if (styles.CommentEnded.Contains(comment.Id))
        {
            styles.OpenComments[comment.Id] = comment;
        }
    }

    /// <summary>Writes the end of a commented range.</summary>
    public static void WriteEnd(XmlWriter writer, CommentEnd end, OdtParagraphStyles styles, UnsupportedFeatureHandler? handler)
    {
        if (!styles.OpenComments.Remove(end.Id, out var comment))
        {
            handler?.Report($"ODT: the end of the comment {end.Id} has no start; it is omitted.");
            return;
        }

        foreach (var each in comment.Replies.Prepend(comment))
        {
            writer.WriteStartElement("office", "annotation-end", OdtNamespaces.Office);
            writer.WriteAttributeString("office", "name", OdtNamespaces.Office, styles.CommentNames[each]);
            writer.WriteEndElement();
        }
    }

    private static void WriteAnnotation(
        XmlWriter writer, Comment comment, string name, string? parentName, InvalidXmlCharacterBehavior xmlBehavior)
    {
        writer.WriteStartElement("office", "annotation", OdtNamespaces.Office);
        writer.WriteAttributeString("office", "name", OdtNamespaces.Office, name);
        if (parentName is not null)
        {
            writer.WriteAttributeString("loext", "parent-name", LibreOfficeExtension, parentName);
        }

        if (comment.Resolved)
        {
            writer.WriteAttributeString("loext", "resolved", LibreOfficeExtension, "true");
        }

        writer.WriteElementString("dc", "creator", OdtNamespaces.DublinCore, XmlText.Clean(comment.Author, xmlBehavior));
        if (comment.Date is { } date)
        {
            writer.WriteElementString(
                "dc", "date", OdtNamespaces.DublinCore,
                date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrEmpty(comment.Initials))
        {
            writer.WriteElementString("meta", "creator-initials", OdtNamespaces.Meta, XmlText.Clean(comment.Initials, xmlBehavior));
        }

        foreach (var paragraph in comment.Paragraphs)
        {
            writer.WriteStartElement("text", "p", OdtNamespaces.Text);
            OdtContentWriter.WriteText(writer, paragraph, xmlBehavior);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// Reads every <c>office:annotation</c> of <c>content.xml</c> (ids by position), so a reply written before its
    /// parent (LibreOffice does) still finds it.
    /// </summary>
    public static Comments Read(XElement content, Action<string> warn)
    {
        var annotations = content.Descendants(Office + "annotation").ToList();
        if (annotations.Count == 0)
        {
            return Comments.Empty;
        }

        string? NameOf(XElement element) => element.Attribute(Office + "name")?.Value;
        string? ParentOf(XElement element) => element.Attribute(Loext + "parent-name")?.Value;

        var names = annotations.Where(element => NameOf(element) is not null && ParentOf(element) is null)
            .GroupBy(NameOf).ToDictionary(group => group.Key!, group => group.First(), StringComparer.Ordinal);
        var ids = annotations.Select((element, index) => (element, index)).ToDictionary(pair => pair.element, pair => pair.index);

        Comment Build(XElement element, IReadOnlyList<Comment>? replies)
        {
            var author = element.Element(Dc + "creator")?.Value.Trim();
            var paragraphs = element.Elements(Text + "p").Select(ReadText).ToList();
            if (paragraphs.Count == 0)
            {
                paragraphs.Add(string.Empty);
            }

            if (element.Elements().Any(child => child.Name != Dc + "creator" && child.Name != Dc + "date"
                && child.Name != Meta + "creator-initials" && child.Name != Meta + "date-string"
                && child.Name != Loext + "sender-initials" && child.Name != Text + "p"))
            {
                warn($"ODT: a list, table or other content in the comment by '{author}' is not mapped.");
            }

            var initials = element.Element(Meta + "creator-initials")?.Value ?? element.Element(Loext + "sender-initials")?.Value;
            var comment = new Comment(ids[element], string.IsNullOrWhiteSpace(author) ? "Unknown" : author, paragraphs)
            {
                Initials = string.IsNullOrEmpty(initials) ? null : initials,
                // LibreOffice writes 0000-00-00T00:00:00 for an unknown date; that does not parse and stays null.
                Date = DateTimeOffset.TryParse(
                    element.Element(Dc + "date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date : null,
                Resolved = element.Attribute(Loext + "resolved")?.Value == "true",
            };
            return replies is { Count: > 0 } ? new Comment(comment.Id, comment.Author, comment.Paragraphs)
            {
                Initials = comment.Initials, Date = comment.Date, Resolved = comment.Resolved, Replies = replies,
            } : comment;
        }

        var byElement = new Dictionary<XElement, Entry>();
        var byName = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var replyLists = new Dictionary<XElement, List<Comment>>();
        foreach (var element in annotations.Where(element => ParentOf(element) is { } parent && names.ContainsKey(parent)))
        {
            var parentElement = names[ParentOf(element)!];
            if (!replyLists.TryGetValue(parentElement, out var list))
            {
                replyLists[parentElement] = list = [];
            }

            var reply = Build(element, null);
            list.Add(reply);
            var entry = new Entry(reply, IsReply: true);
            byElement[element] = entry;
            if (NameOf(element) is { } replyName)
            {
                byName[replyName] = entry;
            }
        }

        foreach (var element in annotations.Where(element => !byElement.ContainsKey(element)))
        {
            var entry = new Entry(Build(element, replyLists.GetValueOrDefault(element)), IsReply: false);
            byElement[element] = entry;
            if (NameOf(element) is { } name)
            {
                byName[name] = entry;
            }
        }

        return new Comments(byElement, byName);
    }

    private static string ReadText(XElement paragraph)
    {
        var text = new StringBuilder();
        void Append(IEnumerable<XNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is XText value)
                {
                    text.Append(value.Value);
                }
                else if (node is XElement element)
                {
                    if (element.Name == Text + "tab")
                    {
                        text.Append('\t');
                    }
                    else if (element.Name == Text + "line-break")
                    {
                        text.Append('\n');
                    }
                    else if (element.Name == Text + "s")
                    {
                        var count = int.TryParse(element.Attribute(Text + "c")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 1;
                        text.Append(' ', Math.Max(1, count));
                    }
                    else
                    {
                        Append(element.Nodes());
                    }
                }
            }
        }

        Append(paragraph.Nodes());
        return text.ToString();
    }
}
