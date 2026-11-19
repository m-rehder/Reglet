using Reglet;

// consumer smoke test: references only the built DLLs and produces a DOCX.
var document = PortableDocument.Create().Heading("Smoke", level: 1).Paragraph("Hallo").Build();
var path = Path.Combine(Path.GetTempPath(), "reglet-smoke-" + Guid.NewGuid().ToString("N") + ".docx");

using (var stream = File.Create(path))
{
    new DocxDocumentWriter().WriteAsync(document, stream).GetAwaiter().GetResult();
}

var bytes = File.ReadAllBytes(path);
if (bytes.Length < 2 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
{
    throw new InvalidOperationException("Smoke.Docx: kein ZIP-Paket.");
}

Console.WriteLine($"Smoke.Docx OK ({bytes.Length} Bytes).");
File.Delete(path);
