<p align="center"><img src="docs/logo/reglet-128.png" width="96" alt="Reglet logo"></p>

<h1 align="center">Reglet</h1>

<p align="center"><b>Create, read and convert Word, LibreOffice, PDF and HTML documents in .NET — with no dependencies.</b></p>

Reglet builds documents from one format-neutral model and writes them as **DOCX** (Word), **ODT** (LibreOffice),
**PDF** and **HTML**. It reads the same formats back, so it also converts between them. It needs nothing but the
.NET base class library: no Office installation, no LibreOffice, no browser, no native libraries.

*A reglet is the strip of wood a printer puts between paragraphs of metal type.*

## Install

```
dotnet add package Reglet
```

Reglet targets .NET 10 and runs on Windows, macOS and Linux (also with Native AOT; WebAssembly is supported for
demos). The packages `Reglet.Core`, `Reglet.Docx`, `Reglet.Odt`, `Reglet.Pdf`, `Reglet.Html`, `Reglet.Fonts` and
the optional `Reglet.Hyphenation` can be used on their own.

## Quick start

```csharp
using Reglet;

var document = PortableDocument.Create()
    .Metadata(title: "Monthly report", author: "Example Ltd")
    .Heading("March 2026")
    .Paragraph(p => p.Text("Status: ").Bold("on track"))
    .BulletList("Layout finished", "Proofs sent", "Printing next week")
    .Table(t => t.Header("Metric", "Value").Row("Pages", "40").Row("Pictures", "12"))
    .Build();

await DocumentWriter.WriteAsync(document, "report.docx");
await DocumentWriter.WriteAsync(document, "report.odt");
await DocumentWriter.WriteAsync(document, "report.pdf");

// Read any supported file and save it in another format.
var read = DocumentReader.Read("report.docx");
await DocumentWriter.WriteAsync(read.Document, "report.html");
```

Every writer returns the list of things a format could not show (for example a chart in HTML), so nothing is lost
silently.

## What it can do

| Area | Features |
| --- | --- |
| Text | Character and paragraph formatting, named styles, lists, links and bookmarks, right-to-left scripts, hyphenation |
| Layout | Page sizes and margins, sections, headers and footers, columns, footnotes and endnotes, table of contents, fields |
| Tables | Column widths, merged cells, shading, borders, repeating header rows, nested tables |
| Graphics | Pictures (PNG, JPEG, SVG), shapes and text boxes, editable charts, barcodes and QR codes, flow diagrams, formulas |
| Collaboration | Comments with replies, tracked changes |
| Automation | Find and replace, templates with placeholders and repeating rows, mail merge from CSV, JSON, XML or Excel |
| PDF | Embedded fonts, PDF/A, encryption, digital signatures (incl. PAdES), form fields, watermarks, merge and split, text extraction, structure recovery (PDF to DOCX) |
| Security | Encrypted DOCX and ODT, signed DOCX and ODT, limits against malicious input |

The exact state of every feature per format is in the [feature matrix](docs/feature-matrix.md).

## Examples

The [example gallery](docs/examples.md) has forty runnable programs — from "Hello World" to a complete invoice, a
report and a business letter:

```
dotnet run --project examples/Gallery -- --list
dotnet run --project examples/Gallery -- --out ./out
```

## Documentation

- [Example gallery](docs/examples.md) — what each example shows and where its code is.
- [Guide](docs/guide.md) — writer options per format and templates in detail.
- [Feature matrix](docs/feature-matrix.md) — what each format supports, known limits.
- [Fields](docs/fields.md), [units](docs/units.md), [hardening](docs/hardening.md), [performance](docs/performance.md),
  [Native AOT and trimming](docs/aot-trimming.md).
- [Architecture decisions](docs/decisions/README.md) — why things are the way they are.

## What Reglet is not

- Not a pixel-perfect Word or LibreOffice layout engine: the content is the same in every format, line and page breaks
  can differ with fonts and programs.
- Reading keeps the content and its formatting, not every detail of a foreign file; what cannot be mapped is reported.
- No OCR: a scanned PDF has no text to recover.

## Building and contributing

```
dotnet build Reglet.sln
dotnet run --project tests/Reglet.CompatibilityTests
build/run-examples.sh
```

See [CONTRIBUTING.md](CONTRIBUTING.md) and [docs/build.md](docs/build.md); security issues go the way described in
[SECURITY.md](SECURITY.md).

## License

Reglet is licensed under the [MIT License](LICENSE). It contains data from the Unicode Character Database and Adobe's
font metrics for the PDF standard fonts; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The shipped packages
have no dependencies (see [ZERO-DEPENDENCY.md](ZERO-DEPENDENCY.md)).
