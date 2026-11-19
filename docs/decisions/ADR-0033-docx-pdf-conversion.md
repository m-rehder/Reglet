# ADR-0033: DOCX↔PDF conversion (PDF structure recovery, DOCX→PDF fidelity)

- **Status:** Accepted (2026-11-18, product owner: "the conversion DOCX<->PDF would be nice", details chosen by the implementer: recommended defaults)
- **Date:** 2026-11-18
- **Context:** DOCX→PDF already works through the model (`DocumentReader` → `DocumentWriter`). PDF→DOCX works only
  as far as the PDF reader goes: one paragraph per text line, bold/italic, no lists, headings, tables or images,
  one section per page. Both directions are about *existing* documents. A layout engine that reproduces Word is
  not part of this decision.

## Decision

1. **PDF→DOCX is a structure recovery in the PDF reader**, switched on by `PdfReadOptions.RecoverStructure`
   (default `false`: the existing reader output, and every test built on it, stays as it is). With it on, the
   reader works from the positioned text segments (x, y, size, bold, italic, end x) it already extracts:
   - **Paragraphs:** consecutive lines of one column join into a paragraph when the vertical gap is at most 1.6 times
     the font size and the left edge is the same (±0.5 size) or the line is an indented first line; a new paragraph
     starts after a line that ends clearly short of the column's right edge and with a sentence end, at a larger gap, or at
     a change of left edge. Hyphenation at a line end is joined as the reader does today.
   - **Headings:** a standalone line (short, no sentence end) whose size is at least 1.15 times the body size
     (the size carrying most characters on the document) or that is bold and larger than the body; the distinct larger sizes
     map to levels 1 to 3 in descending order; the rest is a paragraph.
   - **Lists:** a line that begins with a bullet (• ◦ ▪ ‣ – - ·) or a number (`1.`, `1)`, `a)`) followed by a space,
     with continuation lines at a hanging indent, becomes a list; consecutive items form one list; a nesting level
     follows from the left edge (up to the model limit).
   - **Tables:** consecutive lines in which the segments start at the same x positions (≥ 2 columns, ≥ 2 rows, gaps
     wider than 2 times the size) become a `TableBlock`; no ruling lines are read, so no cell borders or spans are recovered
     (the table gets a plain border).
   - **Images:** an image XObject (`Do`) is placed as an `ImageBlock` at its vertical position between the text
     lines. JPEG (`DCTDecode`) is taken as it is; 8-bit RGB/Gray with `FlateDecode` is re-encoded as PNG; anything else
     (masks, CMYK, JBIG2, JPX, inline images) is reported and left out.
   - **Headers and footers:** lines in the top or bottom 8 % of the page that repeat on at least half the pages
     (at least three pages; digits compared as the same) are dropped and the drop is reported (they are not mapped to
     the header and footer of the section, because the page break decides nothing there).
   - **Page structure:** the recovered document is **one flowing section** (the page size of the first page): the page
     boundaries of the PDF are not page breaks. A paragraph, table or list that runs over a page boundary is joined again
     (the previous page ends at the bottom of the text area, the paragraph is open at its end, the next page starts at the margin;
     tables with equal column count; lists of equal kind and level). Pages of different sizes are reported.
   - **Columns:** six or more rows of aligned cells whose text averages at least 25 characters are taken as columns of prose,
     not as a table: each column becomes one paragraph (read one after the other; paragraph breaks inside a column are lost) and the
     read is reported. A real table of long descriptions is misread this way (accepted trade-off). Tested through a borderless
     table, because the PDF writer's own text columns were not checked as input.
   - **Text columns:** a vertical band that almost no cell crosses (at most 5 % of the cells, at least eight cells on each
     side, at least one font size wide, in the middle 40 % of the page) on a page whose cells average at least 25 characters splits
     the page into a left and a right column, read one after the other (a paragraph that runs from the left to the right column is
     two paragraphs). Tested with the writer's own two-column sections.
   - **Scans:** a page of images only is reported (no OCR).
   - **Reported, not solved:** rotated text, vector graphics, form fields.
2. **DOCX→PDF** keeps using the model and the existing PDF renderer. No new engine. Instead a measuring tool
   (`tools/fidelity`) compares the PDF the library writes with the PDF LibreOffice writes from the same DOCX on a corpus
   (page count, line breaks per paragraph, the y position of each paragraph's first line). The largest systematic
   deviations (line breaking, table column widths, spacing around lists, heading spacing) are fixed in the renderer where
   the model is ambiguous only in the renderer's favour; Word fidelity is not claimed and not measured, because
   LibreOffice is what runs here.
3. **Round trip:** DOCX→PDF→DOCX is checked for text identity and for the recovered paragraph/heading/list/table count
   on documents the library wrote itself; this is the acceptance test of the recovery.
4. **Facade:** nothing changes in `DocumentReader`/`DocumentWriter`; `DocumentReader.Read(pdf, DocumentFormat.Pdf)`
   keeps the old output. The option is passed with `PdfReadOptions`. (A convenience `Convert` method is not added.)
5. **Limits:** 20,000 text lines and 2,000 images per page, the image bytes within `ResourceLimits`, everything iterative.
6. **Swift port:** same algorithm and arithmetic; the models read from PDFs the .NET writer produces are compared in the
   reader parity job.

## Consequences

- The recovery is heuristic; a PDF not made by this library may come out with wrong paragraph breaks, missed
  headings or columns read across. Reports name what was detected but not solved.
- With the option off, nothing changes for existing users.
- The fidelity work for DOCX→PDF is bounded by what the measurements show; the layout of Word itself stays out of reach.

## Not decided here

OCR, ruling-line tables with borders and spans, multi-column reading, PDF forms and annotations to DOCX, tagged-PDF
structure (`/StructTree`) as a better source of paragraphs (a possible later improvement), a layout engine that follows Word.

## Findings from the implementation

- **Measured (FIDELITY=1, `FidelityChecks`, LibreOffice 7.x as reference):** three generated documents (text with headings,
  tables, lists). Without an embedded font the library's PDF uses Helvetica for Calibri, which is wider than Carlito/Calibri,
  so page counts differ (lists: 5 against 6 pages). With a metric-compatible font (`PdfWriteOptions.FontCollection` with Carlito
  registered as "Calibri", `FIDELITY_FONTS=1`) the page counts are equal on all three documents. Fidelity therefore depends on
  the caller supplying a font that matches the document's font; the library ships none.
- **Fixed:** the PDF renderer had no widow/orphan control (Word and LibreOffice default: two lines each side). It now plans the
  break of a paragraph that splits; the lists document went from 3 to 6 of 6 matching page starts. This changes existing PDF output
  (paragraphs near a page end).
- **Renderer bug found by the column test:** a paragraph that continued in the next text column was drawn at the left
  edge of the previous column (over the first lines of that column). Fixed: the line position comes from the current column.
- **Drift explained and fixed:** the remaining difference was the space between a paragraph and the next one. LibreOffice
  uses the larger of "after" and "before"; the library added them. A hand check in Word (`spacing.docx`, screenshot 2026-11-18)
  shows the same as LibreOffice (about 26 pt between the last line of a paragraph and a following 12 pt heading, not 31 pt), so the
  PDF renderer now takes the larger value (not after a section break or at the top of a page). The text document then agrees with
  LibreOffice on 8 instead of 3 of 13 page starts.
- **Foreign input:** the LibreOffice PDFs of the three corpus documents are recovered with exactly the block counts of the source
  (40 headings and 40 paragraphs; 1 heading, 6 tables and 6 paragraphs; 12 paragraphs and 12 lists) once page boundaries are joined.
- **PDF→DOCX:** images are recovered also inside form XObjects (tested with a hand-made PDF); headers/footers are dropped,
  not mapped; the DOCX written from the recovered model keeps the source text in order (`PdfStructureChecks`). LibreOffice's HTML export of the same three documents is read by the HTML reader with the source's block counts (ADR-0032 check).

## Real foreign PDFs (2026-11-19, `FOREIGN_PDFS=dir`)

Five public PDFs read with `RecoverStructure` (share of the `pdftotext` words found again in the result):
Wikipedia print of "Berlin" 93 %, Bundestag Drucksache (62 pages) 92 %, BITV test report 92 %, Berkeley two-column guide 89 %, arXiv physics paper 68 %.
None crashed; 62 pages take about 140 ms. Findings:
- Formula-heavy two-column text is the weak spot: lines with Greek and math symbols are misread as tables (23 tables in a 10-page paper) and a rotated margin stamp (arXiv id) ends up in a heading.
- Wikipedia: headings, lists and 177 images are recovered, "→ Main article" lines become headings (size heuristic).
- Page sizes in the Drucksache differ; the recovered document takes the first one and reports it.
- Fixed the same day: (1) the gutter search is now a white-space strip that stays empty in nearly all text lines (also 6 pt wide), not a cell-based one, so a two-column paper with a narrow gutter is split into columns; (2) prose columns that still end up as a table are recognised from 3 rows (was 6); (3) table cells get the spaces between their pieces (words had been glued together); (4) rotated text (arXiv margin stamp, sideways labels) is left out and reported. arXiv paper: 68 % -> 86 % of the words, 23 -> 10 tables, title is a heading again.
- Fixed afterwards (ligatures and formulas): simple fonts without a ToUnicode map now use the glyph names of `/Encoding /Differences` (LaTeX and dvips: `fi`, `ffi`, `nu`, `alpha`, dashes, math symbols) through a subset of the Adobe Glyph List (`PdfGlyphNames`, also `uniXXXX` and accented letters); and line building puts superscripts and subscripts into their line in horizontal order (the baseline is the largest piece's, the tolerance uses the larger size), so `m2 or m3` stays in place instead of drifting to the end of the line. Berkeley guide 88 % -> 99.5 %, Wikipedia 93 % -> 95 %, arXiv 86 % -> 89 %.
- Still open: no real math layout (fractions, roots, integral limits are read as text lines in reading order); Type 1 fonts with a built-in encoding and no `/Differences` (names only inside the font program) and Type 3 glyphs without names stay unmapped; (the figure-page problem below was fixed).

### Columns on pages with figures (2026-11-19)
A page with a figure whose labels cross the gutter was not split into columns (lines of both columns glued: "heavMSW"). The gutter search now counts, per candidate strip, the lines with broad text on both sides (each side one run, at least 20 % of the page width) against the lines that cross it; the strip wins when it has at least 8 such lines and at least as many as crossing lines, and the strip separating the most column lines is taken (not the widest). Lines that cross the gutter stay in place and divide the page into bands, each read left column first. The "prose, not a table" precondition counts cells of 25+ characters (at least 10) instead of the average, because formulas pull the average down; the one-run rule keeps the cells of a table (several per side) from being taken for columns (the Drucksache keeps its 99 tables). arXiv: tables 8 -> 2, the glued lines are gone. Risk: a two-column layout of broad single-run table cells would be read as columns.
