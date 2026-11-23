# ADR-0041: MigraDoc paging: rows kept together, inline images, hyphen breaks, fixed widths, header placement, kerning

Status: Accepted (2026-11-23)

## Context

After ADR-0040 the first page of a MigraDoc data sheet rebuilt on the model matched MigraDoc within 3 pt, but from the
second page on the sheets broke their pages elsewhere. A layout dump of MigraDoc's renderer traced it to seven
differences: MigraDoc never splits a table row, sets pictures in the line of a paragraph, breaks after a hyphen, keeps
fixed column widths past the margin, starts the body at the top margin whatever the header's height, places the header
1.25 cm from the edge, and sets text without kerning or ligatures. None of it can be built from outside the library.

## Decision

All seven are options; the defaults stay as they were (Word's behaviour), except the hyphen break, which Word also does.

- **`TableRow.KeepTogether`** (Word `w:cantSplit`, ODT `fo:keep-together`, HTML `break-inside: avoid`): a row that does
  not fit at the bottom of a page moves whole to the next one, below the repeated header rows; a row taller than a page
  is still split. Per row only, as in Word; a table-wide switch would only save a loop.
- **`InlineImage`** wraps an `ImageBlock`, so size, crop, alternative text and the writers' image registration stay one
  thing. In the PDF it is an atom of the line: it stands on the baseline and needs the font's line gap above and its
  descent below (MigraDoc's 229.65 pt for a 227.2 pt picture in 10 pt Arial). In a table cell it raises its own line
  only. DOCX writes `wp:inline` in a run, ODT a frame anchored as a character, HTML an `<img>` in the line; SVG uses its
  raster fallback in the PDF. The DOCX reader keeps a picture that stands in a line of text in its paragraph; a
  paragraph of pictures alone still reads as image blocks, so existing readers of the model see no change there.
- **Break after a hyphen** inside a word before a letter (UAX #14 class HY), on by default as in Word, LibreOffice and
  MigraDoc. Never before a digit (HY × NU: `-5`, `2026-09-25`) or after a hyphen that starts a word.
- **`TableBlock.FixedColumnWidths`** (Word's fixed layout, `w:tblLayout w:type="fixed"`) keeps a full set of widths wider
  than the text area; the table runs into the right margin. ODT writes the width on a left-aligned table.
- **`Section.HeaderDistance`/`FooterDistance`** (Word `w:pgMar w:header`/`w:footer`), null keeps 0.5 in.
  **`Section.HeaderPushesBody = false`** keeps the body at the top margin; DOCX and ODT cannot express it and report it.
- **`PdfWriteOptions.Kerning`/`Ligatures`** switch the embedded fonts' `kern` and `liga` features off, for documents and
  `PdfCanvas` alike (measuring included); the Arabic forms stay. `TextRun.Kerning` is honoured as Word's threshold: below
  it a run is drawn and measured without kerning, through a kerning-free variant of its font handle.

## Consequences

- Text with a hyphen inside a word can wrap differently than in 1.4.0, as it does in Word.
- `Section`, `TableRow` and `TableBlock` gain properties; records and `with` copies carry them, the internal tree
  rebuilds copy them explicitly.
- The ODT reader does not read the header distance, fixed widths or inline images back; ODF has no distinct property for
  the first two.
