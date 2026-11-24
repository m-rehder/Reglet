<p align="center"><img src="https://raw.githubusercontent.com/m-rehder/Reglet/main/docs/logo/reglet-128.png" width="96" alt="Reglet logo"></p>

<h1 align="center">Reglet</h1>

<p align="center"><b>Create, read, convert and validate Word, Excel, LibreOffice, PDF and HTML files in .NET — with no dependencies.</b></p>

Reglet builds documents from one format-neutral model and writes them as **DOCX** (Word), **ODT** (LibreOffice),
**PDF** and **HTML**. It reads the same formats back, so it also converts between them. Beside documents it writes
and reads **Excel workbooks** (XLSX), and it **validates** DOCX, XLSX, ODT and PDF files against their standards. It
needs nothing but the .NET base class library: no Office installation, no LibreOffice, no browser, no native
libraries.

*A reglet is the strip of wood a printer puts between paragraphs of metal type.*

## Install

```
dotnet add package Reglet
```

Reglet targets .NET 10 and runs on Windows, macOS and Linux (also with Native AOT; WebAssembly is supported for
demos). The packages `Reglet.Core`, `Reglet.Docx`, `Reglet.Odt`, `Reglet.Pdf`, `Reglet.Html`, `Reglet.Fonts` and
the optional `Reglet.Hyphenation` can be used on their own. Excel workbooks come with `dotnet add package Reglet.Xlsx`, a validator for
DOCX, XLSX, ODF and PDF files with `dotnet add package Reglet.Validation`, and an MCP server for AI agents with
`dotnet tool install -g Reglet.Mcp`.

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

Workbooks and validation come in their own packages, and AI agents can use all of it through the MCP server
(`dotnet tool install -g Reglet.Mcp`, then for example `claude mcp add reglet -- reglet-mcp --root ~/Documents`).

Workbooks and validation in code:

```csharp
// dotnet add package Reglet.Xlsx
var workbook = new Workbook();
var sheet = workbook.AddWorksheet("Quotation");
sheet.Cell("A1").Value = "Monitor arm";
sheet.Cell("B1").Value = 4;
sheet.Cell("C1").Value = 89.90;
sheet.Cell("D1").Formula = "B1*C1";
sheet.Cell("D1").Style = new CellStyle { NumberFormat = "#,##0.00", Font = new CellFont { Bold = true } };
await using (var file = File.Create("quotation.xlsx"))
{
    await new XlsxWorkbookWriter().WriteAsync(workbook, file);
}

// dotnet add package Reglet.Validation
using var input = File.OpenRead("report.docx");
var report = new DocumentValidator().Validate(input);
foreach (var issue in report.Issues)
{
    Console.WriteLine(issue); // Error word/document.xml:12: The element ... is not allowed here
}
```

Every writer returns the list of things a format could not show (for example a chart in HTML), so nothing is lost
silently.

## What it can do

| Area | Features |
| --- | --- |
| Text | Character and paragraph formatting, named styles, lists, links and bookmarks, right-to-left scripts, hyphenation |
| Layout | Page sizes and margins, sections, headers and footers, columns, footnotes and endnotes, table of contents, fields |
| Tables | Column widths (absolute, in percent of the table, or fixed past the margin), merged cells, shading, borders, cell padding per axis, table indent, rows kept on one page, repeating header rows, nested tables |
| Graphics | Pictures (PNG, JPEG, SVG; in the flow, floating or in a line of text), shapes and text boxes (floating, anchored to the page, margin or paragraph), editable charts, barcodes and QR codes, flow diagrams, formulas |
| Collaboration | Comments with replies, tracked changes |
| Automation | Find and replace, templates with placeholders and repeating rows, mail merge from CSV, JSON, XML or Excel |
| PDF | Embedded fonts, PDF/A, encryption, digital signatures (incl. PAdES), form fields, watermarks, merge and split, text extraction, structure recovery (PDF to DOCX), pages drawn at fixed coordinates with text measurement (`PdfCanvas`), inspection of fonts and images for tests |
| HTML | Simple editable HTML in and out; a page-faithful preview with the PDF's layout and embedded fonts, as a page or a fragment for a host page |
| Security | Encrypted DOCX and ODT, signed DOCX and ODT, limits against malicious input |
| Validation | `DocumentValidator` checks DOCX, XLSX, ODT/ODS and PDF files for what Word, Excel, LibreOffice and PDF readers reject: package structure, content types and relationships, the ECMA-376 schemas with Office's markup compatibility, ODF mimetype, manifest and the ODF 1.3/1.4 RELAX NG schemas, PDF cross-references, page tree, content streams and fonts; findings with part and line (`Reglet.Validation`) |
| AI agents | An MCP server (`reglet-mcp`, package `Reglet.Mcp`) gives agents nine tools: create documents from Markdown, convert, read, fill templates, validate, create and read workbooks, merge and split PDFs; files only inside the directories it is given |
| Workbooks | Excel (XLSX) workbooks written and read (`Reglet.Xlsx`): sheets, text, numbers, dates, rich text, formulas recalculated on open, number formats, fonts, fills, borders, alignment, merged cells, column widths and row heights, images, page setup with fit to page, print area and repeated rows; values formatted as Excel shows them |

The exact state of every feature per format is in the [feature matrix](https://github.com/m-rehder/Reglet/blob/main/docs/feature-matrix.md).

## Examples

The [example gallery](https://github.com/m-rehder/Reglet/blob/main/docs/examples.md) has forty-five runnable programs — from "Hello World" to a complete invoice, a
report, a business letter and a calculation workbook:

```
dotnet run --project examples/Gallery -- --list
dotnet run --project examples/Gallery -- --out ./out
```

## Documentation

- [Example gallery](https://github.com/m-rehder/Reglet/blob/main/docs/examples.md) — what each example shows and where its code is.
- [Guide](https://github.com/m-rehder/Reglet/blob/main/docs/guide.md) — writer options per format, templates, workbooks and validation in detail.
- [API reference](https://github.com/m-rehder/Reglet/blob/main/docs/api-reference.md) — every public type and member on one line each, generated; made for
  search and for coding agents.
- [Feature matrix](https://github.com/m-rehder/Reglet/blob/main/docs/feature-matrix.md) — what each format supports, known limits.
- [Fields](https://github.com/m-rehder/Reglet/blob/main/docs/fields.md), [units](https://github.com/m-rehder/Reglet/blob/main/docs/units.md), [hardening](https://github.com/m-rehder/Reglet/blob/main/docs/hardening.md), [performance](https://github.com/m-rehder/Reglet/blob/main/docs/performance.md),
  [Native AOT and trimming](https://github.com/m-rehder/Reglet/blob/main/docs/aot-trimming.md).
- [Architecture decisions](https://github.com/m-rehder/Reglet/blob/main/docs/decisions/README.md) — why things are the way they are.

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

See [CONTRIBUTING.md](https://github.com/m-rehder/Reglet/blob/main/CONTRIBUTING.md) and [docs/build.md](https://github.com/m-rehder/Reglet/blob/main/docs/build.md); security issues go the way described in
[SECURITY.md](https://github.com/m-rehder/Reglet/blob/main/SECURITY.md).

## Support

Reglet is free and stays free. If it saves you time, you can [buy me a coffee on Ko-fi](https://ko-fi.com/mrehder).

## License

Reglet is licensed under the [MIT License](https://github.com/m-rehder/Reglet/blob/main/LICENSE). It contains data from the Unicode Character Database and Adobe's
font metrics for the PDF standard fonts; see [THIRD-PARTY-NOTICES.md](https://github.com/m-rehder/Reglet/blob/main/THIRD-PARTY-NOTICES.md). The shipped packages
have no dependencies (see [ZERO-DEPENDENCY.md](https://github.com/m-rehder/Reglet/blob/main/ZERO-DEPENDENCY.md)).
