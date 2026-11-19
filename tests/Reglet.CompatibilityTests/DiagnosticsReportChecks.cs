using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// concrete loss diagnostics: unsupported field variants, comments, tracked changes, embedded
/// objects and drawing objects are named instead of reported as generic "unmappable content".
/// </summary>
internal static class DiagnosticsReportChecks
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string OfficeNamespace = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string TextNamespace = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string DrawNamespace = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
    private const string OleNamespace = "urn:schemas-microsoft-com:office:office";

    internal static int Run()
    {
        try
        {
            ReadsGeneralDocxFields();
            ReportsDocxCommentsAndTrackedDeletions();
            ReportsDocxEmbeddedObjectByProgramId();
            ReportsOdtUnmappedFieldsCommentsAndDrawings();
            Console.WriteLine("Loss diagnostics (AE): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Loss diagnostics (AE): " + exception);
            return 1;
        }
    }

    /// <summary>
    /// A general field keeps its code and its cached result (like the ODT reader), so nothing is lost
    /// and nothing is reported.
    /// </summary>
    private static void ReadsGeneralDocxFields()
    {
        var package = Docx(
            "<w:p>"
            + "<w:r><w:t>a</w:t></w:r>"
            + "<w:fldSimple w:instr=' IF 1 = 1 \"yes\" \"no\" '><w:r><w:t>yes</w:t></w:r></w:fldSimple>"
            + "<w:r><w:fldChar w:fldCharType='begin'/></w:r>"
            + "<w:r><w:instrText> SEQ Figure </w:instrText></w:r>"
            + "<w:r><w:fldChar w:fldCharType='separate'/></w:r>"
            + "<w:r><w:t>1</w:t></w:r>"
            + "<w:r><w:fldChar w:fldCharType='end'/></w:r>"
            + "</w:p>");

        var result = ReadDocx(package);
        var fields = result.Document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).OfType<Field>().ToList();
        Require(fields.Any(field => field.Instruction == "IF 1 = 1 \"yes\" \"no\"" && field.Result == "yes"), "The simple field IF keeps code and result.");
        Require(fields.Any(field => field.Instruction == "SEQ Figure" && field.Result == "1"), "The complex field SEQ keeps code and result.");
        Require(!result.Warnings.Any(warning => warning.Message.Contains("field '", StringComparison.Ordinal)), "A kept field is not reported.");
    }

    /// <summary>Comment anchors are named, so a changed body reports them; tracked deletions are modelled.</summary>
    private static void ReportsDocxCommentsAndTrackedDeletions()
    {
        var package = Docx(
            "<w:p>"
            + "<w:commentRangeStart w:id='1'/>"
            + "<w:r><w:t>keep</w:t></w:r>"
            + "<w:commentRangeEnd w:id='1'/>"
            + "<w:r><w:commentReference w:id='1'/></w:r>"
            + "<w:del w:id='2' w:author='Author'><w:r><w:delText>gone</w:delText></w:r></w:del>"
            + "</w:p>");

        var result = ReadDocx(package);
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("comment", StringComparison.Ordinal)),
            "A comment anchor is reported.");
        // A tracked deletion is modelled (ADR-0027): nothing to report, the text is read inside the markers.
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("tracked change", StringComparison.Ordinal)),
            "A tracked deletion is not reported.");
        var inlines = result.Document.Sections[0].Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines).ToList();
        Require(inlines.OfType<TrackedChange>().Any(change => change.Kind == TrackedChangeKind.Deletion && change.Author == "Author"), "The deletion is a tracked change.");
        Require(PlainText(result.Document).Contains("keep", StringComparison.Ordinal), "The visible text is read.");
        Require(PlainText(result.Document).Contains("gone", StringComparison.Ordinal), "The deleted text is read inside its markers.");
        Require(result.Document.SourceHasUnmodelledContent, "The loss report is armed for a changed body.");
    }

    /// <summary>An embedded OLE object is named by its program id.</summary>
    private static void ReportsDocxEmbeddedObjectByProgramId()
    {
        var package = Docx(
            "<w:p><w:r><w:object xmlns:o='" + OleNamespace + "'>"
            + "<o:OLEObject ProgID='Excel.Sheet.12'/>"
            + "</w:object></w:r></w:p>");

        var result = ReadDocx(package);
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("Excel.Sheet.12", StringComparison.Ordinal)),
            "The embedded object is named by its program id.");
    }

    /// <summary>ODF fields, comments and drawing objects are named concretely.</summary>
    private static void ReportsOdtUnmappedFieldsCommentsAndDrawings()
    {
        var package = Odt(
            "<office:body><office:text><text:p>"
            + "<text:chapter text:display='name'>Chapter 1</text:chapter>"
            + "<office:annotation><dc:creator>Alice</dc:creator><text:p>Note</text:p></office:annotation>"
            + "<draw:rect draw:name='Box'/>"
            + "</text:p></office:text></office:body>");

        var result = ReadOdt(package);
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("text:chapter", StringComparison.Ordinal)),
            "An unsupported ODF field is named.");
        Require(
            !result.Warnings.Any(warning => warning.Message.Contains("comment", StringComparison.OrdinalIgnoreCase))
            && result.Document.Sections.SelectMany(section => section.Blocks).OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines).OfType<Comment>().Any(comment => comment.Author == "Alice"),
            "A comment is modelled (ADR-0026), not reported.");
        Require(
            result.Warnings.Any(warning => warning.Message.Contains("draw:rect", StringComparison.Ordinal)
                && warning.Message.Contains("Box", StringComparison.Ordinal)),
            "A drawing object is named.");
        Require(PlainText(result.Document).Contains("Chapter 1", StringComparison.Ordinal), "The field text is kept.");
        Require(!PlainText(result.Document).Contains("Note", StringComparison.Ordinal), "Comment text is not adopted.");
    }

    private static byte[] Docx(string body)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipText(
                archive,
                "word/document.xml",
                "<w:document xmlns:w='" + W + "'><w:body>" + body + "</w:body></w:document>");
        }

        return buffer.ToArray();
    }

    private static byte[] Odt(string body)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipText(
                archive,
                "content.xml",
                "<office:document-content"
                + " xmlns:office='" + OfficeNamespace + "'"
                + " xmlns:text='" + TextNamespace + "'"
                + " xmlns:draw='" + DrawNamespace + "'"
                + " xmlns:dc='" + DublinCoreNamespace + "'>"
                + body
                + "</office:document-content>");
        }

        return buffer.ToArray();
    }

    private static void WriteZipText(ZipArchive archive, string name, string value)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static ReadResult ReadDocx(byte[] package)
    {
        using var stream = new MemoryStream(package, writable: false);
        return new DocxDocumentReader().Read(stream);
    }

    private static ReadResult ReadOdt(byte[] package)
    {
        using var stream = new MemoryStream(package, writable: false);
        return new OdtDocumentReader().Read(stream);
    }

    private static string PlainText(PortableDocument document)
    {
        var builder = new StringBuilder();
        foreach (var section in document.Sections)
        {
            foreach (var block in section.Blocks)
            {
                if (block is Paragraph paragraph)
                {
                    foreach (var inline in paragraph.Inlines)
                    {
                        if (inline is TextRun run)
                        {
                            builder.Append(run.Text);
                        }
                    }
                }
            }
        }

        return builder.ToString();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
