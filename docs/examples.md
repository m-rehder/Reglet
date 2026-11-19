# Example gallery

Forty small programs, from "Hello World" to a complete invoice and report. Each one builds a document with
Reglet and writes it to the formats it is meant for (usually DOCX, ODT and PDF); every file is read back, so the
gallery doubles as a test.

```
dotnet run --project examples/Gallery -- --list               # list all examples
dotnet run --project examples/Gallery -- --out ./out          # run all into ./out
dotnet run --project examples/Gallery -- tables --out ./out   # one category
dotnet run --project examples/Gallery -- invoice --out ./out  # one example
```

The output files are named `<category>-<example>.<format>`.


## Basics

Source: [`examples/Gallery/Basics.cs`](../examples/Gallery/Basics.cs)

| Example | What it shows |
| --- | --- |
| `hello-world` | The smallest document: one paragraph, written as DOCX, ODT and PDF. |
| `one-model-four-formats` | One document model written to Word, LibreOffice, PDF and HTML. |
| `document-properties` | Title, author, subject, keywords, language and dates of a document. |
| `read-a-document` | Reads a DOCX, walks through its blocks and writes what it found. |
| `convert-docx-to-pdf` | Opens a Word document and saves it as PDF in one call each. |
| `pdf-to-docx` | Turns a PDF back into an editable document: headings, lists, tables and formulas. |

## Text

Source: [`examples/Gallery/Text.cs`](../examples/Gallery/Text.cs)

| Example | What it shows |
| --- | --- |
| `character-formatting` | Bold, italic, underline, colours, fonts, sizes, highlight, sub- and superscript. |
| `paragraph-formatting` | Alignment, indents, spacing, line spacing, borders, shading and tab stops. |
| `styles` | Named paragraph and character styles that Word and LibreOffice show in their style lists. |
| `lists` | Bulleted, numbered and nested lists, a list that starts at 5 and Roman numbers. |
| `links-and-bookmarks` | Web links, a link to a place in the document and a bookmark. |
| `right-to-left` | Arabic and Hebrew paragraphs next to English, in the right reading order. |

## Page layout

Source: [`examples/Gallery/Layout.cs`](../examples/Gallery/Layout.cs)

| Example | What it shows |
| --- | --- |
| `page-setup` | A4 portrait, A4 landscape and US Letter in one document, each with its own margins. |
| `headers-and-footers` | A different first page, "Page 3 of 7" in the footer and the title in the header. |
| `columns` | A newspaper page: a headline across the page and the text in two columns. |
| `footnotes` | Footnotes numbered automatically at the foot of each page. |
| `table-of-contents` | A table of contents built from the headings, with page numbers and links. |

## Tables

Source: [`examples/Gallery/Tables.cs`](../examples/Gallery/Tables.cs)

| Example | What it shows |
| --- | --- |
| `simple-table` | A header row and three data rows in three lines of code. |
| `table-formatting` | Column widths, a shaded header, merged cells, alignment and borders. |
| `long-table` | A price list over several pages; the header row repeats on every page. |

## Graphics

Source: [`examples/Gallery/Graphics.cs`](../examples/Gallery/Graphics.cs)

| Example | What it shows |
| --- | --- |
| `pictures` | A picture in the text, one scaled to a width and one with text flowing around it. |
| `shapes` | Rectangles, an ellipse, a dashed line and a text box. |
| `charts` | Column, line and pie charts that Word and LibreOffice can still edit. |
| `barcodes` | A QR code, an EAN-13 and a Code 128 barcode, drawn as vector shapes. |
| `flow-diagram` | A flow chart with start, steps, a decision and labelled arrows. |
| `formulas` | Fractions, roots, sums and integrals as real, editable formulas. |

## Collaboration

Source: [`examples/Gallery/Collaboration.cs`](../examples/Gallery/Collaboration.cs)

| Example | What it shows |
| --- | --- |
| `comments` | Comments on a word and a sentence, a reply and a resolved comment. |
| `tracked-changes` | Insertions and deletions by two authors that Word and LibreOffice can accept or reject. |

## Automation

Source: [`examples/Gallery/Automation.cs`](../examples/Gallery/Automation.cs)

| Example | What it shows |
| --- | --- |
| `find-and-replace` | Replaces placeholders like %NAME% in a finished document. |
| `template` | Fills a Word template: text fields and a table that grows with its data. |
| `mail-merge` | One letter per row of a CSV list, each written as its own file. |

## PDF

Source: [`examples/Gallery/Pdf.cs`](../examples/Gallery/Pdf.cs)

| Example | What it shows |
| --- | --- |
| `watermark` | A "DRAFT" watermark and a letterhead on every PDF page, without touching the document. |
| `password-protection` | A PDF and a DOCX that open only with a password; the PDF also forbids copying. |
| `digital-signature` | Signs a PDF with a certificate; the viewer shows who signed it and whether it changed. |
| `form-fields` | A fillable form: text fields, check boxes and a drop-down list. |
| `merge-and-split` | Merges two PDFs into one and takes pages out of a PDF. |
| `extract-text` | Reads the text of a PDF page by page. |

## Complete documents

Source: [`examples/Gallery/Showcase.cs`](../examples/Gallery/Showcase.cs)

| Example | What it shows |
| --- | --- |
| `invoice` | An invoice with letterhead, address, line items, totals, payment QR code and footer. |
| `report` | A report with title page, contents, chapters, chart, table, formula, footnotes and page numbers. |
| `business-letter` | A letter with sender line, address window, date, subject and closing. |

The browser demo [`examples/LivePreview`](../examples/LivePreview) renders a PDF from text typed into a web page
(Blazor WebAssembly; `dotnet run --project examples/LivePreview`, needs the .NET `wasm-tools` workload).
