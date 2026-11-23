# Reglet – Release notes

## Unreleased

See ADR-0041.

### Images

- `InlineImage` places an image in the line of a paragraph, next to text runs (Word's `wp:inline`, MigraDoc's image
  in a paragraph): it stands on the baseline, follows the paragraph's alignment, and its line is as high as the
  font's line gap, the image and the font's descent. Size, crop and alternative text come from its `ImageBlock`. PDF,
  DOCX (`wp:inline` in a run), ODT (a frame anchored as a character) and HTML write it; the DOCX reader keeps a picture
  that stands in a line of text in its paragraph, a paragraph of pictures alone still reads as image blocks.

### PDF

- A line also breaks after a hyphen inside a word, before a letter (UAX #14 class HY, as Word and MigraDoc):
  `handelsübliche UP-` / `Kombination`. A hyphen before a digit (`-5`, `2026-09-25`) or at the start of a word keeps
  its neighbours together.
- `PdfWriteOptions.Kerning = false` and `Ligatures = false` switch off the embedded fonts' kerning and standard
  ligatures, for documents and `PdfCanvas` (measuring included), as PDFsharp and MigraDoc set text. Both stay on by
  default. `TextRun.Kerning` is now honoured: below its threshold size a run is not kerned (Word's `w:kern`).

### Sections

- `Section.HeaderDistance` and `Section.FooterDistance` set how far the header and footer stand from the sheet edge
  (Word's `w:pgMar w:header`/`w:footer`, read and written; ODT writes them). Null keeps 0.5 in.
- `Section.HeaderPushesBody = false` keeps the body at the top margin when the header grows past it (MigraDoc's
  behaviour); the header may then overlap the body. The PDF honours it, DOCX and ODT report it. The default keeps
  Word's.

### Tables

- `TableRow.KeepTogether` keeps a row on one page: a row that does not fit at the bottom moves whole to the next page,
  below the repeated header rows (MigraDoc's behaviour). DOCX reads and writes `w:cantSplit`, ODT
  `fo:keep-together`, HTML `break-inside: avoid`. A row taller than a page is still split.
- `TableBlock.FixedColumnWidths` keeps a full set of column widths wider than the text area, so the table runs into
  the right margin (Word's fixed table layout, read and written as `w:tblLayout w:type="fixed"`; ODT writes the width
  on a left-aligned table). The default still shrinks such a table to the text width.
- DOCX: `w:tblInd` is written before `w:tblBorders`, in schema order (1.4.0 wrote it after).

## 1.4.0

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
