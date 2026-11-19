using System.IO.Compression;
using System.Text;
using Reglet;

/// <summary>Embedded OLE objects (ADR-0029): opaque bytes with a preview picture.</summary>
internal static class EmbeddedObjectChecks
{
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Package = Encoding.ASCII.GetBytes("PK\u0003\u0004FAKE-XLSX");

    // A valid 1x1 PNG.
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=");

    internal static int Run()
    {
        try
        {
            RejectsInvalidObjects();
            WritesAndReadsDocx();
            NamesThePackageByItsProgId();
            KeepsObjectsWhenTheBodyChanges();
            DropsUnreadablePreviewsAndLinks();
            ReportsObjectsOutsideTheBody();
            WritesAndReadsOdt();
            DrawsThePreviewInPdf();
            Console.WriteLine("EmbeddedObjects: all checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("EmbeddedObjects: " + exception);
            return 1;
        }
    }

    private static void NamesThePackageByItsProgId()
    {
        // Without a file extension, an Excel ProgID makes the part an .xlsx (Word picks the program by it).
        var sheet = new EmbeddedObject(Package, EmbeddedObjectKind.Package, Length.FromPoints(100), Length.FromPoints(50)) { ProgId = "Excel.Sheet.12" };
        var bytes = Write(Doc(sheet), DocumentFormat.Docx, out _);
        Require(Part(bytes, "[Content_Types].xml").Contains("/word/embeddings/Package1.xlsx\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.Ordinal),
            "an Excel package without extension is written as .xlsx");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static EmbeddedObject Ole1(bool preview = true) => new(Ole, EmbeddedObjectKind.OleStorage, Length.FromPoints(150), Length.FromPoints(75))
    {
        ProgId = "Package",
        Preview = preview ? new ImageBlock(Png, ImageMediaType.Png) : null,
    };

    private static EmbeddedObject Sheet() => new(Package, EmbeddedObjectKind.Package, Length.FromPoints(100), Length.FromPoints(50))
    {
        ProgId = "Excel.Sheet.12",
        FileExtension = "XLSX",
    };

    private static PortableDocument Doc(params Block[] objects)
        => new(new DocumentMetadata(), new Section(new Block[] { new Paragraph(new TextRun("Vorher")) }.Concat(objects).Append(new Paragraph(new TextRun("Nachher"))).ToArray()));

    private static byte[] Write(PortableDocument document, DocumentFormat format, out WriteResult result)
    {
        using var buffer = new MemoryStream();
        result = DocumentWriter.WriteAsync(document, buffer, format).GetAwaiter().GetResult();
        return buffer.ToArray();
    }

    private static ReadResult Read(byte[] bytes, DocumentFormat format) => DocumentReader.Read(new MemoryStream(bytes), format);

    private static string Part(byte[] package, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        if (zip.GetEntry(name) is not { } entry)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static List<string> Names(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return zip.Entries.Select(entry => entry.FullName).ToList();
    }

    private static byte[] Patch(byte[] package, Func<string, string, string?> change)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(package)))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                var copy = target.CreateEntry(entry.FullName);
                using var writer = new StreamWriter(copy.Open(), new UTF8Encoding(false));
                writer.Write(change(entry.FullName, text) ?? text);
            }
        }

        return output.ToArray();
    }

    private static EmbeddedObject[] Objects(PortableDocument document)
        => document.Sections.SelectMany(section => section.Blocks).OfType<EmbeddedObject>().ToArray();

    private static void RejectsInvalidObjects()
    {
        var invalid = new Action[]
        {
            () => new EmbeddedObject(ReadOnlyMemory<byte>.Empty, EmbeddedObjectKind.OleStorage, Length.FromPoints(1), Length.FromPoints(1)),
            () => new EmbeddedObject(Ole, (EmbeddedObjectKind)9, Length.FromPoints(1), Length.FromPoints(1)),
            () => new EmbeddedObject(Ole, EmbeddedObjectKind.OleStorage, Length.FromPoints(0), Length.FromPoints(1)),
            () => new EmbeddedObject(Ole, EmbeddedObjectKind.OleStorage, Length.FromPoints(1), Length.FromPoints(1)) { Preview = new ImageBlock("<svg/>"u8.ToArray(), ImageMediaType.Svg) },
            () => new EmbeddedObject(Ole, EmbeddedObjectKind.Package, Length.FromPoints(1), Length.FromPoints(1)) { FileExtension = "x.y" },
        };
        foreach (var create in invalid)
        {
            var threw = false;
            try
            {
                create();
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Require(threw, "An invalid embedded object must be rejected.");
        }

        var copy = (byte[])Ole.Clone();
        var obj = new EmbeddedObject(copy, EmbeddedObjectKind.OleStorage, Length.FromPoints(1), Length.FromPoints(1));
        copy[0] = 0;
        Require(obj.Data.Span[0] == 0xD0, "The data is copied.");
        Require(Sheet().FileExtension == "xlsx", "The extension is normalised.");
    }

    private static void WritesAndReadsDocx()
    {
        var package = Write(Doc(Ole1(), Sheet()), DocumentFormat.Docx, out var result);
        Require(!result.HasWarnings, "DOCX: no warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var names = Names(package);
        Require(names.Contains("word/embeddings/oleObject1.bin") && names.Contains("word/embeddings/Package2.xlsx"), "The data parts are written.");
        Require(names.Contains("word/media/object1.png") && names.Contains("word/media/object2.png"), "A preview part for each object.");
        var types = Part(package, "[Content_Types].xml");
        Require(types.Contains("application/vnd.openxmlformats-officedocument.oleObject") && types.Contains("spreadsheetml.sheet"), "The content types are set.");
        var rels = Part(package, "word/_rels/document.xml.rels");
        Require(rels.Contains("relationships/oleObject") && rels.Contains("relationships/package"), "The relationships are set.");
        var body = Part(package, "word/document.xml");
        Require(body.Split("<w:object ").Length == 3 && body.Contains("ProgID=\"Package\"") && body.Contains("ProgID=\"Excel.Sheet.12\""), "Two w:object elements.");
        Require(body.Contains("width:150.00pt;height:75.00pt") && body.Contains("w:dxaOrig=\"3000\""), "The size is written.");

        var read = Read(package, DocumentFormat.Docx);
        Require(read.Warnings.Count == 0, "DOCX read: no warning: " + string.Join("|", read.Warnings.Select(w => w.Message)));
        var objects = Objects(read.Document);
        Require(objects.Length == 2, "Both objects are read.");
        Require(objects[0].Kind == EmbeddedObjectKind.OleStorage && objects[0].ProgId == "Package" && objects[0].Data.Span.SequenceEqual(Ole), "The OLE object keeps its data.");
        Require(objects[0].Width.Points == 150 && objects[0].Height.Points == 75 && objects[0].Preview is { MediaType: ImageMediaType.Png }, "Size and preview are read.");
        Require(objects[1].Kind == EmbeddedObjectKind.Package && objects[1].FileExtension == "xlsx" && objects[1].Data.Span.SequenceEqual(Package), "The package object keeps its data and extension.");
        Require(objects[1].Preview is { MediaType: ImageMediaType.Png }, "The placeholder is read back as a preview.");

        // Writing again gives the same objects.
        var again = Read(Write(read.Document, DocumentFormat.Docx, out _), DocumentFormat.Docx);
        Require(Objects(again.Document).Length == 2 && Names(Write(read.Document, DocumentFormat.Docx, out _)).Count(n => n.StartsWith("word/embeddings/", StringComparison.Ordinal)) == 2, "A second round trip is stable.");
    }

    private static void KeepsObjectsWhenTheBodyChanges()
    {
        var source = Write(Doc(Ole1(), Sheet()), DocumentFormat.Docx, out _);
        var read = Read(source, DocumentFormat.Docx).Document;
        var blocks = read.Sections[0].Blocks.ToList();
        blocks[0] = new Paragraph(new TextRun("Geändert"));
        var package = Write(new PortableDocument(read.Metadata, new Section(blocks)), DocumentFormat.Docx, out var result);
        Require(!result.HasWarnings, "No warning: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        Require(Objects(Read(package, DocumentFormat.Docx).Document).Length == 2, "The objects survive a changed body.");
        var embeddings = Names(package).Where(n => n.StartsWith("word/embeddings/", StringComparison.Ordinal)).ToList();
        Require(embeddings.Count == 2, "No stale embedding part is carried along: " + string.Join(",", embeddings));
    }

    private static void DropsUnreadablePreviewsAndLinks()
    {
        var source = Write(Doc(Ole1()), DocumentFormat.Docx, out _);

        // An EMF preview (what Word writes) is dropped and reported, the object stays.
        var emf = Patch(source, (name, text) => name switch
        {
            "word/_rels/document.xml.rels" => text.Replace("media/object1.png", "media/object1.emf"),
            _ => null,
        });
        var emfRead = Read(emf, DocumentFormat.Docx);
        Require(Objects(emfRead.Document) is [{ Preview: null }], "The object stays without its preview.");
        Require(emfRead.Warnings.Any(w => w.Message.Contains("not a PNG or JPEG")), "The dropped preview is reported.");
        var rewritten = Write(emfRead.Document, DocumentFormat.Docx, out _);
        Require(Names(rewritten).Contains("word/media/object1.png") && Objects(Read(rewritten, DocumentFormat.Docx).Document).Length == 1, "A placeholder is written.");

        // A missing data part (as for a linked object) is reported, the object is left out.
        var linkedRead = Read(Patch(source, (name, text) => name == "word/_rels/document.xml.rels"
            ? text.Replace("embeddings/oleObject1.bin", "embeddings/missing.bin") : null), DocumentFormat.Docx);
        Require(Objects(linkedRead.Document).Length == 0 && linkedRead.Warnings.Any(w => w.Message.Contains("no data part")), "A missing data part is reported.");
    }

    private static void ReportsObjectsOutsideTheBody()
    {
        var footer = new Section(new Block[] { new Paragraph(new TextRun("x")) }) { Footer = new HeaderFooter(new Block[] { Ole1() }) };
        var document = new PortableDocument(new DocumentMetadata(), footer);
        foreach (var format in new[] { DocumentFormat.Docx, DocumentFormat.Odt })
        {
            Write(document, format, out var result);
            Require(result.Warnings.Any(w => w.Message.Contains("embedded object")), format + ": an object in the footer is reported.");
        }
    }

    private static void WritesAndReadsOdt()
    {
        var package = Write(Doc(Ole1(), Sheet()), DocumentFormat.Odt, out var result);
        Require(result.Warnings.Count == 1 && result.Warnings[0].Message.Contains("Open XML package"), "Only the package object is reported: " + string.Join("|", result.Warnings.Select(w => w.Message)));
        var names = Names(package);
        Require(names.Contains("Object 1") && names.Contains("ObjectReplacements/Object 1"), "Data and preview parts.");
        var manifest = Part(package, "META-INF/manifest.xml");
        Require(manifest.Contains("application/vnd.sun.star.oleobject") && manifest.Contains("ObjectReplacements/Object 1"), "Both parts are in the manifest.");
        Require(Part(package, "content.xml").Contains("<draw:object-ole"), "The frame points at the object.");

        var read = Read(package, DocumentFormat.Odt);
        Require(read.Warnings.Count == 0, "ODT read: no warning: " + string.Join("|", read.Warnings.Select(w => w.Message)));
        var objects = Objects(read.Document);
        Require(objects is [{ Kind: EmbeddedObjectKind.OleStorage }] && objects[0].Data.Span.SequenceEqual(Ole), "The OLE object is read back.");
        Require(Math.Abs(objects[0].Width.Points - 150) < 0.1 && Math.Abs(objects[0].Height.Points - 75) < 0.1 && objects[0].Preview is { MediaType: ImageMediaType.Png }, "Size and preview.");

        // DOCX to ODT keeps an OLE storage object.
        var viaDocx = Read(Write(Doc(Ole1()), DocumentFormat.Docx, out _), DocumentFormat.Docx).Document;
        Require(Objects(Read(Write(viaDocx, DocumentFormat.Odt, out _), DocumentFormat.Odt).Document).Length == 1, "DOCX to ODT keeps the object.");
    }

    private static void DrawsThePreviewInPdf()
    {
        static string Pdf(PortableDocument document, out WriteResult result)
        {
            using var buffer = new MemoryStream();
            result = new PdfDocumentWriter().WriteAsync(document, buffer).GetAwaiter().GetResult();
            return Encoding.Latin1.GetString(buffer.ToArray());
        }

        var with = Pdf(Doc(Ole1()), out var resultWith);
        Require(!resultWith.HasWarnings && with.Contains("/Subtype /Image"), "The preview is drawn as an image.");
        var without = Pdf(Doc(Ole1(preview: false)), out var resultWithout);
        Require(!without.Contains("/Subtype /Image") && resultWithout.Warnings.Any(w => w.Message.Contains("without a preview")), "Without a preview nothing is drawn and it is reported.");

        var cell = new TableBlock(new[] { new TableRow(new[] { new TableCell(new Block[] { Ole1() }) }) });
        Require(Pdf(new PortableDocument(new DocumentMetadata(), new Section(new Block[] { cell })), out _).Contains("/Subtype /Image"), "The preview is drawn in a table cell.");
    }
}
