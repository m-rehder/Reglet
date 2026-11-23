# Reglet – Release notes

## Unreleased

### PDF

- Page number, page count, date and other fields take the font and size of their paragraph (its style), like any text
  run; before they took the document's defaults, so a right-aligned "1 von 3" in a 10 pt paragraph of a document with
  another default size was measured and drawn in the wrong size and could overlap.
- A table row is as high as its text: a 9 pt row stays 9 pt in a 10 pt document. The document's font size applies only
  to a row without text.
- An empty paragraph is as high as its (empty) text run or its paragraph style's font size, as in Word and MigraDoc,
  instead of the document's size.
- `ImageBlock.TakesAnchorLine = false` places a floating image without the empty anchor paragraph (MigraDoc's
  behaviour); the default keeps Word's. DOCX and ODT keep the anchor paragraph and report it.

### Tables

- `TableBlock.CellPaddingHorizontal`/`CellPaddingVertical` and `TableCell.PaddingHorizontal`/`PaddingVertical` set
  left/right and top/bottom padding apart; `TableBlock.LeftIndent` (negative allowed) moves the table and its borders.
  Written to DOCX (`w:tcMar`/`w:tblCellMar`, `w:tblInd`), ODT and PDF, and read back from DOCX and ODT (ADR-0040).
- DOCX and ODT tables written with the default padding are read back with 5.4 pt left and right and 0 top and bottom,
  not 5.4 pt on all sides; ODT cells show their table's padding in LibreOffice.

## 1.3.0

### PDF

- Table rows are as far apart as in Word: a horizontal border takes its width (the text starts below it, the table
  ends with its bottom border); before, 0.5 pt borders made Reglet fit about 6 % more rows on a page than Word.
- A floating image or shape takes the line of its own anchor paragraph, as in Word and LibreOffice: text after a
  logo in front of the text (for example as the first block of a header) no longer moves up by one line.
- A paragraph that breaks onto the next page beside a floating image no longer indents its last line there.

### Packaging

- The release workflow publishes the facade `Reglet` only after nuget.org lists its dependencies; with 1.2.0 a restore
  right after the release could fail because `Reglet` was listed before `Reglet.Html`.

## 1.2.0

### PDF

- Paged HTML: `PdfDocumentWriter.WriteHtmlAsync` and `PdfCanvas.WriteHtmlAsync` write the pages of the PDF writer as
  HTML (one SVG page each, real text in reading order, the fonts of the collection embedded as `@font-face`, every image
  once), as a complete document or as a fragment with scoped styles and a class prefix (`PdfHtmlOptions`, ADR-0039).

## 1.1.0

### PDF

- `PdfCanvas`: a PDF drawn page by page at fixed coordinates (text with anchors, lines, rectangles, PNG/JPEG
  images, `Translate`/`SaveState`/`RestoreState`), written with the usual `PdfWriteOptions` (ADR-0037).
- Text measurement (`PdfCanvas.MeasureText`) identical to the drawing; `FontFace.Ascent`, `Descent`, `LineGap`,
  `UnitsPerEm`.
- `ImageInfo.Read`: pixel size and DPI of a PNG or JPEG from its header.
- `PdfInspection.Read`: pages, fonts (embedded, subset) and images of a PDF, for tests.
- `DocumentMetadata.Creator`: written as Info `/Creator` and XMP `xmp:CreatorTool`, read back by the PDF reader.
- A standard font that is only measured, never drawn, is no longer written into the resources.

### Documents

- Floating images and shapes can be anchored to the page, the margin, the column or the paragraph
  (`HorizontalRelativeTo`, `VerticalRelativeTo`) and aligned instead of offset (`HorizontalAlignment`,
  `VerticalAlignment`); written and read in DOCX (`wp:positionH`/`wp:positionV`) and ODT, honoured by the PDF (ADR-0038).

## 1.0.0

The first public release. Reglet creates, reads and converts DOCX, ODT, PDF and HTML documents in .NET 10 with no
dependencies beyond the base class library.

### Documents

- One format-neutral model (`PortableDocument`) with a fluent builder; DOCX, ODT, PDF and HTML writers and readers.
- Text: character formatting (fonts, sizes, colours, highlight, underline styles, strikethrough, sub- and superscript,
  small capitals, spacing, effects), paragraph formatting (alignment, indents, spacing, line spacing, borders, shading,
  tab stops, keep rules), named paragraph and character styles, lists with numbering definitions, links and bookmarks,
  right-to-left scripts (Unicode bidirectional algorithm), hyphenation for the PDF with the patterns of 24
  languages (optional package `Reglet.Hyphenation`).
- Layout: page sizes, orientation and margins per section, section breaks, headers and footers (first, even, odd),
  text columns, footnotes and endnotes, table of contents, table of figures, index, fields (page, page count, date,
  document properties), line numbering, page borders.
- Tables: column widths, merged cells, shading, borders, cell padding and alignment, repeating header rows, row heights,
  nested tables, table styles.
- Graphics: PNG, JPEG and SVG pictures (inline or floating with text wrap), shapes and text boxes, editable charts
  (column, bar, line, area, pie, scatter), barcodes (Code 128, EAN-13, EAN-8, QR code), flow diagrams, display formulas
  (Office Math in DOCX, MathML in ODT).
- Collaboration: comments with replies and resolved state, tracked insertions and deletions.
- Automation: find and replace, document templates with placeholders, conditions and repeating rows or regions, mail
  merge from CSV, JSON, XML or Excel data, merging documents.
- Preservation: unknown content of a read DOCX or ODT is kept when the document is written back unchanged; VBA
  projects (opt-in, never run) and embedded OLE objects are kept as bytes.

### PDF

- Embedded and subset TrueType/OpenType fonts with text shaping, PDF/A-2b and PDF/A-3b, tagged PDF (structure, reading order and alternative texts),
  attachments, page templates and watermarks, annotations, outlines, viewer preferences.
- Encryption (AES-256) with permissions, digital signatures (CMS and PAdES, timestamps, long-term validation data),
  AcroForm form fields, merging and splitting pages, metadata rewrite, text extraction.
- Opt-in structure recovery turns a PDF back into an editable document (paragraphs, headings, lists, tables, columns,
  images, formulas).

### Security and robustness

- Encrypted and signed DOCX and ODT, limits and guards against malicious input (zip bombs, deep nesting, huge
  images), deterministic output, Native AOT and trimming compatible.

### Examples and documentation

- An example gallery of forty runnable programs (`examples/Gallery`), from "Hello World" to an invoice, a report and a
  business letter; a feature matrix per format; architecture decision records.
