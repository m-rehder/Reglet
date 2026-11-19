using System.Diagnostics;
using Reglet;

/// <summary>
/// ADR-0033 measuring tool, off unless FIDELITY=1: writes documents as DOCX, lets LibreOffice make the reference PDF
/// and compares page count and the first line of each page with the PDF the library writes itself.
/// </summary>
internal static class FidelityChecks
{
    private const string Soffice = "/Applications/LibreOffice.app/Contents/MacOS/soffice";

    internal static int Run()
    {
        if (Environment.GetEnvironmentVariable("FOREIGN_PDFS") is { Length: > 0 } foreignDirectory)
        {
            ReadForeignPdfs(foreignDirectory);
        }

        if (Environment.GetEnvironmentVariable("WORD_FILES") is { Length: > 0 } wordDirectory)
        {
            WriteWordFiles(wordDirectory);
        }

        if (Environment.GetEnvironmentVariable("FIDELITY") != "1" || !File.Exists(Soffice))
        {
            return 0;
        }

        var directory = Path.Combine(Path.GetTempPath(), "fidelity-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var failures = 0;
        foreach (var (name, document) in Corpus())
        {
            var docx = Path.Combine(directory, name + ".docx");
            using (var file = File.Create(docx))
            {
                DocumentWriter.WriteAsync(document, file, DocumentFormat.Docx).GetAwaiter().GetResult();
            }

            var ours = new MemoryStream();
            var fontDirectory = "/Applications/LibreOffice.app/Contents/Resources/fonts/truetype";
            var withFonts = Environment.GetEnvironmentVariable("FIDELITY_FONTS") == "1" && Directory.Exists(fontDirectory);
            var options = withFonts
                ? new PdfWriteOptions { FontCollection = Carlito(fontDirectory) }
                : null;
            DocumentWriter.WriteAsync(document, ours, DocumentFormat.Pdf, options).GetAwaiter().GetResult();
            using var process = Process.Start(new ProcessStartInfo(Soffice, $"--headless --convert-to pdf --outdir \"{directory}\" \"{docx}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit(120_000);
            var referencePath = Path.Combine(directory, name + ".pdf");
            if (!File.Exists(referencePath))
            {
                Console.WriteLine($"  {name}: no reference PDF");
                continue;
            }

            using (var htmlProcess = Process.Start(new ProcessStartInfo(Soffice, $"--headless --convert-to html --outdir \"{directory}\" \"{docx}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!)
            {
                htmlProcess.WaitForExit(120_000);
            }

            if (File.Exists(Path.Combine(directory, name + ".html")))
            {
                var page = new HtmlDocumentReader().Read(File.OpenRead(Path.Combine(directory, name + ".html")));
                var htmlKinds = page.Document.Sections.SelectMany(section => section.Blocks).GroupBy(block => block.GetType().Name).Select(group => $"{group.Key} {group.Count()}");
                Console.WriteLine($"  {name}: LibreOffice HTML read as {string.Join(", ", htmlKinds)}; {page.Warnings.Count} warning(s)");
            }

            File.WriteAllBytes(Path.Combine(directory, name + ".ours.pdf"), ours.ToArray());
            var reader = new PdfDocumentReader();
            var a = reader.ReadPageTexts(new MemoryStream(ours.ToArray()));
            var b = reader.ReadPageTexts(new MemoryStream(File.ReadAllBytes(referencePath)));
            var foreign = new PdfDocumentReader().Read(new MemoryStream(File.ReadAllBytes(referencePath)), new PdfReadOptions { RecoverStructure = true });
            var kinds = foreign.Document.Sections.SelectMany(section => section.Blocks).GroupBy(block => block.GetType().Name).Select(group => $"{group.Key} {group.Count()}");
            var expected = string.Join(", ", document.Sections.SelectMany(section => section.Blocks).GroupBy(block => block.GetType().Name).Select(group => $"{group.Key} {group.Count()}"));
            Console.WriteLine($"  {name}: LibreOffice PDF recovered as {string.Join(", ", kinds)} (source model: {expected})");
            var firstLines = a.Zip(b).Count(pair => First(pair.First) == First(pair.Second));
            Console.WriteLine($"  {name}: pages ours {a.Count} / LibreOffice {b.Count}; first line equal on {firstLines} of {Math.Min(a.Count, b.Count)} pages");
            if (a.Count != b.Count)
            {
                failures++;
            }
        }

        Console.WriteLine($"Fidelity: {failures} document(s) with a different page count (directory {directory}).");
        return 0;
    }

    /// <summary>Carlito is metric-compatible with Calibri, the default family, and is what LibreOffice substitutes.</summary>
    private static FontCollection Carlito(string directory)
    {
        var collection = FontCollection.Empty;
        foreach (var file in Directory.GetFiles(directory, "Carlito-*.ttf"))
        {
            collection = collection.Add("Calibri", FontFace.LoadFile(file));
        }

        return collection;
    }

    /// <summary>Files to open by hand in Word (WORD_FILES=dir): the spacing rule between a paragraph and a heading.</summary>
    private static void WriteWordFiles(string directory)
    {
        Directory.CreateDirectory(directory);
        var blocks = new List<Block>();
        for (var i = 0; i < 6; i++)
        {
            blocks.Add(new Paragraph(new TextRun("Paragraph " + i + " (6 pt after, the default). Right below follows a heading with 12 pt before.")));
            blocks.Add(new Heading(HeadingLevel.Level2, new TextRun("Heading " + i + " (12 pt before, 6 pt after)")));
        }

        File.WriteAllBytes(Path.Combine(directory, "spacing.docx"), WriteDocx(new PortableDocument(new DocumentMetadata(), new Section(blocks))));

        // ODT checks in LibreOffice: equation alignment, a diagram in the body and in a table cell.
        if (Environment.GetEnvironmentVariable("ODT_FILES") is { Length: > 0 })
        {
            var left = new Equation(new MathRow([new MathText("a"), new MathText("="), new MathFraction(new MathText("1"), new MathText("2"))])) { Alignment = TextAlignment.Left };
            var right = new Equation(new MathRow([new MathText("b"), new MathText("="), new MathFraction(new MathText("3"), new MathText("4"))])) { Alignment = TextAlignment.Right };
            var odtTable = new TableBlock([new TableRow(new TableCell(new Block[] { EquationChecks.Quadratic() }), new TableCell(new Block[] { DiagramChecks.Sample() }))]);
            var odtDocument = new PortableDocument(new DocumentMetadata(), new Section(new Block[]
            {
                new Paragraph(new TextRun("Left-aligned:")), left, new Paragraph(new TextRun("Right-aligned:")), right,
                new Paragraph(new TextRun("Diagram in the text:")), DiagramChecks.Sample(), new Paragraph(new TextRun("In a table:")), odtTable,
            }));
            using var odt = File.Create(Path.Combine(directory, "odt-check.odt"));
            DocumentWriter.WriteAsync(odtDocument, odt, DocumentFormat.Odt).GetAwaiter().GetResult();
        }

        // Tracked changes to accept and reject in Word (ADR-0027).
        File.WriteAllBytes(Path.Combine(directory, "changes.docx"), WriteDocx(RevisionChecks.Sample()));

        // A formula and a diagram in table cells.
        var table = new TableBlock([new TableRow(new TableCell(new Block[] { EquationChecks.Quadratic() }), new TableCell(new Block[] { DiagramChecks.Sample() }))]);
        File.WriteAllBytes(Path.Combine(directory, "table-formula-diagram.docx"), WriteDocx(new PortableDocument(new DocumentMetadata(), new Section(new Block[] { table }))));

        // An embedded workbook (ADR-0029): WORD_XLSX = a real .xlsx, WORD_XLSX_PNG = its preview picture.
        if (Environment.GetEnvironmentVariable("WORD_XLSX") is { Length: > 0 } xlsx && File.Exists(xlsx))
        {
            var previewPath = Environment.GetEnvironmentVariable("WORD_XLSX_PNG");
            var preview = previewPath is { Length: > 0 } && File.Exists(previewPath) ? File.ReadAllBytes(previewPath) : EmbeddedObjectChecks.Png;
            var sheet = new EmbeddedObject(File.ReadAllBytes(xlsx), EmbeddedObjectKind.Package, Length.FromPoints(200), Length.FromPoints(100))
            {
                ProgId = "Excel.Sheet.12",
                Preview = new ImageBlock(preview, ImageMediaType.Png),
            };
            File.WriteAllBytes(Path.Combine(directory, "embedded-workbook.docx"), WriteDocx(new PortableDocument(new DocumentMetadata(), new Section(new Block[]
            {
                new Paragraph(new TextRun("Below is an embedded Excel workbook (double-click opens it in Excel):")), sheet,
            }))));
        }

        // The formulas recovered from a PDF that Word exported (ADR-0034): WORD_PDF = that PDF.
        if (Environment.GetEnvironmentVariable("WORD_PDF") is { Length: > 0 } wordPdf && File.Exists(wordPdf))
        {
            var recovered = new PdfDocumentReader().Read(File.OpenRead(wordPdf), new PdfReadOptions { RecoverStructure = true, RecoverFormulas = true });
            File.WriteAllBytes(Path.Combine(directory, "formula-from-word-pdf.docx"), WriteDocx(recovered.Document));
        }
    }

    /// <summary>FOREIGN_PDFS=dir: reads PDFs that this library did not write and reports what the structure recovery makes of them.</summary>
    private static void ReadForeignPdfs(string directory)
    {
        foreach (var path in Directory.GetFiles(directory, "*.pdf").Order())
        {
            var name = Path.GetFileName(path);
            try
            {
                var bytes = File.ReadAllBytes(path);
                var watch = Stopwatch.StartNew();
                var plain = new PdfDocumentReader().Read(new MemoryStream(bytes));
                var plainTime = watch.ElapsedMilliseconds;
                watch.Restart();
                var recovered = new PdfDocumentReader().Read(new MemoryStream(bytes), new PdfReadOptions { RecoverStructure = true, RecoverFormulas = Environment.GetEnvironmentVariable("FOREIGN_FORMULAS") is { Length: > 0 } });
                var time = watch.ElapsedMilliseconds;
                var blocks = recovered.Document.Sections.SelectMany(section => section.Blocks).ToList();
                var kinds = string.Join(", ", blocks.GroupBy(block => block.GetType().Name).Select(group => $"{group.Key} {group.Count()}"));
                Console.WriteLine($"  {name}: {plain.Document.Sections.Count} page(s); plain {plainTime} ms, recovered {time} ms; {kinds}");
                foreach (var warning in recovered.Warnings.Select(w => w.Message).Distinct())
                {
                    Console.WriteLine("      warning: " + warning);
                }

                foreach (var heading in blocks.OfType<Heading>().Take(6))
                {
                    Console.WriteLine($"      H{(int)heading.Level}: " + string.Concat(heading.Inlines.OfType<TextRun>().Select(run => run.Text)));
                }

                foreach (var equation in blocks.OfType<Equation>().Take(int.TryParse(Environment.GetEnvironmentVariable("FOREIGN_FORMULAS"), out var many) ? many : 0))
                {
                    Console.WriteLine("      EQ: " + Describe(equation.Root) + "   [" + equation.AltText + "]");
                }

                if (Environment.GetEnvironmentVariable("FOREIGN_TABLES") is { Length: > 0 })
                {
                    foreach (var table in blocks.OfType<TableBlock>().Take(12))
                    {
                        Console.WriteLine("      TABLE: " + string.Join(" / ", table.Rows.Take(3).Select(row => string.Join(" | ", row.Cells.Select(cell => string.Concat(cell.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<TextRun>()).Select(r => r.Text)))))));
                    }
                }

                if (Environment.GetEnvironmentVariable("FOREIGN_GREP") is { Length: > 0 } needle)
                {
                    foreach (var text in blocks.OfType<Paragraph>().Select(p => string.Concat(p.Inlines.OfType<TextRun>().Select(r => r.Text))).Where(t => t.Contains(needle, StringComparison.Ordinal)).Take(3))
                    {
                        Console.WriteLine("      GREP: " + text);
                    }
                }

                var wordsRecovered = Words(blocks).ToList();
                var reference = PdfToText(path);
                if (reference is not null)
                {
                    var referenceWords = reference.Split(new[] { ' ', '\n', '\t', '\f' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    var bag = wordsRecovered.GroupBy(word => word).ToDictionary(group => group.Key, group => group.Count());
                    var found = 0;
                    var missing = new List<string>();
                    foreach (var word in referenceWords)
                    {
                        if (bag.TryGetValue(word, out var count) && count > 0)
                        {
                            found++;
                            bag[word] = count - 1;
                        }
                        else
                        {
                            missing.Add(word);
                        }
                    }

                    Console.WriteLine($"      words: pdftotext {referenceWords.Count}, recovered {wordsRecovered.Count}, of the pdftotext words found {100.0 * found / Math.Max(1, referenceWords.Count):0.0} %");
                    if (Environment.GetEnvironmentVariable("FOREIGN_MISSING") is { Length: > 0 })
                    {
                        Console.WriteLine("      missing: " + string.Join(" ", missing.Take(120)));
                    }
                }

                var docx = new MemoryStream();
                DocumentWriter.WriteAsync(recovered.Document, docx, DocumentFormat.Docx).GetAwaiter().GetResult();
                var back = new DocxDocumentReader().Read(new MemoryStream(docx.ToArray()));
                Console.WriteLine($"      DOCX {docx.Length / 1024} KiB, read back {back.Document.Sections.SelectMany(section => section.Blocks).Count()} block(s)");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"  {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static string Describe(MathNode node) => node switch
    {
        MathText text => text.Text,
        MathRow row => string.Concat(row.Items.Select(Describe)),
        MathFraction fraction => "{" + Describe(fraction.Numerator) + "}/{" + Describe(fraction.Denominator) + "}",
        MathScript script => Describe(script.Base) + (script.Superscript is null ? string.Empty : "^(" + Describe(script.Superscript) + ")") + (script.Subscript is null ? string.Empty : "_(" + Describe(script.Subscript) + ")"),
        MathRadical radical => "√[" + (radical.Degree is null ? string.Empty : Describe(radical.Degree)) + "](" + Describe(radical.Radicand) + ")",
        MathNary nary => nary.Operator + "[" + (nary.Lower is null ? string.Empty : Describe(nary.Lower)) + ".." + (nary.Upper is null ? string.Empty : Describe(nary.Upper)) + "](" + Describe(nary.Body) + ")",
        MathDelimiter delimiter => delimiter.Open + string.Concat(delimiter.Items.Select(Describe)) + delimiter.Close,
        _ => "?",
    };

    private static IEnumerable<string> Words(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            IEnumerable<string> texts = block switch
            {
                Paragraph p => [string.Concat(p.Inlines.OfType<TextRun>().Select(r => r.Text))],
                Heading h => [string.Concat(h.Inlines.OfType<TextRun>().Select(r => r.Text))],
                ListBlock l => Words(l.Items.SelectMany(item => item.Blocks)),
                TableBlock t => Words(t.Rows.SelectMany(row => row.Cells.SelectMany(cell => cell.Blocks))),
                _ => [],
            };
            foreach (var text in texts)
            {
                foreach (var word in text.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return word;
                }
            }
        }
    }

    private static string? PdfToText(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("pdftotext", $"\"{path}\" -") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(60_000);
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] WriteDocx(PortableDocument document)
    {
        var stream = new MemoryStream();
        DocumentWriter.WriteAsync(document, stream, DocumentFormat.Docx).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static string First(string page) => page.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    private static IEnumerable<(string, PortableDocument)> Corpus()
    {
        string Words(string seed, int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => seed + (i % 17)));
        var text = new List<Block>();
        for (var i = 0; i < 40; i++)
        {
            text.Add(new Heading(HeadingLevel.Level2, new TextRun("Section " + i)));
            text.Add(new Paragraph(new TextRun(Words("word", 120 + i * 3))));
        }

        yield return ("text", new PortableDocument(new DocumentMetadata(), new Section(text)));

        var table = new List<Block> { new Heading(HeadingLevel.Level1, new TextRun("Tables")) };
        for (var t = 0; t < 6; t++)
        {
            table.Add(new TableBlock(Enumerable.Range(0, 25).Select(r => new TableRow(new TableCell("Line " + r), new TableCell(Words("w", 4 + r % 5)), new TableCell((r * 7).ToString()))).ToList()));
            table.Add(new Paragraph(new TextRun(Words("after", 60))));
        }

        yield return ("tables", new PortableDocument(new DocumentMetadata(), new Section(table)));

        var numbering = new NumberingDefinition("bul", Enumerable.Range(0, NumberingDefinition.MaxLevels).Select(_ => new NumberingLevel { Kind = ListKind.Bullet }));
        var lists = new List<Block>();
        for (var l = 0; l < 12; l++)
        {
            lists.Add(new Paragraph(new TextRun(Words("before", 40))));
            lists.Add(new ListBlock("bul", Enumerable.Range(0, 12).Select(i => new ListItem(new Paragraph(new TextRun(Words("item", 6 + i))))).ToList()));
        }

        yield return ("lists", new PortableDocument(new DocumentMetadata(), new Section(lists)) { Numbering = [numbering] });
    }
}
