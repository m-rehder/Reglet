# ADR-0046: Column widths as Excel shows them, indented General cells, formula evaluation

Status: Accepted (2026-11-24)

## Context

Three gaps showed when workbooks written with other .NET spreadsheet libraries moved to Reglet.Xlsx:

- `Worksheet.ColumnWidths` was written to the file unchanged. Excel stores a width that includes the cell padding of
  5 pixels at the default font (ECMA-376 part 1, 18.3.1.13) and shows the width without it, so a width of 40 showed as
  39.29 – every column about 5 pixels narrower than intended. Callers think in the shown width (Format → Column Width).
- Excel indents only left or right aligned cells; an indent with General alignment was written and ignored.
- Reglet writes formulas without a calculated value (ADR-0043), so a test that checks a sum or a discount needed a
  calculator of its own.

## Decision

- **`ColumnWidths` is the width Excel shows**, in characters of the default font's widest digit (7 pixels at Calibri
  11). The writer stores `Truncate((chars × 7 + 5) / 7 × 256) / 256`; the reader turns it back into whole pixels less the
  padding, in characters to 1/100. Excel rounds widths to whole pixels, so 10.5 reads back as 10.43. No option for the
  old meaning: the stored width is a file detail, the shown width is what a caller means. This changes the widths of
  workbooks written with 1.6 and 1.7 (they come out as wide as they were meant to be).
- **An indented cell with General alignment is written left aligned**, as Excel's dialog does when an indent is set;
  right alignment with an indent stays right.
- **`WorksheetCell.Evaluate()`** calculates a deliberately small scope, enough to check a workbook in tests: numbers,
  cell references with an optional sheet (`Sheet2!B3`, `'My sheet'!$B$3`), ranges inside `SUM` and `PRODUCT`,
  `+ - * / ^`, a sign, `%` and parentheses, with Excel's precedence (a sign binds tighter than `^`). Referenced formula
  cells are calculated too, each once per call; an empty cell is 0, a date its serial number, a text that reads as a
  number counts, and `SUM`/`PRODUCT` skip text, booleans and empty cells in ranges as Excel does. Everything else – other
  functions, text literals, names, whole columns, a circle, a division by zero, a text that is no number, a chain deeper
  than 500 cells, nesting deeper than 200 – throws a `FormulaException` naming the cell and the reason, instead of
  giving a wrong number. It is not a calculation engine and does not write values into the file.

## Consequences

- Workbooks written with 1.6/1.7 and read with this version show slightly narrower widths than their callers set
  (39.29 instead of 40); written again they keep the shown width.
- The evaluator grows only on request; a full calculation engine stays out of scope (ADR-0043).
