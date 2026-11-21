// Writer fuzzing (fifth audit, 2026-11-21): every seed gives a random document (Generator), which is written as
// DOCX, ODT, PDF and HTML; the XML parts must be well-formed, the HTML must not carry script, and DOCX/ODT/PDF must
// read back. A DocumentValidationException or UnsupportedFeatureException is an accepted refusal; any other
// exception is a finding.
//
//   dotnet run -c Release --project tools/WriterFuzz -- <first seed> <count>
//
// A write or read that takes longer than 20 s prints "HANG" and "NEXT <seed>" and ends the process with exit code 4
// (the stuck thread cannot be stopped); tools/WriterFuzz/run.sh starts it again at the next seed.
using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Reglet;

var first = int.Parse(args[0]);
var count = int.Parse(args[1]);
var seen = new HashSet<string>();
var formats = new[] { DocumentFormat.Docx, DocumentFormat.Odt, DocumentFormat.Pdf, DocumentFormat.Html };
for (var seed = first; seed < first + count; seed++)
{
    PortableDocument document;
    try
    {
        document = new Generator(new Random(seed)).Document();
    }
    catch (ArgumentException)
    {
        continue; // the generator built a model the constructors refuse
    }

    foreach (var format in formats)
    {
        var watch = Stopwatch.StartNew();
        var write = Task.Run(() =>
        {
            using var output = new MemoryStream();
            DocumentWriter.WriteAsync(document, output, format).GetAwaiter().GetResult();
            return output.ToArray();
        });
        Within(write, seed, "write " + format);
        byte[] bytes;
        try
        {
            bytes = write.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is DocumentValidationException or UnsupportedFeatureException)
        {
            continue;
        }
        catch (Exception exception)
        {
            Report(seed, "write " + format, exception);
            continue;
        }

        if (watch.ElapsedMilliseconds > 5000)
        {
            Console.WriteLine($"SLOW seed={seed} write {format} {watch.ElapsedMilliseconds} ms");
        }

        try
        {
            Check(format, bytes);
        }
        catch (Exception exception)
        {
            Report(seed, "check " + format, exception);
        }

        if (format == DocumentFormat.Html)
        {
            continue;
        }

        var read = Task.Run(() => DocumentReader.Read(new MemoryStream(bytes), format));
        Within(read, seed, "read " + format);
        try
        {
            read.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Report(seed, "read " + format, exception);
        }
    }

    if (seed % 100 == 0)
    {
        Console.WriteLine($"seed {seed}");
    }
}

Console.WriteLine("done");

static void Within(Task task, int seed, string what)
{
    if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(20)))
    {
        Console.WriteLine($"HANG seed={seed} {what}");
        Console.WriteLine($"NEXT {seed + 1}");
        Environment.Exit(4);
    }
}

void Report(int seed, string where, Exception exception)
{
    var frame = exception.StackTrace?.Split('\n').FirstOrDefault(line => line.Contains("Reglet.", StringComparison.Ordinal))?.Trim() ?? string.Empty;
    if (seen.Add(where + "|" + exception.GetType().Name + "|" + frame))
    {
        Console.WriteLine($"FIND seed={seed} {where}: {exception.GetType().Name}: {exception.Message.Split('\n')[0]} @ {frame}");
    }
}

static void Check(DocumentFormat format, byte[] bytes)
{
    if (format is DocumentFormat.Docx or DocumentFormat.Odt)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".xml", StringComparison.Ordinal) || entry.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            using var stream = entry.Open();
            try
            {
                XDocument.Load(stream);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException(entry.FullName + ": " + exception.Message);
            }
        }
    }
    else if (format == DocumentFormat.Html)
    {
        var html = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var bad in new[] { "<script", "href=\"javascript", "href=\" javascript", "&#39&", "&#59" })
        {
            if (html.Contains(bad, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("HTML contains " + bad);
            }
        }
    }
    else if (!System.Text.Encoding.ASCII.GetString(bytes, 0, 5).StartsWith("%PDF", StringComparison.Ordinal))
    {
        throw new InvalidDataException("no PDF header");
    }
}
