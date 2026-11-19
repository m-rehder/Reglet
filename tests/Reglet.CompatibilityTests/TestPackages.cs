using System.IO.Compression;
using System.Xml.Linq;
using Reglet;

/// <summary>
/// Shared helpers for the round-trip harness (AF03): write a model to a format, read it back
/// and splice an extra part into a ZIP package (used to model unmodelled/unknown content).
/// </summary>
internal static class TestPackages
{
    /// <summary>A representative document covering the model surface the readers support.</summary>
    public static PortableDocument SampleDocument()
    {
        var table = new TableBlock(new[]
        {
            new TableRow(new[] { new TableCell("A"), new TableCell("B") }) { IsHeader = true },
            new TableRow(new[] { new TableCell("1"), new TableCell("2") }),
        });

        var section = new Section(new Block[]
        {
            new Heading(HeadingLevel.Level1, new TextRun("Heading")),
            new Paragraph(new Inline[]
            {
                new TextRun("Normal "),
                new TextRun("bold") { Bold = true },
            }),
            TestLists.List(ListKind.Bullet, new[] { new ListItem(new Paragraph(new TextRun("item"))) }),
            table,
            new PageBreak(),
        });

        return TestLists.Document(
            new DocumentMetadata { Title = "Title", Author = "Author", Language = "en-US" },
            section);
    }

    public static byte[] Write(PortableDocument document, DocumentFormat format)
    {
        using var buffer = new MemoryStream();
        DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    public static PortableDocument Read(byte[] package, DocumentFormat format)
        => DocumentReader.Read(new MemoryStream(package, writable: false), format).Document;

    /// <summary>
    /// Copies a package and adds one extra part (unknown content for the reader). An ODF package
    /// stays valid: <c>mimetype</c> remains the first, uncompressed entry and the new part is
    /// listed in <c>META-INF/manifest.xml</c> (LibreOffice rejects unlisted parts as damaged).
    /// </summary>
    public static byte[] AddPart(byte[] package, string name, string content)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var created = target.CreateEntry(
                    entry.FullName,
                    entry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                using var input = entry.Open();
                using var destination = created.Open();
                if (entry.FullName == "META-INF/manifest.xml")
                {
                    AddManifestEntry(input, destination, name);
                }
                else
                {
                    input.CopyTo(destination);
                }
            }

            var extra = target.CreateEntry(name);
            using var writer = new StreamWriter(extra.Open());
            writer.Write(content);
        }

        return output.ToArray();
    }

    private static void AddManifestEntry(Stream input, Stream destination, string name)
    {
        XNamespace manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        var document = XDocument.Load(input);
        document.Root!.Add(new XElement(
            manifest + "file-entry",
            new XAttribute(manifest + "full-path", name),
            new XAttribute(manifest + "media-type", name.EndsWith(".xml", StringComparison.Ordinal) ? "text/xml" : string.Empty)));
        document.Save(destination);
    }

    /// <summary>Reads one part of a package.</summary>
    public static byte[] ReadPart(byte[] package, string name)
    {
        using var source = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read);
        var entry = source.GetEntry(name) ?? throw new InvalidOperationException($"Part missing: {name}");
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Replaces the content of one part.</summary>
    public static byte[] ReplacePart(byte[] package, string name, byte[] content)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package, writable: false), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                var created = target.CreateEntry(entry.FullName);
                using var destination = created.Open();
                if (entry.FullName == name)
                {
                    destination.Write(content, 0, content.Length);
                }
                else
                {
                    using var input = entry.Open();
                    input.CopyTo(destination);
                }
            }
        }

        return output.ToArray();
    }
}
