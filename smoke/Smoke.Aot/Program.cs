using Reglet;

// T03: native AOT smoke test. Produces DOCX, ODT and PDF from a model and checks the
// header bytes of the produced files. Runs as a natively compiled application without JIT.
var document = PortableDocument.Create()
    .Metadata(title: "AOT-Smoke", author: "Reglet")
    .Heading("AOT", level: 1)
    .Paragraph("Hello AOT")
    .BulletList("a", "b")
    .Table(table => table.Header("A", "B").Row("1", "2"))
    .Build();

var directory = Path.Combine(Path.GetTempPath(), "reglet-aot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

try
{
    var docx = Path.Combine(directory, "a.docx");
    var odt = Path.Combine(directory, "a.odt");
    var pdf = Path.Combine(directory, "a.pdf");

    await DocumentWriter.WriteAsync(document, docx);
    await DocumentWriter.WriteAsync(document, odt);
    await DocumentWriter.WriteAsync(document, pdf);

    Require(IsZip(docx), "DOCX");
    Require(IsZip(odt), "ODT");
    Require(IsPdf(pdf), "PDF");

    Console.WriteLine("Smoke.Aot OK (DOCX, ODT and PDF produced).");
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static void Require(bool condition, string format)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Smoke.Aot: {format} is not a valid package.");
    }
}

static bool IsZip(string path)
{
    var bytes = File.ReadAllBytes(path);
    return bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K';
}

static bool IsPdf(string path)
{
    var bytes = File.ReadAllBytes(path);
    return bytes.Length > 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F';
}
