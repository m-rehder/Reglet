# Reglet – Release notes

## Unreleased

### PDF

- A line set in an East Asian font is as high as in Word: 1.3 times the font's Windows ascent and descent (OS/2
  `usWinAscent` + `usWinDescent`), the extra room half above and half below the text. A font counts as East Asian when
  its OS/2 table names a Japanese, Chinese or Korean code page. Measured in Word for Windows with 13 fonts, among them
  Microsoft YaHei (1.716 em), Yu Gothic (1.672 em), SimSun (1.30 em) and Meiryo (1.95 em). Before, Reglet took the
  hhea line (YaHei 1.32 em, Yu Gothic 1.60 em), so Chinese documents had fewer pages than in Word and Japanese more.

## 1.7.15

### PDF

- A table row with a paragraph that keeps with the next (`ParagraphStyle.KeepWithNext`, or a heading) moves to the
  next page together with the start of the next row when that one no longer fits, as in Word; a chain of such rows
  moves as one. Before, a subheading row of an order stayed alone at the foot of the page.
- The room a minimum line height (`LineSpacing.AtLeast`) adds lies above the text, as in Word: the baseline sits at
  the foot of the line. Before, the text sat at the top and stood up to the extra room higher than in Word.
- A line may break after a hyphen inside a word before any character but a space, another hyphen or a mark that
  cannot start a line, as Word does: `Staub-` / `/Putzschutzabdeckung.`.

## 1.7.14

### Fonts

- A TrueType collection whose fonts claim more table data than the file holds, or more than 1 024 fonts, is rejected
  with a `FontException` when it is loaded. Before, a crafted `.ttc` whose table records all pointed at one large
  region made Reglet try to copy it thousands of times and run out of memory, also from `FontCollection.FromSystem`.
- `FontFace.Load`/`LoadFile`, with or without a family name, copy out only the font they return; the others of the
  collection are only read in place. A broken font elsewhere in the collection no longer stops them.

### PDF

- The ideographic space U+3000 is East Asian text and takes the East Asian font, as in Word. Before, a space took the
  font of the character before it, so `2020　による` drew U+3000 in Arial, which lacks it, and a document in strict mode
  stopped with "the embedded font does not contain all required glyphs".
- Any other space the font before it lacks (U+2009, U+202F, …) goes through the East Asian and the fallback fonts like
  any other character.

## 1.7.13

### Fonts

- TrueType collections (`.ttc`) load: `FontFace.LoadFile(path, familyName)` and `FontFace.Load(data, familyName)`
  take the font of a family (case-insensitive), so `LoadFile(@"C:\Windows\Fonts\msyh.ttc", "Microsoft YaHei")` and
  `"Microsoft YaHei UI"` are told apart; `FontFace.LoadCollection`/`LoadCollectionFile` list all fonts of a
  collection; `Load`/`LoadFile` without a family take its first font. Only the chosen font is kept and embedded (as a
  subset), not the whole collection. Windows ships its CJK fonts (Microsoft YaHei, Yu Gothic, MS Gothic, Microsoft
  JhengHei, SimSun) only as collections. Before, any `.ttc` failed with "TrueType collections (ttcf) are not
  supported".
- `FontCollection.FromDirectory` and `FromSystem` also read `.ttc` files, every font of them. `FromSystem` skips a
  single unusable font of a collection and keeps the others; `FromDirectory` still fails on the first unusable file.
- An unknown family names the families the file has. A font that cannot be used fails when it is loaded, not later
  when the PDF is written.

## 1.7.12

### Widow and orphan control

- `ParagraphStyle.WidowControl`: null (the default) is on, as in Word, where a missing `w:widowControl` is on; `false`
  switches it off. DOCX reads and writes `w:widowControl` (only an explicit off is written), ODT `fo:widows` and
  `fo:orphans` (the default style states 2, since LibreOffice takes missing values as none).
- The PDF keeps at least two lines of a split paragraph on each page in a table row as well, as in the text: a row
  whose paragraph may not split where the page ends moves whole. Measured in Word 16 for paragraphs of 2 to 5 lines
  with room for 1 to 4 (4 lines with room for 3: 2 + 2, not 3 + 1); LibreOffice lays out Reglet's DOCX and ODT the
  same way. This replaces the 1.7.11 rule that only kept a paragraph's first line from standing alone.
- With `WidowControl = false` a paragraph splits exactly where the room ends, in the text and in a table row.

## 1.7.11

### East Asian text

- `TextRun.EastAsianFontFamily` and `DocumentDefaults.EastAsianFontFamily` name the font for East Asian characters
  (Han, kana, Hangul, Bopomofo, CJK punctuation and full-width forms). DOCX writes and reads them as
  `w:rFonts/@w:eastAsia` (on the run and in `w:docDefaults`), ODT as `style:font-name-asian`; HTML lists the font after
  the run's own in `font-family`.
- `FontCollection.AddFallback(face)` adds a font and makes its family a fallback (`FontCollection.FallbackFamilies`).
- The PDF draws an East Asian character with the run's East Asian font, then the document's; a character the chosen
  font does not have takes the first of the run's font, its East Asian font and the fallback fonts that has it, as Word
  falls back to a font of the character's script. Before, a run in Arial with Chinese or Japanese text stopped the PDF
  with "the embedded font does not contain all required glyphs" (or showed `?` with the standard fonts).

### PDF

- When the automatic table layout widens a column to its longest word, every other column gives up an equal share of
  the room, as Word does. Before, the columns gave in proportion to their width, so a wide description column lost
  most of it and its text broke where Word's did not. A column that reaches its own longest word stops giving.
- A cell's own border (`TableCell.Borders`) also stays inside a row without a minimum height, as Word draws it; only
  the table's grid above a row takes room in it. A table without a grid whose cells have a 0.625 pt bottom border was
  0.625 pt higher per row than in Word.
- A line may break after a hyphen before a digit as well, as Word does: `DIN EN ISO 9170-` / `2`, `12-12-` / `2022`.
  Before, Reglet followed UAX #14 (no break between a hyphen and a number) and moved the whole word to the next line.
  A hyphen that starts a word (`-5`) still stays with what follows. Within a line, the parts of such a word remain one
  piece of text in the page.
- Spaces at the end of a line no longer count when the line is aligned right, centered or justified; they hang over
  the edge, as in Word. A right-aligned `Date: ` ends with the colon at the edge instead of 3 pt before it.
- In a line of exact height (`LineSpacing.Exactly`), the baseline lies at four fifths of the line, whatever the size of
  its text, as in Word: lines of 21 pt with 6 pt and 10 pt text both have it 16.8 pt below their top. Before, it lay at
  the ascender of the text, so small text stood high in its line, up to 11 pt above Word's.
- Each page has its own resource dictionary that names only the images and forms its content draws. Before, every page
  named every image of the document, so a page copied or extracted on its own carried all of them.
- A table row at the foot of a page no longer leaves the first line of a paragraph with more lines alone there; that
  line moves on with the rest, and the row moves whole when nothing else of it would stay, as Word does also without
  widow control (measured in Word with rows of line breaks, wrapped text and single-line paragraphs).

## 1.7.10

### PDF

- The space before the first paragraph of a page stays after a section break and after a hard page or column break
  (`PageBreak`, `ParagraphStyle.PageBreakBefore`, `ColumnBreak`), as Word does in compatibility mode 15, which Reglet's
  DOCX declares.
  It is dropped only at the top of a page or column the flow began. Before, it was dropped after a hard break and
  reduced by 6 pt after a section break, so a page that began with a section break stood 6 pt higher than in Word.
- A paragraph that starts at the top of a page no longer moves to the next page for widow and orphan control because
  of its space before.

### ODT

- The paragraph after a page break or a section break keeps its space before (`fo:margin-top`), as in Word.

### API

- `Typography.SpaceBeforeReductionAtSectionStart` is obsolete; Reglet no longer applies the reduction.

## 1.7.9

### PDF

- Only the table's grid above a row comes on top of a minimum row height. A cell's own border (`TableCell.Borders`)
  stays inside the row, as Word draws it: a table without a grid whose cells have a 0.625 pt bottom border and rows of
  at least 34.5 pt with 1.2 pt of padding is 36.9 pt per row again, as in 1.7.7 and in Word. The check whether a row
  still fits on the page keeps counting its bottom border.
- A line with text takes its height from its runs, as in Word; the paragraph mark only sizes an empty paragraph. A cell
  paragraph with a 9 pt run in a 10 pt document is as high as its 9 pt line, no longer as the 10 pt mark. This applies
  to body paragraphs too: a larger `MarkFontSize` no longer raises the last line of a paragraph with text.
- A table without a fixed layout no longer measures the unbroken text of every cell to widen its columns, only the
  longest word.

### DOCX

- `w:tblBorders` is read as no border only when all four outer sides are `none` or `nil`; inner lines cleared alone
  keep the outer border of the table style.
- A body, cell or band that ends with a table of contents, table of figures or index no longer gets an extra empty
  paragraph after it; these are written as paragraphs. The reader also leaves out the closing paragraph after any
  element that is not a paragraph.

### ODT

- Only the table's grid above a row goes into `style:min-row-height`, not a cell's own border. Rows repeated with
  `table:number-rows-repeated` at the end of a table: only the last one counts the border below the table.

## 1.7.8

### PDF

- A minimum row height also leaves the border above the row on top, as Word does: a 15 pt row with 1.2 pt of padding
  and the 0.5 pt default grid is 17.9 pt high, as in Word and in LibreOffice.
- A row that ends on a page also needs room for its bottom border, as in Word: a row whose bottom edge just fits but
  whose border would reach below the text area moves to the next page.
- The empty paragraph the DOCX writer puts after a table that ends the document, and the one that carries a section end,
  is set too (one twip high), so a table that fills the last page to its end gives the same empty page as in Word.

### DOCX

- A body that ends with a table gets an empty paragraph one twip high (no spacing, `w:line="1"` exact) before
  `w:sectPr`. Without it Word adds a paragraph in the default formatting when it opens the file, which could push a
  full last page onto a new, empty one. The paragraph that carries a section end is as small. The reader leaves this
  paragraph out again.
- A header, footer or cell that ends with a chart, an embedded object or a column break no longer gets an extra empty
  paragraph after it; one that ends with a diagram or equation, which a header or footer cannot hold, now gets the
  paragraph it needs.

### ODT

- A minimum row height is written the way the PDF and Word lay it out: ODF's `style:min-row-height` covers the whole
  row, so the cells' top and bottom padding and the border above the row go into it, and come off again when an ODT
  file is read. DOCX, ODT and PDF now give a row the same height.
- In the table's last row the border below goes into the minimum as well: Word adds it to the table, LibreOffice draws
  it inside the row. A table whose last row just fits with its bottom border now breaks in LibreOffice where it breaks in
  Word and in the PDF.

## 1.7.7

### DOCX reader

- A table whose `w:tblBorders` are `none` or `nil` on every side is read as `ParagraphBorder.None`, so a table the
  writer stored without a border no longer comes back with the default grid.

### PDF

- A minimum row height is for the content, as in Word: the cells' top and bottom padding comes on top of it. A 15 pt
  row with 1.2 pt of padding above and below is now 17.4 pt high, as in Word, instead of 15 pt. An exact height still
  includes the padding.
- Outside a fixed layout (`FixedColumnWidths` false, the default) a column narrower than its longest word widens to it,
  as in Word's automatic layout; the other columns give up the room in proportion. Before, the word broke inside
  ("Materi" / "al"). A fixed layout still breaks inside the word.

## 1.7.6

### DOCX reader

- The paragraph font also comes back when the runs have sizes of their own beside fields: if the field runs carry the
  mark's font or size (the writer puts the paragraph's formatting on every field run since 1.7.2), it becomes the
  paragraph's again, so the fields keep it. Runs with exactly that value leave it to the paragraph, runs with their own
  keep theirs.

## 1.7.5

### DOCX reader

- A cell's own margins (`w:tcMar`) are read as `TableCell.Padding` (or per axis); a side equal to the table's padding
  stays with the table, so a table whose padding the writer repeats on every cell reads back unchanged.
- A paragraph that holds only a picture and is centred or right-aligned stays a paragraph with an `InlineImage`, so the
  alignment is kept (a logo set right in a header). Other picture-only paragraphs still become `ImageBlock`s.
- Empty paragraphs are kept, in the text and in cells, with their style: since 1.7.3 the paragraph mark gives them their
  height. The paragraph that only closes a cell or a header/footer after a table, the bare paragraph of an empty cell
  and the paragraph that carries a section boundary are still left out.
- An empty first-page header or footer is kept as a `HeaderFooter` without blocks ("nothing on page 1") instead of
  falling back to the default one. The first-page bands are read only with `w:titlePg`, as Word uses them.
- A paragraph font that Word stores on the paragraph mark and on every run comes back as `ParagraphStyle.FontFamily` and
  `FontSize` instead of as the mark's font plus a font on each run; a mark equal to the paragraph's font is dropped.
  Paragraphs with hyperlinks keep the mark as it is.

### Writers

- A table without rows or cells, which no format shows, is reported once per document by every writer (DOCX, ODT, PDF,
  HTML) instead of being left out silently.

### Templates

- A stray `{{EndIf}}` and an `{{If:…}}` without an end are removed with their paragraph like paired markers, instead of
  leaving an empty paragraph (the unclosed one is still reported).

### PDF

- The last line of a paragraph beside a square-wrapped picture starts at the margin once the picture has ended. A last
  line that held only one word after a wrap fell back to the indent of the paragraph's start.

## 1.7.4

### Tables

- `TableBlock.AutoFit` sizes the columns by their content (Word's AutoFit to contents) when the table has neither
  `ColumnWidths` nor `ColumnWidthPercents`. Every column gets at least its longest word and at most its text without
  line breaks; the rest of the room goes to the columns in proportion to what they still want. Without `WidthPercent`
  the table is only as wide as its content needs, with it the table fills that width.
- DOCX writes `w:tblLayout w:type="autofit"` and `w:tcW w:type="auto"`, so Word sizes the columns itself; the reader
  recognises such a table. The PDF measures the cells; cells that span several columns, images and nested tables do
  not count yet. ODT has no automatic layout: it reports the table and writes equal columns.

## 1.7.3

### Paragraph mark

- New `ParagraphStyle.MarkFontFamily` and `MarkFontSize` format the paragraph mark. Without them the mark takes the
  paragraph's `FontFamily`/`FontSize`, else the document default. DOCX writes the mark as `w:pPr/w:rPr` and reads it
  back; ODT has no paragraph mark.
- PDF, as in Word: the mark sets the height of a paragraph without visible text; an empty run no longer gives its
  paragraph a line of its size. The mark also counts towards the last line of a paragraph, so a paragraph whose text is
  smaller than the mark gets a taller last line. This changes the layout of such paragraphs; give the paragraph style
  the text's size (or `MarkFontSize`) to keep the old one.

### Header distance

- PDF: a `Section.HeaderDistance` (or `FooterDistance`) larger than the page margin is no longer cut to the margin.
  The header starts at its distance, the body below max(top margin, distance + header height), as in Word.

## 1.7.2

### Fields

- DOCX: fields (page number, page count, date, document property, …) write the run formatting of their paragraph style
  (font, size, bold, italic, color, …) on every run of the field, as text runs without their own do. The field result
  no longer appears in the document's default font. The PDF already drew it so.

### Tables

- `TableBlock.Border = null` keeps the thin default grid (0.5 pt), as all writers have done; the documentation said
  "no borders" and is corrected. The new `ParagraphBorder.None` gives a table without borders: DOCX writes every side
  of `w:tblBorders` as `nil`, ODT clears the cell borders. Before, `BorderStyle.None` fell back to the default grid in
  DOCX and ODT.
- A negative `LeftIndent` gives a full set of `ColumnWidths` the room it opens in the left margin: such a table is
  shrunk only where it is wider than the text width plus that room.
- `WidthPercent` accepts up to 600 percent, Word's limit; above 100 the table runs into the right margin. The DOCX and
  ODT readers keep such widths.
- DOCX: a header or footer that ends with a table gets the closing paragraph the format requires without height
  (`w:spacing` before and after 0, exactly 1 twip line), so it no longer adds a line under the table.

## 1.7.1

See ADR-0046.

### Workbooks

- `Worksheet.ColumnWidths` is now the width Excel shows (Format → Column Width): the writer adds Excel's cell padding
  (5 px at Calibri 11) when it stores a width, and the reader takes it off. Before, a width of 40 showed as 39.29 in
  Excel. Excel rounds widths to whole pixels, so 10.5 reads back as 10.43.
- A cell with an indent and General alignment is written left aligned; Excel indents only left or right aligned cells
  and ignored the indent before.
- `WorksheetCell.Evaluate()` calculates a cell for tests: numbers, references (also to other sheets), `SUM` and
  `PRODUCT` over references and ranges, `+ - * / ^`, sign, `%` and parentheses; referenced formulas are calculated
  too. Anything else throws a `FormulaException` that names the cell and the reason. Excel still calculates the file
  itself when it opens it.

## 1.7.0

See ADR-0045.

### MCP server (new tool Reglet.Mcp)

- `reglet-mcp` (`dotnet tool install -g Reglet.Mcp`) gives AI agents nine tools over the Model Context Protocol
  (stdio): create DOCX, ODT, PDF or HTML from Markdown, convert between them, read a document as text, fill a DOCX/ODT
  template, validate a file, create and read Excel workbooks, merge PDFs and extract pages.
- It uses files only inside the `--root` directories (default: the working directory), also not through `..` or a
  symbolic link, and replaces an existing file only on request and only once the new file is complete.
- An application under `apps/`, with the MCP C# SDK and Microsoft.Extensions as dependencies; the libraries stay free
  of dependencies.

## 1.6.1

Fixes from a review of 1.6.0, most of them for files built to harm a service that validates or reads untrusted input.

### Validation

- A PDF whose page tree is nested very deeply no longer exhausts the stack (which ends the process); trees deeper
  than 256 levels are reported and not walked further.
- Decoding PDF streams has a budget of 512 MB per check, so a file of many highly compressed streams, or one content
  stream shared by very many pages, cannot keep a validation busy for long; the rest is reported as not checked.
- The ODF schema check no longer keeps the patterns it derives in the shared schema: memory stays flat across
  validations, validations run in parallel without a lock, and one part that would need more than 400,000 patterns
  stops with a warning.
- Whatever stops a check (an ODF package with an entry twice, a part over the size limit, …) is now a finding in the
  report instead of an exception.

### Workbooks

- `XlsxWorkbookReader` keeps sheet names and cell texts beyond Excel's limits (from other programs) instead of
  refusing the workbook; `XlsxWorkbookWriter` refuses such a name and warns about such a text.
- Reading stops at 10,000 merged ranges per sheet, so a workbook with very many merges reads in bounded time.
- Fractions in number formats (`# ??/??`) are found by continued fractions instead of trying every denominator.

### DOCX

- The warning that a highlight colour falls back to shading comes once per colour, not once per run.

## 1.6.0

See ADR-0043 and ADR-0044.

### DOCX

- The story parts (document, headers, footers, notes, comments) declare the Word 2010 extension namespaces they may
  use (`w14`, `wps`, `wpg`) at their root and mark them ignorable (`mc:Ignorable`), so Word 2007 and ECMA-only readers
  skip shapes, check boxes and comment ids they do not know instead of refusing the file. Word 2010 and later and
  LibreOffice read the files as before.

### ODT

- The output is valid against the ODF schema: heading styles write their paragraph properties before their text
  properties, tracked-change regions carry `xml:id` (beside `text:id`), a pie chart keeps its categories in an x axis,
  and a table's border and cell padding, which ODF only knows on cells, are written as `reglet:border`/`reglet:padding*`
  instead of `fo:*` on the table (read back as before; the old attributes are still read). LibreOffice shows the files
  as before.

### Validation (new package Reglet.Validation)

- `DocumentValidator.Validate(stream)` checks DOCX, XLSX, ODF and PDF files for what Word, Excel, LibreOffice and PDF
  readers reject and reports each finding with severity, part and line. Office Open XML: package structure, content
  types, relationships, well-formed XML and the ECMA-376 Transitional schemas (embedded) after Office's markup
  compatibility; ODF: mimetype and manifest and the ODF 1.3/1.4 RELAX NG schemas (own validator, foreign markup removed
  first); PDF: header, end marker, cross-references without repair, trailer and catalog, and the structure (stream
  lengths and data, page tree, page boxes, content operators and nesting, fonts, annotations).

### Workbooks (new package Reglet.Xlsx)

- A workbook model in Reglet.Core, beside the document model and filled cell by cell: `Workbook`, `Worksheet`,
  `WorksheetCell`, `CellStyle` (font, fill, borders, alignment, wrap, indent, number format), `RichText`,
  `WorksheetImage`, `SheetPageSetup`. `Workbook.AddWorksheet` rejects names Excel refuses (over 31 characters, one of
  `[ ] : * ? / \`, an apostrophe at either end, a duplicate ignoring case); `Workbook.IsValidSheetName` checks one first.
- `XlsxWorkbookWriter` writes it as XLSX: text, numbers, dates, booleans, rich text, formulas (recalculated by Excel on
  open; no cached values), number formats, styles, merged ranges, column widths, row heights, images anchored at a cell,
  page setup with fit to page, print area and repeated rows.
- `XlsxWorkbookReader` reads workbooks back (values, formulas, number formats, styles, geometry, images, page setup),
  resolves theme and indexed colours, and expands shared formulas into each cell. `WorksheetCell.GetFormattedText`
  gives the text Excel shows for a value, including conditions, scientific notation, fractions, elapsed time and digits split by literals
  (`000-00-0000`).
- The OPC package writer moved from Reglet.Docx to Reglet.Core (internal); DOCX output is unchanged.

## 1.5.2

See ADR-0042.

### Tables

- `TableBlock.ColumnWidthPercents` gives the columns in percent of the table width, for code that builds a table
  without knowing the section's text width. Columns past the list share the rest of 100 percent; a full list that sums
  to less than 100 is stretched, as Word does. It replaces `ColumnWidths` when both are set. The PDF, ODT and DOCX
  writers resolve it against the section's text width (times `WidthPercent`); DOCX writes `w:tcW w:type="pct"`, a
  percent `w:tblW` and the grid from the text width, and the reader takes the percents back.

## 1.5.1

See ADR-0042.

### DOCX

- `TextRun.Highlight` (and a character or paragraph style's highlight) is written as Word's marker, `w:highlight`,
  when its colour is one of Word's 16 marker colours (`FF0000` → `red`, `FFFF00` → `yellow`, …), so Word shows it as
  highlighting and "No Color" removes it. Highlight and `Background` now stay apart: a run with both writes
  `w:highlight` and `w:shd`. Any other highlight colour is written as shading, as before, and reported in the write
  warnings (body text; a style's highlight falls back without a warning). 1.5.0 wrote every highlight as `w:shd`.
- The reader reads `w:highlight` back into `Highlight` (it was dropped before).
- Run properties are written in schema order: `w:vanish` after the text effects, `w:bdr` and `w:shd` after
  `w:u` (1.5.0 wrote them earlier).

## 1.5.0

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
