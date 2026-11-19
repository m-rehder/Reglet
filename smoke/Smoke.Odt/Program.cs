using Reglet;

// consumer smoke test: references only the built DLLs and produces an ODT.
var document = PortableDocument.Create().Heading("Smoke", level: 1).Paragraph("Hallo").Build();
var path = Path.Combine(Path.GetTempPath(), "reglet-smoke-" + Guid.NewGuid().ToString("N") + ".odt");

using (var stream = File.Create(path))
{
    new OdtDocumentWriter().WriteAsync(document, stream).GetAwaiter().GetResult();
}

var bytes = File.ReadAllBytes(path);
if (bytes.Length < 2 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K')
{
    throw new InvalidOperationException("Smoke.Odt: kein ZIP-Paket.");
}

Console.WriteLine($"Smoke.Odt OK ({bytes.Length} Bytes).");
File.Delete(path);
