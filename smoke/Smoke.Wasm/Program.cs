using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Reglet;

// Blazor WebAssembly smoke test. Produces DOCX, ODT and PDF in memory inside the browser and
// renders the outcome into #result; build/run-wasm.sh reads it from the headless DOM.
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<SmokeResult>("#app");
await builder.Build().RunAsync();

public sealed class SmokeResult : ComponentBase
{
    private string _message = "running";

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var document = PortableDocument.Create()
                .Metadata(title: "WASM-Smoke", author: "Reglet")
                .Heading("WASM", level: 1)
                .Paragraph("Hello WebAssembly – äöü ß €")
                .BulletList("a", "b")
                .Table(table => table.Header("A", "B").Row("1", "2"))
                .Build();

            Require(IsZip(await WriteAsync(document, DocumentFormat.Docx)), "DOCX");
            Require(IsZip(await WriteAsync(document, DocumentFormat.Odt)), "ODT");
            Require(IsPdf(await WriteAsync(document, DocumentFormat.Pdf)), "PDF");
            _message = "Smoke.Wasm OK (DOCX, ODT and PDF produced).";
        }
        catch (Exception exception)
        {
            _message = "Smoke.Wasm FAILED: " + exception;
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "pre");
        builder.AddAttribute(1, "id", "result");
        builder.AddContent(2, _message);
        builder.CloseElement();
    }

    private static async Task<byte[]> WriteAsync(PortableDocument document, DocumentFormat format)
    {
        using var stream = new MemoryStream();
        await DocumentWriter.WriteAsync(document, stream, format);
        return stream.ToArray();
    }

    private static void Require(bool condition, string format)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{format} is not a valid package.");
        }
    }

    private static bool IsZip(byte[] bytes) => bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K';

    private static bool IsPdf(byte[] bytes) => bytes.Length > 4 && "%PDF"u8.SequenceEqual(bytes.AsSpan(0, 4));
}
