using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>
/// calculation of the general fields (phase AJ, AJ02): IF, MERGEFIELD, SEQ, REF,
/// DOCPROPERTY, DOCVARIABLE and DATE with nested fields.
/// </summary>
internal static class FieldCalculationChecks
{
    internal static int Run()
    {
        try
        {
            EvaluatesConditions();
            EvaluatesNestedFields();
            EvaluatesMergeFields();
            EvaluatesSequences();
            EvaluatesReferences();
            EvaluatesDates();
            EvaluatesEveryDatePicture();
            TreatsNestedValuesAsData();
            LimitsNestingAndNumbers();
            CalculatesUnknownFieldAsNull();
            ResolvesDocument();
            WritesCalculatedDocxResult();
            WritesCalculatedOdtResult();
            DrawsCalculatedPdfResult();
            Console.WriteLine("Field calculation (AJ02): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Field calculation (AJ02): " + exception);
            return 1;
        }
    }

    private static void EvaluatesConditions()
    {
        var context = FieldContext.Empty;

        Require(
            FieldEvaluator.Evaluate("IF 1 = 1 \"yes\" \"no\"", context) == "yes",
            "IF: a true numeric comparison returns the true text.");
        Require(
            FieldEvaluator.Evaluate("IF 2 > 3 \"yes\" \"no\"", context) == "no",
            "IF: a false numeric comparison returns the false text.");
        Require(
            FieldEvaluator.Evaluate("IF \"a\" = \"A\" \"same\" \"different\"", context) == "same",
            "IF: the text comparison ignores the case.");
        Require(
            FieldEvaluator.Evaluate("IF 1 <> 2 \"yes\" \"no\"", context) == "yes",
            "IF: the operator <> is supported.");
    }

    private static void EvaluatesNestedFields()
    {
        var context = FieldContext.Create(mergeValues: new Dictionary<string, string> { ["Country"] = "DE" });

        Require(
            FieldEvaluator.Evaluate("IF { MERGEFIELD Country } = \"DE\" \"Hello\" \"Bye\"", context) == "Hello",
            "IF: a nested field is calculated first.");
        Require(
            FieldEvaluator.Evaluate("{ MERGEFIELD Country }", context) == "DE",
            "A nested field on its own returns its value.");
    }

    private static void EvaluatesMergeFields()
    {
        var context = FieldContext.Create(mergeValues: new Dictionary<string, string> { ["Name"] = "Ada" });

        Require(FieldEvaluator.Evaluate("MERGEFIELD Name", context) == "Ada", "MERGEFIELD: the value is returned.");
        Require(
            FieldEvaluator.Evaluate("MERGEFIELD name", context) == "Ada",
            "MERGEFIELD: the name is compared case-insensitively.");
        Require(
            FieldEvaluator.Evaluate("MERGEFIELD Unknown", context) is null,
            "MERGEFIELD: an unknown name has no value.");
        Require(
            FieldEvaluator.Evaluate("MERGEFIELD Name \\* Upper", context) == "ADA",
            "MERGEFIELD: the format switch is applied.");
    }

    private static void EvaluatesSequences()
    {
        var context = FieldContext.Empty;

        Require(FieldEvaluator.Evaluate("SEQ Figure", context) == "1", "SEQ: the first value is 1.");
        Require(FieldEvaluator.Evaluate("SEQ Figure", context) == "2", "SEQ: the counter continues.");
        Require(
            FieldEvaluator.Evaluate("SEQ Figure \\* ROMAN", context) == "III",
            "SEQ: the format switch roman is applied.");
        Require(FieldEvaluator.Evaluate("SEQ Other \\r 5", context) == "5", "SEQ: the counter is reset.");
        Require(FieldEvaluator.Evaluate("SEQ Other \\c", context) == "5", "SEQ: the current value is repeated.");
    }

    private static void EvaluatesReferences()
    {
        var context = FieldContext.Create(
            properties: new Dictionary<string, string> { ["Title"] = "Report" },
            variables: new Dictionary<string, string> { ["Version"] = "2.0" },
            bookmarks: new Dictionary<string, string> { ["Target"] = "See page 3" });

        Require(FieldEvaluator.Evaluate("DOCPROPERTY Title", context) == "Report", "DOCPROPERTY: the value is returned.");
        Require(FieldEvaluator.Evaluate("DOCVARIABLE Version", context) == "2.0", "DOCVARIABLE: the value is returned.");
        Require(FieldEvaluator.Evaluate("REF Target", context) == "See page 3", "REF: the bookmark text is returned.");
        Require(
            FieldEvaluator.Evaluate("DOCPROPERTY Unknown", context) is null,
            "DOCPROPERTY: an unknown property has no value.");
    }

    private static void EvaluatesDates()
    {
        var context = new FieldContext { Now = new DateTimeOffset(2026, 11, 3, 14, 5, 6, TimeSpan.Zero) };

        Require(
            FieldEvaluator.Evaluate("DATE \\@ \"yyyy-MM-dd\"", context) == "2026-11-03",
            "DATE: the date picture is applied.");
        Require(
            FieldEvaluator.Evaluate("DATE \\@ \"dd.MM.yyyy HH:mm\"", context) == "03.11.2026 14:05",
            "DATE: the time is part of the picture.");
    }

    private static void EvaluatesEveryDatePicture()
    {
        var context = new FieldContext { Now = new DateTimeOffset(2026, 3, 4, 14, 5, 6, TimeSpan.Zero) };
        foreach (var (picture, expected) in new[]
        {
            ("d.M.yyyy", "4.3.2026"), ("H:m:s", "14:5:6"), ("h:mm", "2:05"), ("hh", "02"), ("M", "3"), ("d", "4"), ("m", "5"), ("s", "6"),
            ("HH:mm 'Uhr'", "14:05 Uhr"), ("'d'", "d"),
        })
        {
            var actual = FieldEvaluator.Evaluate($"DATE \\@ \"{picture}\"", context);
            Require(actual == expected, $"DATE: picture '{picture}' gave '{actual}', expected '{expected}'.");
        }

        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf })
        {
            using var output = new MemoryStream();
            var document = new PortableDocument(new DocumentMetadata(), new Section(new Paragraph(new Field("DATE \\@ \"H:mm\""))));
            DocumentWriter.WriteAsync(document, output, format).GetAwaiter().GetResult();
            Require(output.Length > 0, $"{format}: a one-letter hour picture is written.");
        }
    }

    private static void TreatsNestedValuesAsData()
    {
        var context = FieldContext.Create(mergeValues: new Dictionary<string, string>
        {
            ["Hostile"] = "a\" = \"a\" \"INJECTED\" \"",
            ["Name"] = "Bob Ross",
        });

        Require(
            FieldEvaluator.Evaluate("IF \"{ MERGEFIELD Hostile }\" = \"b\" \"yes\" \"no\"", context) == "no",
            "IF: quotes in a value cannot end the operand.");
        Require(
            FieldEvaluator.Evaluate("IF { MERGEFIELD Name } = \"Bob Ross\" \"yes\" \"no\"", context) == "yes",
            "IF: a value with spaces is one operand.");
        Require(
            FieldEvaluator.Evaluate("IF { MERGEFIELD Name } = \"Ada\" \"yes\" \"no\"", context) == "no",
            "IF: a different value with spaces does not match.");
    }

    private static void LimitsNestingAndNumbers()
    {
        var context = new FieldContext();
        Require(FieldEvaluator.Evaluate("{{{QUOTE x}}}", context) == "x", "Nesting: a shallow field is calculated.");
        var deep = new string('{', 200_000) + "QUOTE x" + new string('}', 200_000);
        Require(FieldEvaluator.Evaluate(deep, context) is null, "Nesting: a too deep field cannot be calculated.");

        Require(FieldEvaluator.Evaluate("SEQ A \\r 3999 \\* ROMAN", context) == "MMMCMXCIX", "ROMAN: the largest roman number.");
        Require(FieldEvaluator.Evaluate("SEQ B \\r 2147483647 \\* ROMAN", context) == "2147483647", "ROMAN: a larger value is decimal.");
        Require(FieldEvaluator.Evaluate("SEQ B", context) == "2147483647", "SEQ: the counter does not overflow.");
    }

    private static void CalculatesUnknownFieldAsNull()
    {
        Require(
            FieldEvaluator.Evaluate("UNKNOWNCODE", FieldContext.Empty) is null,
            "An unknown field code has no value.");
        Require(
            FieldEvaluator.Evaluate("PAGE", FieldContext.Empty) is null,
            "PAGE without a page number has no value.");
    }

    private static void ResolvesDocument()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Title = "Report" },
            new Section(new Block[]
            {
                new Paragraph(new Inline[] { new Field("DOCPROPERTY Title") }),
                new TableBlock(new[]
                {
                    new TableRow(new TableCell(new Paragraph(new Inline[] { new Field("SEQ Figure") }))),
                }),
            }));

        var resolved = FieldResolver.Resolve(document, FieldContext.FromMetadata(document.Metadata));

        var paragraph = (Paragraph)resolved.Sections[0].Blocks[0];
        var field = (Field)paragraph.Inlines[0];
        Require(field.Result == "Report", "FieldResolver: the field has the calculated result.");

        var table = (TableBlock)resolved.Sections[0].Blocks[1];
        var cell = table.Rows[0].Cells[0];
        var cellParagraph = (Paragraph)cell.Blocks[0];
        var sequence = (Field)cellParagraph.Inlines[0];
        Require(sequence.Result == "1", "FieldResolver: the field in a table cell is calculated.");

        var original = (Field)((Paragraph)document.Sections[0].Blocks[0]).Inlines[0];
        Require(original.Result is null, "FieldResolver: the original document stays unchanged.");
    }

    private static void WritesCalculatedDocxResult()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Title = "Report" },
            new Section(new Paragraph(new Inline[] { new Field("DOCPROPERTY Title") })));

        using var buffer = new MemoryStream();
        new DocxDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "word/document.xml");
        Require(content.Contains("DOCPROPERTY Title", StringComparison.Ordinal), "DOCX: the field code is written.");
        Require(content.Contains("<w:t>Report</w:t>", StringComparison.Ordinal), "DOCX: the calculated result is written.");
    }

    private static void WritesCalculatedOdtResult()
    {
        var document = new PortableDocument(
            new DocumentMetadata { Title = "Report" },
            new Section(new Paragraph(new Inline[] { new Field("DOCPROPERTY Title") })));

        using var buffer = new MemoryStream();
        new OdtDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        var content = ReadZipEntry(buffer.ToArray(), "content.xml");
        Require(content.Contains("Report", StringComparison.Ordinal), "ODT: the calculated result is written as text.");
    }

    private static void DrawsCalculatedPdfResult()
    {
        var document = new PortableDocument(
            new DocumentMetadata(),
            new Section(new Paragraph(new Inline[] { new Field("SEQ Figure") })));

        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer)
            .GetAwaiter()
            .GetResult();

        var content = PdfStreams.Decompress(buffer.ToArray());
        Require(content.Contains("(1) Tj", StringComparison.Ordinal), "PDF: the calculated result is drawn.");
    }

    private static string ReadZipEntry(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
