using System.Diagnostics;
using Reglet;

/// <summary>
/// K11: visual regression. Checks image differences with tolerance (ImageDiff) and renders —
/// if an external renderer is configured — the fixtures against approved
/// baselines. Without a renderer the render step is skipped.
/// </summary>
internal static class VisualRegressionChecks
{
    private static byte[] Gray(int width, int height, Func<int, int, byte> pixel)
    {
        using var output = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(type);
            output.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(data.Length)));
            output.Write(name);
            output.Write(data);
            var crc = 0xFFFFFFFFu;
            foreach (var value in name.Concat(data))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }

            output.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder((int)~crc)));
        }

        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 0;
        Chunk("IHDR", header);
        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                raw[(y * (width + 1)) + 1 + x] = pixel(x, y);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return output.ToArray();
    }

    internal static int Run()
    {
        try
        {
            SelfTest();
            return RenderIfAvailable();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Visuelle Regression: " + exception);
            return 1;
        }
    }

    private static void SelfTest()
    {
        // A small drawn area counts against what is drawn, not against the white page.
        var page = Gray(100, 100, (x, y) => x < 10 && y < 10 ? (byte)0 : (byte)255);
        var moved = Gray(100, 100, (x, y) => x is >= 5 and < 15 && y < 10 ? (byte)0 : (byte)255);
        Require(ImageDiff.DifferenceRatio(page, moved) > 0.01, "a change of a small drawn area is noticed on a white page.");

        var reference = RenderFixtures.CreatePng(4, 4, 10, 20, 30);
        var identical = RenderFixtures.CreatePng(4, 4, 10, 20, 30);
        var different = RenderFixtures.CreatePng(4, 4, 200, 20, 30);

        Require(ImageDiff.DifferenceRatio(reference, identical) == 0, "Identical images have no difference.");
        Require(ImageDiff.DifferenceRatio(reference, different) > 0.1, "A clear color change is detected.");
        Require(ImageDiff.DifferenceRatio(reference, RenderFixtures.CreatePng(2, 2)) == 1,
            "Different sizes count as completely different.");
    }

    private static int RenderIfAvailable()
    {
        var soffice = Environment.GetEnvironmentVariable("REGLET_SOFFICE");
        var rasterizer = Environment.GetEnvironmentVariable("REGLET_PDFTOPPM");
        if (string.IsNullOrEmpty(soffice) || string.IsNullOrEmpty(rasterizer))
        {
            Console.WriteLine("Visual regression skipped (REGLET_SOFFICE/REGLET_PDFTOPPM not set).");
            return 0;
        }

        var baselineDirectory = Path.Combine(GoldenPaths.RepositoryRoot, "tests", "Reglet.CompatibilityTests", "fixtures", "baselines");
        var update = Environment.GetEnvironmentVariable("UPDATE_BASELINES") == "1";
        var failures = 0;

        foreach (var (name, document) in RenderFixtures.All())
        {
            var rendered = RenderFixture(name, document, soffice, rasterizer);
            var baselinePath = Path.Combine(baselineDirectory, name + ".png");

            if (update || !File.Exists(baselinePath))
            {
                Directory.CreateDirectory(baselineDirectory);
                File.WriteAllBytes(baselinePath, rendered);
                continue;
            }

            var difference = ImageDiff.DifferenceRatio(File.ReadAllBytes(baselinePath), rendered);
            if (difference > 0.01)
            {
                Console.Error.WriteLine($"Visuelle Regression '{name}': {difference:P2} Abweichung.");
                failures++;
            }
        }

        Require(failures == 0, $"{failures} visuelle Fixture(s) weichen ab.");
        Console.WriteLine("Visual regression: all fixtures within tolerance.");
        return 0;
    }

    private static byte[] RenderFixture(string name, PortableDocument document, string soffice, string rasterizer)
    {
        var work = Path.Combine(Path.GetTempPath(), "reglet-visual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var docx = Path.Combine(work, name + ".docx");
            using (var stream = File.Create(docx))
            {
                new DocxDocumentWriter().WriteAsync(document, stream).GetAwaiter().GetResult();
            }

            RunProcess(soffice, ["--headless", "-env:UserInstallation=file://" + Path.Combine(work, "profile"), "--convert-to", "pdf", "--outdir", work, docx]);
            var pdf = Path.Combine(work, name + ".pdf");
            RunProcess(rasterizer, ["-png", "-singlefile", "-r", "96", pdf, Path.Combine(work, name)]);

            return File.ReadAllBytes(Path.Combine(work, name + ".png"));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void RunProcess(string fileName, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{fileName} could not be started.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} ended with code {process.ExitCode}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
