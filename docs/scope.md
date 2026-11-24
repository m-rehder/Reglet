# Reglet – Scope version 1

This document records the binding feature set of version 1. It complements the short
description in [../README.md](../README.md).

## MVP – feature set

Version 1 supports the following content and capabilities:

- **Document metadata:** title, author, subject, keywords, language and timestamps.
- **Page settings:** A4 and Letter, portrait and landscape, as well as freely definable
  page margins.
- **Text structure:** headings (levels 1–6) and paragraphs.
- **Text markup:** bold, italic, underline, font size, color and alignment.
- **Lists:** bullet and numbered lists with at least two levels.
- **Tables:** header rows, column widths, borders and cell spacing.
- **Images:** PNG and JPEG with alternative text and controlled scaling.
- **Page control:** manual page breaks as well as a simple header and footer with page
  number.
- **Output:** to a file path or stream (see *MVP – outputs*).

File-based DOCX and ODT templates can optionally declare placeholder types. Text, ISO dates,
invariant decimal numbers, images and fields are validated when bound; undeclared placeholders
retain their existing behavior. See [ADR-0005](decisions/ADR-0005-templates.md).
Named repeated regions can expand paragraphs or table rows from shared or per-parent data sets,
including nested regions; unsafe-to-duplicate source content is reported.

## MVP – outputs

All three formats can be written in two ways:

| Output | Signature (sketch) | Note |
| --- | --- | --- |
| File path | `WriteAsync(document, path, …)` | The format is recognized from the normalized extension (`.docx`, `.odt`, `.pdf`). |
| Stream | `WriteAsync(document, stream, format, options, cancellationToken)` | The format is passed explicitly; the foreign stream is **not** closed. |

In addition there is an explicit `DocumentFormat` value per format (`Docx`, `Odt`,
`Pdf`). A built document is immutable and can be written to several formats in parallel;
each write operation accepts a `CancellationToken`.

The writers work **synchronously**; the `Task<WriteResult>` returned by `WriteAsync` is
already completed on return. The asynchronous signature unifies the writer interface without
occupying a real background thread. The stream does not need to be seekable: internally PDF
does need the stream position, but a non-seekable target stream is buffered and the finished
PDF is then transferred. DOCX and ODT (ZIP-based) write directly into non-seekable streams.

## Outside the current scope

The version-1 baseline above has been extended since (PDF/A, encryption and signatures, tagged
PDF, the subset readers, the preserving round trip). What is in scope today, what is open and what
is deliberately out of scope is listed in the [feature matrix](feature-matrix.md), in particular
its sections [Open work](feature-matrix.md#open-work) and
[Out of scope](feature-matrix.md#out-of-scope). Moving a feature from "out of scope" into scope
changes this file and the relevant ADR first. The drawing layer (shapes and text boxes, [ADR-0022](decisions/ADR-0022-drawing-layer.md)) moved into scope on 2026-11-13. Barcodes ([ADR-0023](decisions/ADR-0023-barcodes.md)) moved into scope on 2026-11-17, as did form fields ([ADR-0024](decisions/ADR-0024-form-fields.md)); charts ([ADR-0025](decisions/ADR-0025-charts.md)) followed the same day. Comments ([ADR-0026](decisions/ADR-0026-comments.md)) moved into scope on 2026-11-18, revisions ([ADR-0027](decisions/ADR-0027-revisions.md)) followed the same day. VBA preservation ([ADR-0028](decisions/ADR-0028-vba-preservation.md), opt-in, never executed) followed on the same day, as did embedded OLE objects ([ADR-0029](decisions/ADR-0029-embedded-objects.md), preserved as bytes), flow diagrams ([ADR-0030](decisions/ADR-0030-flow-diagrams.md)) display equations ([ADR-0031](decisions/ADR-0031-equations.md)) HTML as a format ([ADR-0032](decisions/ADR-0032-html.md)) DOCX↔PDF conversion ([ADR-0033](decisions/ADR-0033-docx-pdf-conversion.md)) and formula recovery from PDF ([ADR-0034](decisions/ADR-0034-pdf-formula-recovery.md), opt-in). Workbooks (a spreadsheet model with an XLSX writer and reader, package `Reglet.Xlsx`, [ADR-0043](decisions/ADR-0043-xlsx-workbooks.md)) moved into scope on 2026-11-24, as did an MCP server for AI agents (`apps/Reglet.Mcp`, [ADR-0045](decisions/ADR-0045-mcp-server.md)) and a document validator for all formats (package `Reglet.Validation`, [ADR-0044](decisions/ADR-0044-document-validator.md)).

## Compatibility promise

Version 1 promises **semantically equivalent content**, but **no pixel-identical rendering**
between the target programs (Microsoft Word, LibreOffice and the own PDF renderer).

- The **content** — text, structure, metadata, lists, tables and images — is equivalent in
  all three formats.
- The **design** is comparable, but can differ visibly depending on the program, font choice
  and line breaking.
- Font-dependent line and page breaks are not part of the promise.

Known deviations between the formats are documented in a published feature matrix (phase I).
Unsupported features produce a warning or an error depending on the option, but never silent
data loss.
