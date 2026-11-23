# ADR-0040: Paragraph-based line heights, floats without an anchor line, padding per axis

Status: Accepted (2026-11-23)

## Context

A MigraDoc document rebuilt on the document model (a confirmation data sheet: header with a logo at the right margin and
page fields, tables with merged cells, borders per edge and pictures) matched MigraDoc's PDF only with workarounds: a
document font size of 1 pt, sizes on every run, no-break spaces in spacer paragraphs and indents on every cell
paragraph. The 1 pt workaround in turn shrank the page fields to 1 pt.

## Decision

- **Fields are text runs.** Page number, page count, date and the other fields take the font, size, style and colour a
  text run without formatting of its own takes in their paragraph (style, then document defaults).
- **A line's least height comes from its paragraph.** A table row's line height is the largest of its cells' text; the
  document's font size applies only to a row without any text line (and to a cell without blocks, which Word gives an
  empty paragraph in the document's font). An empty paragraph's line has the size of its first (empty) run, else of its
  paragraph style, else of the document — as in Word and MigraDoc.
- **`ImageBlock.TakesAnchorLine`** (default true, the Word behaviour of 1.3.0). False places a floating image without the
  empty anchor paragraph, as MigraDoc's floating images do. DOCX and ODT need a paragraph to anchor to and keep it; they
  report that instead of moving the image into the next paragraph (that would change what the readers return). Shapes
  keep their anchor line.
- **Cell padding per axis and a table indent.** `TableBlock.CellPaddingHorizontal`/`CellPaddingVertical` and
  `TableCell.PaddingHorizontal`/`PaddingVertical` beside the uniform values; `TableLayout.CellPadding` resolves them once
  for every writer (cell axis, cell uniform, table axis, table uniform, table style, Word's 5.4 pt / 0 defaults).
  `TableBlock.LeftIndent` (negative allowed) moves the table and its borders: DOCX `w:tblInd`, ODT `fo:margin-left` with
  `table:align="left"`, PDF and nested tables alike.
- The readers return the axes: DOCX `w:tblCellMar` and ODT cell styles left/top padding become one uniform value when
  equal, otherwise the two axes. Before, a table written with the defaults was read back with 5.4 pt on all four sides.

## Consequences

- ODT cells now carry their table's padding in their cell style (ODF has no table-level cell padding); before, LibreOffice
  showed `TableBlock.CellPadding` as the default 5.4 pt.
- Documents whose rows relied on the document size as a floor get rows as low as their text, as in Word.
