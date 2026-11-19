using Reglet;

namespace Gallery;

/// <summary>
/// Runs the examples: <c>dotnet run --project examples/Gallery -- [category|name] [--out directory] [--list]</c>.
/// Every file written is read back, so a broken example cannot pass silently.
/// </summary>
public static class ExampleRunner
{
    /// <summary>Runs the chosen examples and returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args, IReadOnlyList<Example> all)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(all);

        var output = Path.Combine(Path.GetTempPath(), "reglet-examples");
        string? filter = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--out" && index + 1 < args.Length)
            {
                output = args[++index];
            }
            else if (args[index] == "--list")
            {
                foreach (var group in all.GroupBy(example => example.Category))
                {
                    Console.WriteLine(group.Key);
                    foreach (var example in group)
                    {
                        Console.WriteLine($"  {example.Name,-28} {example.Description}");
                    }
                }

                return 0;
            }
            else
            {
                filter = args[index];
            }
        }

        var chosen = all.Where(example => filter is null || example.Category == filter || example.Name == filter).ToList();
        if (chosen.Count == 0)
        {
            Console.Error.WriteLine($"No example or category '{filter}'. Use --list to see them.");
            return 1;
        }

        Directory.CreateDirectory(output);
        foreach (var example in chosen)
        {
            Console.WriteLine($"{example.Category}/{example.Name}: {example.Description}");
            IReadOnlyList<string> files;
            if (example.Run is { } run)
            {
                files = await run(output);
            }
            else
            {
                var document = example.Document!();
                var written = new List<string>();
                foreach (var extension in example.Formats)
                {
                    var path = Path.Combine(output, example.FileName + "." + extension);
                    var result = await DocumentWriter.WriteAsync(document, path, example.OptionsFor?.Invoke(extension));
                    foreach (var warning in result.Warnings)
                    {
                        Console.WriteLine($"    note: {warning.Message}");
                    }

                    written.Add(path);
                }

                files = written;
            }

            foreach (var path in files)
            {
                Console.WriteLine($"  {Path.GetFileName(path)}");
                if (Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".odt" or ".pdf" or ".html")
                {
                    // The protected examples share one password; an unprotected file ignores it.
                    using var stream = File.OpenRead(path);
                    var format = Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".docx" => DocumentFormat.Docx,
                        ".odt" => DocumentFormat.Odt,
                        ".pdf" => DocumentFormat.Pdf,
                        _ => DocumentFormat.Html,
                    };
                    _ = DocumentReader.Read(stream, format, Assets.Password);
                }
            }
        }

        Console.WriteLine($"Done: {chosen.Count} example(s) in {output}");
        return 0;
    }
}
