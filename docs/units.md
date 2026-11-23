# Units, defaults and typography

This document records the cross-format definitions (phase I).

## Canonical lengths (I02)

The model stores **all lengths in points** (`Length.Points`, 1 inch = 72 points). Millimeters,
centimeters and inches are converted with low loss on creation; the writers convert only at
their format boundary (twips, `cm`, EMU). This makes the meaning of a length unambiguous,
independent of the output format.

| Input | Canonical value |
| --- | --- |
| 1 inch | 72 pt |
| 25.4 mm | 72 pt |
| 2.54 cm | 72 pt |

## Default paper size (I03)

**A4** (210 × 297 mm) is the documented default size of a section; **Letter** (8.5 × 11
inches) remains explicitly selectable. The writers adopt the section's size unchanged.

## Default margins (I04)

A section without a specification uses `PageMargins.Normal`: **1 inch (72 pt) on all four
sides**. All writers read the same section, so the initial values are identical; they convert
only internally to the format (DOCX: 1440 twips, ODT: `72pt`, PDF: point coordinates).

## Typography baseline (I05)

The central typographic values live in `Reglet.Typography` and apply to all writers:

| Value | Size |
| --- | --- |
| Headings 1–6 | 16 / 14 / 12 / 11 / 11 / 11 pt |
| Space before heading | 12 pt |
| Space after heading | 6 pt |
| Space after paragraph (`ParagraphSpaceAfter`) | 6 pt |
| Cell padding left/right (`TableCellPaddingHorizontal`) | 5.4 pt (108 twips, Word default) |
| Cell padding top/bottom (`TableCellPaddingVertical`) | 0 pt |
| Line height | PDF embedded: the metrics of the font file (ascender + descender + line gap from `hhea`); PDF standard font: 1.2 × font size (`Typography.LineHeightFactor`); DOCX and ODT "single" |
| Space before after a section break | `max(0, SpaceBefore − 6 pt)` (`Typography.SpaceBeforeReductionAtSectionStart`). Measured in LibreOffice (and observed as "~5 pt" in Word at 12 pt); DOCX lets Word/LibreOffice compute it, ODT and PDF reproduce it. After a manual page break: 0. |
| Baseline | PDF: at the ascender of the font below the line start (Word/LibreOffice model); DOCX and ODT leave it to the renderer |
| Table border (`TableBorderWidth`) | 0.5 pt, black, outer and inner |
| Horizontal table borders in the PDF | take their width, as in Word: a row is its top border plus its content, the table ends with its bottom border |
| Line height (factor) | 1.2 × font size (PDF); DOCX and ODT "single" |

The writers convert these values into their own unit (twips, `pt`, point coordinates).

Headers/footers in DOCX and ODT lie inside the page margin, with the distance
`Typography.HeaderFooterDistance` (0.5 inch) from the sheet edge. The body begins at
max(page margin, distance + height of the area). ODT reproduces this via a reduced page margin
and the minimum height of the area; for multi-line footers the ODT body is shortened somewhat
more than in Word.

Space before at the top of the page (as in Word, confirmed by Word screenshot): if a paragraph
or heading starts at the top of a page that was created by a manual page break or by overflow,
its space before is dropped. At the start of the document and after a section break it remains.
ODT sets `fo:margin-top="0pt"` on the break style for this (overflow is handled by LibreOffice
itself), PDF omits the space at the start of the page.

Headings never stand alone at the end of a page: DOCX uses `keepNext`, ODT
`fo:keep-with-next="always"`, PDF keeps the heading together with the first line of the
following block (provided both fit on one page).

### Space after paragraphs

`ParagraphStyle.SpaceAfter = null` means "not set" and takes the default. `Length.Zero` is an
explicit zero and any other value applies unchanged. The default depends on the context and is the
same in all formats (`ParagraphSpacing`):

| Context | Space after the paragraph |
| --- | --- |
| Normal paragraph | `Typography.ParagraphSpaceAfter` (6 pt) |
| List item | 0 (compact) |
| Last paragraph of a list | 6 pt, so that there is air after the list |
| Paragraph in a table cell, header/footer | 0 (compact) |

Implementation: DOCX sets the default in `docDefaults` and sets compact paragraphs explicitly
to `w:after="0"`; ODT sets it on the default paragraph style and uses an automatic style with
`fo:margin-bottom="0pt"` for compact paragraphs; PDF accounts for it directly. Both spacings are
tri-state since [ADR-0015](decisions/ADR-0015-tri-state-spacing.md): an explicit zero written by
a caller survives a write/read cycle, while an unset value inherits.

## Table widths (I07)

A shared, documented priority rule applies to column widths (`Reglet.TableLayout`):

1. Widths given in `TableBlock.ColumnWidths` are **absolute** and apply unchanged.
2. All other columns share the **remaining** available width **equally**
   (available width = page width − left/right margin).

More widths given than columns, or explicit widths without room for remaining columns, lead to
an error; no zero widths are output.

The computation is in points; the writers convert into their unit (DOCX: twips, ODT: `cm`
column styles, PDF: point coordinates).
