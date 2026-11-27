using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Reglet;

/// <summary>
/// phase R: PDF/A. Checks the archive output (R02), attachments (R03), XMP consistency (R04)
/// and — if configured — the external validator (R01/R05).
/// </summary>
internal static class PdfAChecks
{
    internal static int Run()
    {
        try
        {
            WritesPdfA2b();
            BuildsValidSrgbIccProfile();
            RejectsPdfAWithoutEmbeddedFont();
            WritesPdfA3Attachments();
            RejectsAttachmentsInPdfA2();
            WritesAnnotationAppearances();
            RejectsMissingGlyph();
            ToleratesInvisibleCharacters();
            KeepsConformanceInPageOperations();
            RejectsUnusableIccProfile();
            KeepsXmpWellFormedWithControlCharacters();
            ValidatesWithVeraPdfWhenConfigured();
            Console.WriteLine("PDF/A (R01–R05): all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("PDF/A (R): " + exception);
            return 1;
        }
    }

    /// <summary>R02: the supplied ICC profile is structurally valid (header, tag table, tags).</summary>
    private static void BuildsValidSrgbIccProfile()
    {
        var profile = PdfIccProfile.Srgb();
        Require(BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(0, 4)) == profile.Length, "Profile size matches.");
        Require(BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(8, 4)) == 0x02100000, "ICC-Version 2.1.");
        AssertContains(Encoding.ASCII.GetString(profile, 36, 4), "acsp");
        AssertContains(Encoding.ASCII.GetString(profile, 12, 4), "mntr");
        AssertContains(Encoding.ASCII.GetString(profile, 16, 4), "RGB ");
        AssertContains(Encoding.ASCII.GetString(profile, 20, 4), "XYZ ");

        var tagCount = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128, 4));
        Require(tagCount >= 9, $"At least 9 tags (is {tagCount}).");

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tagCount; i++)
        {
            var entry = 132 + (int)i * 12;
            signatures.Add(Encoding.ASCII.GetString(profile, entry, 4));
            var tagOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 4, 4));
            var tagSize = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(entry + 8, 4));
            Require(tagOffset >= 0 && tagSize > 0 && tagOffset + tagSize <= profile.Length, "Tag lies in the profile.");
        }

        foreach (var required in new[] { "desc", "wtpt", "rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC", "cprt" })
        {
            Require(signatures.Contains(required), $"Tag '{required}' present.");
        }
    }

    /// <summary>R02: PDF/A-2b with OutputIntent, trailer ID and consistent XMP.</summary>
    private static void WritesPdfA2b()
    {
        var bytes = Write(PdfAConformance.PdfA2b, attachments: null);
        var latin = Encoding.Latin1.GetString(bytes);

        AssertContains(latin, "/OutputIntents [");
        AssertContains(latin, "/S /GTS_PDFA1");
        AssertContains(latin, "/DestOutputProfile ");
        AssertContains(latin, "/N 3 ");
        AssertContains(latin, "/ID [<");
        AssertContains(latin, "<pdfaid:part>2</pdfaid:part>");
        AssertContains(latin, "<pdfaid:conformance>B</pdfaid:conformance>");

        // PDF/A forbids the standard 14 fonts; only the embedded font may occur.
        Require(!latin.Contains("/BaseFont /Helvetica", StringComparison.Ordinal), "No default font in PDF/A.");
        AssertContains(latin, "/FontFile2 ");
    }

    /// <summary>R02: without an embedded font PDF/A is rejected.</summary>
    private static void RejectsPdfAWithoutEmbeddedFont()
    {
        var document = PortableDocument.Create().Paragraph("Hello").Build();
        using var buffer = new MemoryStream();
        Throws<UnsupportedFeatureException>(() => new PdfDocumentWriter()
            .WriteAsync(document, buffer, new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b })
            .GetAwaiter().GetResult());
    }

    /// <summary>R03: PDF/A-3 embeds attachments as /EmbeddedFile with a relationship.</summary>
    private static void WritesPdfA3Attachments()
    {
        var attachment = new PdfAttachment
        {
            Name = "data.txt",
            Data = Encoding.UTF8.GetBytes("Appendix"),
            MimeType = "text/plain",
            Description = "Sample data",
            Relationship = "Data",
        };

        var bytes = Write(PdfAConformance.PdfA3b, [attachment]);
        var latin = Encoding.Latin1.GetString(bytes);
        AssertContains(latin, "<pdfaid:part>3</pdfaid:part>");
        AssertContains(latin, "/Type /EmbeddedFile ");
        AssertContains(latin, "/Subtype /text#2Fplain");
        AssertContains(latin, "/Type /Filespec");
        AssertContains(latin, "/AFRelationship /Data");
        AssertContains(latin, "/EmbeddedFiles ");
        AssertContains(latin, "(data.txt)");
        Require(System.Text.RegularExpressions.Regex.IsMatch(Latin(Extracted(bytes)), @"/AF\s*\["), "an extracted PDF/A-3 keeps the catalog's /AF.");
    }

    /// <summary>R03: PDF/A-2 rejects attachments.</summary>
    private static void RejectsAttachmentsInPdfA2()
    {
        var attachment = new PdfAttachment { Name = "a.txt", Data = Encoding.UTF8.GetBytes("x") };
        var document = PortableDocument.Create().Paragraph("Hello").Build();
        using var buffer = new MemoryStream();
        Throws<ArgumentException>(() => new PdfDocumentWriter()
            .WriteAsync(
                document,
                buffer,
                new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b, Attachments = [attachment] })
            .GetAwaiter().GetResult());
    }

    /// <summary>Optional external PDF/A validator (path in REGLET_VERAPDF).</summary>
    private static void ValidatesWithVeraPdfWhenConfigured()
    {
        var verapdf = Environment.GetEnvironmentVariable("REGLET_VERAPDF");
        if (string.IsNullOrEmpty(verapdf) || !File.Exists(verapdf))
        {
            Console.WriteLine("PDF/A validator skipped (REGLET_VERAPDF not set).");
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "reglet-pdfa-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var sample = 0;
            foreach (var pdf in ValidatorSamples())
            {
                sample++;
                File.WriteAllBytes(path, pdf);

                var startInfo = new ProcessStartInfo(verapdf) { RedirectStandardOutput = true, RedirectStandardError = true };
                startInfo.ArgumentList.Add("--format");
                startInfo.ArgumentList.Add("text");
                startInfo.ArgumentList.Add("--verbose");
                startInfo.ArgumentList.Add(path);
                using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Validator could not be started.");
                var report = process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                Require(process.ExitCode == 0, $"veraPDF reports deviations in sample {sample} (code {process.ExitCode}): {report.GetAwaiter().GetResult()}");
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>A title and author with a control character, which XML 1.0 cannot carry.</summary>
    private static byte[] WithControlCharacters(PdfAConformance conformance)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            PortableDocument.Create().Metadata(title: "Ti\u0001tle & Co", author: "Au\u0002thor", language: "de\u0003").Paragraph("Hello").Build(),
            buffer,
            new PdfWriteOptions { PdfA = conformance, Fonts = new Dictionary<string, byte[]> { [""] = Font() } }).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static void KeepsXmpWellFormedWithControlCharacters()
    {
        var written = WithControlCharacters(PdfAConformance.PdfA2b);
        var rewritten = Rewritten(Write(PdfAConformance.PdfA2b, attachments: null), new DocumentMetadata { Title = "Ti\u0001tle & Co", Author = "Au\u0002thor" });
        foreach (var pdf in new[] { written, rewritten })
        {
            var text = Encoding.UTF8.GetString(pdf);
            var start = text.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
            var end = text.IndexOf("</x:xmpmeta>", StringComparison.Ordinal);
            Require(start >= 0 && end > start, "the PDF carries an XMP package.");
            var xmp = new System.Xml.XmlDocument();
            xmp.LoadXml(text[start..(end + "</x:xmpmeta>".Length)]);
            Require(xmp.InnerText.Contains("Ti\uFFFDtle & Co", StringComparison.Ordinal), "XMP keeps the title with U+FFFD.");

            // The Info entries mirror XMP, as PDF/A requires.
            var metadata = new PdfDocumentReader().Read(new MemoryStream(pdf)).Document.Metadata;
            Require(metadata.Title == "Ti\uFFFDtle & Co" && metadata.Author == "Au\uFFFDthor", $"Info matches XMP (title '{metadata.Title}').");
        }
    }

    private static IEnumerable<byte[]> ValidatorSamples()
    {
        yield return Write(PdfAConformance.PdfA2b, attachments: null);
        yield return WriteOptions(new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b, Annotations = AllAnnotations() });
        var source = Write(PdfAConformance.PdfA2b, attachments: null);
        yield return Extracted(source);
        yield return Merged(source, source);
        yield return Rewritten(source, new DocumentMetadata { Title = "New" });
        yield return WithControlCharacters(PdfAConformance.PdfA2b);

        PdfAttachment[] attachments = [new() { Name = "data.txt", Data = Encoding.UTF8.GetBytes("Appendix"), MimeType = "text/plain", Relationship = "Data" }];
        var source3 = Write(PdfAConformance.PdfA3b, attachments);
        yield return source3;
        yield return WriteOptions(new PdfWriteOptions { PdfA = PdfAConformance.PdfA3b, Attachments = attachments, Annotations = AllAnnotations() });
        yield return Extracted(source3);
        yield return Merged(source3, source3);
        yield return Rewritten(source3, new DocumentMetadata { Title = "New" });
    }

    private static PdfAnnotation[] AllAnnotations() =>
    [
        new() { Kind = PdfAnnotationKind.Text, X = 100, Y = 100 },
        new() { Kind = PdfAnnotationKind.Highlight, X = 100, Y = 200, Width = 50, Height = 10 },
        new() { Kind = PdfAnnotationKind.Stamp, X = 100, Y = 300, Width = 50, Height = 30, Content = "Hello" },
        new() { Kind = PdfAnnotationKind.FreeText, X = 100, Y = 400, Width = 50, Height = 30, Content = "Hello" },
    ];

    private static byte[] WriteOptions(PdfWriteOptions options, string text = "Hello")
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            PortableDocument.Create().Paragraph(text).Build(),
            buffer,
            options with { Fonts = new Dictionary<string, byte[]> { [""] = Font() } }).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static byte[] Extracted(byte[] source)
    {
        using var output = new MemoryStream();
        PdfPageOperations.Extract(new MemoryStream(source), 1, 1, output);
        return output.ToArray();
    }

    private static byte[] Merged(params byte[][] sources)
    {
        using var output = new MemoryStream();
        PdfPageOperations.Merge(sources.Select(source => (Stream)new MemoryStream(source)), output);
        return output.ToArray();
    }

    private static byte[] Rewritten(byte[] source, DocumentMetadata metadata)
    {
        using var output = new MemoryStream();
        PdfRewriter.Rewrite(new MemoryStream(source), output, metadata);
        return output.ToArray();
    }

    private static string Latin(byte[] pdf) => System.Text.Encoding.Latin1.GetString(pdf);

    private static void WritesAnnotationAppearances()
    {
        var text = Latin(WriteOptions(new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b, Annotations = AllAnnotations() }));
        Require(System.Text.RegularExpressions.Regex.Matches(text, "/AP").Count == 4, "every annotation of a PDF/A file has an appearance.");
        Require(text.Contains("/QuadPoints", StringComparison.Ordinal) && text.Contains("/DA", StringComparison.Ordinal), "the required entries of highlight and free text are written.");
        var plain = Latin(WriteOptions(new PdfWriteOptions { Annotations = AllAnnotations() }));
        Require(!plain.Contains("/AP", StringComparison.Ordinal), "other files leave the appearance to the viewer.");
    }

    private static void RejectsMissingGlyph()
    {
        try
        {
            WriteOptions(new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b }, "Hello Z");
        }
        catch (UnsupportedFeatureException)
        {
            return;
        }

        throw new InvalidOperationException("PDF/A must not draw the replacement glyph.");
    }

    private static void ToleratesInvisibleCharacters()
    {
        // A tab and invisible characters are no missing glyphs; a strict run must not stop at them.
        var strict = new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b, UnsupportedContent = UnsupportedContentBehavior.Fail };
        WriteOptions(strict, "Hal\tlo");
        WriteOptions(strict, "Ha\uFEFFllo\u0001");

        var pages = DocumentReader.ReadPdfPages(new MemoryStream(WriteOptions(new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b }, "Hal\u2028lo")));
        Require(pages[0].Contains('\n', StringComparison.Ordinal), "a line separator breaks the line.");
    }

    private static void KeepsConformanceInPageOperations()
    {
        var source = Write(PdfAConformance.PdfA2b, attachments: null);
        foreach (var kept in new[] { Extracted(source), Merged(source, source) })
        {
            var text = Latin(kept);
            Require(text.Contains("pdfaid:part", StringComparison.Ordinal) && text.Contains("/OutputIntents", StringComparison.Ordinal) && text.Contains("/ID", StringComparison.Ordinal), "a PDF/A selection stays PDF/A.");
        }

        var plain = WriteOptions(new PdfWriteOptions());
        Require(!Latin(Merged(source, plain)).Contains("pdfaid:part", StringComparison.Ordinal), "a mix must not claim PDF/A.");

        var rewritten = Latin(Rewritten(source, new DocumentMetadata { Title = "New" }));
        Require(rewritten.Contains("pdfaid:part", StringComparison.Ordinal) && rewritten.Contains("/ID", StringComparison.Ordinal), "a rewrite keeps the claim and the identifier.");
    }

    private static void RejectsUnusableIccProfile()
    {
        WriteOptions(new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b, IccProfile = PdfIccProfile.Srgb() });
        try
        {
            WriteOptions(new PdfWriteOptions { PdfA = PdfAConformance.PdfA2b, IccProfile = new byte[200] });
        }
        catch (DocumentValidationException)
        {
            return;
        }

        throw new InvalidOperationException("A file that is no ICC output profile is refused.");
    }

    private static byte[] Write(PdfAConformance conformance, IReadOnlyList<PdfAttachment>? attachments)
    {
        var document = PortableDocument.Create().Paragraph("Hello").Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(
            document,
            buffer,
            new PdfWriteOptions
            {
                PdfA = conformance,
                Fonts = new Dictionary<string, byte[]> { [""] = Font() },
                Attachments = attachments,
            }).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    /// <summary>Small synthetic font for "Hello" (glyphs 3–6).</summary>
    internal static byte[] Font() => Font("PdfATest", ('H', 3), ('a', 4), ('e', 4), ('l', 5), ('o', 6));

    /// <summary>A small TrueType font with its own name and character map (glyph ids 1 to 6).</summary>
    internal static byte[] Font(string name, params (int Code, ushort Glyph)[] characters) => Font(name, null, characters);

    /// <summary>The same with an OS/2 table (version 1: Windows ascent 900, descent 300 and the given code pages).</summary>
    internal static byte[] Font(string name, uint? codePages, params (int Code, ushort Glyph)[] characters)
    {
        const int count = 7;
        var glyf = new List<byte>();
        var offsets = new List<uint> { 0 };
        for (var i = 0; i < count; i++)
        {
            glyf.AddRange(new byte[10]);
            while (glyf.Count % 4 != 0)
            {
                glyf.Add(0);
            }

            offsets.Add((uint)glyf.Count);
        }

        var loca = new byte[offsets.Count * 4];
        for (var i = 0; i < offsets.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4, 4), offsets[i]);
        }

        var head = SyntheticTrueTypeFont.Head(1000, 0, 0, 500, 800);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50, 2), 1);

        var metrics = Enumerable.Repeat((Advance: (ushort)500, Lsb: (short)0), count).ToArray();
        var tables = new List<(string Tag, byte[] Data)>
        {
            ("head", head),
            ("hhea", SyntheticTrueTypeFont.Hhea(800, -200, 0, count)),
            ("maxp", SyntheticTrueTypeFont.Maxp(count)),
            ("hmtx", SyntheticTrueTypeFont.Hmtx(metrics)),
            ("name", SyntheticTrueTypeFont.Name(name)),
            ("cmap", SyntheticTrueTypeFont.Cmap12(characters)),
            ("glyf", glyf.ToArray()),
            ("loca", loca),
        };

        if (codePages is { } pages)
        {
            tables.Add(("OS/2", Os2(pages)));
        }

        return SyntheticTrueTypeFont.Build(tables.ToArray());
    }

    private static byte[] Os2(uint codePages)
    {
        var os2 = new byte[86];
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4, 2), 400);
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(74, 2), 900);
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(76, 2), 300);
        BinaryPrimitives.WriteUInt32BigEndian(os2.AsSpan(78, 4), codePages);
        return os2;
    }

    private static void AssertContains(string text, string expected)
        => Require(text.Contains(expected, StringComparison.Ordinal), $"Expected: {expected}");

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
