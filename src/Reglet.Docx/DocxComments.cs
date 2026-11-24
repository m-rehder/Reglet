using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Reglet;

/// <summary>
/// Comments in DOCX (ADR-0026): the anchors in the body, <c>word/comments.xml</c> and, for replies and the
/// "done" flag, <c>word/commentsExtended.xml</c>.
/// </summary>
internal static class DocxComments
{
    private const string Word2012Namespace = "http://schemas.microsoft.com/office/word/2012/wordml";

    private static readonly XNamespace W = DocxConstants.WordprocessingML;
    private static readonly XNamespace W14 = DocxFormControls.Word2010Namespace;
    private static readonly XNamespace W15 = Word2012Namespace;

    /// <summary>A comment read from DOCX; a reply shares the anchor of its parent and adds none.</summary>
    internal sealed record Entry(Comment Comment, bool IsReply);

    /// <summary>Gives every comment of the body its id in the comments part (a parent first, then its replies).</summary>
    public static void Prepare(DocxWriteContext context, MarkScan.MarkInfo<Comment> info)
    {
        var next = 0;
        foreach (var comment in info.Body)
        {
            context.CommentWordIds[comment] = next++;
            foreach (var reply in comment.Replies)
            {
                context.CommentWordIds[reply] = next++;
            }
        }

        context.CommentEnded = info.EndedIds;
    }

    /// <summary>Writes the start of a commented range (a point comment is closed right away).</summary>
    public static void WriteStart(XmlWriter writer, Comment comment, DocxWriteContext context)
    {
        if (!context.CommentWordIds.ContainsKey(comment))
        {
            return;
        }

        foreach (var each in Thread(comment))
        {
            writer.WriteStartElement("w", "commentRangeStart", DocxConstants.WordprocessingML);
            WriteId(writer, context.CommentWordIds[each]);
            writer.WriteEndElement();
        }

        if (context.CommentEnded.Contains(comment.Id))
        {
            context.OpenComments[comment.Id] = comment;
        }
        else
        {
            WriteClose(writer, comment, context);
        }
    }

    /// <summary>Writes the end of a commented range.</summary>
    public static void WriteEnd(XmlWriter writer, CommentEnd end, DocxWriteContext context)
    {
        if (!context.OpenComments.Remove(end.Id, out var comment))
        {
            context.Handler.Report($"DOCX: the end of the comment {end.Id} has no start; it is omitted.");
            return;
        }

        WriteClose(writer, comment, context);
    }

    private static IEnumerable<Comment> Thread(Comment comment) => comment.Replies.Prepend(comment);

    private static void WriteClose(XmlWriter writer, Comment comment, DocxWriteContext context)
    {
        foreach (var each in Thread(comment))
        {
            var id = context.CommentWordIds[each];
            writer.WriteStartElement("w", "commentRangeEnd", DocxConstants.WordprocessingML);
            WriteId(writer, id);
            writer.WriteEndElement();
            writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
            writer.WriteStartElement("w", "commentReference", DocxConstants.WordprocessingML);
            WriteId(writer, id);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
    }

    private static void WriteId(XmlWriter writer, int id)
        => writer.WriteAttributeString("w", "id", DocxConstants.WordprocessingML, id.ToString(CultureInfo.InvariantCulture));

    private static string ParaId(int index) => (0x10000000 + (index * 0x101)).ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Writes <c>word/comments.xml</c>.</summary>
    public static void WriteCommentsPart(XmlWriter writer, DocxWriteContext context, IReadOnlyList<Comment> comments)
    {
        writer.WriteStartElement("w", "comments", DocxConstants.WordprocessingML);
        DocxConstants.WriteCompatibilityNamespaces(writer);

        foreach (var comment in comments.SelectMany(Thread))
        {
            var id = context.CommentWordIds[comment];
            writer.WriteStartElement("w", "comment", DocxConstants.WordprocessingML);
            WriteId(writer, id);
            writer.WriteAttributeString("w", "author", DocxConstants.WordprocessingML, XmlText.Clean(comment.Author, context.XmlBehavior));
            if (comment.Date is { } date)
            {
                writer.WriteAttributeString("w", "date", DocxConstants.WordprocessingML,
                    date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(comment.Initials))
            {
                writer.WriteAttributeString("w", "initials", DocxConstants.WordprocessingML, XmlText.Clean(comment.Initials, context.XmlBehavior));
            }

            for (var index = 0; index < comment.Paragraphs.Count; index++)
            {
                writer.WriteStartElement("w", "p", DocxConstants.WordprocessingML);
                if (index == comment.Paragraphs.Count - 1)
                {
                    // commentsExtended.xml points at the last paragraph of a comment.
                    writer.WriteAttributeString("w14", "paraId", DocxFormControls.Word2010Namespace, ParaId(id));
                }

                if (index == 0)
                {
                    writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
                    writer.WriteStartElement("w", "annotationRef", DocxConstants.WordprocessingML);
                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                WriteText(writer, XmlText.Clean(comment.Paragraphs[index], context.XmlBehavior));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteText(XmlWriter writer, string text)
    {
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            if (index < text.Length && text[index] is not ('\n' or '\t'))
            {
                continue;
            }

            if (index > start)
            {
                writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
                writer.WriteStartElement("w", "t", DocxConstants.WordprocessingML);
                writer.WriteAttributeString("xml", "space", null, "preserve");
                writer.WriteString(text[start..index]);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            if (index < text.Length)
            {
                writer.WriteStartElement("w", "r", DocxConstants.WordprocessingML);
                writer.WriteStartElement("w", text[index] == '\n' ? "br" : "tab", DocxConstants.WordprocessingML);
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            start = index + 1;
        }
    }

    /// <summary>Whether replies or "done" flags need <c>word/commentsExtended.xml</c>.</summary>
    public static bool NeedsExtended(IReadOnlyList<Comment> comments)
        => comments.Any(comment => comment.Resolved || comment.Replies.Count > 0);

    /// <summary>Writes <c>word/commentsExtended.xml</c>.</summary>
    public static void WriteExtendedPart(XmlWriter writer, DocxWriteContext context, IReadOnlyList<Comment> comments)
    {
        writer.WriteStartElement("w15", "commentsEx", Word2012Namespace);

        foreach (var parent in comments)
        {
            foreach (var comment in Thread(parent))
            {
                writer.WriteStartElement("w15", "commentEx", Word2012Namespace);
                writer.WriteAttributeString("w15", "paraId", Word2012Namespace, ParaId(context.CommentWordIds[comment]));
                if (comment != parent)
                {
                    writer.WriteAttributeString("w15", "paraIdParent", Word2012Namespace, ParaId(context.CommentWordIds[parent]));
                }

                writer.WriteAttributeString("w15", "done", Word2012Namespace, comment.Resolved ? "1" : "0");
                writer.WriteEndElement();
            }
        }

        writer.WriteEndElement();
    }

    /// <summary>Reads <c>word/comments.xml</c> (and the replies and "done" flags of <c>word/commentsExtended.xml</c>).</summary>
    /// <returns>The comments by their id in the file.</returns>
    public static Dictionary<string, Entry> Read(XElement? comments, XElement? extended, Action<string> warn)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        if (comments is null)
        {
            return result;
        }

        var links = (extended?.Elements(W15 + "commentEx") ?? [])
            .Where(element => element.Attribute(W15 + "paraId") is not null)
            .GroupBy(element => element.Attribute(W15 + "paraId")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var raw = new List<(string Key, int Id, XElement Element, string? ParentParaId, bool Done)>();
        foreach (var element in comments.Elements(W + "comment"))
        {
            var key = element.Attribute(W + "id")?.Value;
            if (key is null || !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0)
            {
                continue;
            }

            var lastPara = element.Elements(W + "p").LastOrDefault()?.Attribute(W14 + "paraId")?.Value;
            var link = lastPara is not null && links.TryGetValue(lastPara, out var found) ? found : null;
            var done = link?.Attribute(W15 + "done")?.Value is "1" or "true";
            raw.Add((key, id, element, link?.Attribute(W15 + "paraIdParent")?.Value, done));
        }

        Comment Build(string key, int id, XElement element, bool done, IReadOnlyList<Comment>? replies)
        {
            var author = element.Attribute(W + "author")?.Value;
            var paragraphs = element.Elements(W + "p").Select(ReadText).ToList();
            if (paragraphs.Count == 0)
            {
                paragraphs.Add(string.Empty);
            }

            if (element.Descendants().Any(item => item.Name == W + "drawing" || item.Name == W + "tbl" || item.Name == W + "pict"))
            {
                warn($"DOCX: a picture or table in the comment by '{author}' is not mapped.");
            }
            else if (element.Descendants().Any(item => item.Name == W + "b" || item.Name == W + "i" || item.Name == W + "u"))
            {
                warn($"DOCX: the character formatting in the comment by '{author}' is not mapped.");
            }

            var comment = new Comment(id, string.IsNullOrWhiteSpace(author) ? "Unknown" : author, paragraphs)
            {
                Initials = element.Attribute(W + "initials")?.Value is { Length: > 0 } initials ? initials : null,
                Date = DateTimeOffset.TryParse(
                    element.Attribute(W + "date")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date : null,
                Resolved = done,
            };
            return replies is { Count: > 0 } ? new Comment(comment.Id, comment.Author, comment.Paragraphs)
            {
                Initials = comment.Initials, Date = comment.Date, Resolved = comment.Resolved, Replies = replies,
            } : comment;
        }

        // A parent is addressed by the paraId of its last paragraph; a reply points at it.
        string? ParaIdOf(XElement element) => element.Elements(W + "p").LastOrDefault()?.Attribute(W14 + "paraId")?.Value;
        var parents = raw.Where(item => item.ParentParaId is null)
            .Where(item => ParaIdOf(item.Element) is not null)
            .GroupBy(item => ParaIdOf(item.Element)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);

        var repliesByParent = new Dictionary<string, List<Comment>>(StringComparer.Ordinal);
        var replyKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in raw.Where(item => item.ParentParaId is not null))
        {
            if (!parents.TryGetValue(item.ParentParaId!, out var parentKey) || parentKey == item.Key)
            {
                continue;
            }

            if (!repliesByParent.TryGetValue(parentKey, out var list))
            {
                repliesByParent[parentKey] = list = [];
            }

            var reply = Build(item.Key, item.Id, item.Element, item.Done, null);
            list.Add(reply);
            result[item.Key] = new Entry(reply, IsReply: true);
            replyKeys.Add(item.Key);
        }

        foreach (var item in raw.Where(item => !replyKeys.Contains(item.Key)))
        {
            var replies = repliesByParent.GetValueOrDefault(item.Key);
            result[item.Key] = new Entry(Build(item.Key, item.Id, item.Element, item.Done, replies), IsReply: false);
        }

        return result;
    }

    private static string ReadText(XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var item in paragraph.Descendants())
        {
            if (item.Name == W + "t")
            {
                text.Append(item.Value);
            }
            else if (item.Name == W + "tab")
            {
                text.Append('\t');
            }
            else if (item.Name == W + "br" || item.Name == W + "cr")
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }
}
