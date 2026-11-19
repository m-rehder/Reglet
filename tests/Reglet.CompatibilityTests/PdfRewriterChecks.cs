using System.Text;
using Reglet;

/// <summary>
/// AF15: <see cref="PdfRewriter"/> normalises a PDF while preserving the whole reachable object
/// graph. Checks that content, outline and (un)encryption behave as documented.
/// </summary>
internal static class PdfRewriterChecks
{
    internal static int Run()
    {
        try
        {
            PreservesContentAndOutline();
            ReplacesMetadataConsistently();
            DecryptsEncryptedSource();
            ExtractLeavesOtherPagesOut();
            ExtractKeepsLinksBetweenCopiedAnnotations();
            SurvivesDeepOutlines();
            KeepsForeignNamesAndNumbers();
            RejectsFilesWithoutCatalog();
            FollowsLongUpdateChains();
            LimitsObjectStreams();
            LeavesNoPartialOutput();
            ReportsCorruptFlateStreams();
            Console.WriteLine("PDF rewriter (AF15/AF16): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF-Rewriter (AF15): " + exception);
            return 1;
        }
    }

    private static void PreservesContentAndOutline()
    {
        var source = Write(Document());
        Require(
            Encoding.Latin1.GetString(source).Contains("/Outlines", StringComparison.Ordinal),
            "The source has an outline.");

        using var output = new MemoryStream();
        PdfRewriter.Rewrite(new MemoryStream(source), output);
        var rewritten = output.ToArray();

        Require(
            Encoding.Latin1.GetString(rewritten).Contains("/Outlines", StringComparison.Ordinal),
            "The outline survives the rewrite.");

        var before = new PdfDocumentReader().ReadPageTexts(new MemoryStream(source));
        var after = new PdfDocumentReader().ReadPageTexts(new MemoryStream(rewritten));
        Require(before.SequenceEqual(after), "The extracted text is unchanged.");

        _ = PdfFile.Load(rewritten);
    }

    /// <summary>AF16: replacing the metadata updates Info and XMP while everything else survives.</summary>
    private static void ReplacesMetadataConsistently()
    {
        var source = Write(Document());

        using var output = new MemoryStream();
        PdfRewriter.Rewrite(
            new MemoryStream(source),
            output,
            new DocumentMetadata { Title = "New", Author = "Neu", Language = "en-US" });
        var rewritten = output.ToArray();

        var read = new PdfDocumentReader().Read(new MemoryStream(rewritten)).Document;
        Require(read.Metadata.Title == "New", "The Info title is updated.");
        Require(read.Metadata.Author == "Neu", "The Info author is updated.");

        var latin = Encoding.Latin1.GetString(rewritten);
        Require(!latin.Contains("Rewritten", StringComparison.Ordinal), "The old title is gone.");
        Require(latin.Contains("<dc:title>", StringComparison.Ordinal) && latin.Contains("New", StringComparison.Ordinal), "The XMP title is updated.");
        Require(latin.Contains("/Outlines", StringComparison.Ordinal), "The outline survives.");

        var before = new PdfDocumentReader().ReadPageTexts(new MemoryStream(source));
        var after = new PdfDocumentReader().ReadPageTexts(new MemoryStream(rewritten));
        Require(before.SequenceEqual(after), "The text is unchanged.");
    }

    private static void DecryptsEncryptedSource()
    {
        var source = Write(Document(), new PdfEncryptionOptions { UserPassword = "user" });

        using var output = new MemoryStream();
        PdfRewriter.Rewrite(new MemoryStream(source), output, new PdfReadOptions { Password = "user" });
        var rewritten = output.ToArray();

        Require(
            !Encoding.Latin1.GetString(rewritten).Contains("/Encrypt", StringComparison.Ordinal),
            "The rewritten document is decrypted.");

        var text = new PdfDocumentReader().ReadPageTexts(new MemoryStream(rewritten));
        Require(
            text.Count == 1 && text[0].Contains("Body", StringComparison.Ordinal),
            "The decrypted rewrite is readable without a password.");
    }

    private static byte[] Build(IReadOnlyDictionary<int, string> objects)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new SortedDictionary<int, int>();
        foreach (var (number, body) in objects.OrderBy(pair => pair.Key))
        {
            offsets[number] = builder.Length;
            builder.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        var size = objects.Keys.Max() + 1;
        var xref = builder.Length;
        builder.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        for (var number = 1; number < size; number++)
        {
            builder.Append(offsets.TryGetValue(number, out var offset) ? $"{offset:D10} 00000 n \n" : "0000000000 00000 f \n");
        }

        builder.Append($"trailer << /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static string Stream(string text) => $"<< /Length {text.Length} >>\nstream\n{text}\nendstream";

    private static Dictionary<int, string> TwoPages(string firstExtra, string secondType = "/Type /Page")
        => new()
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            [3] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 99 99] /Contents 5 0 R {firstExtra} >>",
            [4] = $"<< {secondType} /Parent 2 0 R /MediaBox [0 0 99 99] /Contents 6 0 R >>",
            [5] = Stream("BT (ONE) Tj ET"),
            [6] = Stream("BT (SECRETPAGE) Tj ET"),
        };

    private static string ExtractFirst(Dictionary<int, string> objects)
    {
        using var output = new MemoryStream();
        PdfPageOperations.Extract(new MemoryStream(Build(objects)), 1, 1, output);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    private static void ExtractLeavesOtherPagesOut()
    {
        var field = TwoPages("/Annots [7 0 R]");
        field[4] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 99 99] /Contents 6 0 R /Annots [8 0 R] >>";
        field[7] = "<< /Type /Annot /Subtype /Widget /Rect [0 0 9 9] /Parent 9 0 R /P 3 0 R >>";
        field[8] = "<< /Type /Annot /Subtype /Widget /Rect [0 0 9 9] /Parent 9 0 R /AP << /N 10 0 R >> >>";
        field[9] = "<< /FT /Tx /T (name) /V (SECRETVALUE) /Kids [7 0 R 8 0 R] >>";
        field[10] = Stream("BT (SECRETLOOK) Tj ET");
        var fromField = ExtractFirst(field);
        Require(fromField.Contains("ONE", StringComparison.Ordinal) && !fromField.Contains("SECRET", StringComparison.Ordinal), "Extract: a shared field tree stays out.");

        var withoutType = TwoPages("/Annots [7 0 R]", secondType: string.Empty);
        withoutType[7] = "<< /Type /Annot /Subtype /Link /Rect [0 0 9 9] /P 4 0 R >>";
        Require(!ExtractFirst(withoutType).Contains("SECRET", StringComparison.Ordinal), "Extract: a page without /Type stays out.");

        var comment = TwoPages("/Annots [7 0 R]");
        comment[7] = "<< /Type /Annot /Subtype /Text /Rect [0 0 9 9] /IRT 8 0 R >>";
        comment[8] = "<< /Type /Annot /Subtype /Text /Rect [0 0 9 9] /Contents (SECRETNOTE) >>";
        Require(!ExtractFirst(comment).Contains("SECRET", StringComparison.Ordinal), "Extract: a comment of another page stays out.");
    }

    private static void ExtractKeepsLinksBetweenCopiedAnnotations()
    {
        var objects = TwoPages("/Annots [7 0 R 8 0 R]");
        objects[7] = "<< /Type /Annot /Subtype /Text /Rect [0 0 9 9] /Popup 8 0 R >>";
        objects[8] = "<< /Type /Annot /Subtype /Popup /Rect [0 0 9 9] /Parent 7 0 R >>";
        var text = ExtractFirst(objects);
        Require(text.Contains("/Popup ", StringComparison.Ordinal) && text.Contains("/Parent ", StringComparison.Ordinal), "Extract: a popup on the page stays linked.");
    }

    private static void SurvivesDeepOutlines()
    {
        foreach (var depth in new[] { 27_000, 28_000 })
        {
            var objects = TwoPages(string.Empty);
            objects[1] = "<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R >>";
            objects[7] = "<< /First 8 0 R >>";
            for (var index = 0; index < depth; index++)
            {
                objects[8 + index] = "<< /Title (x) /Dest [3 0 R /XYZ 0 0 0]" + (index < depth - 1 ? $" /First {9 + index} 0 R" : string.Empty) + " >>";
            }

            try
            {
                ExtractFirst(objects);
            }
            catch (DocumentValidationException)
            {
                // Too deep for this stack: a reported error instead of a dead process.
            }
        }
    }

    private static void KeepsForeignNamesAndNumbers()
    {
        var objects = TwoPages(string.Empty);
        objects[1] = "<< /Type /Catalog /Pages 2 0 R /A 0.00000123 /B 1.7976931348623157 /C 123456789.123456 /D /N#C3#A4me /E -0.5 >>";
        objects[5] = "<< /Length 14 /Nm /Gr#C3#B6 /Key#E4 1 >>\nstream\nBT (ONE) Tj ET\nendstream";
        using var output = new MemoryStream();
        PdfRewriter.Rewrite(new MemoryStream(Build(objects)), output);
        var text = Encoding.Latin1.GetString(output.ToArray());
        foreach (var expected in new[] { "/A 0.00000123 ", "/B 1.7976931348623157 ", "/C 123456789.123456 ", "/D /N#C3#A4me ", "/E -0.5 ", "/Nm /Gr#C3#B6 ", "/Key#E4 " })
        {
            Require(text.Contains(expected, StringComparison.Ordinal), $"Rewrite keeps '{expected}'.");
        }
    }

    private static void RejectsFilesWithoutCatalog()
    {
        foreach (var objects in new[] { new Dictionary<int, string> { [1] = "[1 2 3]" }, new Dictionary<int, string> { [2] = "<< >>" }, new Dictionary<int, string> { [1] = "<< /Type /Catalog >>" } })
        {
            using var output = new MemoryStream();
            var pdf = Build(objects);
            try
            {
                PdfRewriter.Rewrite(new MemoryStream(pdf), output);
                throw new InvalidOperationException("Rewrite accepted a file without a catalog.");
            }
            catch (DocumentValidationException)
            {
                Require(output.Length == 0, "A rejected file writes nothing.");
            }
        }
    }

    private static void FollowsLongUpdateChains()
    {
        var builder = new StringBuilder(Encoding.Latin1.GetString(Build(TwoPages(string.Empty))));
        var previous = int.Parse(builder.ToString().Split("startxref\n")[1].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        for (var section = 0; section < 200; section++)
        {
            var offset = builder.Length;
            builder.Append($"xref\n0 0\ntrailer << /Size 7 /Root 1 0 R /Prev {previous} >>\n");
            previous = offset;
        }

        builder.Append($"startxref\n{previous}\n%%EOF\n");
        var pages = DocumentReader.ReadPdfPages(new MemoryStream(Encoding.Latin1.GetBytes(builder.ToString())));
        Require(pages.Count == 2, $"All update sections are followed, got {pages.Count} pages.");
    }

    private static void LimitsObjectStreams()
    {
        var file = PdfFile.Load(ObjectStreamPdf(streams: 2, padding: 1000));
        file.ObjectStreamBudget = 1500;
        _ = file.GetObject(100);
        Throws<DocumentValidationException>(() => file.GetObject(101), "The object streams exceed their common limit.");

        var enough = PdfFile.Load(ObjectStreamPdf(streams: 2, padding: 1000));
        Require(enough.GetObject(101) is PdfDictionary, "Object streams within the limit are read.");
    }

    private static byte[] ObjectStreamPdf(int streams, int padding)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var entries = new Dictionary<int, (int Type, int A, int B)>();
        void Add(int number, string body)
        {
            entries[number] = (1, builder.Length, 0);
            builder.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Add(2, "<< /Type /Pages /Kids [] /Count 0 >>");
        for (var index = 0; index < streams; index++)
        {
            var head = $"{100 + index} 0 ";
            var data = head + new string(' ', padding) + "<<>>";
            Add(10 + index, $"<< /Type /ObjStm /N 1 /First {head.Length + padding} /Length {data.Length} >>\nstream\n{data}\nendstream");
            entries[100 + index] = (2, 10 + index, 0);
        }

        var size = 100 + streams + 1;
        var xref = builder.Length;
        entries[size] = (1, xref, 0);
        var table = new StringBuilder();
        for (var number = 0; number <= size; number++)
        {
            var (type, a, b) = entries.GetValueOrDefault(number);
            table.Append((char)type).Append((char)((a >> 24) & 255)).Append((char)((a >> 16) & 255)).Append((char)((a >> 8) & 255)).Append((char)(a & 255)).Append((char)(b >> 8)).Append((char)(b & 255));
        }

        builder.Append($"{size} 0 obj\n<< /Type /XRef /Size {size + 1} /W [1 4 2] /Root 1 0 R /Length {table.Length} >>\nstream\n").Append(table).Append($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static void LeavesNoPartialOutput()
    {
        var objects = TwoPages(string.Empty);
        objects[1] = "<< /Type /Catalog /Pages 2 0 R /Broken 7 0 R >>";
        objects[7] = "<< /A [ >>";
        using var output = new MemoryStream();
        Throws<DocumentValidationException>(() => PdfRewriter.Rewrite(new MemoryStream(Build(objects)), output), "The broken object is reported.");
        Require(output.Length == 0, "A failed rewrite leaves the destination empty.");
    }

    private static void ReportsCorruptFlateStreams()
    {
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.ASCII.GetBytes(new string('a', 500)));
        }

        var valid = compressed.ToArray();
        var random = new Random(3);
        for (var round = 0; round < 20_000; round++)
        {
            var data = (byte[])valid.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
            {
                data[random.Next(data.Length)] ^= (byte)random.Next(1, 256);
            }

            data = data[..random.Next(1, data.Length + 1)];
            try
            {
                PdfStreamFilters.Decode(data, [new PdfFilter("FlateDecode", null)]);
            }
            catch (DocumentValidationException)
            {
            }
        }
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static PortableDocument Document()
        => new(
            new DocumentMetadata { Title = "Rewritten", Author = "Me", Language = "en-US" },
            new Section(new Block[]
            {
                new Heading(HeadingLevel.Level1, new TextRun("Heading")),
                new Paragraph(new Inline[] { new Bookmark("mark"), new TextRun("Body") }),
            }));

    private static byte[] Write(PortableDocument document, PdfEncryptionOptions? encryption = null)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter()
            .WriteAsync(document, buffer, new PdfWriteOptions { Encryption = encryption })
            .GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
