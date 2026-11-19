using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>VBA preservation (ADR-0028): opaque, opt-in, never executed.</summary>
internal static class MacroChecks
{
    private static readonly byte[] Project = [0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

    internal static int Run()
    {
        try
        {
            RejectsEmptyProject();
            WritesMacroEnabledPackage();
            DropsByDefault();
            KeepsOnRequest();
            RebuildsWithOrWithoutProject();
            Console.WriteLine("Macros: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Macros: " + exception);
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static PortableDocument Doc(MacroProject? project = null)
        => new(new DocumentMetadata { MacroProject = project }, new Section(new Block[] { new Paragraph(new TextRun("Hello")) }));

    private static byte[] Write(PortableDocument document)
    {
        using var buffer = new MemoryStream();
        new DocxDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static string Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name);
        if (entry is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static bool Has(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        return zip.GetEntry(name) is not null;
    }

    private static ReadResult Read(byte[] package, bool keep)
        => new DocxDocumentReader().Read(new MemoryStream(package), new DocxReadOptions { KeepMacros = keep });

    private static void RejectsEmptyProject()
    {
        var threw = false;
        try
        {
            _ = new MacroProject { Data = ReadOnlyMemory<byte>.Empty };
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Require(threw, "An empty project must be rejected.");
        var source = new byte[] { 1, 2 };
        var project = new MacroProject { Data = source };
        source[0] = 9;
        Require(project.Data.Span[0] == 1, "The bytes are copied.");
    }

    private static void WritesMacroEnabledPackage()
    {
        var package = Write(Doc(new MacroProject { Data = Project }));
        Require(Has(package, "word/vbaProject.bin"), "The part is written.");
        Require(Part(package, "[Content_Types].xml").Contains("macroEnabled.main+xml"), "The main part is macro-enabled.");
        Require(Part(package, "[Content_Types].xml").Contains("application/vnd.ms-office.vbaProject"), "The project has its content type.");
        Require(Part(package, "word/_rels/document.xml.rels").Contains("relationships/vbaProject"), "The document points to the project.");
        var plain = Write(Doc());
        Require(!Has(plain, "word/vbaProject.bin") && !Part(plain, "[Content_Types].xml").Contains("macroEnabled"), "Nothing without a project.");
    }

    private static void DropsByDefault()
    {
        var source = Write(Doc(new MacroProject { Data = Project }));

        // Unchanged document: the original package is adopted, but without the project.
        var result = Read(source, keep: false);
        Require(result.Document.Metadata.MacroProject is null, "Not read by default.");
        Require(result.Warnings.Any(w => w.Message.Contains("KeepMacros")), "The drop is reported.");
        var unchanged = Write(result.Document);
        Require(!Has(unchanged, "word/vbaProject.bin"), "The unchanged path drops the part.");
        Require(!Part(unchanged, "[Content_Types].xml").Contains("macroEnabled")
            && !Part(unchanged, "[Content_Types].xml").Contains("vbaProject"), "The types are plain again.");
        Require(!Part(unchanged, "word/_rels/document.xml.rels").Contains("vbaProject"), "The relationship is gone.");
        Require(Read(unchanged, keep: false).Warnings.All(w => !w.Message.Contains("KeepMacros")), "The result is a plain package.");

        // Changed body: the rebuilt package has no project either.
        var edited = new PortableDocument(result.Document.Metadata, new Section(new Block[] { new Paragraph(new TextRun("New")) }));
        var rebuilt = Write(edited);
        Require(!Has(rebuilt, "word/vbaProject.bin") && !Part(rebuilt, "[Content_Types].xml").Contains("macroEnabled"), "The rebuilt package has no project.");
    }

    private static void KeepsOnRequest()
    {
        var source = Write(Doc(new MacroProject { Data = Project }));
        var result = Read(source, keep: true);
        Require(result.Document.Metadata.MacroProject is { } project && project.Data.Span.SequenceEqual(Project), "The bytes are read.");
        Require(result.Warnings.All(w => !w.Message.Contains("KeepMacros")), "Nothing to report when kept.");

        var unchanged = Write(result.Document);
        Require(unchanged.AsSpan().SequenceEqual(source), "An untouched document keeps its package.");

        var edited = new PortableDocument(result.Document.Metadata, new Section(new Block[] { new Paragraph(new TextRun("New")) }));
        var rebuilt = Write(edited);
        Require(Has(rebuilt, "word/vbaProject.bin") && Part(rebuilt, "[Content_Types].xml").Contains("macroEnabled.main+xml"), "A changed body keeps the project.");
        Require(Read(rebuilt, keep: true).Document.Metadata.MacroProject is { } again && again.Data.Span.SequenceEqual(Project), "It reads back.");
        Require(Part(rebuilt, "word/_rels/document.xml.rels").Split("relationships/vbaProject").Length == 2, "One relationship only.");
    }

    private static void RebuildsWithOrWithoutProject()
    {
        // A project added to a document read without one is written (the source package cannot be adopted).
        var plain = Read(Write(Doc()), keep: false).Document;
        var withProject = new PortableDocument(plain.Metadata with { MacroProject = new MacroProject { Data = Project } }, plain.Sections);
        var package = Write(withProject);
        Require(Has(package, "word/vbaProject.bin"), "An added project is written.");

        // The project is replaced when the model carries other bytes.
        var other = new byte[] { 7, 7, 7 };
        var source = Write(Doc(new MacroProject { Data = Project }));
        var kept = Read(source, keep: true).Document;
        var replaced = Write(new PortableDocument(kept.Metadata with { MacroProject = new MacroProject { Data = other } }, kept.Sections));
        Require(Read(replaced, keep: true).Document.Metadata.MacroProject!.Data.Span.SequenceEqual(other), "The model bytes win.");
    }
}
