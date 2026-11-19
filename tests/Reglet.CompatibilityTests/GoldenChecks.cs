using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Reglet;

/// <summary>
/// semantic golden tests: normalized XML parts (DOCX/ODT) and PDF properties
/// instead of raw ZIP bytes. Golden files live under tests/.../fixtures/golden.
/// </summary>
internal static class GoldenChecks
{
    private static readonly string GoldenDirectory = GoldenPaths.Directory;

    internal static int Run()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1";
        var failures = 0;
        var written = 0;

        foreach (var (name, document) in RenderFixtures.All())
        {
            var summary = Summarize(document);
            var path = Path.Combine(GoldenDirectory, name + ".golden.txt");

            if (update)
            {
                File.WriteAllText(path, summary);
                written++;
                continue;
            }

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Golden file missing: {name}.golden.txt");
                failures++;
                continue;
            }

            if (File.ReadAllText(path) != summary)
            {
                Console.Error.WriteLine($"Golden '{name}' deviates from the output.");
                failures++;
            }
        }

        if (update)
        {
            Console.WriteLine($"Golden files updated: {written}.");
            return 0;
        }

        if (failures > 0)
        {
            Console.Error.WriteLine($"{failures} golden check(s) failed.");
            return 1;
        }

        Console.WriteLine("Golden tests: all fixtures match.");
        return 0;
    }

    private static string Summarize(PortableDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DOCX word/document.xml");
        builder.AppendLine(Normalize(ReadDocxPart(document, "word/document.xml")));
        builder.AppendLine("# ODT content.xml");
        builder.AppendLine(Normalize(ReadOdtPart(document, "content.xml")));
        builder.AppendLine("# PDF");
        builder.AppendLine(PdfSummary(document));
        return builder.ToString();
    }

    private static string ReadDocxPart(PortableDocument document, string part)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        return ReadEntry(zip, part);
    }

    private static string ReadOdtPart(PortableDocument document, string part)
    {
        using var buffer = new MemoryStream();
        new OdtDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        using var zip = new ZipArchive(new MemoryStream(buffer.ToArray()), ZipArchiveMode.Read);
        return ReadEntry(zip, part);
    }

    private static string PdfSummary(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        var bytes = buffer.ToArray();
        var latin = Encoding.Latin1.GetString(bytes);

        var pages = latin.Split("/Type /Page ", StringSplitOptions.None).Length - 1;
        var fonts = string.Join(
            ",",
            Regex.Matches(latin, @"/BaseFont /([A-Za-z0-9\-]+)")
                .Select(match => match.Groups[1].Value)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal));
        var text = PdfStreams.ExtractText(bytes);

        return $"pages={pages}; fonts={fonts}; text={text}";
    }

    private static string Normalize(string xml)
        => Regex.Replace(xml, @">\s+<", "><").Trim();

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
