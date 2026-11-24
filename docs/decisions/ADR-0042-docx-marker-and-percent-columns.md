# ADR-0042: Word's text marker and column widths in percent of the table

Status: Accepted (2026-11-24)

## Context

A Word document generator moving onto the model (OpenXML replaced by Reglet.Docx) found two gaps. Its calculation
header uses Word's text marker (`w:highlight w:val="red"`), which 1.5.0 wrote as shading, so Word's "No Color" did not
remove it and highlight and background could not be told apart. Its tables give their columns in percent of the table
(`w:tblW w:type="pct"` with percent cells), because the code that builds a table does not know the section's text width;
the model only took absolute widths.

## Decision

- **Highlight (1.5.1):** a `Highlight` that is exactly one of Word's 16 marker colours (ST_HighlightColor) is written as
  `w:highlight`; the background stays `w:shd`. Only exact matches: mapping to the nearest marker colour would change the
  colour without telling. Any other colour is written as shading with a write warning, or omitted with a warning when
  the run also has a background. The reader reads `w:highlight` back into `Highlight`. A style's highlight falls back
  without a warning (the style writer has no warning sink).
- **`TableBlock.ColumnWidthPercents` (1.5.2):** a list of percents of the table width, each positive, summing to at most
  100. Columns past the list share what is left of 100 equally; a full list that sums to less than 100 is stretched in
  proportion, as Word stretches percent cells. It replaces `ColumnWidths` when both are set (one property per meaning
  instead of a `Length` with a relative unit, which every other `Length` user would have to reject). All writers
  resolve it in `TableLayout` against the section's text width times `WidthPercent`, so the PDF, the ODT column widths
  and the DOCX grid agree. DOCX writes `w:tblW` in percent (100 without `WidthPercent`), `w:tcW w:type="pct"` per cell
  and `w:gridCol` from the text width; the reader takes the percents back when every cell of the first row has a percent
  width of its own.

## Consequences

- ODT gets absolute column widths, not `style:rel-column-width`. LibreOffice 26.8 still keeps the proportions when the
  text area changes: a table written as 30/70 on A4 with 72 pt margins stays 30.0 %/70.0 % with 5 cm margins and in
  landscape (measured in the PDF export), because the table fills the margins and LibreOffice scales its columns.
- HTML still writes no column widths.
