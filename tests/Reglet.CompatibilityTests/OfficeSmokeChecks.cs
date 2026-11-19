using System.Diagnostics;
using Reglet;

/// <summary>
/// AF10: optional office smoke. If <c>REGLET_SOFFICE</c> points at a LibreOffice
/// <c>soffice</c> binary, every round-trip corpus document (DOCX/ODT) is converted to PDF; a
/// successful conversion (exit code 0 and an output file) is the headless proxy for "opens without
/// a repair prompt". Without the variable the step is skipped, like the visual regression.
/// </summary>
internal static class OfficeSmokeChecks
{
    internal static int Run()
    {
        try
        {
            var soffice = Environment.GetEnvironmentVariable("REGLET_SOFFICE");
            if (string.IsNullOrEmpty(soffice))
            {
                Console.WriteLine("Office smoke skipped (REGLET_SOFFICE not set).");
                return 0;
            }

            var failures = 0;
            foreach (var item in RoundTripCorpus.Load())
            {
                if (item.Format == DocumentFormat.Pdf)
                {
                    continue;
                }

                var rewritten = TestPackages.Write(TestPackages.Read(item.Original, item.Format), item.Format);
                if (!Converts(soffice, item.Id, item.Format, rewritten))
                {
                    failures++;
                }
            }

            if (failures > 0)
            {
                throw new InvalidOperationException($"{failures} office smoke case(s) failed.");
            }

            ReadsLibreOfficeDocuments(soffice);
            KeepsCommentsInLibreOffice(soffice);
            KeepsRevisionsInLibreOffice(soffice);
            PreservesLibreOfficePdf(soffice);
            ConvertsSignedOdt(soffice);
            Console.WriteLine("Office smoke (AF10): all documents converted without a repair prompt.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Office smoke (AF10): " + exception);
            return 1;
        }
    }

    /// <summary>
    /// OF-E05: a signed ODT still opens in LibreOffice (the signature file must not make the
    /// package invalid).
    /// </summary>
    private static void ConvertsSignedOdt(string soffice)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Reglet Office Smoke",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var document = PortableDocument.Create().Paragraph("Signed ODT").Build();
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(
            document,
            buffer,
            new OdtWriteOptions { Signature = new OdtSignatureOptions(certificate) }).GetAwaiter().GetResult();
        if (!Converts(soffice, "odt-signed", DocumentFormat.Odt, buffer.ToArray()))
        {
            throw new InvalidOperationException("LibreOffice could not open the signed ODT.");
        }
    }

    /// <summary>
    /// OF-A05: LibreOffice resaves every corpus document in the other format; the reader then
    /// opens the LibreOffice-written DOCX/ODT and the visible text survives.
    /// </summary>
    private static void ReadsLibreOfficeDocuments(string soffice)
    {
        var failures = 0;
        foreach (var item in RoundTripCorpus.Load())
        {
            if (item.Format == DocumentFormat.Pdf)
            {
                continue;
            }

            var target = item.Format == DocumentFormat.Docx ? DocumentFormat.Odt : DocumentFormat.Docx;
            var source = DocumentReader.Read(new MemoryStream(item.Original), item.Format).Document;
            var expected = Normalize(PlainText(source));
            if (expected.Length == 0)
            {
                continue;
            }

            var converted = Convert(soffice, item.Id, item.Format, target, item.Original);
            if (converted is null)
            {
                Console.Error.WriteLine($"Office import '{item.Id}': LibreOffice produced no {target} file.");
                failures++;
                continue;
            }

            var read = DocumentReader.Read(new MemoryStream(converted), target);
            var actual = Normalize(PlainText(read.Document));
            if (!actual.Contains(expected, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"Office import '{item.Id}': the visible text changed.");
                failures++;
            }
        }

        if (failures > 0)
        {
            throw new InvalidOperationException($"{failures} LibreOffice import case(s) failed.");
        }
    }

    /// <summary>
    /// ADR-0026: LibreOffice reads the comments of a generated DOCX and writes them again (DOCX → ODT → DOCX); the
    /// own reader finds the authors, the text, the reply and the done flag in what LibreOffice wrote.
    /// </summary>
    private static void KeepsCommentsInLibreOffice(string soffice)
    {
        var docx = TestPackages.Write(CommentChecks.Sample(), DocumentFormat.Docx);
        var odt = Convert(soffice, "comments", DocumentFormat.Docx, DocumentFormat.Odt, docx)
            ?? throw new InvalidOperationException("LibreOffice could not convert the DOCX with comments to ODT.");
        var back = Convert(soffice, "comments-back", DocumentFormat.Odt, DocumentFormat.Docx, odt)
            ?? throw new InvalidOperationException("LibreOffice could not convert the ODT with comments to DOCX.");
        // The own ODT reader reads what LibreOffice wrote (the reply comes before its parent there).
        var fromOdt = DocumentReader.Read(new MemoryStream(odt), DocumentFormat.Odt).Document;
        RequireThread(fromOdt, "LibreOffice ODT");
        // And LibreOffice reads the own ODT: ODT → DOCX.
        var ownOdt = TestPackages.Write(CommentChecks.Sample(), DocumentFormat.Odt);
        var viaDocx = Convert(soffice, "comments-own", DocumentFormat.Odt, DocumentFormat.Docx, ownOdt)
            ?? throw new InvalidOperationException("LibreOffice could not convert the own ODT with comments to DOCX.");
        RequireThread(DocumentReader.Read(new MemoryStream(viaDocx), DocumentFormat.Docx).Document, "own ODT through LibreOffice");
        var read = DocumentReader.Read(new MemoryStream(back), DocumentFormat.Docx).Document;
        var comments = read.Sections.SelectMany(section => section.Blocks).OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines).OfType<Comment>().ToList();
        if (comments.Count != 3 || !comments.Select(comment => comment.Author).SequenceEqual(new[] { "Ann", "Cy", "Di" }))
        {
            throw new InvalidOperationException("LibreOffice lost comments: " + string.Join(",", comments.Select(comment => comment.Author)));
        }

        var thread = comments[0];
        if (thread.Replies.Count != 1 || thread.Replies[0].Author != "Bob" || !thread.Resolved
            || thread.Paragraphs[0] != "First & <more>")
        {
            throw new InvalidOperationException("LibreOffice changed the thread: " + thread.Replies.Count + " replies, resolved " + thread.Resolved);
        }
    }

    /// <summary>
    /// ADR-0027: LibreOffice reads the tracked changes of a generated DOCX and ODT and writes them again; the own
    /// reader finds the kind, the author and the text of every change in what LibreOffice wrote.
    /// </summary>
    private static void KeepsRevisionsInLibreOffice(string soffice)
    {
        var expected = new[] { "ins:Ann:new", "del:Bob:old", "del:Ann:gone", "del:Ann:too" };
        var docx = TestPackages.Write(RevisionChecks.Sample(), DocumentFormat.Docx);
        var odt = Convert(soffice, "revisions", DocumentFormat.Docx, DocumentFormat.Odt, docx)
            ?? throw new InvalidOperationException("LibreOffice could not convert the DOCX with tracked changes to ODT.");
        RequireChanges(DocumentReader.Read(new MemoryStream(odt), DocumentFormat.Odt).Document, expected, "DOCX through LibreOffice to ODT");
        var ownOdt = TestPackages.Write(RevisionChecks.Sample(), DocumentFormat.Odt);
        var viaDocx = Convert(soffice, "revisions-own", DocumentFormat.Odt, DocumentFormat.Docx, ownOdt)
            ?? throw new InvalidOperationException("LibreOffice could not convert the own ODT with tracked changes to DOCX.");
        RequireChanges(DocumentReader.Read(new MemoryStream(viaDocx), DocumentFormat.Docx).Document, expected, "own ODT through LibreOffice to DOCX");
    }

    private static void RequireChanges(PortableDocument document, string[] expected, string label)
    {
        // kind:author:text per change, in document order; a change across paragraphs counts per piece.
        var found = new List<string>();
        var open = new Dictionary<int, (string Kind, string Author, System.Text.StringBuilder Text)>();
        foreach (var inline in document.Sections.SelectMany(section => section.Blocks).OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines))
        {
            switch (inline)
            {
                case TrackedChange change:
                    open[change.Id] = (change.Kind == TrackedChangeKind.Insertion ? "ins" : "del", change.Author, new System.Text.StringBuilder());
                    break;
                case TrackedChangeEnd end when open.Remove(end.Id, out var done):
                    found.Add($"{done.Kind}:{done.Author}:{done.Text}");
                    break;
                case TextRun run:
                    foreach (var entry in open.Values)
                    {
                        entry.Text.Append(run.Text);
                    }

                    break;
            }
        }

        if (!expected.All(found.Contains))
        {
            throw new InvalidOperationException($"{label}: tracked changes changed: {string.Join(", ", found)}");
        }
    }

    private static void RequireThread(PortableDocument document, string label)
    {
        var comments = document.Sections.SelectMany(section => section.Blocks).OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines).OfType<Comment>().ToList();
        if (comments.Count != 3 || !comments.Select(comment => comment.Author).SequenceEqual(new[] { "Ann", "Cy", "Di" })
            || comments[0].Replies.Count != 1 || comments[0].Replies[0].Author != "Bob" || !comments[0].Resolved)
        {
            throw new InvalidOperationException($"{label}: the comments or the thread changed ({comments.Count} comments).");
        }
    }

    private static byte[]? Convert(
        string soffice,
        string id,
        DocumentFormat sourceFormat,
        DocumentFormat targetFormat,
        byte[] document)
    {
        var work = Path.Combine(Path.GetTempPath(), "reglet-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var extension = sourceFormat == DocumentFormat.Docx ? ".docx" : ".odt";
            var targetExtension = targetFormat == DocumentFormat.Docx ? "docx" : "odt";
            var input = Path.Combine(work, id + extension);
            File.WriteAllBytes(input, document);
            var startInfo = new ProcessStartInfo(soffice)
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add(targetExtension);
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(work);
            startInfo.ArgumentList.Add(input);
            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(120_000) || process.ExitCode != 0)
            {
                process?.Kill(entireProcessTree: true);
                return null;
            }

            var output = Path.Combine(work, id + "." + targetExtension);
            return File.Exists(output) ? File.ReadAllBytes(output) : null;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }

    private static string Normalize(string text)
        => new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static string PlainText(PortableDocument document)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var section in document.Sections)
        {
            AppendBlocks(builder, section.Blocks);
        }

        return builder.ToString();
    }

    private static void AppendBlocks(System.Text.StringBuilder builder, IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    AppendInlines(builder, paragraph.Inlines);
                    break;
                case Heading heading:
                    AppendInlines(builder, heading.Inlines);
                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        AppendBlocks(builder, item.Blocks);
                    }

                    break;
                case TableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            AppendBlocks(builder, cell.Blocks);
                        }
                    }

                    break;
            }
        }
    }

    private static void AppendInlines(System.Text.StringBuilder builder, IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is TextRun run)
            {
                builder.Append(run.Text);
            }
        }
    }

    /// <summary>
    /// AF15/AF16: a PDF produced by LibreOffice (not by Reglet) is extracted, merged and
    /// rewritten, and the text survives each operation.
    /// </summary>
    private static void PreservesLibreOfficePdf(string soffice)
    {
        var document = PortableDocument.Create()
            .Heading("LibreOffice PDF", level: 1)
            .Paragraph("The quick brown fox jumps over the lazy dog.")
            .BulletList("first", "second")
            .Build();

        var work = Path.Combine(Path.GetTempPath(), "reglet-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var docx = Path.Combine(work, "source.docx");
            using (var file = File.Create(docx))
            {
                new DocxDocumentWriter().WriteAsync(document, file).GetAwaiter().GetResult();
            }

            if (!TryConvertToPdf(soffice, work, docx))
            {
                throw new InvalidOperationException("LibreOffice could not produce a PDF.");
            }

            var pdf = File.ReadAllBytes(Path.Combine(work, "source.pdf"));
            var texts = new PdfDocumentReader().ReadPageTexts(new MemoryStream(pdf));
            if (!texts.Any(text => text.Contains("quick brown fox", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("The LibreOffice PDF text is not extracted.");
            }

            using var rewritten = new MemoryStream();
            PdfRewriter.Rewrite(new MemoryStream(pdf), rewritten);
            var rewrittenTexts = new PdfDocumentReader().ReadPageTexts(new MemoryStream(rewritten.ToArray()));
            if (!texts.SequenceEqual(rewrittenTexts))
            {
                throw new InvalidOperationException("The rewrite changed the LibreOffice PDF text.");
            }

            using var extracted = new MemoryStream();
            PdfPageOperations.Extract(new MemoryStream(pdf), 1, 1, extracted);
            if (new PdfDocumentReader().ReadPageTexts(new MemoryStream(extracted.ToArray())).Count != 1)
            {
                throw new InvalidOperationException("The page extraction of the LibreOffice PDF failed.");
            }

            using var merged = new MemoryStream();
            PdfPageOperations.Merge([new MemoryStream(pdf), new MemoryStream(pdf)], merged);
            var mergedTexts = new PdfDocumentReader().ReadPageTexts(new MemoryStream(merged.ToArray()));
            if (mergedTexts.Count != texts.Count * 2)
            {
                throw new InvalidOperationException("The merge of the LibreOffice PDF failed.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }

    private static bool TryConvertToPdf(string soffice, string work, string input)
    {
        var startInfo = new ProcessStartInfo(soffice)
        {
            WorkingDirectory = work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--convert-to");
        startInfo.ArgumentList.Add("pdf");
        startInfo.ArgumentList.Add("--outdir");
        startInfo.ArgumentList.Add(work);
        startInfo.ArgumentList.Add(input);
        using var process = Process.Start(startInfo);
        return process is not null
            && process.WaitForExit(120_000)
            && process.ExitCode == 0
            && File.Exists(Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".pdf"));
    }

    private static bool Converts(string soffice, string id, DocumentFormat format, byte[] document)
    {
        var work = Path.Combine(Path.GetTempPath(), "reglet-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var extension = format == DocumentFormat.Docx ? ".docx" : ".odt";
            var input = Path.Combine(work, id + extension);
            File.WriteAllBytes(input, document);

            var startInfo = new ProcessStartInfo(soffice)
            {
                WorkingDirectory = work,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(work);
            startInfo.ArgumentList.Add(input);

            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(120_000))
            {
                process?.Kill(entireProcessTree: true);
                Console.Error.WriteLine($"Office smoke '{id}': the converter did not finish.");
                return false;
            }

            var pdf = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".pdf");
            if (process.ExitCode != 0 || !File.Exists(pdf))
            {
                Console.Error.WriteLine($"Office smoke '{id}': exit code {process.ExitCode}, output present: {File.Exists(pdf)}.");
                return false;
            }

            return true;
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not a test failure.
            }
        }
    }
}
