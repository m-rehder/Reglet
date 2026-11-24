# ADR-0043: Workbooks: a spreadsheet model and an XLSX writer and reader

Status: Accepted (2026-11-24)

## Context

Reglet writes documents: flowing text in sections, paragraphs and tables. Reports and quotes often come with a
calculation workbook beside them, which today needs a second library (and, through it, the OpenXML SDK). A workbook is
not a document: it is sheets of addressed cells with formulas, number formats and a print setup, so it does not fit
`PortableDocument`. The package format, however, is the same OPC container DOCX uses.

## Decision

- **Own model in Reglet.Core**, beside the document model: `Workbook`, `Worksheet`, `WorksheetCell`, `CellStyle`,
  `RichText`, `WorksheetImage`, `SheetPageSetup`. Core, so that an ODS writer can follow without moving types. Unlike
  the document model it is **mutable**: a sheet is filled cell by cell (`sheet.Cell("B3").Value = 5`), as with the
  common spreadsheet libraries, which keeps migrations short. Styles are immutable records changed with `with`.
- **Sheet names are checked when a sheet is added**: 1–31 characters, none of `[ ] : * ? / \`, not starting or ending
  with an apostrophe, unique ignoring case. An invalid name throws, since a workbook with one is refused by Excel;
  `Workbook.IsValidSheetName` lets a caller check names it builds from data first.
- **Values**: text, numbers (stored as double), `DateTime`/`DateOnly` (as serial numbers), booleans and rich text (runs
  with their own font). A date without a number format gets Excel's short date (built-in format 14, with a time 22),
  so it shows as a date.
- **Formulas** are written as text without a cached value, and the workbook asks Excel to recalculate on load
  (`calcPr fullCalcOnLoad`). There is no calculation engine: computing formulas is a project of its own and Excel and
  LibreOffice do it when they open the file.
- **Number formats** are format codes. Every code except General is written as a custom format, also when it equals a
  built-in one, so the file shows the same in every locale.
- **Styles**: font (name, size, bold, italic, colour), a solid fill, borders per side (thin, medium, thick, with
  colour), horizontal and vertical alignment, wrap and indent. `Worksheet.SetOutsideBorder` frames a range, and
  `SetStyle` changes the style of every cell in a range. Fonts, fills, borders, formats and cell formats are shared in
  `styles.xml`.
- **Geometry**: merged ranges, column widths in Excel's character units, row heights in points.
- **Images**: PNG or JPEG anchored at a cell (`xdr:oneCellAnchor`), sized in pixels; without a size the image's own
  pixel size applies (`ImageInfo` reads it).
- **Page setup**: orientation, paper (A4, Letter), fit to N pages wide and M tall (0 = as many as needed), margins,
  print area and rows repeated at the top (the defined names `_xlnm.Print_Area` and `_xlnm.Print_Titles`).
- **Package Reglet.Xlsx** with `XlsxWorkbookWriter` and `XlsxWorkbookReader`. The OPC package writer moves from
  Reglet.Docx to Reglet.Core (internal) and serves both. The facade does not reference Reglet.Xlsx: a document user
  does not pay for workbooks.
- **Reader, narrow on purpose**: sheets, cells with value, formula, number format and style, merged ranges, column
  widths, row heights, PNG/JPEG images (one- and two-cell anchors) and the page setup; enough to read back what the
  writer writes and to check a workbook in tests. Colours given by theme (the workbook's colour scheme; indices 0 and 1
  are Background 1 and Text 1) or by the legacy index palette are resolved to RGB, with Excel's tint applied in HSL.
  The dependent cells of a shared formula get the first cell's formula with its relative references moved by their
  offset, as Excel does (whole-column and whole-row references are not moved).
- **`WorksheetCell.GetFormattedText`** formats a value with its number format as Excel shows it, for the given culture:
  sections, conditions (`[>=100]`), digit placeholders, thousands separators and scaling, percent, scientific notation
  (also engineering, `##0.0E+0`), fractions (`# ?/?`, fixed denominators), literals, `[$…]` currency tokens, `_`
  spacing, dates and times. Which section applies and when it shows a sign follows ExcelNumberFormat, the formatter common .NET
  spreadsheet libraries use, so tests that compared their formatted strings keep their expectations.
- Out of scope for now: a calculation engine, charts, conditional formatting, data validation, defined names other
  than the print ones, tables, filters, hyperlinks, comments, protection, frozen panes, outlines and encryption.

## Consequences

- One more package in the release; the zero-dependency and dependency-boundary checks cover it (Reglet.Xlsx → Core).
- A workbook's cells are only as correct as the formulas a caller writes; the writer does not parse them.
- ODS can reuse the model; until then a workbook is XLSX only.
