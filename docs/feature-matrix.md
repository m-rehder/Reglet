# Feature matrix version 1

This matrix is the **single leading document for feature status**: how each public feature is
implemented in the three formats, the known limits, the open work and what is out of scope. It is
the binding basis for the compatibility promise (see [../README.md](../README.md)). Other documents
(ADRs, README, release notes, sample mappings) explain decisions and history and link here instead
of repeating a status. A change that alters a status updates this file in the same commit.

Sections: [Document and pages](#document-and-pages) · [Text structure](#text-structure) ·
[Lists and tables](#lists-and-tables) · [Images and control](#images-and-control) ·
[Reading and round trip](#reading-and-round-trip-phase-o) ·
[Complex scripts](#complex-scripts-and-bidirectional-text-phase-p) ·
[Accessibility](#accessibility-phase-q) · [PDF/A](#pdfa-archive-formats-phase-r) ·
[Templates](#templates-phase-s) · [Known limits](#known-limits) · [Open work](#open-work) ·
[Out of scope](#out-of-scope) ·
[Fallback behavior](#fallback-behavior-i09i10)

Legend:
- **supported** – the feature is mapped directly in the respective format.
- **approximated** – the feature is mapped in spirit, but not exactly.
- **unsupported** – the feature is not mapped (produces a warning or error depending on the
  option, see [Fallback behavior](#fallback-behavior)).

## Document and pages

| Feature | DOCX | ODT | PDF |
| --- | --- | --- | --- |
| Metadata (title, author, subject, keywords) | supported | supported | supported |
| Language (`Language`) | supported | supported | supported |
| Creation time (`Created`) | supported | supported | supported |
| Custom properties | supported | supported | supported (Info dictionary entries) |
| Content statistics (`DocumentStatistics`, AE02) | supported | supported | supported |
| Write progress (`IProgress<WriteProgress>`, AE01) | supported | supported | supported |
| Write protection (`w:documentProtection`, AD03) | supported (with an optional password as a salted hash; `None` keeps a source protection, `Remove` removes it) | unsupported | unsupported |
| Encryption (PDF standard security handler, AD02; DOCX agile, OF-E01; ODT ODF 1.3, OF-E05) | supported (ECMA-376 agile, AES-256/SHA-512 with integrity check; `DocxWriteOptions.Encryption`/`DocxReadOptions.Password`; only the algorithm variant Word writes, no certificate key encryptor) | supported (ODF 1.3 package encryption: AES-256-CBC, PBKDF2-HMAC-SHA1, SHA-256 start key/checksum; `OdtWriteOptions.Encryption`/`OdtReadOptions.Password`; round-trip; LibreOffice interop is manual) | supported (writes R6/AES-256, reads R2–R6; `PdfWriteOptions.Encryption`/`PdfReadOptions.Password`) |
| Digital signature (BJ02, OF-E02; ODT OF-E05) | supported (OPC signature part with an XML signature over the parts, RSA/SHA-256 and C14N 1.0; a second signature keeps the first valid; `DocxWriteOptions.Signature`, `DocxSignatureReader`) | supported (XML signature in `META-INF/documentsignatures.xml`, RSA/SHA-256, C14N for plain XML parts and raw octets for binary/encrypted parts; a second signature is appended; `OdtWriteOptions.Signature`, `OdtSignatureReader`; certificate chain and trust stay with the caller) | supported (detached CMS SignedData, RSA/SHA-256; PAdES baselines B-B, B-T (RFC 3161) and B-LT (store and VRI in an incremental update) as well as a document timestamp; `PdfWriteOptions.Signature`, `PdfDocumentTimestamp.Add`; `PdfSignatureReader` verifies byte range, CMS signature, time-stamp token and document timestamp, and can check the chain against caller-supplied roots) |
| Non-link PDF annotations (OF-F01) | – | – | supported (sticky note, highlight, stamp and free text; `PdfWriteOptions.Annotations`, `PdfDocumentReader.ReadAnnotations`) |
| Viewer preferences, page layout, page mode (OF-F02) | – | – | supported (`PdfWriteOptions.ViewerPreferences`/`PageLayout`/`PageMode`) |
| Rounded table corners (OF-B04) | reported (a Word cell is a rectangle) | reported (a table cell is a rectangle) | supported (`TableBlock.CornerRadius`, outer corners only) |
| Picture watermark (OF-B09) | – | – | supported (`PageTemplate.PictureWatermark`, PNG/JPEG) |
| Paper size (A4/Letter/A3/A5/Legal, arbitrary) | supported | supported | supported |
| Orientation portrait/landscape | supported | supported | supported |
| Page margins | supported | supported | supported |
| Several sections with their own layout | supported | supported | supported |
| Header/footer with page number | supported | supported | supported |
| Header/footer: first/even pages | supported | supported | supported |
| Starting page number per section | supported | supported | supported |
| Line numbering (every n-th line, start, distance, restart) | supported (`w:lnNumType`) | supported (document-wide `text:linenumbering-configuration`) | supported (numbers in the left margin; empty lines are not counted) |
| Page border of a section (`Section.PageBorders`, AJ05) | supported (`w:pgBorders`) | supported (`fo:border-top` and the other sides in the page layout, ODF 1.3) | supported (drawn around the text area) |
| Automatic hyphenation (BJ01) | supported (`w:autoHyphenation` with `DocxWriteOptions.AutoHyphenation`; the soft hyphens of the text are break opportunities) | supported (`OdtWriteOptions.AutoHyphenation` sets `fo:hyphenate` on the default style; LibreOffice uses its own dictionary, the soft hyphens of the text are break opportunities) | supported (soft hyphens and, with `PdfWriteOptions.Hyphenation`, the words are broken with the patterns of the caller) |
| Headings, lists, tables in the header/footer | supported | supported | supported (artifacts; a band may take at most a third of the page) |
| Images in the header/footer | supported | supported | supported |
| Header/footer position (0.5 in from the sheet edge, body at max(margin, distance + band height)) | supported | supported | supported |
| Page breaks in the header/footer | unsupported | unsupported | unsupported |

## Text structure

| Feature | DOCX | ODT | PDF |
| --- | --- | --- | --- |
| Paragraphs | supported | supported | supported |
| Headings 1–6 | supported | supported | supported (bold + size; tagged `H1`–`H6`, outline entry per level) |
| Heading not alone at the end of a page | supported | supported | supported |
| Alignment left/center/right | supported | supported | supported |
| Justification | supported | supported | supported (paragraphs, table cells and bands) |
| Paragraph spacing before/after | supported | supported (via automatic styles) | supported |
| Indents (left/right/first line) | supported | supported (via automatic styles) | supported |
| Bold, italic | supported | supported | supported (standard fonts with real variants; embedded fonts with real variants from `FontCollection`, missing variants synthetic) |
| Underline | supported | supported | supported (line 0.1 em below the baseline) |
| Underline style and color | supported | supported | approximated (single/double/dotted/dashed; wave is drawn solid) |
| Strikethrough (single/double) | supported | supported | supported |
| Superscript/subscript (`Baseline`) | supported | supported | approximated (baseline shift; no automatic reduction) |
| Casing (`AllCaps`/`SmallCaps`) | supported | supported | approximated (SmallCaps reduced to 80 %) |
| Text effects (`TextEffects`: outline, shadow, emboss, engrave) | supported (`w:outline`, `w:shadow`, `w:emboss`, `w:imprint`) | supported (`style:text-outline`, `fo:text-shadow`, `style:font-relief`) | not drawn |
| Character background/highlight | supported (as shading) | supported (one `fo:background-color`; a highlight wins over a background) | supported |
| Named character styles | supported (AI01, `w:rStyle`) | supported (AI01, `style:parent-style-name`) | applied (AI01, no style mechanism) |
| Kerning | supported (threshold) | approximated (boolean; a threshold is written as enabled and read back as 0) | approximated (shaping of embedded fonts; standard fonts without kerning) |
| Character spacing | supported | supported | supported (`Tc`) |
| Horizontal scaling | supported | supported | supported (`Tz`) |
| Baseline shift (`Position`) | supported | supported | supported |
| Line spacing (multiple/atLeast/exact) | supported | supported | supported |
| Paragraph borders | supported | supported | supported (individual lines) |
| Paragraph shading | supported | supported | supported |
| `keepNext`/`keepTogether`/`pageBreakBefore` | supported | supported | approximated (page break and keep-together; `keepNext` as with headings) |
| Tab stops (position/alignment/leader; positions count from the text margin) | supported | supported (converted, ODF counts from the indent) | supported (left/right/center/decimal, leaders) |
| Paragraph direction RTL | supported (`w:bidi`) | supported (`writing-mode`) | supported (mirrored: start/end alignment, indents, tab stops and list markers count from the right) |
| Text columns | supported | supported | supported (2–12 columns) |
| Section start type (next page, continuous, odd/even page) | supported (AI02, `w:type`) | supported (AI02, no master page change) | supported (AI02, blank page if needed) |
| Column break | supported | supported | supported |
| Font family | supported (optionally embedded via `EmbeddedFonts`) | supported (optionally embedded via `EmbeddedFonts`) | approximated (standard or embedded font) |
| Font size, color | supported | supported | supported |
| Page number field in the document text | supported | supported | supported |
| General field code (`SEQ`, `IF`, `MERGEFIELD`, …) | supported (`w:instrText`) | `SEQ`, `REF`, document information, `PAGE`/`NUMPAGES` as ODF fields; others approximated (the cached result is kept as text, the field is reported) | approximated (the cached result is drawn) |
| Calculation of the fields (AJ02) | supported (the calculated result is written as the cached result; `FieldContext` supplies page, date and section values) | supported (the calculated result is written as text) | supported (a field without a cached result is calculated and drawn; `FieldContext` supplies page/date/section values) |
| Find and replace in the model (`DocumentSearch`) | supported (model level; body and bands, also in a read source; an unchanged band block keeps its source XML) | supported (model level; body and bands, also in a read source; a rebuilt package reports unmodelled content) | supported (model level; body and bands) |
| Total page count/date field | supported | supported | supported (total page count via an extra layout pass) |
| Hyperlinks | supported (external relationship; `#bookmark` as `w:anchor`) | supported (`text:a`) | supported (`/Annots` `/Link`: `/URI` action as 7-bit ASCII, `#bookmark` as a named destination) |
| Bookmarks / PDF outline | supported (bookmark starts read and written as anchors) | supported (bookmarks and bookmark starts read as anchors) | supported (`/Outlines`, named destinations) |
| Cross-reference that leads nowhere (`#bookmark` link, page reference or `REF` field whose bookmark is missing, or which only a band or only a note declares) | reported (the reference is written as it is; a `REF` field resolves the bookmark text) | reported (as in DOCX; the field result stays text) | a reference to a band bookmark reaches its first page and a bookmark in a note reaches the note's first line, so only a missing target is reported |
| Bookmark ranges | supported (`w:bookmarkStart`/`w:bookmarkEnd` with paired ids; a bookmark without an end closes right after its start) | supported (`text:bookmark-start`/`text:bookmark-end`, `text:bookmark` for a point) | the start is a named destination; the end adds nothing |
| Table of contents | supported (`TOC` field) | supported (`text:table-of-content`) | supported (dot leaders, right-aligned page numbers from a layout pass, entries link to their heading) |
| Table of figures (`TableOfFigures`, captions are paragraphs with a `SEQ` field) | supported (`TOC \c` field over the label, cached entries are the caption texts) | supported (`text:illustration-index`, `text:table-index` for the label "Table") | supported (dot leaders, pages from a layout pass, entries link to the caption) |
| Alphabetical index (`AlphabeticalIndex`, `IndexEntry` marks) | supported (`INDEX` field, hidden `XE` fields; the application fills in the pages) | supported (`text:alphabetical-index`, `text:alphabetical-index-mark` with `key1` for a sub entry) | supported (letter groups, sort without case and accents, pages of all marks, entries link to the first mark) |
| Footnotes and endnotes | supported (`w:footnote`/`w:endnote`) | supported (`text:note`) | supported (footnotes at the page end, endnotes at the document end) |
| Notes in a header/footer | written as text and reported (Word has no notes in a band, and a note reference in a band part makes the file unreadable for LibreOffice) | written as text and reported (LibreOffice drops a `text:note` in a band) | written as text and reported, as in the other formats, so the bands of all three writers show the same |

## Lists and tables

| Feature | DOCX | ODT | PDF |
| --- | --- | --- | --- |
| Bullet and numbered lists (start value and number format configurable) | supported | supported | supported |
| Character after the marker (`NumberingLevel.Suffix`: tab, space, nothing) | supported (`w:suff`) | supported (`text:label-followed-by`) | approximated (always at the item indent) |
| Lists: color/format per level | supported (AJ04, the color and the weight of the number) | supported (AJ04, the color and the weight of the number) | supported (AJ04, the color and the weight of the number) |
| Lists: one definition continues across separate lists (OF-B02) | supported (one `w:numId` per definition) | supported (`xml:id` + `text:continue-list`) | supported (counter per definition and level) |
| List nesting (several levels) | supported | supported | supported (36 pt indent and bullet •/◦/▪ per level; WinAnsi standard fonts keep •) |
| Tables: rows, cells, header rows | supported | supported | supported (leading header rows repeat on continuation pages as artifacts) |
| Tables: lists, images and nested tables in cells | supported | supported | supported (lists as marked lines; images scaled to the cell; a nested table taller than a page or nested deeper than four levels is drawn as text, warning) |
| Tables: column widths | supported | supported | supported |
| Tables: cell alignment | supported | supported | supported |
| Tables: borders (0.5 pt, outer and inner) | supported | supported | supported |
| Tables: cell spacing (fixed default, see units.md) | supported | supported | supported |
| Tables: column span (`ColumnSpan`) | supported | supported | supported |
| Tables: row span (`RowSpan`) | supported (`w:vMerge`) | supported (`table:number-rows-spanned`) | supported (continuation cells) |
| Tables: row height | supported | supported | supported |
| Tables: vertical cell alignment | supported | supported | supported |
| Tables: cell background | supported | supported | supported |
| Tables: custom cell borders/padding | supported | supported | supported |
| Tables: width in percent | supported | supported | supported |
| Tables: custom borders (style/width/color) | supported | supported | supported |
| Tables: named styles, header/banded backgrounds | supported (`w:tblStyle`, `w:tblStylePr`) | supported (materialized in cell styles) | supported |
| Explicitly disabled cell border | supported (`w:val="nil"`) | supported (`fo:border-*="none"`) | supported (the edge is not drawn) |
| Tables: conditional borders and text formatting per area | supported (AI03, `w:tblStylePr`) | supported (AI03, cell styles) | supported (AI03, applied) |

## Images and control

| Feature | DOCX | ODT | PDF |
| --- | --- | --- | --- |
| PNG | supported | supported | supported (1/2/4/8/16 bit, Adam7 interlacing; transparency as `/SMask`; 16 bit reduced to 8 bit) |
| JPEG | supported | supported | supported (grayscale and RGB) |
| Alternative text | supported | supported (`svg:title`/`svg:desc`) | supported (marked content `/Span /Alt`) |
| Scaling original/fit width/contain | supported | supported | supported |
| Images: crop | supported (`a:srcRect`) | supported (`fo:clip`) | supported (clipping path) |
| Images: floating with wrap | supported (`wp:anchor`, also `Through`/`Behind`/`InFront`) | supported (`draw:frame` and graphic style; ODF cannot distinguish `Through` from `Behind`) | supported (offsets, all six wrap kinds, several images, reflow per line; a `Through` wrap follows the alpha contour of a PNG) |
| SVG images | supported (SVG plus optional caller-provided PNG/JPEG fallback) | supported (`image/svg+xml`) | supported (converted to vector graphics; a documented subset of SVG — paths, basic shapes, simple text with a whole-text or per-glyph rotation (each glyph placed separately, so kerning between glyphs is lost), transforms, colours, opacity, stroke dash patterns, line caps, joins and miter limits, `use` references, style-sheet rules for element/class/id selectors, selected gradients (linear and radial, including repeating linear gradients as tiling patterns and repeating radial gradients as a sampled colour function), tiling patterns and clipping paths (a `clipPath` with several shapes clips to their union; each shape may carry its own transform, rounded-rectangle clips are supported, a nested `clip-path` on a single shape intersects with the outer clip, and a multi-shape union with up to four nested references is applied exactly through the clause decomposition of the union; more than four are reported); masks, markers at the start, the inner vertices and the end of a path (also the `marker` shorthand), base64-embedded PNG/JPEG images; filters and anything else outside the subset are reported) |
| Page breaks | supported | supported | supported |

## Reading and round trip (phase O)

Reading existing files is a scope after version 1 (phase O). Layout is not preserved; a
lossless round trip remains expressly excluded (boundary O06).

| Aspect | DOCX | ODT | PDF |
| --- | --- | --- | --- |
| Paragraphs, headings, formatted text | readable (O02) | readable (O03) | – |
| Paragraph formatting (alignment, spacing, indents) | readable (O02) | readable (O03) | – |
| Lists and tables | readable (O02) | readable (O03) | – |
| Sections, page number field, page break | readable (O02) | readable (O03) | – |
| Metadata (title, author, language, timestamps) | readable (O02) | readable (O03) | – |
| Images (media part, size, alternative text) | readable (O02) | readable (O03) | – |
| Named paragraph styles | readable and referenced (AC01, with the inherited formatting of `w:basedOn`; the run formatting of the style stays on the style, ADR-0010; a body paragraph keeps `w:pStyle`, ADR-0011) | readable and referenced (AC01, with the inherited formatting of `style:parent-style-name`; same) | applied (the run formatting of the style reaches the runs, ADR-0010; an edited paragraph style reaches the layout, ADR-0011) |
| Named character styles | readable (AI01; the run references the style, the formatting stays on it, ADR-0010) | readable (AI01; same) | applied (AI01, resolved at draw time; a style can switch a toggle of the paragraph style off) |
| Table properties (style, borders, padding, widths, row height, cell properties) | readable (AJ01) | partially readable (inherited cell formatting, row heights, table border and default cell padding, relative table width and column widths; relative columns resolve against the table or text width) | – |
| Repeated ODT table rows, cells and columns | – | expanded while reading, subject to reader resource limits | – |
| Page border of a section | readable (AJ05) | readable (AJ05) | – |
| Color and weight of the number of a list | readable (AJ04) | readable (AJ04) | – |
| Text extraction (ToUnicode/CMap) | – | – | readable (O04) |

**Paragraph and section properties when reading:** the DOCX reader takes over the line spacing
(`w:spacing w:line`), the break control (`w:keepNext`, `w:keepLines`, `w:pageBreakBefore`), the
writing direction (`w:bidi`), the tab stops (`w:tabs`), the paragraph borders (`w:pBdr`) and the
shading (`w:shd`); the ODT reader takes over `fo:keep-with-next`, `fo:keep-together` and
`style:tab-stops`. The text columns of a section (`w:cols`) and the numbering definitions of a document
(`numbering.xml`, all `w:lvl` with kind, format, start, colour and weight, `w:lvlOverride`
applied) are read into `PortableDocument.Numbering`; lists reference a definition by name, so
lists that shared a `w:numId` in Word continue each other after a write.

Warnings when reading: unmappable content is reported instead of swallowed and is named
concretely. Headers/footers, images, nested lists and footnotes/endnotes are read (AF18); DOCX
names an unsupported field by its code (`IF`, `SEQ`, …), a comment or a tracked deletion, an
embedded object by its OLE program id, and image positions or crops it cannot map; ODT names an
unsupported ODF field by its element (`text:chapter`, `text:sequence-ref`, …), a comment by its
author, and a drawing object by its element and name, next to images it cannot map. Each message
appears once per document. Empty paragraphs are not adopted.

**Link targets when reading (DOCX, ODT):** only `http`, `https`, `mailto`, `#bookmark` and relative
paths become a `Hyperlink`. Any other scheme (`javascript:`, `file:`, `smb:`, …) and network paths
(`\\host`, `//host`) are dropped with a warning and the link text stays. Links created in code are not
filtered. An unchanged block rewritten with its source XML keeps its original link.

**DOCX runs when reading:** hyperlinks are read as `Hyperlink` (with the external relationship
`r:id` target), tracked insertions (`w:ins`), content controls (`w:sdt`) and simple fields are read
as runs, deleted text (`w:del`) is not. `w:tab` and `w:br` become `\t` and `\n`. A hard page break
in the middle of a normal paragraph splits it into two paragraphs with a page break between them.
PAGE fields become the page number field; their cached result is dropped. `NUMPAGES`, `DATE`,
`DOCPROPERTY`, `DOCVARIABLE` and `PAGEREF` become the matching field inlines (their cached result
is dropped, AF19); any other field (`TOC`, `SEQ`, `REF`, `IF`, `TITLE`, …) becomes a `Field` with its
code and cached result, like in the ODT reader, so it survives a rewrite.
Direct run formatting and named character styles also include underline style and color, shading,
character borders, character spacing, horizontal scaling, kerning threshold and baseline position
where these have model equivalents.

**ODT when reading:** `text:note` becomes a `Footnote` (footnote/endnote), and `text:page-number`,
`text:page-count`, `text:date`, `text:user-defined`, `text:user-field-get` and `text:bookmark-ref`
become their typed field inlines (`PageNumberField`, `PageCountField`, `DateField`,
`DocPropertyField`, `DocVariableField`, `PageRefField`); the cached result is dropped. The writer
puts the built-in properties (`Title`, `Subject`, `Author`, `Keywords`) of a `DocPropertyField` into
the document information fields (`text:title`, …), which read back as `Field("TITLE")` and so on.
Image graphic styles inherit wrapping and crop properties from their parent styles.
Direct text and named character styles retain supported underline styles and colors, double
strikethrough, text borders, character spacing and horizontal scaling across DOCX/ODT conversion.
Numeric baseline shifts in ODT are reconstructed from the effective font size: the text style, a
named parent style, the paragraph style or the document default. The writer uses the same inherited
size when it turns the absolute shift into ODF's percentage, so a shift set directly on a run works
even when the font size comes from its paragraph. ODT enables kerning as a boolean: a DOCX threshold
is written as enabled and read back as a threshold of 0 (kerning from every size), and an explicit
`false` cannot switch an inherited kerning off. ODF has one `fo:background-color` for text, so a
highlight and a background share it; the writer keeps the highlight, reports a differing background
and a read maps the colour to `Background`. Explicit zero character spacing, 100 % scaling and 0 %
baseline shift are written so they can switch off an inherited value.

**ODT without sections:** content that (as with LibreOffice) sits directly in `office:text`
instead of in a `text:section` forms its own section.

**Round trip (O05/O06):** read → write → read yields semantically equal content for the covered
subset (checked in `RoundTripChecks`). A *lossless* round trip remains expressly excluded:
layout is lost when reading; text, headings, lists, paragraph/run formatting, tables, images
and DOCX/ODT headers/footers are read).

**ODT section layout when reading:** the page layout of a section is resolved via the break
paragraph style (`style:master-page-name`, ODF 1.3 § 19.501) of the first paragraph. Here
`style:parent-style-name` is followed (styles from content.xml and styles.xml), so that
inherited master pages of foreign files are also found. Only if no paragraph names a master
page does it fall back to the first page layout.

**PDF text extraction (O04):** text is extracted from the content streams and decoded via the
ToUnicode CMap (Type0/Identity-H) or WinAnsi (simple fonts). Marked content with `/ActualText`
replaces the glyph text, so tagged PDFs with replacement text extract logically. The PDF writer
emits `/ActualText` for a line that mixes right-to-left and Latin letters, so such lines round-trip
in logical order. Classic xref tables, xref streams and object streams (PDF 1.5+ including /Prev
chains and hybrid references) are supported, as well as the filters FlateDecode, LZWDecode,
ASCII85Decode, ASCIIHexDecode and RunLengthDecode including /DecodeParms predictor (TIFF and PNG).
Image filters (DCT/JPX/CCITT) are not decoded.
Text is grouped heuristically into paragraphs (heading/body via font size, columns via x gaps),
layout, images and vectors are not reconstructed. Metadata comes from the Info dictionary; the
page size from the MediaBox. Encrypted PDFs are decrypted with the user or owner password
(`PdfReadOptions.Password`; R2–R4 with RC4/AES-128 and V5/R5–R6 with AES-256, each crypt filter including `/Identity`); without a matching
password the read
raises a `DocumentValidationException`.

**PDF rewriting (AF15/AF16):** `PdfRewriter.Rewrite` reads a PDF and writes an equivalent,
normalised PDF by copying the whole reachable catalog graph, so outlines, name trees, metadata and
forms survive (unlike `PdfPageOperations` extract/merge). Passing a `DocumentMetadata` replaces the
Info dictionary and the catalog's XMP stream. `PdfPageOperations.Extract`/`Merge` carry the
reference source's Info and XMP over; Extract rebuilds the outline hierarchy, the named destinations and
the `/EmbeddedFiles` name tree (filespecs + streams) filtered to the selected pages, and Merge
concatenates the source outlines (page numbers remapped, hierarchy kept). Extract copies no data of other pages
through references: an annotation keeps `/Parent`, `/Popup` and `/IRT` only when the target is copied too. Resources
that a selected page shares with other pages are copied whole. `PdfRewriter` decrypts an encrypted source (the output
is not re-encrypted); Extract and Merge reject it.

**DOCX preservation (AF05/AF07/AF09):** the DOCX reader keeps the source package on the model. An
**unmodified** document (open+save) is written back **byte-identically**, so all modelled and
unmodelled content is preserved. A document whose **sections were not touched** (edited only via
metadata, e.g. `read with { Metadata = ... }`) adopts the original package and patches only
`docProps/core.xml` and `word/settings.xml` — the body and all unmodelled XML stay byte-identical.
A document with a **changed body** is regenerated, but every package part the writer does not
generate itself (comments, theme, glossary, embeddings, unknown custom parts) is carried over
byte-identically — except parts of the kinds the model writes (headers, footers, notes, media, charts) that
nothing in the written package points to any more, and the signature, which no longer covers the document
(ADR-0036) — and if the source had content the reader could not model a `WriteWarning` is
reported instead of losing it silently (`UnsupportedContent.Fail` makes it an error). Unmodelled
content *inside* `word/document.xml` is preserved for open+save, body-unchanged edits and — in a
changed body — for top-level blocks that are unchanged (reference identity) and reference only
surviving definitions (styles/numbering preserved by AF08) and relationships whose source
relationship is copied over, including drawings with offset `wp:docPr` ids (AF06). A block with a
note reference is preserved too when the note keeps its source id: the writer keeps unique source
note ids (new notes get ids above the source maximum), so unmodelled XML in the same block survives
an unrelated insert or delete. Blocks are flattened across sections. The same rule applies inside
header and footer parts (OF-B08): a band block that is still the reader's instance and is
self-contained is written with its source XML, so search/replace (`DocumentSearch`) can change the
text of a band in a read document without losing the unmodelled XML of its other blocks.

**ODT preservation (AF11):** the ODT reader also keeps its source package; an unmodified ODT is
written back **byte-identically** (open+save). When the body is changed, every package part the
ODT writer does not generate itself is carried over byte-identically (pictures and embedded objects only
while written source XML points to them; macros and signatures are dropped with a warning, ADR-0036) and listed in the regenerated
manifest. A metadata-only ODT edit adopts the original package and patches only `meta.xml`, so
`content.xml` and styles stay byte-identical. In a changed single-section body, reference-unchanged
ODT blocks that reference no automatic/named style, plus headings, formatted paragraphs
(automatic paragraph/text styles `P`/`T` are preserved with generated names offset), lists (their
numbering definition keeps its name) and tables (fixed `Cell*`/column styles, directly formatted `TC`/`TR` styles, `TT`
table-level styles and `TB` table-break styles, table names offset), are re-emitted verbatim
(AF12/AF13); named styles and page layouts are preserved by adopting the source `styles.xml` when
the section layout is unchanged. A block with a drawing, embedded image or external link is kept too
when its automatic graphic styles and picture parts survive: the writer preserves those styles,
avoids the source picture names for generated images, and treats a paragraph with inline frames as
one source element — the paragraph is emitted once and the image blocks hoisted from it are skipped,
so the picture is not duplicated. A `#bookmark` link is regenerated because its target may move to a
different block. A source `settings.xml` is
kept verbatim on a
changed-body rewrite (AF13). The preservation paths are gated on the source format being the same
as the writer's, so a DOCX→ODT (or ODT→DOCX) conversion regenerates. A source `word/settings.xml` is adopted and only the managed elements
are merged in (AF08), so compatibility flags and rsids survive a modified rewrite. A source
`word/styles.xml` is adopted too when the model adds no named styles and does not change the
document defaults (custom docDefaults and character/table/latent styles survive); generated
styles missing by `w:styleId` are merged in, and a changed default regenerates. A source
`word/numbering.xml` is preserved too: the generated list definitions are appended with
`numId`/`abstractNumId` above the source maxima (AF08), so no id collides.

## Complex scripts and bidirectional text (phase P)

With **embedded TrueType fonts** the PDF writer shapes text itself; standard fonts (WinAnsi)
remain unchanged. DOCX and ODT leave shaping to the target program (Word, LibreOffice), which
applies the necessary layout tables itself.

| Aspect | PDF (embedded fonts) | DOCX / ODT |
| --- | --- | --- |
| GSUB ligatures (`liga`, `rlig`) | applied (P02) | target program |
| Arabic contextual forms (`isol`/`init`/`medi`/`fina`) | applied (P03) | target program |
| GPOS kerning | applied (P02) | target program |
| Bidi / UAX #9 | runs ordered in the correct direction (P04) | target program |
| CJK font subsetting | applied (P05, only without layout tables) | – |

**Bidi (P04):** the resolution follows UAX #9 with class, bracket and mirroring tables generated
from UCD 18.0.0 (`tools/generate-bidi-data.py`); mirrored characters such as `(` or `«` take
their mirror glyph in right-to-left runs (L4). Each word is shaped with the levels of its whole line, so
neutral characters at word edges (a final period) follow the line context.
Characters removed by X9 (ZWJ, ZWNJ, …) keep the level of their neighbour, so they never split a
right-to-left word. Against the complete `BidiCharacterTest.txt`, 91 691 of 91 707 cases pass
(`BidiTest.txt`: 99.8 %); the deviations are rare combinations of the explicit format codes
`LRE`/`RLE`/`LRO`/`RLO`/`PDF` with brackets or nested isolates. The words of a line are placed in
visual order (also between tab stops); the base direction is the paragraph direction
(`RightToLeft`). Fonts without GSUB/GPOS/GDEF still get the bidi order, only without ligatures
and kerning. Indents of a right-to-left paragraph count from the start edge in all three
formats (`w:ind` with `w:bidi`, `fo:margin-left` with `writing-mode="rl-tb"`, as LibreOffice
renders both); a list marker reads in the paragraph direction (`1.` is shown as `.1`).

**List geometry:** all three formats indent list text 36 pt per level with the marker hanging
18 pt before it (DOCX `w:ind left`/`hanging`, ODT `label-alignment` with a list tab, PDF). A marker
wider than the hanging indent moves the first line to the next default tab stop (36 pt grid), as
the list tab does in Word and LibreOffice.

**Arabic joining (P03):** the Arabic block (U+0600–U+06FF), Arabic Supplement (U+0750–U+077F),
ZWJ/ZWNJ and combining marks follow `ArabicShaping.txt`; Arabic Extended-A/B are treated as
non-joining.

## Accessibility (phase Q)

Tagged PDF as well as style and language information in DOCX and ODT.

| Aspect | PDF | DOCX | ODT |
| --- | --- | --- | --- |
| Structure tree (`/StructTreeRoot`) | yes (Q01) | – (structure from styles) | – (structure from styles) |
| Roles `P`/`H1`–`H6`/`Table`/`TR`/`TH`/`TD`/`Figure` | yes (Q01) | heading styles | outline level |
| Alternative texts | `/Alt` on the `Figure` | `wp:docPr descr` | `svg:title`/`svg:desc` |
| Header rows (`TH`) | yes | `w:tblHeader` | `table:table-header-rows` |
| Language | `/Lang` + XMP | `w:lang` + `dc:language` | `fo:language`/`fo:country` + `dc:language` |
| Document metadata | `/MarkInfo`, `/ViewerPreferences`, XMP | core properties | meta |
| Headers/footers | `/Artifact` | – | – |

**PDF/UA (Q02):** display title, language, alternative texts, reading order and tagged PDF
structure are set. Full PDF/UA conformance (e.g. `pdfuaid:part`, `Table` `/Headers`) is not
assured; the audit (Q05) checks the structure offline, not with an external PDF/UA validator.

## PDF/A archive formats (phase R)

Optional via `PdfWriteOptions.PdfA` (`PdfAConformance.None`/`PdfA2b`/`PdfA3b`). The standard
output remains unchanged.

| Component | PDF/A-2b | PDF/A-3b |
| --- | --- | --- |
| Embedded fonts (mandatory) | yes | yes |
| OutputIntent with ICC profile | yes | yes |
| Document ID in the trailer (`/ID`) | yes | yes |
| XMP with `pdfaid:part`/`conformance` | yes | yes |
| Attachments (`/EmbeddedFile`, `/AF`) | – (rejected) | yes |

**Embedded fonts:** in PDF/A mode every standard font triggers an
`UnsupportedFeatureException`. A standard font can be provided via the key `""` in
`PdfWriteOptions.Fonts` or via `FontCollection.AddDefault`.

**Font variants and license (N11):** `PdfWriteOptions.FontCollection` accepts up to four styles
per family (normal, bold, italic, bold-italic). The PDF writer embeds the matching style for
each text run; if it is missing, the nearest one is used and only the missing attribute is
reproduced synthetically (bold via render mode 2, italic via slanting).
`PdfWriteOptions.Fonts` is retained (one file per family, variants synthetic). Family names are
compared case-insensitively. If a font forbids embedding according to OS/2 `fsType`, it is not
embedded: this is reported (warning or error depending on `UnsupportedContent`) and its text
falls back to the standard fonts. If it forbids subsetting, it is embedded completely; otherwise
only the glyphs actually drawn keep their outlines (the glyph IDs are retained).

**ICC profile:** if `PdfWriteOptions.IccProfile` is missing, a minimal sRGB profile is generated
at runtime (no binary file in the repository).

**Validator:** if `REGLET_VERAPDF` is set, the `PDF/A` suite checks, for PDF/A-2b and 3b (with an attachment), a text page, annotations
and the results of `Extract`, `Merge` and `Rewrite` with veraPDF; without a validator this step is
omitted. CI installs veraPDF 1.30.4 and runs it; the visual regression does not run in CI.

**Glyphs and annotations:** in PDF/A mode a character the embedded font cannot draw is an error
(invisible characters are dropped), and annotations get an appearance stream. Free text is written
line by line without wrapping; what does not fit the box is clipped.

**Page operations:** a PDF/A claim survives `Extract` and `Merge` only when every source claims the
same part; a mix is written without XMP metadata. `Rewrite` keeps the claim of parts 2 and 3.

**Encryption:** PDF/A requires unencrypted files, so `PdfWriteOptions.PdfA` and
`PdfWriteOptions.Encryption` cannot be combined (an `ArgumentException` is raised).

## Templates (phase S)

Templates supply the design and named placeholders `{{Name}}` from an existing DOCX/ODT file;
content bound in `DocumentTemplate` (inline or as blocks) is inserted when writing, the
template's styles are preserved. For PDF, `PageTemplate` bundles header/footer, watermark and
page frame (model-based).

| Aspect | DOCX | ODT | PDF |
| --- | --- | --- | --- |
| File-based template (`DocumentTemplate`) | supported (S02) | supported (S03) | – |
| Table row repetition (`RepeatRows`, AB03) | supported | supported | – |
| Repeat paragraph or row regions (`RepeatRegion`, `RepeatNestedRegion`) | nested regions with shared or per-parent data; unsafe source content is reported | same | – |
| Template row repetition clones the raw XML (merged rows are not recomputed) | ⚠️ | – |
| Bind placeholders, preserve styles | supported (S02) | supported (S03) | – |
| Placeholders in headers and footers | supported in referenced header/footer parts | supported in master-page header/footer styles | – |
| Word `MERGEFIELD` templates | simple and ordinary complex fields with unique inline bindings, also a simple or complex `MERGEFIELD` nested in another field; `MERGEFORMAT`/`CHARFORMAT`, `Upper`/`Lower`/`Caps`/`FirstCap`, `\v`/`\h`, conditional `\b`/`\f` and `\#` numeric pictures (custom numeric format with literal prefix/suffix and percent); native `TableStart`/`TableEnd` regions map to repeated paragraph or table-row regions; unsupported combinations remain and are reported, other field codes are left for Word | – | – |
| Optional typed placeholders | text, ISO date, invariant number, image or field; validated when bound | same | – |
| Page template (`PageTemplate`: header/footer, watermark, frame) | – | – | supported (S04) |
| Mail merge: bind data rows, one file per row | supported (`MailMerge`, AB) | supported | – (PDF uses page templates) |
| Mail merge: conditional regions (`{{If:Name}}`…`{{EndIf}}`), empty bindings | supported | supported | – |
| Data sources of the mail merge (AJ03) | supported (`MergeDataSource`: CSV, XML, JSON, Excel including rectangular named ranges) | supported | – |

The contracts (`DocumentTemplate`, `TemplateContent`, `PageTemplate`, `Watermark`, `PageFrame`)
live in Core (S01). Bindings are format-neutral model content; the template is not read into the
document model. `Declare(name, type)` validates bindings and repeated-row values, including
values modified after row registration; undeclared names retain the existing behavior. Dates use
`yyyy-MM-dd` text or a `DateField` with a value; numbers use invariant decimal syntax. The body
placeholder cannot be typed; reading a template into the model is not promised. A placeholder in
the template without a binding and a binding whose placeholder the template does not contain are
both reported by name, so content is never dropped silently.
`{{Repeat:Name}}` and `{{EndRepeat}}` as standalone paragraphs or table rows delimit a repeated
region. Word's native `MERGEFIELD TableStart:Name` and `TableEnd:Name` markers are converted to the
same regions when data is supplied; a marker inside a table row delimits that row, a marker in a
standalone paragraph delimits the paragraphs between the markers. `RepeatRegion` supplies shared
rows; `RepeatNestedRegion` and `TemplateRegionRow` supply
per-parent rows. A per-parent set overrides a shared set; an absent set falls back to the shared
one, while an empty set produces no content. The writers report regions containing source content
they cannot safely duplicate, and enforce resource limits. Simple embedded pictures, plain content
controls (cloned with a fresh `w:id`) and bookmarks (also ranges across paragraphs, cloned with
fresh names) are supported. A complex inline drawing is cloned too: DOCX gives every
`docPr`/`cNvPr` a fresh id; ODT removes the `draw` names and `draw:id` of the copies and remaps
`xml:id`, `copy-of` and `caption-id`. DOCX also clones a footnote/endnote, renames its bookmarks,
duplicates nested notes recursively, copies a comment inside the note and clones tracked changes
with fresh ids; ODT copies a tracked change into a fresh changed-region. A comment outside a note
is cloned with a fresh id too. Data-bound content controls and sections remain unexpanded and are
reported: a section break (DOCX) or a named `text:section` (ODT) inside a repeated region would
duplicate page setup and a section name, so both writers leave the region unchanged.

The body placeholder binds the document blocks. If it occurs once in each section of the template
and the document has the same number of sections, each occurrence receives the blocks of the model
section at the same position; the sections are no longer merged. A different number of sections or
a section without the placeholder keeps the previous behavior and is reported.

Conversion works via the model (phase AC): a read document can be written to another format
(DOCX↔ODT, and PDF text → DOCX/ODT). Unknown package parts of a template survive the write
(preservation, AC02).

**DOCX (S02):** `DocxWriteOptions.Template` adopts the template's package and binds the
`{{Name}}` placeholders in `word/document.xml` (body and table cells). A whole-paragraph
placeholder replaces the paragraph (block content), a placeholder within a paragraph only the
text (inline content); paragraph and run styles at the placeholder are adopted as the basis, so
that the template's styles are preserved. The body is inserted via the body placeholder
(`DocumentTemplate.Body`) or appended at the end. Inserted lists and images add numbering,
relationships and media; external hyperlinks are registered in the relationship part of the body
or the band. The same bindings apply to referenced headers and footers, including the
default, first and even variants, placeholders split across runs, tables and repeated regions; the
body placeholder applies only to the document body.

**ODT (S03):** `OdtWriteOptions.Template` adopts the template's package and binds the
`{{Name}}` placeholders in `content.xml` (body and table cells). Inline content is inserted
within the template's text span (unformatted text inherits the text style), block content
replaces the paragraph and adopts its paragraph style. Formatted inserted text uses the generated
automatic style and does not additionally inherit the remaining properties of the template style.
Automatic styles are added for inserted content; colliding style names are renamed. The body is
inserted via the body placeholder or appended at the end. Inserted lists and images add styles,
manifest entries and media. The same bindings apply to master-page headers and footers, including
the default, first and even variants, placeholders split across runs, tables and repeated regions;
the body placeholder applies only to the document body.

**PDF (S04):** `PdfWriteOptions.PageTemplate` defines page-wide design. Header and footer of the
template apply to sections without their own header/footer (a section's own area has priority).
The `Watermark` is centered and rotated behind the content with the specified opacity (ExtGState),
the `PageFrame` is drawn as a frame around the type area; both appear on every page.

## Known limits

Supported features with a documented restriction. Each one is reported where content would
otherwise be lost.

- **Fonts:** the caller provides the TrueType files (`FontCollection`, also from a directory);
  `FontCollection.FromSystem()` loads the installed TrueType fonts of the platform, skipping
  unsupported or duplicate files. Hyphenation patterns ship in the optional package `Reglet.Hyphenation`.
- **Lists:** a Roman number above 3999 falls back to decimal (PDF). One numbering definition is one
  continuation, so an ODT document whose two lists share a style but restart reads as continuing;
  restarting is expressed by a second definition (`w:startOverride` is not written).
- **Tables:** positions covered by a row span must be omitted in the model (a cell there is
  skipped). Band parity counts the data rows without leading header rows; Word's `band1Horz` may
  band differently. With a percent width, explicit column widths win and automatic remaining
  columns are approximated. ODT relative columns resolve against an absolute table width, the
  percent width of the text area or, without either, the text area itself; LibreOffice may
  re-adjust `style:rel-width` plus absolute columns.
- **Images:** a DOCX SVG without a caller-provided raster fallback may not display in older Word
  versions (reported). An ODT floating image is placed with absolute offsets relative to its
  paragraph (renderer-dependent), and ODF cannot tell `Through` from `Behind` (reads back as
  behind). In the PDF an opaque image, a JPEG or an SVG has no contour and wraps like a box.
- **Notes:** a reused `Footnote` instance gets one ID (several references share the note); a note
  inside a note becomes a nested `text:note`, which ODF disallows.
- **ODT character formatting:** a highlight and a background share one `fo:background-color` (the
  highlight wins and a differing background is reported); kerning is a boolean, so a DOCX threshold
  becomes "enabled from every size" and an explicit ODT kerning off reads as "not set"; a named
  style without its own font size resolves a numeric baseline shift against the document default.
- **Fields and references:** the PDF lays out at most four times until page numbers are stable;
  outline entries of nested tables that are flattened to text are lost. Page-dependent fields have
  values only in the PDF (see [fields.md](fields.md)).
- **Templates:** the conditional marker is `{{If:Name}}` (a space would become `text:s` in ODT);
  placeholders in DOCX footnotes are not bound; with several sections the body placeholder gets
  all blocks merged (warning).
- **Search and replace:** a text spread over several runs is not found; a band block whose text is
  replaced loses its unmodelled XML (the reader findings are reported). Highlighting search results
  is not part of the model. A regular expression has a fixed limit of one second per match step
  (`DocumentValidationException`); a run with many matches can take longer, so use the
  `CancellationToken` overload, checked before each run, for a total limit.
- **Diagnostics:** write progress is reported per section, not per page or block.
- **PDF reading:** `ReadPdfPages` reconstructs lines and spaces heuristically. Marked content with
  `/ActualText` replaces the glyph text; a mixed right-to-left/Latin line written by Reglet
  carries such a replacement and round-trips logically. Lines are ordered top to bottom and left to
  right even when the content stream draws them out of order; a hyphen at a line end joins the word
  on the next line without a space. A pure right-to-left line is returned in logical order, including
  numbers and combining marks (numeric tokens stay left-to-right). Mixed-direction lines with Latin
  letters in an external PDF without `/ActualText` stay in visual order. Scanned pages need OCR,
  which is not included.
- **Paragraph styles:** a body paragraph keeps its named style reference and the formatting it
  inherited, so an edit of `PortableDocument.Styles` after reading reaches the paragraph in DOCX,
  ODT and PDF and the writers state only direct formatting
  ([ADR-0011](decisions/ADR-0011-paragraph-style-reference.md)). The public `Paragraph.Style`
  stays the resolved snapshot; the break flags (`KeepWithNext`, `KeepTogether`,
  `PageBreakBefore`) are tri-state since [ADR-0012](decisions/ADR-0012-tri-state-break-flags.md),
  the alignment since [ADR-0013](decisions/ADR-0013-tri-state-alignment.md), the indents since
  [ADR-0014](decisions/ADR-0014-tri-state-indents.md), the space before/after since
  [ADR-0015](decisions/ADR-0015-tri-state-spacing.md), the borders since
  [ADR-0016](decisions/ADR-0016-tri-state-borders.md) (`ParagraphBorders.None` clears an
  inherited set), the paragraph direction since
  [ADR-0017](decisions/ADR-0017-tri-state-direction.md) and the tab stops since
  [ADR-0018](decisions/ADR-0018-tri-state-tab-stops.md) (an empty list clears inherited stops).
  The document paragraph defaults
  (`w:docDefaults/w:pPrDefault`, the ODT default style) are modelled in
  `DocumentDefaults.ParagraphStyle`, are written by DOCX and ODT and are applied by the PDF layout
  for a document built from the model. Headings, notes and derived paragraphs still state their
  resolved formatting.
- **Signatures:** a DOCX signature is RSA/SHA-256 with inclusive C14N only (no ECDSA, SHA-1 or
  exclusive C14N) and carries no visible Word signature line (`w:sig`); trust and revocation of
  certificates, the TSA and the validation material stay with the caller
  ([ADR-0006](decisions/ADR-0006-encryption-signatures.md)).

## Open work

In-scope work that is not finished. IDs (`OF-…`, `AF…`) are the identifiers used in commits and
release notes. Every item ends with tests, a read/write/read check where a reader exists, and an
update of this file.

**Implementation status:** every implementation feature of the follow-up scope is implemented. The
remaining checkboxes below are verification against external applications and documents, manual
interoperability checks, or the ongoing consistency task — not missing code.

**Templates and everyday documents (priority)**

- [ ] Build a corpus of real, license-safe Word and LibreOffice documents (formatting, tables,
  images, templates); check import, editing and reopening in both applications, including the loss
  reports (OF-A05). The optional `OfficeSmokeChecks` now resaves every corpus document through
  LibreOffice and reads it back cross-format, and processes LibreOffice-produced PDFs. Word 16.114
  opens all 23 example DOCX files without a repair prompt and shows them like the PDF (checked
  2026-11-12 by screenshot; Word without a licence cannot save, so editing and reopening in Word
  remain). A hand-authored corpus remains.
- [ ] Formatting on import and rewrite: inheritance and later edits of paragraph and character
  styles, explicit offs, direct vs. style formatting against real files; paragraph formatting is
  still a resolved snapshot (OF-A04 remainder). The break flags, alignment, indents, space
  before/after, borders, direction and tab stops are tri-state since ADR-0012 to ADR-0018;
  every property of this remainder is now nullable, so a direct value no longer hides behind an
  inherited default. What remains is the validation against real Word/LibreOffice documents and
  their explicit-off edge cases. The document paragraph defaults are now modelled in
  `DocumentDefaults.ParagraphStyle`. ADR-0011 covers the reference slice that is implemented.
- [ ] Table widths and layout: row heights with auto-fit and conditional band parity against Word
  and LibreOffice on real files.
- [ ] Images and wrapping: positioning and anchoring across applications, ODT offsets and the wrap
  kinds ODF cannot distinguish; further image formats and SVG features only as needed.

**PDF and cross-format layout**

- PDF text extraction uses a documented heuristic for lines and spaces; see
  [Known limits](#known-limits). Mixed-direction right-to-left text with Latin letters in an
  external PDF without `/ActualText` stays in visual order. No further change is planned without a
  concrete failing document.
- [ ] PDF preservation: check extract, merge and rewrite with real documents; a full rewrite
  decrypts the output and does not keep existing signatures valid. The optional
  `OfficeSmokeChecks` covers a LibreOffice-produced PDF; Word-produced, signed and encrypted
  documents remain.
- Hyphenation patterns ship only in the optional package `Reglet.Hyphenation` (de-1996, de-1901,
  de-CH-1901, en-US, en-GB, fr, es, it, nl, pt, pl, da, nb, sk, lt, et, uk, sl, el-monoton, be, sq, gl, bg,
  eu: the MIT or freely redistributable patterns of
  `hyph-utf8`; the LPPL-only and GPL patterns are left out). The PDF uses them or the caller's; the target
  program uses its own dictionary (ODT/DOCX). See [Known limits](#known-limits).
- [ ] Signatures and protection: check interoperability of the existing procedures. ODT encryption
  (ADR-0019) and ODT signatures (ADR-0020) are implemented; a manual LibreOffice verification
  remains because its command line has no password or signature option.

**Verification and documentation**

- The optional checks are documented in [packaging.md](packaging.md) and
  [visual-regression.md](visual-regression.md). LibreOffice smoke and import, veraPDF,
  `msoffcrypto-tool`, the XML signature check and pyHanko pass with the installed tools; the
  visual baselines are tied to their renderer environment and Word is not automated.
- [ ] Keep this file, `scope.md`, the ADRs and the release notes consistent with every change
  (OF-G01).

## Drawing layer (ADR-0022, accepted, implemented)

Shapes (line, rectangle, rounded rectangle, ellipse, polygon/freeform) and text boxes, inline or
anchored, also in headers and footers. Version 1: solid fill and outline, no gradients, no tables in
text boxes. Charts, barcodes and equations stay out of scope until their own ADRs.

- [x] Model `DrawingShape` in `Core` (`ShapeKind`, `ShapePathStep`); the path type is not shared with the SVG converter yet (decide when the PDF renderer is written)
- [x] DOCX writer (`wps:wsp`, inline or anchored, also in headers and footers; checked with LibreOffice)
- [x] PDF renderer (inline and floating like images, rotation, text boxes laid out like a one-cell table and clipped to the box, also in headers/footers; headings in a box are not outline entries; checked visually with pdftoppm)
- [x] ODT writer (`draw:rect`, `draw:ellipse`, `draw:line`, `draw:path`; a text box is a `draw:frame` with `draw:text-box`; graphic styles named from the look, dash definitions in `styles.xml`; rotation as `draw:transform` (`rotate` about the shape's top left corner, then `translate`; measured against LibreOffice), rotated lines carry rotated end points; checked in LibreOffice)
- [x] DOCX reader (`wps:wsp` inline or anchored, also in `mc:AlternateContent`; preset geometries rect, roundRect, ellipse, line, custom paths, RGB and theme colors (`schemeClr` from `theme1.xml` or the Office defaults, with `lumMod`/`lumOff`/`shade`/`tint`; also the `fillRef`/`lnRef` of `wps:style`), dash, rotation, text box paragraphs; anything else is reported and left out)
- [x] ODT reader (`draw:rect`, `draw:ellipse`, `draw:line`, `draw:path` with absolute M/L/C/Z, text box frames; solid fill and outline, dash, rotation from `draw:transform`; a rotated line comes back as an unrotated line between its end points; other drawing objects are reported)
- [x] Tests per format and a LibreOffice check per shape kind (Word checked by eye, 2026-11-13)
- [x] Swift port (DOCX, ODT, PDF; cross-parity document `shapes`)

## Alignment tabs

- [x] `AlignmentTab` (`w:ptab`: left, center, right, leader, relative to margin or indent): DOCX writes and reads it natively (Word shows it correctly, LibreOffice ignores `ptab` and shows a plain tab); ODT and PDF turn it into a tab and a tab stop at the matching position (body, headers, footers, lists; in a table cell or a text box it is a plain tab); ODT reads such a tab back as tab and tab stop, not as `AlignmentTab`
- [ ] Swift port

## Barcodes

[ADR-0023](decisions/ADR-0023-barcodes.md): `Barcode.Create` returns a `DrawingShape` (freeform, dark modules as rectangles), so DOCX, ODT and PDF need no format code of their own; a file read back holds an ordinary freeform with the content as alt text.

- [x] Code 128 (sets A, B, C, automatic switching, SHIFT, check symbol), EAN-13 and EAN-8 (check digit computed or verified); no extended guard bars and no text line (the caller adds a paragraph), transparent quiet zone
- [x] Tests: symbol table, values and check symbol, EAN layout against the digit tables, rejected input, write and read in DOCX/ODT; decoded with the macOS Vision framework from the PDF (all four codes read correctly, 2026-11-17, by hand)
- [x] QR code (`QrEncoder`: versions 1–40, one numeric, alphanumeric or byte (UTF-8) segment, levels L/M/Q/H, mask by penalty; no ECI, no mixed segments; `errorCorrection` parameter, square, quiet zone 4). 100 generated codes over all four levels, the three modes and up to the maximum size of version 40 were decoded by the macOS Vision framework without a difference (2026-11-17, by hand); the suite checks capacities, alignment positions, finder and timing patterns
- [x] Swift port (`barcodes` parity document, 2026-11-17)

## Custom XML parts

`CustomXmlPart` (`DocumentMetadata`): an application-defined XML document that travels with the document, for example the data content controls are bound to.

- [x] DOCX: `customXml/itemN.xml` with relationships, written and read (`CustomXmlChecks`); the XML must be well-formed. ODT and PDF have no equivalent and ignore the parts

## Comments

[ADR-0026](decisions/ADR-0026-comments.md): `Comment` (author, initials, date, text body, replies, resolved) with `CommentStart`/`CommentEnd` markers in the text.

- [x] Model: `Comment` (inline at the start of the range) and `CommentEnd`; replies nested, resolved flag, plain-text body
- [x] DOCX: `word/comments.xml` and, for replies and the done flag, `word/commentsExtended.xml`; the anchors (`commentRangeStart/End`, `commentReference`) are written from the model, also across paragraphs and as point comments; read back into the model (also from LibreOffice files, where a comment without a range is a bare reference). LibreOffice reads the comments and writes them again with author, text, reply and done flag (DOCX → ODT → DOCX, office smoke 2026-11-18); Word not checked. Reported: a comment in a header, footer or note, an end without a start, a picture, table or character formatting in a comment read from a file. An anchor whose comment the reader cannot map (no entry in the comments part) stays preserved XML
- [x] ODT: `office:annotation` at the start and `office:annotation-end` at the end of the range (a point comment has no end), author and date (`dc:`), initials (`meta:creator-initials`), a reply as an annotation with `loext:parent-name`, done flag as `loext:resolved`, written in the order LibreOffice uses (replies first); read back, also LibreOffice's own files (reply before parent, `0000-00-00T00:00:00` as unknown date, end right after the start as a point). LibreOffice reads the own ODT and keeps author, text, reply and done flag, and the own reader reads LibreOffice's ODT (office smoke 2026-11-18). Not read: a comment in a master page (reported), a list or table in a comment (reported)
- [x] PDF: `PdfWriteOptions.Comments = PdfCommentMode.Annotations` (default `None`): a sticky note (`/Text` annotation) per anchored comment in the right margin level with its anchor, replies appended to the contents; PDF/A-2b with an appearance stream (veraPDF: compliant, 144/0, 2026-11-18); rendered with `pdftoppm`, the note sits beside the text. Not written: the done flag, the date, a comment in a header, footer or note (reported)
- [x] Swift port (2026-11-18; DOCX, ODT, PDF; cross-parity `comments`, reader parity with a comment thread)

## Revisions

[ADR-0027](decisions/ADR-0027-revisions.md): `TrackedChange` (insertion or deletion, author, date) with `TrackedChangeEnd` as marker pair, like comments; version 1 covers insertions and deletions of inline content.

- [x] Model: `TrackedChange`, `TrackedChangeEnd`, `TrackedChangeKind`; accept and reject (`DocumentRevisions.AcceptAll/RejectAll/Accept/Reject`, body text only, paragraph marks stay)
- [x] DOCX: `w:ins` and `w:del` with `w:delText`, author and date; a range across paragraphs or a hyperlink border is written in pieces with fresh `w:id`s (Word has no element across them); read back as marker pairs (every piece its own pair), a move (`w:moveFrom`/`w:moveTo`) as deletion and insertion, reported. A block with tracked changes is regenerated from the model instead of being preserved as XML; a change of the paragraph mark (`w:rPr/w:ins`) is not modelled and stays preserved. Fields inside a deletion are written with `w:instrText` (not `w:delInstrText`)
- [x] ODT: `text:tracked-changes` with a `text:changed-region` per change (`text:insertion` / `text:deletion` with `office:change-info`), `text:change-start`/`text:change-end` for an insertion (also across paragraphs), a `text:change` point for a deletion with its text in the region (one region per paragraph piece); read back, also LibreOffice's own files (deletion across paragraphs read as one paragraph with line breaks, `0000-00-00T00:00:00` as unknown date, format changes reported). LibreOffice reads our DOCX and ODT and writes the changes again, and the own reader reads them (office smoke 2026-11-18); Word not checked. Reported: a change inside a deleted text, a hyperlink across a change (written as text), a change in a header, footer or note, an end without a start, a start without an end
- [x] PDF: `PdfWriteOptions.Revisions` = `PdfRevisionMode`: `Final` (default: insertions plain, deletions left out), `Original`, `Markup` (insertions underlined, deletions struck through, one color per author from a fixed palette; rendered with `pdftoppm` 2026-11-18)
- [x] Tests: model, accept/reject, DOCX, ODT, LibreOffice round trip, PDF text per mode (`RevisionChecks`)
- [x] Swift port (2026-11-18; DOCX, ODT, PDF; cross-parity `revisions`, reader parity with a change per kind)

## VBA

[ADR-0028](decisions/ADR-0028-vba-preservation.md): `MacroProject` (the bytes of `word/vbaProject.bin`) in `DocumentMetadata`; opaque, never executed or changed. Read only with `DocxReadOptions.KeepMacros` (default off: dropped and reported).

- [x] Model: `MacroProject`, `DocumentMetadata.MacroProject` (no limit of its own: the reader's general part bound applies)
- [x] DOCX: part, relationship, `.bin` default type and macro-enabled main content type written; read with `KeepMacros`; dropped and reported without it, also on the body-unchanged path
- [x] ODT and PDF ignore it
- [x] Tests (`MacroChecks`): round trip with and without the option, limit, body-unchanged path, content types
- [x] Swift port (2026-11-18; cross-parity `macros`)

## Flow diagrams

[ADR-0030](decisions/ADR-0030-flow-diagrams.md): `Diagram` block (`FlowNode`, `FlowConnector`) with fixed node positions; connectors are drawn paths with arrowheads (no glue, no obstacle avoidance, no automatic layout). Expanded to `DrawingShape`s.

- [x] Model: `Diagram`, `FlowNode`, `FlowConnector`, `FlowNodeKind`, `FlowSide`, `FlowRoute`; geometry (node shapes, straight and elbow paths, arrowheads, labels)
- [x] DOCX: inline group `wpg:wgp` of `wps:wsp`
- [x] ODT: `draw:g` of the shapes (text at the top of a node, see ADR)
- [x] PDF: shapes at their positions in a reserved area (not in table cells yet)
- [x] Reported: a diagram in a header, footer or note
- [x] Tests (`DiagramChecks`), LibreOffice check, picture check of the PDF
- [x] Swift port (2026-11-18; cross-parity `flow`, reader parity unchanged)

## Equations

[ADR-0031](decisions/ADR-0031-equations.md): display `Equation` block of a math tree (text, row, fraction, script, radical, n-ary operator, delimiter). Not in version 1: inline equations, matrices, accents, equation arrays, a LaTeX parser.

- [x] Model: `Equation`, `MathNode` (`MathText`, `MathRow`, `MathFraction`, `MathScript`, `MathRadical`, `MathNary`, `MathDelimiter`), limits and depth guard
- [x] DOCX: write and read `m:oMathPara`/`m:oMath`
- [x] ODT: write and read the MathML formula object (alignment not kept)
- [x] PDF: own layout and drawing (vector minus and big operators; Greek and math symbols need an embedded font)
- [x] Reported: an equation in a header, footer, note or text box; unsupported OMML/MathML elements when reading
- [x] Tests (`EquationChecks`), LibreOffice check, picture check of the PDF
- [x] Swift port (2026-11-18; cross-parity `math`, reader parity unchanged)

## HTML

[ADR-0032](decisions/ADR-0032-html.md): `DocumentFormat.Html` with a tolerant subset reader and a self-contained writer in a new project `Reglet.Html`. HTML→PDF, DOCX, ODT and back go through the model. No CSS layout, no network, no external style sheets.

- [x] Project `Reglet.Html`, `DocumentFormat.Html`, facade (`DocumentReader`, `DocumentWriter`, extensions `.html`/`.htm`, `MailMerge`)
- [x] Reader: tokenizer and tree builder (implied end tags, void elements, character references, encodings), mapping of blocks, inline formatting, lists, tables, images from `data:` URIs, links, `title`
- [x] Reader: security (nothing fetched, scripts and styles dropped, link targets checked, limits) and reports
- [x] Writer: document, paragraphs, headings, lists, tables, images, links, formatting; reports for what is omitted
- [x] Tests (`HtmlChecks`), HTML→PDF/DOCX/ODT through the model; a real page from Word or LibreOffice as input and a visual PDF check still open
- [x] Swift port (`html` parity)

## DOCX↔PDF conversion

[ADR-0033](decisions/ADR-0033-docx-pdf-conversion.md): opt-in structure recovery in the PDF reader (`PdfReadOptions.RecoverStructure`) so PDF→DOCX gets paragraphs, headings, lists, tables and images; DOCX→PDF fidelity measured against LibreOffice and the largest deviations fixed. No OCR, no Word layout engine.

- [x] `PdfReadOptions.RecoverStructure`, body size, paragraph grouping
- [x] Headings (by size), lists (bullet/number, hanging indent, nesting)
- [x] Tables from aligned columns
- [x] Images (JPEG and 8-bit Flate RGB/Gray as PNG, soft mask as alpha), reports for the rest (also inside form XObjects)
- [x] Repeated headers and footers dropped and reported; one flowing section, blocks joined over page boundaries ; scan report; columns of prose read column by column and reported
- [x] Tests (`PdfStructureChecks`): the library's own PDF read back with headings, lists, table, footer; the round trip PDF → recovery → DOCX keeps the text and its order; LibreOffice's own PDF and HTML of the three corpus documents are read with exactly the source's block counts
- [x] Fidelity measured against LibreOffice (`FidelityChecks`, `FIDELITY=1`); widow/orphan control added to the PDF renderer; with a Calibri-compatible font the page counts match, without one (Helvetica fallback) they do not; the space between paragraphs is the larger of after/before (confirmed in Word and LibreOffice); page starts of the text corpus agree on 8 of 13 pages
- [x] Swift port (reader parity)

## Embedded objects

[ADR-0029](decisions/ADR-0029-embedded-objects.md): `EmbeddedObject` block (`ProgId`, `Kind` `OleStorage`|`Package`, opaque `Data`, `FileExtension`, size, optional PNG/JPEG `Preview`). Never opened, run or rendered beyond the preview picture.

- [x] Model: `EmbeddedObject`, `EmbeddedObjectKind`
- [x] DOCX: `w:object` with `v:shape`/`v:imagedata` and `o:OLEObject`; data in `word/embeddings/` (`oleObjectN.bin` or `PackageN.<ext>`); read back; EMF/WMF preview dropped and reported, placeholder written
- [x] ODT: `draw:object-ole` in a frame with the preview in `ObjectReplacements/`; `Package` objects reported and omitted
- [x] PDF: preview drawn at the object size
- [x] Reported: linked objects, objects in header/footer/notes/comments, missing data parts
- [x] Tests (`EmbeddedObjectChecks`); Word opens the DOCX and counts the objects (2026-11-18, placeholder data); LibreOffice round trip not run (the test data is not a real OLE file)
- [x] Swift port (2026-11-18; cross-parity `ole`, reader parity with an object)

## Form fields

[ADR-0024](decisions/ADR-0024-form-fields.md): `FormField` (text, check box, drop-down, radio button) is a block in the text flow and in table cells.

- [x] PDF: AcroForm widgets with appearance streams (new fields only; also in PDF/A-2b, checked with veraPDF; read back with Apple PDFKit: types, values, options and the radio group, 2026-11-17, by hand); coexists with a signature field; reported: duplicate name, radio without or with a repeated export value, characters the font cannot show. Not yet: tagging in the structure tree (PDF/UA), fields in headers and footers
- [x] DOCX content controls (`w:sdt`: text, multi-line, drop-down, check box; radio button as check box with a tag, reported; read back as `FormField`, round trip stable; LibreOffice shows the values, Word opens the file and shows the text, 2026-11-17, by hand; the click behaviour in Word is not checked). Not stored: size; reported: `Required`, `MaxLength`. Not modelled: date control (stays text), controls outside a paragraph
- [x] ODT form controls (`form:text`, `form:textarea`, `form:checkbox`, `form:listbox`, `form:radio` with a `draw:control` per field; real radio groups; LibreOffice loads, saves and reloads all of them and the .NET reader reads LibreOffice's own file, 2026-11-17; not written: `form:control-implementation` (LibreOffice then drops the control), fields in headers and footers, `Required`, `FontSize`; the multi-line text area shows a dark background in a headless LibreOffice PDF export, window not checked)
- [ ] Later, own decision: read and fill existing AcroForms, flatten
- [x] Swift port (2026-11-17; DOCX, ODT, PDF; cross-parity `forms`)

## Charts

[ADR-0025](decisions/ADR-0025-charts.md): `Chart` (column, bar, line, area, pie, scatter) is a block in the text flow and in table cells. No embedded workbook: Word shows the chart, the data are edited through the library.

- [x] PDF: own vector rendering (title, legend at the bottom or right, 1-2-5 axis scale, grid, stacking, axis titles, percent labels in a pie), document font, figure with alt text in the tag tree; PDF/A-2b conforms (veraPDF, 144/0), looked at by eye for all kinds (2026-11-17). Reported: a negative pie value (drawn as zero), characters the font cannot show (question mark). Not drawn: data labels, secondary axis, combinations, 3D, trend lines, per-point colors, logarithmic scale
- [x] DOCX native chart part (`word/charts/chartN.xml`, values as literals, no workbook, inline, scaled to the cell width; LibreOffice draws all six kinds; a chart in a header/footer is reported; reading: the chart part is read into a `Chart`, also from Word and LibreOffice files; other plot types, combinations and 3-D are reported)
- [x] ODT chart object (`draw:object` in an as-char frame, sub-document `Object N/content.xml` with `chart:chart` and a local table; LibreOffice draws all six kinds, checked by eye 2026-11-17). Limits: not scaled to a narrow table cell, a chart in a header/footer is reported, the bar chart's value axis sits on top (LibreOffice with reversed categories); reading: the chart object is read into a `Chart` through the cell ranges of its local table, also from LibreOffice files
- [x] Swift port (2026-11-17; cross-parity `charts`); reading charts ported too (2026-11-17)

## Out of scope

Deliberately not planned. A workstream here starts only after the product owner changes
[scope.md](scope.md) and writes or amends an ADR (new boundary, API shape, what stays
unsupported, zero-dependency check, its own acceptance gate).

| Area | Content | Depends on | Size |
| --- | --- | --- | --- |
| Conversion engine: other formats | Markdown, TXT, RTF, XPS, DOC, flat OPC, EMF/WMF/TIFF/GIF/BMP, HTML→PDF with CSS layout, a layout engine that follows Word, DOCX→image (HTML as a format moved into scope: [HTML](#html)) | comments, a layout engine | XL |
| Printing and platform adapters | print helpers, ASP.NET/Blazor/MAUI/WPF/WinForms packages | – | M/L |
| Full-fidelity model | every WordprocessingML/ODF/PDF construct modelled (10–50× the codebase); the preserving round trip covers most of the value | preservation complete | not recommended |
| Swift port parity | readers, templates, protection, shaping, PDF/A and the rest of the .NET feature set, in the Swift version of Reglet (to be published later) | stable .NET features | XL |
| Small formatting details | numbering spacing, drawing the text effects in the PDF (the effects and the numbering trailing character are written and read in DOCX and ODT) | drawing/forms decisions | M |

These stay out even in a later scope: executing VBA macros (preserving them moved into scope: [VBA](#vba)), pixel-identical rendering between Word,
LibreOffice and the PDF renderer, a general Office editor, and formats without a documented subset
and fidelity report.

## Permanent boundaries (not part of phase N)

The following "unsupported" rows are deliberate, permanent boundaries and not open phase N gaps:

- **Page breaks in headers/footers** (all formats). A break within a header/footer is not a
  meaningful layout and is omitted (warning).
- **PDF header/footer height**: a band uses the full block layout (headings, lists, tables,
  images, markup) but may take at most a third of the page; a taller band is cut (warning).

## Notes

- **PDF justification** applies to paragraphs in the body, in table cells and in headers/footers:
  the extra width is distributed evenly across the word gaps, the last line stays left-aligned.
- **Generic font names:** `serif`, `sans-serif` and `monospace` are written in DOCX and ODT as
  Times New Roman, Arial and Courier New; ODT additionally carries the category
  (`style:font-family-generic`) so that the application falls back sensibly. PDF maps the same
  categories to Times, Helvetica and Courier. All other names are adopted unchanged.
- **Nested tables** are valid DOCX and are rendered by Word and LibreOffice; Apple Pages imports
  them as text (a Pages limitation).

## Fallback behavior (I09/I10)

Unsupported features are never silently swallowed. The behavior is controlled via
`WriteOptions.UnsupportedContent`:

| Value | Behavior |
| --- | --- |
| `Warn` (default) | The feature is omitted and a `WriteWarning` is collected. |
| `Fail` | `WriteAsync` throws `NotSupportedException`. |

`WriteAsync` (writer and facade) returns a `WriteResult` with all collected warnings
(`WriteResult.Warnings`, `WriteResult.HasWarnings`).

Examples of reported features: DOCX and ODT page breaks in the header/footer; PDF missing glyphs
as well as non-encodable WinAnsi characters.

## Formula recovery from PDF

[ADR-0034](decisions/ADR-0034-pdf-formula-recovery.md): opt-in `PdfReadOptions.RecoverFormulas` turns display formulas of a PDF into `Equation` (fractions, radicals, scripts, sums and integrals), so PDF→DOCX gets editable formulas.

- [x] Rules (fraction bars, minus signs drawn as lines) from the content stream (`re f`, `m l S`)
- [x] Fractions, radicals (with or without a root glyph), scripts, sums with limits, text styles, TeX CMEX operators
- [x] Equation number at the right margin, warning, opt-in
- [x] Bars drawn as thin images (dvips), operators drawn as outlines (∑ ∏ ∫ ∬ ∮ ⋃ ⋂, root sign)
- [ ] Not recoverable: matrices, aligned multi-line equations, stretchy delimiters, operators as unnamed glyphs of an embedded font
