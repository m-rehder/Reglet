using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Reglet;

// U03/U04/U06: measures throughput and memory. Runs fixed scenarios, prints a
// table and compares the measurements against the thresholds in build/benchmarks.txt.
// An exceedance aborts the CI.
//
// Note: the PNG creation and root search are deliberately duplicated locally because the
// benchmark project should reference neither the test project nor its helpers.
var root = FindRoot();
var thresholds = LoadThresholds(Path.Combine(root, "build", "benchmarks.txt"));

var results = new List<Result>();
foreach (var scenario in Scenarios.All())
{
    scenario.Run(); // Aufwaermen (JIT, Caches)
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    var retainedBefore = GC.GetTotalMemory(forceFullCollection: true);
    var stopwatch = Stopwatch.StartNew();
    var bytes = scenario.Run();
    stopwatch.Stop();
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    var retained = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - retainedBefore);

    results.Add(new Result(scenario.Name, stopwatch.Elapsed.TotalMilliseconds, allocated, retained, bytes));
}

Console.WriteLine($"{"Szenario",-24} {"Dauer ms",10} {"allokiert",14} {"zurückgehalten",14} {"Bytes",12}");
foreach (var result in results)
{
    Console.WriteLine(
        $"{result.Name,-24} {result.Milliseconds,10:0.0} {result.AllocatedBytes,14:N0} {result.RetainedBytes,14:N0} {result.Bytes,12:N0}");
}

var failures = 0;
foreach (var result in results)
{
    if (!thresholds.TryGetValue(result.Name, out var limit))
    {
        Console.WriteLine($"Note: no threshold for '{result.Name}'.");
        continue;
    }

    if (result.Milliseconds > limit.MaxMilliseconds)
    {
        Console.Error.WriteLine(
            $"REGRESSION: '{result.Name}' dauerte {result.Milliseconds:0.0} ms (Grenze {limit.MaxMilliseconds:0.0} ms).");
        failures++;
    }

    if (result.AllocatedBytes > limit.MaxAllocatedBytes)
    {
        Console.Error.WriteLine(
            $"REGRESSION: '{result.Name}' allokierte {result.AllocatedBytes:N0} Bytes (Grenze {limit.MaxAllocatedBytes:N0}).");
        failures++;
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} benchmark(s) above the threshold.");
    return 1;
}

Console.WriteLine("Benchmarks within the thresholds.");
return 0;

static string FindRoot()
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "Reglet.sln")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static Dictionary<string, Threshold> LoadThresholds(string path)
{
    var thresholds = new Dictionary<string, Threshold>(StringComparer.Ordinal);
    if (!File.Exists(path))
    {
        return thresholds;
    }

    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            continue;
        }

        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxMs)
            && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxBytes))
        {
            thresholds[parts[0]] = new Threshold(maxMs, maxBytes);
        }
    }

    return thresholds;
}

internal sealed record Threshold(double MaxMilliseconds, long MaxAllocatedBytes);

internal sealed record Result(string Name, double Milliseconds, long AllocatedBytes, long RetainedBytes, long Bytes);

internal sealed record Scenario(string Name, Func<long> Run);

internal static class Scenarios
{
    public static IEnumerable<Scenario> All()
    {
        yield return new Scenario("pdf-1000-pages", () => WritePdf(1_000));
        yield return new Scenario("pdf-10000-pages", () => WritePdf(10_000));
        yield return new Scenario("docx-10000-bloecke", () => WriteBlocks(DocumentFormat.Docx));
        yield return new Scenario("odt-10000-bloecke", () => WriteBlocks(DocumentFormat.Odt));
        yield return new Scenario("pdf-large-image", WriteLargeImage);
        yield return new Scenario("parallel-8x1000-pages", WriteParallel);
        var docx = Blocks(DocumentFormat.Docx);
        var odt = Blocks(DocumentFormat.Odt);
        var pdf = Blocks(DocumentFormat.Pdf);
        yield return new Scenario("read-docx-10000-bloecke", () => Read(docx, DocumentFormat.Docx));
        yield return new Scenario("read-odt-10000-bloecke", () => Read(odt, DocumentFormat.Odt));
        yield return new Scenario("read-pdf-10000-bloecke", () => Read(pdf, DocumentFormat.Pdf));
        yield return new Scenario("template-docx-10000-bloecke", () => BindTemplate(docx, DocumentFormat.Docx));
        yield return new Scenario("template-odt-10000-bloecke", () => BindTemplate(odt, DocumentFormat.Odt));
        yield return new Scenario("rewrite-pdf-10000-bloecke", () => RewritePdf(pdf));
    }

    private static long WritePdf(int pages)
    {
        var document = PagedDocument(pages);
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.Length;
    }

    private static long WriteBlocks(DocumentFormat format) => Blocks(format).Length;

    private static byte[] Blocks(DocumentFormat format)
    {
        var builder = PortableDocument.Create();
        for (var index = 0; index < 10_000; index++)
        {
            builder.Paragraph("Paragraph " + index);
        }

        var document = builder.Build();
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static long Read(byte[] file, DocumentFormat format)
        => DocumentReader.Read(new MemoryStream(file), format).Document.Sections[0].Blocks.Count;

    private static long BindTemplate(byte[] file, DocumentFormat format)
    {
        var template = DocumentTemplate.FromBytes(file).BindText("Name", "Value");
        WriteOptions options = format == DocumentFormat.Docx
            ? new DocxWriteOptions { Template = template }
            : new OdtWriteOptions { Template = template };
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(new PortableDocument(new DocumentMetadata(), new Section(Array.Empty<Block>())), buffer, format, options).GetAwaiter().GetResult();
        return buffer.Length;
    }

    private static long RewritePdf(byte[] file)
    {
        using var buffer = new MemoryStream();
        PdfRewriter.Rewrite(new MemoryStream(file), buffer);
        return buffer.Length;
    }

    private static long WriteLargeImage()
    {
        var png = CreatePng(2000, 2000);
        var document = PortableDocument.Create().Image(png, ImageMediaType.Png, altText: "Large").Build();
        using var buffer = new MemoryStream();
        new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.Length;
    }

    private static long WriteParallel()
    {
        var document = PagedDocument(1_000);
        var tasks = new Task<long>[8];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = Task.Run(() =>
            {
                using var buffer = new MemoryStream();
                new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
                return buffer.Length;
            });
        }

        Task.WaitAll(tasks);
        return tasks.Sum(task => task.Result);
    }

    private static PortableDocument PagedDocument(int pages)
    {
        var builder = PortableDocument.Create();
        for (var index = 0; index < pages; index++)
        {
            builder.PageBreak().Paragraph("Page " + index);
        }

        return builder.Build();
    }

    private static byte[] CreatePng(int width, int height)
    {
        // Single-color RGB PNG (valid IDAT data) so that the PDF writer can decode it.
        var raw = new byte[height * (1 + width * 3)];
        var position = 0;
        for (var y = 0; y < height; y++)
        {
            raw[position++] = 0;
            for (var x = 0; x < width; x++)
            {
                raw[position++] = 20;
                raw[position++] = 80;
                raw[position++] = 160;
            }
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = output.ToArray();
        }

        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        AppendChunk(png, "IHDR", ihdr);
        AppendChunk(png, "IDAT", compressed);
        AppendChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void AppendChunk(List<byte> target, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        target.AddRange(length.ToArray());
        target.AddRange(Encoding.ASCII.GetBytes(type));
        target.AddRange(data);
        target.AddRange(new byte[4]);
    }
}
