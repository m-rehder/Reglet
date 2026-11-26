# Reglet API reference

Generated from the public API and its XML comments; do not edit by hand. Regenerate with
`dotnet run --project tools/ApiReference -- docs/api-reference.md`.

Layout, so that it can be searched line by line: `## <package>`, `### <namespace>`,
`#### <type>` with its declaration, then one line per member: `- <signature> — <summary>`.
Members inherited from a base type are listed under that base type. Start with
`DocumentWriter` (write a `PortableDocument` as DOCX/ODT/PDF/HTML) and `DocumentReader` (read one);
build documents with `PortableDocument.Create()` (a `PortableDocumentBuilder`). Lengths are `Length` values
(see docs/units.md). Examples: docs/examples.md; guide: docs/guide.md.

## Reglet

### Reglet

#### DocumentFormat

`enum DocumentFormat` — Output format of a document.

- `Docx = 0` — WordprocessingML according to ECMA-376 (`.docx`).
- `Odt = 1` — OpenDocument Text according to ODF 1.3 (`.odt`).
- `Pdf = 2` — Portable Document Format according to ISO 32000-1 (`.pdf`).
- `Html = 3` — HTML (a subset, ADR-0032).

#### DocumentReader

`static class DocumentReader` — entry point of the library: reads a DOCX, ODT or PDF file into a immutable `PortableDocument` content model. The format is recognized for a path recognized from the extension, or specified explicitly for a stream. Layout is not preserved (reader subset, see feature matrix).

- `static DocumentFormat FormatFromPath(string path)` — Determines the source format from a file extension (normalized).
- `static ReadResult Read(string path, CancellationToken cancellationToken = default)` — Reads a file into the content model based on the file extension.
- `static ReadResult Read(Stream input, DocumentFormat format, CancellationToken cancellationToken = default)` — Reads a file in the specified format into the content model.
- `static ReadResult Read(Stream input, DocumentFormat format, string? password, CancellationToken cancellationToken = default)` — Reads a stream in the specified format, opening an encrypted DOCX (OF-E01), ODT (OF-E05) or PDF (AD02) with `password`. An unencrypted document ignores the password.
- `static IReadOnlyList<string> ReadPdfPages(Stream input, CancellationToken cancellationToken = default)` — Extracts the text of each page of a PDF (phase AC, AC05). Layout is not reconstructed.
- `static IReadOnlyList<string> ReadPdfPages(Stream input, string? password, CancellationToken cancellationToken = default)` — Extracts the text of each page of a PDF and opens an encrypted file with `password` (phase AD).

#### DocumentWriter

`static class DocumentWriter` — entry point of the library: writes an immutable `PortableDocument` to DOCX, ODT or PDF. For a path the format is recognized from the extension, or specified explicitly for a stream.

- `static DocumentFormat FormatFromPath(string path)` — Determines the output format from a file extension (normalized).
- `static Task<WriteResult> WriteAsync(PortableDocument document, string path, CancellationToken cancellationToken = default)` — Writes the document to a file based on the file extension.
- `static Task<WriteResult> WriteAsync(PortableDocument document, string path, WriteOptions? options, CancellationToken cancellationToken = default)` — Writes the document to a file based on the file extension, with options.
- `static Task<WriteResult> WriteAsync(PortableDocument document, Stream output, DocumentFormat format, WriteOptions? options = null, CancellationToken cancellationToken = default)` — Writes the document in the specified format to a stream.

#### MailMerge

`static class MailMerge` — mail merge (phase AB): binds a template with data rows and writes one document per row.

- `static Task<IReadOnlyList<string>> MergeAsync(DocumentTemplate template, IEnumerable<IReadOnlyDictionary<string, string?>> rows, string directory, DocumentFormat format, CancellationToken cancellationToken = default)` — Binds each row (phase AB, AB01/AB03) and writes one document per row into `directory`. Only DOCX and ODT templates are supported (PDF uses page templates). The files are named `merge-001.docx` and so on; a file with that name in the directory is replaced.

#### MergeDataSource

`static class MergeDataSource` — data sources of a mail merge (phase AJ, AJ03): CSV, XML, JSON and Excel (xlsx). The rows are returned as dictionaries of name and value; the first row of a CSV or a spreadsheet holds the names.

- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromCsv(TextReader reader, char separator = ,)` — reads the rows of a CSV file (comma separated, RFC 4180: a quoted field may contain the separator and line breaks). The first line holds the names of the fields.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromCsvFile(string path, char separator = ,)` — reads the rows of a CSV file.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcel(Stream stream)` — reads the rows of an Excel workbook (xlsx). The first sheet is read; its first line holds the names of the fields. Only the values are read, no formulas, no dates.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcel(Stream stream, string namedRange)` — Reads a single rectangular named range from an Excel workbook. Its first row contains field names.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcelFile(string path)` — reads the rows of an Excel workbook (xlsx).
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromExcelFile(string path, string namedRange)` — Reads a named rectangular range from an Excel workbook file.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromJson(TextReader reader)` — reads the rows of a JSON file: an array of objects. The name of a property is the name of the field; a null value is null, a number and a boolean are written as text.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromJsonFile(string path)` — reads the rows of a JSON file.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromXml(TextReader reader, string? rowElement = null)` — reads the rows of an XML file. Every element with the given name is a row; its attributes and its child elements are the fields. Without a name, the child elements of the root element are the rows.
- `static IReadOnlyList<IReadOnlyDictionary<string, string?>> FromXmlFile(string path, string? rowElement = null)` — reads the rows of an XML file.

## Reglet.Core

### Reglet

#### AlignmentTab

`sealed class AlignmentTab : Inline` — an alignment tab (`w:ptab`): the text after it is aligned left, centered or right in the text area, whatever the tab stops of the paragraph are. DOCX writes and reads it as it is. ODT and PDF have no such tab; they write a normal tab and a tab stop at the position it stands for. That works for paragraphs in the body, in headers, footers and lists; in a table cell or a text box it is a plain tab.

- `new AlignmentTab(TabStopAlignment alignment, TabStopLeader leader = TabStopLeader.None, AlignmentTabRelativeTo relativeTo = AlignmentTabRelativeTo.Margin)` — Creates an alignment tab.
- `TabStopAlignment Alignment { get; }` — Where the text after the tab sits.
- `TabStopLeader Leader { get; }` — The leader before the text.
- `AlignmentTabRelativeTo RelativeTo { get; }` — What the position is measured from.

#### AlignmentTabRelativeTo

`enum AlignmentTabRelativeTo` — What an `AlignmentTab` is measured from.

- `Margin = 0` — The page margins: right is the right margin, center the middle of the text area.
- `Indent = 1` — The paragraph indents: right is the right indent, center the middle between the indents.

#### AlphabeticalIndex

`sealed class AlphabeticalIndex : Block` — an alphabetical index of the `IndexEntry` marks of the document. DOCX writes an `INDEX` field and ODT an alphabetical index, so the application fills in the page numbers; PDF renders the sorted entries with page numbers and links.

- `new AlphabeticalIndex()`
- `string? Title { get; init; }` — Optional title above the entries.

#### Barcode

`static class Barcode` — Creates barcodes as vector shapes (ADR-0023): a `DrawingShape` of kind freeform whose path is the dark modules. The shape is transparent around the code; put it on a light surface.

- `static DrawingShape Create(BarcodeSymbology symbology, string content, Length? moduleWidth = null, Length? height = null, Color? color = null, int? quietZoneModules = null, QrErrorCorrection errorCorrection = QrErrorCorrection.Medium)` — Creates a barcode shape.
- `static int EanCheckDigit(string digits)` — The check digit of an EAN-13 or EAN-8 number without its check digit.

#### BarcodeSymbology

`enum BarcodeSymbology` — The symbology of a barcode created with `Barcode` (ADR-0023).

- `Code128 = 0` — Code 128 (ISO/IEC 15417), character codes 0–127.
- `Ean13 = 1` — EAN-13: 12 digits plus check digit (the check digit may be omitted).
- `Ean8 = 2` — EAN-8: 7 digits plus check digit (the check digit may be omitted).
- `QrCode = 3` — QR code (ISO/IEC 18004): any text, versions 1-40, square.

#### Block

`abstract class Block` — common base type for all block elements of a section: paragraph, heading, list, table, image and page break.


#### Bookmark

`sealed class Bookmark : Inline` — a named target (bookmark) at a position in the text. DOCX and ODT write a zero-width bookmark; PDF records a named destination and adds it to the outline.

- `new Bookmark(string name)` — Creates a bookmark.
- `string Name { get; }` — The bookmark name (unique within the document).

#### BookmarkEnd

`sealed class BookmarkEnd : Inline` — the end of a bookmark range. A `Bookmark` marks the start; the text between the two is the content of the bookmark, which is what a `REF` field shows. Without an end the bookmark is a zero-width target (a jump destination), which is what DOCX `w:bookmarkStart` with an adjacent `w:bookmarkEnd` and ODT `text:bookmark` mean.

- `new BookmarkEnd(string name)` — Creates the end of a bookmark range.
- `string Name { get; }` — The name of the bookmark that starts the range.

#### BorderStyle

`enum BorderStyle` — Line style of a paragraph or frame border.

- `None = 0` — No border.
- `Single = 1` — Solid line.
- `Double = 2` — Double line.
- `Dotted = 3` — Dotted line.
- `Dashed = 4` — Dashed line.

#### CellBorderLine

`sealed record CellBorderLine` — One border line of a cell.

- `new CellBorderLine(CellBorderStyle Style, Color? Color = null)` — One border line of a cell.
- `CellBorderStyle Style { get; init; }` — Thin, medium or thick.
- `Color? Color { get; init; }` — The colour; null means automatic (black).

#### CellBorderStyle

`enum CellBorderStyle` — The weight of a cell border.

- `Thin = 0` — Thin (Excel's default line).
- `Medium = 1` — Medium.
- `Thick = 2` — Thick.

#### CellBorders

`sealed record CellBorders` — The borders of a cell's four sides; a null side has none.

- `new CellBorders()`
- `static CellBorders None { get; }` — No borders.
- `CellBorderLine? Top { get; init; }` — The top side.
- `CellBorderLine? Bottom { get; init; }` — The bottom side.
- `CellBorderLine? Left { get; init; }` — The left side.
- `CellBorderLine? Right { get; init; }` — The right side.
- `static CellBorders All(CellBorderLine line)` — The same line on all four sides.

#### CellFont

`sealed record CellFont` — The font of a cell or of a rich-text run; a null property keeps the workbook's default.

- `new CellFont()`
- `static CellFont Default { get; }` — The workbook's default font (Calibri 11 when written).
- `string? Name { get; init; }` — The font family; null keeps the default.
- `double? Size { get; init; }` — The size in points; null keeps the default.
- `bool Bold { get; init; }` — Bold.
- `bool Italic { get; init; }` — Italic.
- `Color? Color { get; init; }` — The text colour; null keeps the default (automatic).

#### CellHorizontalAlignment

`enum CellHorizontalAlignment` — Horizontal alignment in a cell.

- `General = 0` — Text left, numbers right.
- `Left = 1` — Left.
- `Center = 2` — Centred.
- `Right = 3` — Right.

#### CellRange

`readonly record struct CellRange` — A rectangular range of cells, such as `A1:C3`.

- `new CellRange((int, int) First, (int, int) Last)` — A rectangular range of cells, such as `A1:C3`.
- `(int, int) First { get; init; }` — The top-left cell (1-based).
- `(int, int) Last { get; init; }` — The bottom-right cell (1-based).
- `bool Overlaps(CellRange other)` — Whether the two ranges share a cell.
- `static CellRange Parse(string range)` — Parses `A1:C3` (corners in any order) or a single cell `B2`.
- `string ToString()` — The range as `A1:C3` (a single cell as `B2`).

#### CellStyle

`sealed record CellStyle` — The formatting of a worksheet cell (ADR-0043); change it with `with`.

- `new CellStyle()`
- `static CellStyle Default { get; }` — No formatting: the workbook's default font, no fill, no border, General format.
- `CellFont Font { get; init; }` — The font; `CellFont.Default` keeps the workbook's.
- `Color? Fill { get; init; }` — A solid background; null means none.
- `CellBorders Border { get; init; }` — The borders of the cell's four sides.
- `CellHorizontalAlignment HorizontalAlignment { get; init; }` — Horizontal alignment; General aligns text left and numbers right.
- `CellVerticalAlignment VerticalAlignment { get; init; }` — Vertical alignment; Excel's default is bottom.
- `bool WrapText { get; init; }` — Wraps text onto several lines inside the cell.
- `int Indent { get; init; }` — Indent in Excel's steps (about three characters each); 0 means none. Excel indents only left or right aligned cells, so the writer aligns an indented cell with `CellHorizontalAlignment.General` to the left.
- `string? NumberFormat { get; init; }` — An Excel number format code, such as `#,##0.00` or `m/d/yyyy`; null means General.

#### CellVerticalAlignment

`enum CellVerticalAlignment` — Vertical alignment in a cell.

- `Bottom = 0` — Bottom (Excel's default).
- `Top = 1` — Top.
- `Center = 2` — Centred.

#### CharacterStyle

`sealed class CharacterStyle` — A named character style (a reusable set of character formatting). A run references a style by name (`TextRun.CharacterStyleName`); the formatting of the run itself takes precedence.

- `new CharacterStyle(string name)` — Creates a character style.
- `string Name { get; }` — The name of the style (referenced by `TextRun.CharacterStyleName`).
- `string? LinkedStyleName { get; init; }` — Optional name of the linked paragraph style (DOCX `w:link`); null means none.
- `bool? Bold { get; init; }` — Bold; `null` means "not set".
- `bool? Italic { get; init; }` — Italic; `null` means "not set".
- `bool? Underline { get; init; }` — Single underline; `null` means "not set".
- `TextUnderlineStyle? UnderlineStyle { get; init; }` — Underline style; `null` means "not set", `TextUnderlineStyle.None` switches an inherited underline off. Overrides `CharacterStyle.Underline` when not null.
- `Color? UnderlineColor { get; init; }` — Underline color; null means the text color.
- `TextStrikethrough? Strikethrough { get; init; }` — Strikethrough (single or double); `null` means "not set", `TextStrikethrough.None` switches an inherited one off.
- `TextBaseline? Baseline { get; init; }` — Vertical position (superscript or subscript); `null` means "not set", `TextBaseline.Normal` switches an inherited one off.
- `bool? AllCaps { get; init; }` — All capitals; `null` means "not set".
- `bool? SmallCaps { get; init; }` — Small capitals; `null` means "not set".
- `TextEffects? Effects { get; init; }` — Text effects (outline, shadow, relief); `null` means "not set", None switches them off. DOCX and ODT write them, the PDF does not draw them.
- `bool? Hidden { get; init; }` — Hidden text; `null` means "not set".
- `Color? Color { get; init; }` — The text color.
- `Color? Background { get; init; }` — The background color of the text.
- `Color? Highlight { get; init; }` — The highlighting color.
- `string? FontFamily { get; init; }` — The font family.
- `Length? FontSize { get; init; }` — The font size.
- `ParagraphBorder? Border { get; init; }` — The border around the text.
- `Length? Kerning { get; init; }` — The minimum font size for the kerning.
- `Length? CharacterSpacing { get; init; }` — The spacing between the characters.
- `double? HorizontalScaling { get; init; }` — The horizontal scaling in percent (100 = unchanged).
- `Length? Position { get; init; }` — The vertical position (raised or lowered).

#### Chart

`sealed class Chart : Block` — A chart (ADR-0025), inline in the text flow like a shape. The PDF writer draws it, DOCX and ODT carry it as a native chart object.

- `new Chart(ChartKind kind, IEnumerable<ChartSeries> series, IEnumerable<string>? categories = null, Length? width = null, Length? height = null)` — Creates a chart.
- `static IReadOnlyList<Color> DefaultPalette { get; }` — The palette used when a series has no color (readable for color-blind viewers).
- `ChartKind Kind { get; }` — The kind.
- `IReadOnlyList<ChartSeries> Series { get; }` — The series.
- `IReadOnlyList<string>? Categories { get; }` — The category labels; null for numbers.
- `Length Width { get; }` — The width.
- `Length Height { get; }` — The height.
- `string? Title { get; init; }` — The title above the chart.
- `ChartLegendPosition Legend { get; init; }` — Where the legend stands; at the bottom by default (a single series without a name shows none).
- `string? CategoryAxisTitle { get; init; }` — The title of the category axis (the x axis of a scatter).
- `string? ValueAxisTitle { get; init; }` — The title of the value axis.
- `double? Minimum { get; init; }` — The lowest value of the value axis; computed from the data when null.
- `double? Maximum { get; init; }` — The highest value of the value axis; computed from the data when null.
- `bool Stacked { get; init; }` — Whether the series of a column, bar or area chart are stacked.
- `string? AltText { get; init; }` — The description for screen readers.
- `Color SeriesColor(int index)` — The color of a series: its own, else the palette color of its index.

#### ChartKind

`enum ChartKind` — The kind of a `Chart` (ADR-0025).

- `Column = 0` — Vertical bars, grouped by category.
- `Bar = 1` — Horizontal bars, grouped by category.
- `Line = 2` — Lines with markers through the values of each series.
- `Area = 3` — Filled areas below the values of each series.
- `Pie = 4` — A pie of the values of the single series.
- `Scatter = 5` — Points at (x, y) pairs; every series needs x values.

#### ChartLegendPosition

`enum ChartLegendPosition` — Where the legend of a chart stands.

- `None = 0` — No legend.
- `Bottom = 1` — Below the plot area.
- `Right = 2` — Right of the plot area.

#### ChartSeries

`sealed class ChartSeries` — One data series of a `Chart`.

- `new ChartSeries(string name, IEnumerable<double> values, IEnumerable<double>? xValues = null)` — Creates a series.
- `string Name { get; }` — The name shown in the legend.
- `IReadOnlyList<double> Values { get; }` — The values.
- `IReadOnlyList<double>? XValues { get; }` — The x values of a scatter; null otherwise.
- `Color? Color { get; init; }` — The color of the series; the palette color when null.

#### Color

`readonly record struct Color` — an RGB color value without a dependency on backend types. The channels are on 0–255 limited; hex values are validated.

- `new Color(byte red, byte green, byte blue)` — Creates a color from the three color channels.
- `byte Red { get; }` — Red channel (0–255).
- `byte Green { get; }` — Green channel (0–255).
- `byte Blue { get; }` — Blue channel (0–255).
- `static Color Black { get; }` — Black (0, 0, 0).
- `static Color White { get; }` — White (255, 255, 255).
- `static Color FromHex(string value)` — creates a color from a hex value of the form `RRGGBB` or `#RRGGBB`.
- `static Color FromRgb(byte red, byte green, byte blue)` — Creates a color from the three color channels.
- `string ToString()`
- `static bool TryParseHex(string? value, out Color color)` — parses a hex value of the form `RRGGBB` or `#RRGGBB` without throwing.

#### ColumnBreak

`sealed class ColumnBreak : Block` — A column break (switch to the next column of a multi-column section).

- `new ColumnBreak()`

#### Comment

`sealed class Comment : Inline` — a review comment, anchored in the text. The comment starts here; a `CommentEnd` with the same `Comment.Id` ends the commented range. Without an end the comment is a point. A reply is a comment in `Comment.Replies`; it has no anchor of its own.

- `new Comment(int id, string author, string text)` — Creates a comment with a single paragraph of text.
- `new Comment(int id, string author, IEnumerable<string> paragraphs)` — Creates a comment with several paragraphs of text.
- `int Id { get; }` — The id that ties the comment to its `CommentEnd`; unique within the document.
- `string Author { get; }` — The author's name.
- `string? Initials { get; init; }` — The author's initials (default: none).
- `DateTimeOffset? Date { get; init; }` — When the comment was written (default: unknown).
- `IReadOnlyList<string> Paragraphs { get; }` — The paragraphs of the comment text.
- `bool Resolved { get; init; }` — Whether the comment is marked as done (resolved).
- `IReadOnlyList<Comment> Replies { get; init; }` — The replies, oldest first. A reply has no replies of its own, and the ids of all replies must differ.

#### CommentEnd

`sealed class CommentEnd : Inline` — the end of a commented range. A `Comment` marks the start; the text between the two is what the comment refers to. Without an end the comment is a point in the text.

- `new CommentEnd(int id)` — Creates the end of a commented range.
- `int Id { get; }` — The id of the comment that starts the range.

#### CustomXmlPart

`sealed record CustomXmlPart` — An application-defined XML document that travels with the document (DOCX `customXml/itemN.xml`, for example the data that content controls are bound to). Only DOCX stores it; ODT and PDF ignore it.

- `new CustomXmlPart()`
- `required string Xml { get; init; }` — The XML text of the part; must be a well-formed document.
- `string? ItemId { get; init; }` — The store item id (a GUID such as `{1B3C...}`); null derives a stable one from the content.

#### DateField

`sealed class DateField : Inline` — inline placeholder for a date. In DOCX/ODT a date field is written (the value serves as a cached result), in PDF the value is rendered directly.

- `new DateField(DateTimeOffset? value = null)` — Creates a date field.
- `DateTimeOffset? Value { get; }` — The value of the field; `null` means the current date.

#### Diagram

`sealed class Diagram : Block` — A flow diagram (ADR-0030): nodes with fixed positions and connectors between them, in an area of fixed size. It is drawn as a group of shapes; the connectors are paths with arrowheads, not glued to the nodes. The caller places the nodes; there is no automatic layout and the elbow route does not avoid other nodes.

- `new Diagram(Length width, Length height, IEnumerable<FlowNode> nodes, IEnumerable<FlowConnector>? connectors = null)` — Creates a diagram.
- `const int MaxNodes` — The most nodes in one diagram.
- `const int MaxConnectors` — The most connectors in one diagram.
- `Length Width { get; }` — The width of the area.
- `Length Height { get; }` — The height of the area.
- `IReadOnlyList<FlowNode> Nodes { get; }` — The nodes.
- `IReadOnlyList<FlowConnector> Connectors { get; }` — The connectors.
- `string? AltText { get; init; }` — Alternative text for accessibility.

#### DocPropertyField

`sealed class DocPropertyField : Inline` — inline placeholder for a document property (DOCPROPERTY), e.g. the title or author. In DOCX/ODT the field is written; the PDF resolves it from `DocumentMetadata` (Title, Subject, Author, Keywords, CreateTime or a custom property).

- `new DocPropertyField(string name)` — Creates a document property field.
- `string Name { get; }` — Name of the document property (e.g. `Title`, `Author`).

#### DocVariableField

`sealed class DocVariableField : Inline` — inline placeholder for a document variable (DOCVARIABLE). In DOCX/ODT the field is written; the PDF resolves it from `DocumentMetadata.Variables`.

- `new DocVariableField(string name)` — Creates a document variable field.
- `string Name { get; }` — Name of the document variable.

#### DocumentBookmarks

`static class DocumentBookmarks` — edits a document at a bookmark: replaces the content of a bookmark range or inserts blocks after the block that holds the bookmark. The model is immutable, so each call returns a new document. The body, list items and table cells are searched; headers, footers and notes are not. Content taken from another document keeps the styles it names; the styles of the other document are not merged.

- `static PortableDocument InsertBlocksAfter(PortableDocument document, string name, IEnumerable<Block> blocks)` — Inserts `blocks` right after the block that holds the bookmark.
- `static PortableDocument ReplaceContent(PortableDocument document, string name, IEnumerable<Inline> content)` — replaces what lies between a `Bookmark` and its `BookmarkEnd` with `content`; a bookmark without an end gets the content right after it.

#### DocumentDefaults

`sealed record DocumentDefaults` — Document-wide defaults (the default style).

- `new DocumentDefaults()`
- `static DocumentDefaults Default { get; }` — Documented initial values.
- `string FontFamily { get; init; }` — Default font family.
- `Length FontSize { get; init; }` — Default font size.
- `Color TextColor { get; init; }` — Default text color.
- `ParagraphStyle ParagraphStyle { get; init; }` — Default paragraph style. Readers fill it from `w:docDefaults/w:pPrDefault` or the ODT default style; DOCX and ODT write it and the PDF layout applies it to a document built from the model.

#### DocumentMetadata

`sealed record DocumentMetadata` — Optional document metadata.

- `new DocumentMetadata()`
- `string? Title { get; init; }` — Title of the document.
- `string? Author { get; init; }` — Author or creator.
- `string? Creator { get; init; }` — The application that created the document. Written to PDF as Info `/Creator` and XMP `xmp:CreatorTool`; DOCX and ODT do not store it.
- `string? Subject { get; init; }` — Subject.
- `IReadOnlyList<string>? Keywords { get; init; }` — Keywords.
- `string? Language { get; init; }` — Language (e.g. `en-GB`).
- `DateTimeOffset? Created { get; init; }` — Creation time.
- `IReadOnlyDictionary<string, string>? CustomProperties { get; init; }` — custom properties (name → value). Are written in DOCX as `docProps/custom.xml`, in ODT as `meta:user-defined` and in PDF as non-standard entries of the Info dictionary.
- `IReadOnlyDictionary<string, string>? Variables { get; init; }` — document variables (name → value) that `DocVariableField` refers to. Are written in DOCX as `w:docVars` and in ODT as `text:user-field-decl`; the PDF resolves the fields with them.
- `MacroProject? MacroProject { get; init; }` — The VBA project (DOCX only, ADR-0028). Opaque bytes, never executed; the DOCX reader fills it only with `DocxReadOptions.KeepMacros`. Setting it makes the DOCX writer produce a macro-enabled package.
- `IReadOnlyList<CustomXmlPart>? CustomXmlParts { get; init; }` — Custom XML parts, written to DOCX as `customXml/itemN.xml` and read back from there. ODT and PDF do not store them.

#### DocumentRevisions

`static class DocumentRevisions` — Accepts and rejects tracked changes (ADR-0027). The model is immutable, so every call returns a new document; the markers of the changes touched are gone, the content they marked is kept or removed. Only the text of the body is changed (the writers write tracked changes there only). A paragraph left empty by a deletion across paragraphs is not merged with its neighbour.

- `static PortableDocument Accept(PortableDocument document, int id)` — Accepts the tracked change with this id; the document is returned unchanged if there is none.
- `static PortableDocument AcceptAll(PortableDocument document)` — Accepts every tracked change: insertions stay, deleted text is removed.
- `static PortableDocument Reject(PortableDocument document, int id)` — Rejects the tracked change with this id; the document is returned unchanged if there is none.
- `static PortableDocument RejectAll(PortableDocument document)` — Rejects every tracked change: insertions are removed, deleted text stays.

#### DocumentSearch

`static class DocumentSearch` — searches and replaces text in the document model (phase AH). The model is immutable, so replacing returns a new document; the formatting of the affected runs is kept.

- `static IReadOnlyList<TextMatch> Find(PortableDocument document, TextQuery query)` — Finds all occurrences of the query in the document, including headers and footers. The search runs over the text of the text runs; a text that is spread over several runs is not found. Replace changes the same occurrences, headers and footers included.
- `static IReadOnlyList<TextMatch> Find(PortableDocument document, TextQuery query, CancellationToken cancellationToken)` — Finds all occurrences of the query; the token is checked before each text run.
- `static PortableDocument Replace(PortableDocument document, TextQuery query, string replacement)` — Replaces all occurrences of the query with the replacement and returns the new document. The original document is not changed. Headers and footers are replaced as well: a writer that adopts the source package keeps the band blocks the model did not change, so unmodelled band XML survives (DOCX; ODT writes its bands from the model).
- `static PortableDocument Replace(PortableDocument document, TextQuery query, string replacement, CancellationToken cancellationToken)` — Replaces all occurrences of the query; the token is checked before each text run.

#### DocumentStatistics

`sealed record DocumentStatistics` — content statistics of a document (phase AE, AE02): counts of the model elements and of the text.

- `new DocumentStatistics(int Sections, int Blocks, int Paragraphs, int Headings, int Lists, int ListItems, int Tables, int TableRows, int TableCells, int Images, int Words, int Characters)` — content statistics of a document (phase AE, AE02): counts of the model elements and of the text.
- `int Sections { get; init; }` — Number of sections.
- `int Blocks { get; init; }` — Number of top-level blocks (including the bands).
- `int Paragraphs { get; init; }` — Number of paragraphs.
- `int Headings { get; init; }` — Number of headings.
- `int Lists { get; init; }` — Number of lists.
- `int ListItems { get; init; }` — Number of list items.
- `int Tables { get; init; }` — Number of tables.
- `int TableRows { get; init; }` — Number of table rows.
- `int TableCells { get; init; }` — Number of table cells.
- `int Images { get; init; }` — Number of images.
- `int Words { get; init; }` — Number of words (whitespace separated).
- `int Characters { get; init; }` — Number of characters.

#### DocumentTemplate

`sealed class DocumentTemplate` — a template for DOCX and ODT: an existing file whose named placeholders `{{Name}}` are bound with format-neutral content when writing. The writers adopt the template unchanged and patch a copy, so that styles and layout are preserved.

- `const string OpeningDelimiter` — Opening identifier of a placeholder (`{{`).
- `const string ClosingDelimiter` — Closing identifier of a placeholder (`}}`).
- `ReadOnlyMemory<byte> Data { get; }` — The template bytes as a defensive copy.
- `string? BodyPlaceholder { get; }` — the name of the placeholder that receives the document's blocks; null means that the document blocks are appended at the end of the template content.
- `IReadOnlyDictionary<string, TemplateContent> Placeholders { get; }` — The bound placeholders (name → content). The collection is immutable.
- `IReadOnlyDictionary<string, TemplatePlaceholderType> PlaceholderTypes { get; }` — Declared placeholder types; names are compared ordinally.
- `IReadOnlyDictionary<string, bool> Conditions { get; }` — The conditions of the `{{If Name}}` regions (phase AB, AB02).
- `IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> Repeats { get; }` — The row repetitions (phase AB, AB03): placeholder → data rows. The table row containing the placeholder is repeated once per data row and the `{{Field}}` values are bound.
- `IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>> RegionRepeats { get; }` — Named data sets for repeated regions.
- `IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> NestedRegionRepeats { get; }` — Named repeated-region data with per-row nested data sets.
- `DocumentTemplate Bind(string name, TemplateContent content)` — Binds a placeholder to content; an already bound name is replaced.
- `DocumentTemplate Bind(string name, Inline inline)` — Binds a placeholder to exactly one inline element.
- `DocumentTemplate Bind(string name, IEnumerable<Inline> inlines)` — Binds a placeholder to inline content.
- `DocumentTemplate Bind(string name, Block block)` — Binds a placeholder to exactly one block element.
- `DocumentTemplate Bind(string name, IEnumerable<Block> blocks)` — Binds a placeholder to block content.
- `DocumentTemplate Bind(IReadOnlyDictionary<string, string?> values)` — Binds all placeholders named in `values` to their text value (phase AB, mail merge). A name with a null value is skipped.
- `DocumentTemplate BindEmpty(string name)` — Binds a placeholder to empty block content, removing its paragraph (phase AB, AB02).
- `DocumentTemplate BindText(string name, string text)` — Binds a placeholder to plain text.
- `DocumentTemplate Body(string placeholderName)` — sets which placeholder receives the document's blocks. If the template contains the placeholder once in each of several sections, the writers bind the model section at the same position instead of merging all sections into one area.
- `DocumentTemplate Condition(string name, bool value)` — sets the condition of an `{{If Name}}…{{EndIf}}` region (phase AB, AB02). A region whose condition is false (or absent) is removed when writing.
- `DocumentTemplate Declare(string name, TemplatePlaceholderType type)` — Declares a type and validates existing bindings and repeat rows for the name.
- `static DocumentTemplate FromBytes(ReadOnlyMemory<byte> data)` — Creates a template from the bytes of an existing DOCX or ODT file.
- `static string Marker(string name)` — Forms the identifier of a placeholder, e.g. `{{Title}}`.
- `DocumentTemplate RepeatNestedRegion(string name, IEnumerable<TemplateRegionRow> rows)` — Sets rows whose nested regions can have different data for each parent row. A nested data set overrides a shared one with the same name; an absent set falls back to it.
- `DocumentTemplate RepeatRegion(string name, IEnumerable<IReadOnlyDictionary<string, string?>> rows)` — Sets the rows for a region delimited by standalone `{{Repeat:Name}}` and `{{EndRepeat}}` paragraphs or table rows. Regions may be nested.
- `DocumentTemplate RepeatRows(string name, IEnumerable<IReadOnlyDictionary<string, string?>> rows)` — repeats the table row that contains `name` once per data row (phase AB, AB03).

#### DocumentValidationException

`sealed class DocumentValidationException : ArgumentException, IRegletError` — Invalid input or model values.

- `new DocumentValidationException(string message)` — Creates the exception with a message.
- `new DocumentValidationException(string message, string? paramName)` — Creates the exception with a message and a parameter name.
- `new DocumentValidationException(string message, Exception innerException)` — Creates the exception with a message and an inner exception.

#### DocumentWriteException

`sealed class DocumentWriteException : IOException, IRegletError` — Error when writing to the target file or stream.

- `new DocumentWriteException(string message)` — Creates the exception with a message.
- `new DocumentWriteException(string message, Exception innerException)` — Creates the exception with a message and an inner exception.

#### DrawingShape

`sealed class DrawingShape : Block` — A vector shape or a text box (ADR-0022), inline or anchored like an `ImageBlock`. In version 1 the fill is a solid color, a text box holds paragraphs and headings only.

- `new DrawingShape(ShapeKind kind, Length width, Length height, IEnumerable<Block>? content = null, IEnumerable<ShapePathStep>? path = null)` — Creates a shape.
- `ShapeKind Kind { get; }` — The kind of the shape.
- `Length Width { get; }` — The width of the bounding box.
- `Length Height { get; }` — The height of the bounding box.
- `IReadOnlyList<Block>? Content { get; }` — The paragraphs of a text box; null for another kind.
- `IReadOnlyList<ShapePathStep>? Path { get; }` — The path of a freeform; null for another kind.
- `Color? Fill { get; init; }` — The fill color; null means no fill.
- `Color? Outline { get; init; }` — The outline color; null means no outline.
- `Length OutlineWidth { get; init; }` — The outline width (default one point).
- `ShapeDash Dash { get; init; }` — The dash pattern of the outline.
- `double Rotation { get; init; }` — Rotation in degrees, clockwise.
- `ImageWrap Wrap { get; init; }` — Wrapping of the text; `ImageWrap.None` is inline.
- `HorizontalAnchor HorizontalRelativeTo { get; init; }` — What `DrawingShape.OffsetX` or `DrawingShape.HorizontalAlignment` of a floating object counts from; the column by default.
- `VerticalAnchor VerticalRelativeTo { get; init; }` — What `DrawingShape.OffsetY` or `DrawingShape.VerticalAlignment` of a floating object counts from; the paragraph by default.
- `HorizontalFloatAlignment? HorizontalAlignment { get; init; }` — Aligns a floating object within `DrawingShape.HorizontalRelativeTo`; when set, `DrawingShape.OffsetX` is ignored.
- `VerticalFloatAlignment? VerticalAlignment { get; init; }` — Aligns a floating object within `DrawingShape.VerticalRelativeTo`; when set, `DrawingShape.OffsetY` is ignored. The PDF writer aligns within the page or the margin; relative to the paragraph or line it places the object at the top.
- `Length? OffsetX { get; init; }` — Horizontal offset of an anchored shape; null means 0.
- `Length? OffsetY { get; init; }` — Vertical offset of an anchored shape; null means 0.
- `string? AltText { get; init; }` — Alternative text for accessibility.

#### EmbeddedObject

`sealed class EmbeddedObject : Block` — An object another program owns, embedded in the document (ADR-0029): kept as opaque bytes with a preview picture. The library never opens, runs or updates it. DOCX and ODT store it; PDF draws the preview.

- `new EmbeddedObject(ReadOnlyMemory<byte> data, EmbeddedObjectKind kind, Length width, Length height)` — Creates an embedded object.
- `EmbeddedObjectKind Kind { get; }` — What the bytes are.
- `ReadOnlyMemory<byte> Data { get; }` — The bytes of the object as a defensive copy.
- `Length Width { get; }` — The size on the page.
- `Length Height { get; }` — The size on the page.
- `string ProgId { get; init; }` — The OLE program id (`Excel.Sheet.12`); may be empty.
- `string FileExtension { get; init; }` — The file extension of a `EmbeddedObjectKind.Package` part without the dot (`xlsx`); letters and digits, at most 8. `bin` by default; an `EmbeddedObjectKind.OleStorage` is always `bin`.
- `ImageBlock? Preview { get; init; }` — The preview picture (PNG or JPEG), or null.

#### EmbeddedObjectKind

`enum EmbeddedObjectKind` — What the bytes of an `EmbeddedObject` are.

- `OleStorage = 0` — An OLE compound file (DOCX `oleObjectN.bin`, ODT `draw:object-ole`).
- `Package = 1` — An Open XML package such as an embedded workbook (DOCX `PackageN.xlsx`); DOCX only.

#### Equation

`sealed class Equation : Block` — A display equation (ADR-0031): a formula in a paragraph of its own. DOCX and ODT store it as an editable formula (OMML, MathML); the PDF draws it.

- `new Equation(MathNode root)` — Creates an equation.
- `MathNode Root { get; }` — The formula.
- `TextAlignment? Alignment { get; init; }` — Alignment of the formula; null means centred.
- `string? AltText { get; init; }` — Alternative text for accessibility.

#### Field

`sealed class Field : Inline` — a field with a field code, for example `SEQ Figure \* ARABIC`, `IF` or `MERGEFIELD Name`. The typed fields (`PageNumberField`, `DateField`, `DocPropertyField`, …) cover the common cases; this type carries every other field code (phase Z). DOCX writes the field code, so the application calculates it. ODF has no element for a general field, therefore the ODT writer keeps the cached result as text and reports the field. The PDF renders the cached result; a field without a result is reported and not drawn.

- `new Field(string instruction, string? result = null)` — Creates a field.
- `string Instruction { get; }` — The field code without the surrounding braces.
- `string? Result { get; }` — The cached result of the field: the text that an application shows until it calculates the field. Null means that the field has no result.

#### FieldContext

`sealed class FieldContext` — the values a field calculation uses (phase AJ, AJ02): the document properties, the document variables, the values of a mail merge, the text of the bookmarks, the page numbers and the date. The names are compared case-insensitively, as in Word.

- `new FieldContext()` — Creates an empty context.
- `static FieldContext Empty { get; }` — A context without any values.
- `IReadOnlyDictionary<string, string> Properties { get; init; }` — The document properties (`DOCPROPERTY`).
- `IReadOnlyDictionary<string, string> Variables { get; init; }` — The document variables (`DOCVARIABLE`).
- `IReadOnlyDictionary<string, string> MergeValues { get; init; }` — The values of a mail merge (`MERGEFIELD`).
- `IReadOnlyDictionary<string, string> Bookmarks { get; init; }` — The text of the bookmarks (`REF`).
- `int? PageNumber { get; init; }` — The current page number (`PAGE`); null when it is unknown.
- `int? TotalPages { get; init; }` — The total number of pages (`NUMPAGES`); null when it is unknown.
- `int? SectionPages { get; init; }` — The number of pages in the current section (`SECTIONPAGES`); null when unknown. Callers provide this value; the built-in writers do not paginate DOCX/ODT.
- `DateTimeOffset? Created { get; init; }` — The document creation time (`CREATEDATE`); null means unknown.
- `DateTimeOffset? Saved { get; init; }` — The last save time (`SAVEDATE`); caller-supplied, null means unknown.
- `DateTimeOffset? Printed { get; init; }` — The last print time (`PRINTDATE`); caller-supplied, null means unknown.
- `DateTimeOffset? Now { get; init; }` — The date of the calculation (`DATE`); null means "now".
- `static FieldContext Create(IReadOnlyDictionary<string, string>? properties = null, IReadOnlyDictionary<string, string>? variables = null, IReadOnlyDictionary<string, string>? mergeValues = null, IReadOnlyDictionary<string, string>? bookmarks = null)` — Creates a context with the given values.
- `FieldContext ForPage(int? pageNumber, int? totalPages)` — a context for the given page numbers. The sequence counters are shared with this context, so a `SEQ` field continues over several fields.
- `static FieldContext FromMetadata(DocumentMetadata metadata)` — a context with the properties and the variables of the document metadata (`DOCPROPERTY` and `DOCVARIABLE`).
- `int NextSequenceValue(string name)` — the number of the next `SEQ` field with the given name. The counter belongs to the context, so the sequence continues over several fields.
- `int PeekSequenceValue(string name)` — the current number of the `SEQ` counter with the given name, without counting on (`\c`).
- `int ResetSequenceValue(string name, int value)` — sets the number of the `SEQ` counter with the given name (`\r`).

#### FieldEvaluator

`static class FieldEvaluator` — calculates the fields of a document (phase AJ, AJ02). Supported field codes: `IF`, `MERGEFIELD`, `SEQ`, `REF`, `DOCPROPERTY`, `DOCVARIABLE`, `PAGE`, `NUMPAGES`, `DATE`, `TIME` and `QUOTE`. Nested fields (a field code in braces) are calculated first. The calculation follows the rules of Word: names are compared case-insensitively, the comparison of `IF` is numeric when both operands are numbers and otherwise a case-insensitive text comparison. A field whose value cannot be resolved (an unknown name, a missing page number) returns null; the caller decides whether to report it or to keep the cached result.

- `static string? Evaluate(Field field, FieldContext context)` — Calculates a field.
- `static string? Evaluate(string instruction, FieldContext context)` — Calculates a field code.
- `static bool TryEvaluate(string instruction, FieldContext context, out string? value)` — Calculates a field code and reports whether a value was found.

#### FieldResolver

`static class FieldResolver` — calculates the general fields of a document and returns a new document with the results (phase AJ, AJ02). The model is immutable, so the input document stays unchanged. A field whose value cannot be calculated keeps its cached result.

- `static PortableDocument Resolve(PortableDocument document, FieldContext context)` — calculates the fields of the document (body, headers and footers).

#### FlowConnector

`sealed class FlowConnector` — An arrow between two nodes of a `Diagram`, drawn as a path (not glued to the nodes).

- `new FlowConnector(string from, FlowSide fromSide, string to, FlowSide toSide)` — Creates a connector.
- `string From { get; }` — The id of the node it leaves.
- `FlowSide FromSide { get; }` — The side it leaves.
- `string To { get; }` — The id of the node it reaches.
- `FlowSide ToSide { get; }` — The side it reaches.
- `FlowRoute Route { get; init; }` — The path; elbow by default.
- `string? Label { get; init; }` — A short text next to the start ("Yes", "No"); null for none.
- `Color? Color { get; init; }` — The color of line and arrowheads; black when null.
- `ShapeDash Dash { get; init; }` — The dash pattern of the line.
- `bool ArrowEnd { get; init; }` — Whether an arrowhead stands at the end (default true).
- `bool ArrowStart { get; init; }` — Whether an arrowhead stands at the start (default false).

#### FlowNode

`sealed class FlowNode` — A symbol of a `Diagram`: its look, text and place.

- `new FlowNode(string id, FlowNodeKind kind, string text, Length x, Length y, Length width, Length height)` — Creates a node.
- `string Id { get; }` — The identifier.
- `FlowNodeKind Kind { get; }` — The symbol.
- `string Text { get; }` — The text.
- `Length X { get; }` — The left edge in the diagram.
- `Length Y { get; }` — The top edge in the diagram.
- `Length Width { get; }` — The width.
- `Length Height { get; }` — The height.
- `Color? Fill { get; init; }` — The fill; a light blue when null.
- `Color? Outline { get; init; }` — The outline; a dark blue when null.
- `Color? TextColor { get; init; }` — The text color; black when null.
- `Length FontSize { get; init; }` — The font size of the text (default 10 points).

#### FlowNodeKind

`enum FlowNodeKind` — The symbol of a `FlowNode` (ADR-0030).

- `Process = 0` — A step: a rectangle.
- `Decision = 1` — A decision: a diamond.
- `Terminator = 2` — Start or end: a pill.
- `InputOutput = 3` — Input or output: a parallelogram.

#### FlowRoute

`enum FlowRoute` — The path of a `FlowConnector`.

- `Elbow = 0` — Horizontal and vertical segments (default).
- `Straight = 1` — One straight line between the two points.

#### FlowSide

`enum FlowSide` — The side of a node a connector leaves or reaches.

- `Top = 0` — The middle of the top edge.
- `Right = 1` — The middle of the right edge.
- `Bottom = 2` — The middle of the bottom edge.
- `Left = 3` — The middle of the left edge.

#### FontException

`sealed class FontException : Exception, IRegletError` — A font file is corrupted or unsupported.

- `new FontException(string message)` — Creates the exception with a message.
- `new FontException(string message, Exception innerException)` — Creates the exception with a message and an inner exception.

#### Footnote

`sealed class Footnote : Inline` — a footnote or endnote reference in the text. The writers place the reference marker and the note content (for example at the bottom of the page).

- `new Footnote(Block block)` — Creates a footnote with exactly one block.
- `new Footnote(IEnumerable<Block> blocks)` — Creates a footnote with several blocks.
- `NoteKind Kind { get; init; }` — Footnote or endnote (default: footnote).
- `IReadOnlyList<Block> Blocks { get; }` — The blocks of the note content.

#### FormField

`sealed class FormField : Block` — A fillable form field (ADR-0024), placed in the text flow like a shape and also allowed in table cells. Only the PDF writer writes it so far (an AcroForm widget); DOCX and ODT report it.

- `new FormField(FormFieldKind kind, string name, Length? width = null, Length? height = null, IEnumerable<string>? options = null)` — Creates a form field.
- `FormFieldKind Kind { get; }` — The kind.
- `string Name { get; }` — The field name.
- `Length Width { get; }` — The width.
- `Length Height { get; }` — The height.
- `IReadOnlyList<string>? Options { get; }` — The entries of a drop-down; null for another kind.
- `string? Value { get; init; }` — The text of a text field or the selected entry of a drop-down (one of `FormField.Options`).
- `bool Checked { get; init; }` — Whether a check box or radio button is checked.
- `string? ExportValue { get; init; }` — The value the group takes when this radio button is chosen (required for a radio button; the values of one group differ). Ignored by the other kinds.
- `bool Multiline { get; init; }` — Whether a text field takes several lines.
- `int? MaxLength { get; init; }` — The most characters a text field takes; null for no limit.
- `bool ReadOnly { get; init; }` — Whether the user may change the field.
- `bool Required { get; init; }` — Whether the field must be filled in before the form is submitted.
- `Length? FontSize { get; init; }` — The font size of the text; 10 pt when null.
- `string? ToolTip { get; init; }` — The name a screen reader or tooltip shows (the PDF entry `TU`).

#### FormFieldKind

`enum FormFieldKind` — The kind of a `FormField` (ADR-0024).

- `Text = 0` — A text entry (single line, or several lines with `FormField.Multiline`).
- `CheckBox = 1` — A box that is checked or not.
- `DropDown = 2` — A choice of one entry from `FormField.Options`.
- `RadioButton = 3` — One button of a group of radio buttons; the buttons of a group share the name.

#### FormulaException

`sealed class FormulaException : InvalidOperationException, IRegletError` — A worksheet formula cannot be calculated (see `WorksheetCell.Evaluate`).

- `new FormulaException(string message)` — Creates the exception with a message.
- `new FormulaException(string message, Exception innerException)` — Creates the exception with a message and an inner exception.

#### HeaderFooter

`sealed record HeaderFooter` — A simple header or footer with blocks.

- `new HeaderFooter(Block block)` — Creates a header/footer with exactly one block.
- `new HeaderFooter(IEnumerable<Block> blocks)` — Creates a header/footer with several blocks.
- `IReadOnlyList<Block> Blocks { get; }` — The blocks of the header/footer.

#### Heading

`sealed class Heading : Block` — a heading of level 1 to 6. Internally it is mapped to a paragraph style (mapped to `Heading1` to `Heading6`).

- `new Heading(HeadingLevel level, Inline inline)` — Creates a heading with exactly one inline element.
- `new Heading(HeadingLevel level, IEnumerable<Inline> inlines)` — Creates a heading with several inline elements.
- `HeadingLevel Level { get; }` — The heading level.
- `IReadOnlyList<Inline> Inlines { get; }` — The inline content of the heading.
- `ParagraphStyle Style { get; init; }` — Paragraph style; by default `Heading1` to `Heading6`.
- `static string StyleNameFor(HeadingLevel level)` — Returns the internal paragraph style name for a level.

#### HeadingLevel

`enum HeadingLevel` — Level of a heading (1 to 6).

- `Level1 = 1` — Heading 1.
- `Level2 = 2` — Heading 2.
- `Level3 = 3` — Heading 3.
- `Level4 = 4` — Heading 4.
- `Level5 = 5` — Heading 5.
- `Level6 = 6` — Heading 6.

#### HorizontalAnchor

`enum HorizontalAnchor` — What the horizontal position of a floating image or shape counts from.

- `Column = 0` — The text column (the default).
- `Page = 1` — The left edge of the page.
- `Margin = 2` — The left page margin.
- `Character = 3` — The anchor character; the PDF writer places a block-level object like `HorizontalAnchor.Column`.

#### HorizontalFloatAlignment

`enum HorizontalFloatAlignment` — A horizontal alignment of a floating object within its `HorizontalAnchor`, instead of an offset.

- `Left = 0` — At the left edge.
- `Center = 1` — Centred.
- `Right = 2` — At the right edge.

#### Hyperlink

`sealed class Hyperlink : Inline` — a hyperlink with target and visible text. The target is a URL (http, https, mailto or a file path).

- `new Hyperlink(string target, string text)` — Creates a hyperlink with plain text.
- `new Hyperlink(string target, IEnumerable<Inline> inlines)` — Creates a hyperlink with several inline elements.
- `string Target { get; }` — The target (URL or path).
- `IReadOnlyList<Inline> Inlines { get; }` — The visible content of the hyperlink.

#### HyphenationPatterns

`sealed class HyphenationPatterns` — the hyphenation patterns of a language (the Liang algorithm, phase AJ, BJ01). The patterns have the TeX format: letters with digits that give the priority of the position, for example `hy3ph` or `.ach4`. A position may be broken where the priority is odd. The core library ships no patterns; the caller supplies them, or uses the optional package `Reglet.Hyphenation`, which carries the TeX patterns of several languages. Without patterns the hyphenation does nothing.

- `new HyphenationPatterns()` — Creates a pattern set without patterns (no hyphenation).
- `static HyphenationPatterns Empty { get; }` — A pattern set without patterns (no hyphenation).
- `int Count { get; }` — The number of patterns.
- `int LeftMinimum { get; init; }` — The least number of characters before a break (TeX: `\lefthyphenmin`); the default is 2.
- `int RightMinimum { get; init; }` — The least number of characters after a break (TeX: `\righthyphenmin`); the default is 3.
- `string Apply(string word)` — inserts the soft hyphen (U+00AD) at the positions where a break is allowed. A word processor that honours the soft hyphen breaks the word there.
- `string ApplyToText(string text)` — inserts the soft hyphens in a text: every word between letters is hyphenated, the punctuation and the spaces stay as they are.
- `IReadOnlyList<int> BreakPositions(string word)` — the positions of a word where a break is allowed (the number of characters before the break). An empty list means that the word is not broken.
- `static HyphenationPatterns Parse(TextReader reader)` — reads the patterns (one per line; a line may hold several patterns separated by spaces, a pattern may be commented out with `%`).
- `static HyphenationPatterns Parse(TextReader reader, int leftMinimum, int rightMinimum)` — reads the patterns and sets the least number of characters before and after a break (the `\lefthyphenmin` and `\righthyphenmin` of the language).
- `static HyphenationPatterns Parse(string text)` — reads the patterns from a text (see `HyphenationPatterns.Parse`).

#### IDocumentReader

`interface IDocumentReader` — contract for a format reader. The reader reads a file (stream) into a `PortableDocument` content model. Layout is not preserved; the reader represents only a documented subset (see feature matrix). The passed stream is not closed.

- `ReadResult Read(Stream input, CancellationToken cancellationToken = default)` — Reads a document from the stream into the content model.

#### IDocumentWriter

`interface IDocumentWriter` — contract for a format writer. The writer reads the immutable model and writes it into the specified stream. The foreign stream is not closed.

- `Task<WriteResult> WriteAsync(PortableDocument document, Stream output, CancellationToken cancellationToken = default)` — Writes the document to the stream and returns collected warnings.

#### IRegletError

`interface IRegletError` — marker for all errors raised by Reglet. This allows its own catch errors together while the concrete types remain distinguishable.


#### ImageBlock

`sealed class ImageBlock : Block` — An embedded image.

- `new ImageBlock(ReadOnlyMemory<byte> data, ImageMediaType mediaType)` — Creates an image from bytes and media type.
- `ReadOnlyMemory<byte> Data { get; }` — The image bytes as a defensive copy.
- `ImageMediaType MediaType { get; }` — The media type of the image bytes.
- `ImageBlock? RasterFallback { get; init; }` — Optional PNG or JPEG used by DOCX readers that do not support SVG.
- `string? AltText { get; init; }` — Alternative text for accessibility.
- `Length? Width { get; init; }` — Display width; null means automatic.
- `Length? Height { get; init; }` — Display height; null means automatic.
- `ImageScaleMode ScaleMode { get; init; }` — Scaling mode.
- `ImageCrop? Crop { get; init; }` — cropping cut off on each side (phase AA, AA02); null means no cropping.
- `ImageWrap Wrap { get; init; }` — wrapping of the text around a floating image (phase AA, AA01); `ImageWrap.None` is inline.
- `bool TakesAnchorLine { get; init; }` — Whether a floating image takes an empty line of its own (its anchor paragraph), as Word and LibreOffice lay it out; false places it without one, as MigraDoc does, so the next block starts where the image starts. The PDF writer honours it; DOCX and ODT need the anchor paragraph and report that they keep it.
- `HorizontalAnchor HorizontalRelativeTo { get; init; }` — What `ImageBlock.OffsetX` or `ImageBlock.HorizontalAlignment` of a floating object counts from; the column by default.
- `VerticalAnchor VerticalRelativeTo { get; init; }` — What `ImageBlock.OffsetY` or `ImageBlock.VerticalAlignment` of a floating object counts from; the paragraph by default.
- `HorizontalFloatAlignment? HorizontalAlignment { get; init; }` — Aligns a floating object within `ImageBlock.HorizontalRelativeTo`; when set, `ImageBlock.OffsetX` is ignored.
- `VerticalFloatAlignment? VerticalAlignment { get; init; }` — Aligns a floating object within `ImageBlock.VerticalRelativeTo`; when set, `ImageBlock.OffsetY` is ignored. The PDF writer aligns within the page or the margin; relative to the paragraph or line it places the object at the top.
- `Length? OffsetX { get; init; }` — Horizontal offset of a floating image; null means 0.
- `Length? OffsetY { get; init; }` — Vertical offset of a floating image; null means 0.

#### ImageCrop

`readonly record struct ImageCrop` — cropping of an image (phase AA, AA02): the fraction cut off on each side (0 to 0.5 of the image size).

- `new ImageCrop(double Left, double Top, double Right, double Bottom)` — cropping of an image (phase AA, AA02): the fraction cut off on each side (0 to 0.5 of the image size).
- `double Left { get; init; }`
- `double Top { get; init; }`
- `double Right { get; init; }`
- `double Bottom { get; init; }`
- `static ImageCrop None { get; }` — No cropping.
- `bool IsEmpty { get; }` — Whether nothing is cropped.

#### ImageException

`sealed class ImageException : Exception, IRegletError` — Image data is corrupted, incomplete or unsupported.

- `new ImageException(string message)` — Creates the exception with a message.
- `new ImageException(string message, Exception innerException)` — Creates the exception with a message and an inner exception.

#### ImageInfo

`sealed record ImageInfo` — The intrinsic size of a PNG or JPEG image, read from its header without decoding the pixels.

- `new ImageInfo(ImageMediaType MediaType, int PixelWidth, int PixelHeight, double? DpiX, double? DpiY)` — The intrinsic size of a PNG or JPEG image, read from its header without decoding the pixels.
- `ImageMediaType MediaType { get; init; }` — PNG or JPEG, recognised from the file signature.
- `int PixelWidth { get; init; }` — Width in pixels.
- `int PixelHeight { get; init; }` — Height in pixels.
- `double? DpiX { get; init; }` — Horizontal resolution (PNG `pHYs`, JPEG JFIF density); null when the file carries none.
- `double? DpiY { get; init; }` — Vertical resolution; null when the file carries none.
- `static ImageInfo Read(ReadOnlySpan<byte> data)` — Reads the header of a PNG or JPEG image.

#### ImageMediaType

`enum ImageMediaType` — Supported media type of an image.

- `Png = 0` — Portable Network Graphics (PNG).
- `Jpeg = 1` — JPEG.
- `Svg = 2` — Scalable Vector Graphics (SVG). Supported by ODT; DOCX and PDF report it.

#### ImageScaleMode

`enum ImageScaleMode` — Scaling mode of an image.

- `Original = 0` — Original size from pixels and resolution.
- `FitWidth = 1` — Scale to the available width (preserving the aspect ratio).
- `Contain = 2` — Fit into the available area (preserving the aspect ratio).

#### ImageWrap

`enum ImageWrap` — Wrapping of the text around a floating image.

- `None = 0` — Inline (as character); the image is part of the text line.
- `Square = 1` — Floating; the text wraps on both sides of the image.
- `TopAndBottom = 2` — Floating; the text stops above and continues below the image.
- `Through = 3` — Floating; the text runs through the image (Word wraps tightly around its contour, the PDF and ODT wrap around the bounding box without reserving space).
- `Behind = 4` — Floating behind the text; the text is not moved.
- `InFront = 5` — Floating in front of the text; the text is not moved.

#### IndexEntry

`sealed class IndexEntry : Inline` — a zero-width mark for an entry of the `AlphabeticalIndex` at this position of the text. DOCX writes an `XE` field, ODT an alphabetical index mark; PDF records the page.

- `new IndexEntry(string text, string? subText = null)` — Creates an index entry, optionally below a main entry.
- `string Text { get; }` — The entry; the main entry when `IndexEntry.SubText` is set.
- `string? SubText { get; }` — The entry below the main entry, or null.

#### Inline

`abstract class Inline` — common base type for inline content of a paragraph or a heading.


#### InlineImage

`sealed class InlineImage : Inline` — An image in the line of a paragraph (Word's `wp:inline`): it stands on the baseline like a large character and follows the paragraph's alignment. The line is as high as the font's line gap, the image and the font's descent.

- `new InlineImage(ImageBlock image)` — Creates an inline image from an image.
- `new InlineImage(ReadOnlyMemory<byte> data, ImageMediaType mediaType, Length? width = null, Length? height = null)` — Creates an inline image from bytes and media type, with an optional size (null keeps the image's own).
- `ImageBlock Image { get; }` — The image: bytes, size, crop and alternative text.

#### InvalidXmlCharacterBehavior

`enum InvalidXmlCharacterBehavior` — Behavior for characters that XML 1.0 does not allow.

- `Replace = 0` — Replace invalid characters with U+FFFD (default).
- `Fail = 1` — Throw an error.

#### Length

`readonly record struct Length` — canonical length value. Stored internally in points; other units are converted with low loss (1 inch = 72 points). Negative values are allowed (e.g. for indents); non-finite values are rejected.

- `new Length(double points)` — Creates a length from a point value.
- `double Points { get; }` — Value in points (1/72 inch).
- `static Length Zero { get; }` — Length of zero points.
- `static Length From(double value, LengthUnit unit)` — Creates a length from value and unit.
- `static Length FromCentimeters(double centimeters)` — Creates a length from centimeters.
- `static Length FromInches(double inches)` — Creates a length from inches.
- `static Length FromMillimeters(double millimeters)` — Creates a length from millimeters.
- `static Length FromPoints(double points)` — Creates a length from points.
- `double To(LengthUnit unit)` — Converts the length into the specified unit.
- `string ToString()`

#### LengthUnit

`enum LengthUnit` — Unit for specifying a length.

- `Points = 0` — Points (1/72 inch), the canonical unit.
- `Millimeters = 1` — Millimeter.
- `Centimeters = 2` — Centimeters.
- `Inches = 3` — Inches.

#### LineNumberRestart

`enum LineNumberRestart` — describes when the line numbering restarts.

- `Continuous = 0` — The numbering continues across pages and sections.
- `NewPage = 1` — The numbering restarts on every page (the default, as in Word).
- `NewSection = 2` — The numbering restarts with every section.

#### LineNumbering

`sealed record LineNumbering` — the line numbering of a section: the numbers are placed in the margin next to the text lines (phase D). DOCX writes `w:lnNumType`, ODT the document-wide `text:linenumbering-configuration`, the PDF draws the numbers in the left margin.

- `new LineNumbering()`
- `int CountBy { get; init; }` — Every n-th line receives a number; 1 numbers every line.
- `int Start { get; init; }` — The number of the first line.
- `Length Distance { get; init; }` — The distance between the text and the numbers (default 12 pt).
- `LineNumberRestart Restart { get; init; }` — When the numbering restarts (default: on every page).

#### LineSpacing

`sealed record LineSpacing` — Line spacing of a paragraph: a multiple or a fixed minimum/line height.

- `LineSpacingRule Rule { get; }` — Rule of the line spacing.
- `double Factor { get; }` — Multiple (only for `LineSpacingRule.Multiple`).
- `Length Height { get; }` — Height (only for `LineSpacingRule.AtLeast` or `LineSpacingRule.Exactly`).
- `static LineSpacing AtLeast(Length height)` — Creates a line spacing with a minimum height.
- `static LineSpacing Exactly(Length height)` — Creates a fixed line spacing.
- `static LineSpacing Multiple(double factor)` — Creates a line spacing as a multiple of a single line.

#### LineSpacingRule

`enum LineSpacingRule` — Rule for the line spacing of a paragraph.

- `Multiple = 0` — Multiple of a single line (e.g. 1.5).
- `AtLeast = 1` — Minimum height of a row.
- `Exactly = 2` — Fixed line height.

#### ListBlock

`sealed class ListBlock : Block` — An ordered or unordered list that uses a numbering definition of the document.

- `new ListBlock(string numberingName, IEnumerable<ListItem> items)` — Creates a list.
- `const int MaxNestingLevel` — Maximum supported nesting depth.
- `string NumberingName { get; }` — name of the numbering definition the list uses (`PortableDocument.Numbering`); later lists with the same name continue.
- `IReadOnlyList<ListItem> Items { get; }` — The entries of the list.
- `int NestingLevel { get; init; }` — minimum nesting depth (1 = top level). A list in a list item sits in DOCX and PDF automatically at least one level deeper than the surrounding list; ODT represents the level solely via the nesting. The depth selects the level of the numbering definition; a deeper list repeats the last defined level.

#### ListItem

`sealed record ListItem` — An item of a list.

- `new ListItem(Block block)` — Creates a list item with exactly one block.
- `new ListItem(IEnumerable<Block> blocks)` — Creates a list item with several blocks.
- `IReadOnlyList<Block> Blocks { get; }` — The blocks of the item.

#### ListKind

`enum ListKind` — Kind of a list.

- `Bullet = 0` — Bullet list (unordered).
- `Number = 1` — Numbered list (ordered).

#### ListNumberFormat

`enum ListNumberFormat` — Number format of a numbered list.

- `Decimal = 0` — Arabic numerals: 1, 2, 3.
- `LowerRoman = 1` — Lowercase Roman numerals: i, ii, iii.
- `UpperRoman = 2` — Uppercase Roman numerals: I, II, III.
- `LowerLetter = 3` — Lowercase letters: a, b, c.
- `UpperLetter = 4` — Uppercase letters: A, B, C.

#### ListSuffix

`enum ListSuffix` — What follows the number or the bullet of a list item before the text.

- `Tab = 0` — A tab to the text indent (the default).
- `Space = 1` — A single space.
- `Nothing = 2` — Nothing: the text follows the marker directly.

#### MacroProject

`sealed record MacroProject` — The VBA project of a macro-enabled document (DOCX `word/vbaProject.bin`), carried as opaque bytes (ADR-0028). The library never runs, parses or changes it. Only DOCX stores it; ODT and PDF ignore it.

- `new MacroProject()`
- `required ReadOnlyMemory<byte> Data { get; init; }` — The bytes of the project; must not be empty. The value is copied.

#### MathDelimiter

`sealed class MathDelimiter : MathNode` — Brackets around one or more parts separated by commas.

- `new MathDelimiter(char? open, char? close, IEnumerable<MathNode> items)` — Creates a bracketed group.
- `const string Brackets` — The characters that are allowed as opening or closing bracket.
- `char? Open { get; }` — The opening bracket; null for none.
- `char? Close { get; }` — The closing bracket; null for none.
- `IReadOnlyList<MathNode> Items { get; }` — The parts, set apart by commas.

#### MathFraction

`sealed class MathFraction : MathNode` — A fraction.

- `new MathFraction(MathNode numerator, MathNode denominator)` — Creates a fraction.
- `MathNode Numerator { get; }` — The part above the bar.
- `MathNode Denominator { get; }` — The part below the bar.

#### MathNary

`sealed class MathNary : MathNode` — A big operator (sum, product, integral, union, intersection) with limits and a body.

- `new MathNary(char op, MathNode body, MathNode? lower = null, MathNode? upper = null)` — Creates an operator.
- `const string Operators` — The operators that are allowed.
- `char Operator { get; }` — The operator character.
- `MathNode Body { get; }` — What the operator applies to.
- `MathNode? Lower { get; }` — The lower limit; null for none.
- `MathNode? Upper { get; }` — The upper limit; null for none.
- `bool LimitsAbove { get; }` — Whether the limits stand above and below (sum, product, union, intersection) and not beside (integrals).

#### MathNode

`abstract class MathNode` — A part of a formula (ADR-0031). The kinds are fixed: text, row, fraction, script, radical, n-ary operator, delimiter.

- `const int MaxNodes` — The most nodes in one formula.
- `const int MaxDepth` — The deepest nesting of one formula.

#### MathRadical

`sealed class MathRadical : MathNode` — A root: a square root, or a root with a degree.

- `new MathRadical(MathNode radicand, MathNode? degree = null)` — Creates a root.
- `MathNode Radicand { get; }` — The part under the sign.
- `MathNode? Degree { get; }` — The degree; null for a square root.

#### MathRow

`sealed class MathRow : MathNode` — A sequence of parts, set side by side.

- `new MathRow(IEnumerable<MathNode> items)` — Creates a row.
- `IReadOnlyList<MathNode> Items { get; }` — The parts.

#### MathScript

`sealed class MathScript : MathNode` — A base with a superscript, a subscript or both.

- `new MathScript(MathNode baseNode, MathNode? superscript = null, MathNode? subscript = null)` — Creates a script.
- `MathNode Base { get; }` — The part the scripts belong to.
- `MathNode? Superscript { get; }` — The raised part; null for none.
- `MathNode? Subscript { get; }` — The lowered part; null for none.

#### MathText

`sealed class MathText : MathNode` — A run of characters in a formula.

- `new MathText(string text, MathTextStyle? style = null)` — Creates a run.
- `string Text { get; }` — The characters.
- `MathTextStyle Style { get; }` — How the text is set (never null: the automatic choice is resolved).

#### MathTextStyle

`enum MathTextStyle` — How the text of a `MathText` is set.

- `Normal = 0` — Upright: numbers, operators, function names.
- `Italic = 1` — Slanted: variables.

#### NoteKind

`enum NoteKind` — Kind of a note.

- `Footnote = 0` — Footnote at the bottom of the page.
- `Endnote = 1` — Endnote at the end of the document.

#### NumberingDefinition

`sealed record NumberingDefinition` — a named multi-level numbering definition. Lists reference it by name (`ListBlock.NumberingName`), so several lists share format, start value and number formatting; the level is selected by the nesting depth.

- `new NumberingDefinition(string name, IEnumerable<NumberingLevel> levels)` — Creates a definition; missing levels repeat the last one.
- `const int MaxLevels` — Maximum number of levels (Word and ODF both define nine).
- `string Name { get; }` — Unique name of the definition; it is also the ODT list style name.
- `IReadOnlyList<NumberingLevel> Levels { get; }` — The nine levels, level 1 first; levels above the given ones repeat the last.

#### NumberingLevel

`sealed record NumberingLevel` — one level of a `NumberingDefinition`: the kind, the number format, the start value and the formatting of the number or bullet.

- `new NumberingLevel()`
- `ListKind Kind { get; init; }` — Bullet or numbered level (default: `ListKind.Number`).
- `ListNumberFormat Format { get; init; }` — Number format of a numbered level (default: `ListNumberFormat.Decimal`). Ignored for bullet levels.
- `int Start { get; init; }` — First number of a numbered level (default 1). Ignored for bullet levels.
- `Color? NumberColor { get; init; }` — the color of the number or of the bullet (phase AJ, AJ04); null means the color of the text. The formats that have no list styles per list (ODT) report the value.
- `bool NumberBold { get; init; }` — the number or the bullet is written in bold (phase AJ, AJ04); false means normal.
- `ListSuffix Suffix { get; init; }` — what follows the marker before the text: a tab (default), a space or nothing. DOCX (`w:suff`) and ODT (`text:label-followed-by`) keep it; the PDF always starts the text at the item indent.

#### PageBorders

`sealed record PageBorders` — the border of a page (phase AJ, AJ05): the four sides (optional) and the distance between the border and the text. A page border belongs to the section; the formats draw it around the text area of every page of the section.

- `new PageBorders()`
- `ParagraphBorder? Top { get; init; }` — The top border; null means no border on this side.
- `ParagraphBorder? Right { get; init; }` — The right border; null means no border on this side.
- `ParagraphBorder? Bottom { get; init; }` — The bottom border; null means no border on this side.
- `ParagraphBorder? Left { get; init; }` — The left border; null means no border on this side.
- `Length? Offset { get; init; }` — the distance between the border and the text (default: 24 points). The formats that measure the border from the edge of the page use the value as the distance from the text.
- `bool IsEmpty { get; }` — Whether no side has a border.

#### PageBreak

`sealed class PageBreak : Block` — A manual page break.

- `new PageBreak()`

#### PageCountField

`sealed class PageCountField : Inline` — inline placeholder for the total page count of the document (typically in the header or footer of a continuous document).

- `new PageCountField()`

#### PageFrame

`sealed record PageFrame` — a frame at the paper edge (phase S). The frame lies `PageFrame.Inset` from the paper edge removed and is drawn with `PageFrame.Width`.

- `new PageFrame()`
- `Length Width { get; init; }` — Line width of the border. Default is 1 point.
- `Length Inset { get; init; }` — Distance of the frame from the paper edge. Default is 18 points.
- `Color Color { get; init; }` — Border color. Default is black.

#### PageMargins

`sealed record PageMargins` — Page margins.

- `new PageMargins(Length all)` — Creates uniform margins.
- `new PageMargins(Length top, Length right, Length bottom, Length left)` — Creates margins with individual values.
- `static PageMargins Normal { get; }` — Equal margin on all sides (default: 1 inch).
- `Length Top { get; }` — Top margin.
- `Length Right { get; }` — Right margin.
- `Length Bottom { get; }` — Bottom margin.
- `Length Left { get; }` — Left margin.

#### PageNumberField

`sealed class PageNumberField : Inline` — inline placeholder for the current page number (typically in the header or footer).

- `new PageNumberField()`

#### PageOrientation

`enum PageOrientation` — Orientation of a page.

- `Portrait = 0` — Portrait.
- `Landscape = 1` — Landscape.

#### PageRefField

`sealed class PageRefField : Inline` — inline placeholder for a cross-reference to a bookmark (PAGEREF). In DOCX/ODT the field is written (the application resolves the page number); the PDF shows the displayed page number of the bookmark.

- `new PageRefField(string bookmarkName)` — Creates a page reference field.
- `string BookmarkName { get; }` — Name of the referenced bookmark.

#### PageSize

`sealed record PageSize` — Paper size of a page.

- `new PageSize(Length width, Length height)` — Creates a paper size from width and height.
- `Length Width { get; }` — Width of the page.
- `Length Height { get; }` — Height of the page.
- `static PageSize A4 { get; }` — DIN A4 (210 × 297 mm).
- `static PageSize A5 { get; }` — DIN A5 (148 × 210 mm).
- `static PageSize A3 { get; }` — DIN A3 (297 × 420 mm).
- `static PageSize Letter { get; }` — US Letter (8.5 × 11 in).
- `static PageSize Legal { get; }` — US Legal (8.5 × 14 in).

#### PageTemplate

`sealed record PageTemplate` — a page template: design elements applied to every page. Unlike `DocumentTemplate` it is not file-based, but is built from the model built up (phase S).

- `new PageTemplate()`
- `HeaderFooter? Header { get; init; }` — Optional header for all pages.
- `HeaderFooter? Footer { get; init; }` — Optional footer for all pages.
- `Watermark? Watermark { get; init; }` — Optional watermark behind the content.
- `PictureWatermark? PictureWatermark { get; init; }` — Optional picture watermark behind the content (OF-B09).
- `PageFrame? PageFrame { get; init; }` — Optional page frame around the type area.

#### Paragraph

`sealed class Paragraph : Block` — A paragraph with inline content and paragraph style.

- `new Paragraph(Inline inline)` — Creates a paragraph with exactly one inline element.
- `new Paragraph(IEnumerable<Inline> inlines)` — Creates a paragraph with several inline elements.
- `IReadOnlyList<Inline> Inlines { get; }` — The inline content of the paragraph.
- `ParagraphStyle Style { get; init; }` — Paragraph style.

#### ParagraphBorder

`sealed record ParagraphBorder` — A border (line style, width, color).

- `new ParagraphBorder(BorderStyle style = BorderStyle.Single, Length? width = null, Color? color = null)` — Creates a border with style, width and color.
- `static ParagraphBorder None { get; }` — No border; on a `TableBlock` it removes the default grid.
- `BorderStyle Style { get; }` — Line style.
- `Length Width { get; }` — Line width.
- `Color Color { get; }` — Line color.

#### ParagraphBorders

`sealed record ParagraphBorders` — Borders of a paragraph (per side optional).

- `new ParagraphBorders()`
- `static ParagraphBorders None { get; }` — The explicit empty border set (no borders on any side). A `null``ParagraphStyle.Borders` means "not set" and inherits; this instance removes an inherited border.
- `ParagraphBorder? Top { get; init; }` — Top border.
- `ParagraphBorder? Right { get; init; }` — Right border.
- `ParagraphBorder? Bottom { get; init; }` — Bottom border.
- `ParagraphBorder? Left { get; init; }` — Left border.
- `static ParagraphBorders All(ParagraphBorder border)` — Creates margins on all four sides.

#### ParagraphBuilder

`sealed class ParagraphBuilder` — Fluent builder for a paragraph.

- `ParagraphBuilder Alignment(TextAlignment alignment)` — Sets the horizontal alignment.
- `ParagraphBuilder Bold(string text)` — Appends a bold text run.
- `Paragraph Build()` — Creates the paragraph.
- `ParagraphBuilder Italic(string text)` — Appends an italic text run.
- `ParagraphBuilder LeftIndent(Length length)` — Sets the left indent.
- `ParagraphBuilder RightIndent(Length length)` — Sets the right indent.
- `ParagraphBuilder Run(TextRun run)` — Appends a finished text run with full formatting.
- `ParagraphBuilder SpaceAfter(Length length)` — Sets the spacing after the paragraph.
- `ParagraphBuilder SpaceBefore(Length length)` — Sets the spacing before the paragraph.
- `ParagraphBuilder Span(string text, bool bold = false, bool italic = false, bool underline = false, Color? color = null, Length? fontSize = null, string? fontFamily = null)` — Appends a text run with freely choosable formatting.
- `ParagraphBuilder Style(ParagraphStyle style)` — Sets a complete paragraph style.
- `ParagraphBuilder Text(string text)` — Appends a normal text run.
- `ParagraphBuilder Underline(string text)` — Appends an underlined text run.

#### ParagraphStyle

`sealed record ParagraphStyle` — Style attributes of a paragraph.

- `new ParagraphStyle()`
- `static ParagraphStyle Default { get; }` — Default style without special attributes.
- `string? StyleName { get; init; }` — Optional name of the paragraph style (e.g. `Heading1`).
- `string? LinkedStyleName { get; init; }` — Optional name of the linked character style (DOCX `w:link`); null means none.
- `TextAlignment? Alignment { get; init; }` — Horizontal alignment; `null` means "not set" and inherits.
- `Length? SpaceBefore { get; init; }` — Space before the paragraph; `null` means "not set" and inherits.
- `Length? SpaceAfter { get; init; }` — Space after the paragraph; `null` means "not set" and inherits. The built-in default is `Typography.ParagraphSpaceAfter` for normal paragraphs and 0 in lists, table cells and headers/footers; `Length.Zero` is an explicit zero.
- `Length? LeftIndent { get; init; }` — Left indent; `null` means "not set" and inherits.
- `Length? RightIndent { get; init; }` — Right indent; `null` means "not set" and inherits.
- `Length? FirstLineIndent { get; init; }` — First-line indent (negative = hanging indent); `null` means "not set" and inherits.
- `LineSpacing? LineSpacing { get; init; }` — Line spacing (multiple or fixed height); null means the font's default.
- `ParagraphBorders? Borders { get; init; }` — Paragraph borders; `null` means "not set" and inherits, an empty `ParagraphBorders` (`ParagraphBorders.None`) means no borders.
- `Color? Background { get; init; }` — Background color of the paragraph (shading); null means none.
- `bool? KeepWithNext { get; init; }` — Keep a heading/paragraph together with the first line of the following block; `null` means "not set".
- `bool? KeepTogether { get; init; }` — Keep the whole paragraph on one page (do not split across pages); `null` means "not set".
- `bool? PageBreakBefore { get; init; }` — Start a new page before the paragraph; `null` means "not set".
- `IReadOnlyList<TabStop>? TabStops { get; init; }` — Tab stops; `null` means "not set" and inherits, an empty list means no tab stops.
- `bool? RightToLeft { get; init; }` — Paragraph direction from right to left (base direction); `null` means "not set" and inherits, `false` is an explicit left-to-right.
- `bool? Bold { get; init; }` — Bold for the runs of the paragraph; `null` means "not set".
- `bool? Italic { get; init; }` — Italic for the runs of the paragraph; `null` means "not set".
- `bool? Underline { get; init; }` — Single underline; `null` means "not set".
- `TextUnderlineStyle? UnderlineStyle { get; init; }` — Underline style; `null` means "not set", None switches one off.
- `Color? UnderlineColor { get; init; }` — Underline color; null means the text color.
- `TextStrikethrough? Strikethrough { get; init; }` — Strikethrough; `null` means "not set", None switches one off.
- `TextBaseline? Baseline { get; init; }` — Vertical position; `null` means "not set", Normal switches one off.
- `bool? AllCaps { get; init; }` — All capitals; `null` means "not set".
- `bool? SmallCaps { get; init; }` — Small capitals; `null` means "not set".
- `TextEffects? Effects { get; init; }` — Text effects (outline, shadow, relief); `null` means "not set", None switches them off. DOCX and ODT write them, the PDF does not draw them.
- `bool? Hidden { get; init; }` — Hidden text; `null` means "not set".
- `Color? TextColor { get; init; }` — text color of the runs; `null` means the document default. (`ParagraphStyle.Background` is the shading of the paragraph box, not of the text.)
- `Color? TextBackground { get; init; }` — Background of the text (not the paragraph shading); null means none.
- `Color? Highlight { get; init; }` — Highlighting of the text; null means none.
- `string? FontFamily { get; init; }` — Font family of the runs; null means the document default.
- `Length? FontSize { get; init; }` — Font size of the runs; null means the document default.
- `string? MarkFontFamily { get; init; }` — Font family of the paragraph mark (Word's `w:pPr/w:rPr`); null means `ParagraphStyle.FontFamily`, else the document default. The mark sets the height of a paragraph without visible text and counts towards its last line. DOCX and PDF honour it; ODT has no paragraph mark.
- `Length? MarkFontSize { get; init; }` — Font size of the paragraph mark; null means `ParagraphStyle.FontSize`, else the document default. See `ParagraphStyle.MarkFontFamily`.
- `ParagraphBorder? TextBorder { get; init; }` — Border around the text (`ParagraphStyle.Borders` is the box of the paragraph).
- `Length? Kerning { get; init; }` — Minimum font size for the kerning; null means no specification.
- `Length? CharacterSpacing { get; init; }` — Additional character spacing; null means the default.
- `double? HorizontalScaling { get; init; }` — Horizontal scaling in percent (100 = unchanged); null means the default.
- `Length? Position { get; init; }` — Baseline shift of the runs (positive = up); null means the default.

#### PictureWatermark

`sealed record PictureWatermark` — a picture watermark that is placed behind the page content (OF-B09). It is the picture counterpart of the text `Watermark`.

- `new PictureWatermark(ReadOnlyMemory<byte> data, ImageMediaType mediaType)` — Creates a picture watermark from image bytes and a media type.
- `ReadOnlyMemory<byte> Data { get; }` — The image bytes as a defensive copy.
- `ImageMediaType MediaType { get; }` — The media type of the image bytes.
- `Length? Width { get; init; }` — Display width. Null means: half the page width. The aspect ratio of the image is kept if only one of the two sizes is given.
- `Length? Height { get; init; }` — Display height. Null means: the height that follows from the width and the aspect ratio of the image.
- `double Opacity { get; init; }` — Opacity between 0 (invisible) and 1 (opaque). Default is 0.1.
- `Length? OffsetX { get; init; }` — Horizontal offset from the center of the page. Default is 0.
- `Length? OffsetY { get; init; }` — Vertical offset from the center of the page. Default is 0.

#### PortableDocument

`sealed record PortableDocument` — immutable root object of a document: metadata, default style and at least one section. A built document can be written to several formats in parallel.

- `new PortableDocument(DocumentMetadata metadata, Section section)` — Creates a document with exactly one section.
- `new PortableDocument(DocumentMetadata metadata, IEnumerable<Section> sections)` — Creates a document with several sections.
- `DocumentMetadata Metadata { get; init; }` — The document metadata. Settable via `with`, e.g. to adjust a read document.
- `IReadOnlyList<Section> Sections { get; init; }` — The sections of the document (at least one). Settable via `with`.
- `DocumentDefaults Defaults { get; init; }` — The document-wide default values (default style).
- `IReadOnlyList<ParagraphStyle> Styles { get; init; }` — named paragraph styles defined by the document (each with `ParagraphStyle.StyleName` set); empty means none. DOCX and ODT write the definitions, the PDF uses them for named style references.
- `IReadOnlyList<CharacterStyle> CharacterStyles { get; init; }` — named character styles defined by the document (see `CharacterStyle`); empty means none. A run references a style by name (`TextRun.CharacterStyleName`).
- `IReadOnlyList<NumberingDefinition> Numbering { get; init; }` — the numbering definitions of the document (empty means none). A list references one by `ListBlock.NumberingName`; the writers reject a name that does not resolve.
- `static PortableDocumentBuilder Create()` — Creates a new fluent builder for a document.
- `DocumentStatistics Statistics()` — Computes the content statistics of the document (phase AE, AE02).

#### PortableDocumentBuilder

`sealed class PortableDocumentBuilder` — fluent builder for a `PortableDocument`. It collects metadata, page settings and blocks and produces in `PortableDocumentBuilder.Build` a validated, immutable document.

- `PortableDocument Build()` — Creates the validated, immutable document.
- `PortableDocumentBuilder BulletList(params string[] items)` — Adds a bullet list.
- `PortableDocumentBuilder Defaults(DocumentDefaults defaults)` — Sets the document-wide default values (default style).
- `PortableDocumentBuilder Heading(string text, int level = 1)` — Adds a heading of level 1–6.
- `PortableDocumentBuilder Image(ReadOnlyMemory<byte> data, ImageMediaType mediaType, string? altText = null, Length? width = null, Length? height = null, ImageScaleMode scaleMode = ImageScaleMode.Original)` — Adds an image.
- `PortableDocumentBuilder List(string numberingName, params string[] items)` — adds a list that uses a numbering definition added via `PortableDocumentBuilder.Numbering`. Several such lists continue each other.
- `PortableDocumentBuilder Metadata(string? title = null, string? author = null, string? subject = null, IEnumerable<string>? keywords = null, string? language = null, DateTimeOffset? created = null)` — Sets document metadata; unspecified values remain.
- `PortableDocumentBuilder NumberedList(params string[] items)` — Adds a numbered list.
- `PortableDocumentBuilder Numbering(params NumberingDefinition[] definitions)` — adds numbering definitions that lists can reference by name. `PortableDocumentBuilder.BulletList` and `PortableDocumentBuilder.NumberedList` create one definition per call, so they restart their numbering; a definition added here lets several lists continue each other.
- `PortableDocumentBuilder Page(PageSize size, PageMargins? margins = null, PageOrientation orientation = PageOrientation.Portrait, LineNumbering? lineNumbering = null)` — Sets paper size, margins and orientation.
- `PortableDocumentBuilder PageBreak()` — Adds a manual page break.
- `PortableDocumentBuilder Paragraph(string text)` — Adds a paragraph from plain text.
- `PortableDocumentBuilder Paragraph(Action<ParagraphBuilder> configure)` — Adds a paragraph via the paragraph builder.
- `PortableDocumentBuilder Table(Action<TableBuilder> configure)` — Adds a table via the table builder.

#### QrErrorCorrection

`enum QrErrorCorrection` — The error correction level of a QR code: the share of damaged codewords it survives.

- `Low = 0` — About 7 %.
- `Medium = 1` — About 15 %.
- `Quartile = 2` — About 25 %.
- `High = 3` — About 30 %.

#### ReadResult

`sealed record ReadResult` — Result of a read operation: content model plus collected warnings.

- `new ReadResult(PortableDocument document, IEnumerable<ReadWarning> warnings)` — Creates a read result from the model and a snapshot of the warnings.
- `PortableDocument Document { get; }` — The read content model.
- `IReadOnlyList<ReadWarning> Warnings { get; }` — Immutable snapshot of the warnings.
- `bool HasWarnings { get; }` — True if at least one warning arose when reading.
- `static ReadResult Create(PortableDocument document)` — Result without warnings.

#### ReadWarning

`sealed record ReadWarning` — A warning that arose when reading (unmappable content element).

- `new ReadWarning(string Message)` — A warning that arose when reading (unmappable content element).
- `string Message { get; init; }`

#### ResourceLimits

`sealed record ResourceLimits` — configurable limits for resource consumption and document size. Protect server processes from oversized input.

- `new ResourceLimits()`
- `static ResourceLimits Default { get; }` — The default limits.
- `long MaxImageBytes { get; init; }` — Maximum byte count of an image (default: 32 MiB).
- `long MaxImagePixels { get; init; }` — Maximum pixel count of an image (default: 100 megapixels).
- `int MaxBlocks { get; init; }` — Maximum number of blocks in the document (default: 1,000,000).
- `int MaxTableCells { get; init; }` — Maximum number of table cells in the document (default: 1,000,000).
- `int MaxTextLength { get; init; }` — Maximum text length of a text run (default: 10,000,000 characters).
- `int MaxNestingDepth { get; init; }` — Maximum nesting depth of tables, lists and notes (default: 256). The writers recurse per level, and a stack overflow would end the process instead of throwing.
- `int MaxPages { get; init; }` — Maximum number of pages a PDF may get (default: 100,000). Pages cost layout work and output that no other limit bounds: a line taller than the page or a large header repeated on every page can make very many of them.
- `long MaxTemplateCharacters { get; init; }` — Maximum number of characters that binding a template may add in total (default: 256 MiB, the size up to which the readers accept a decompressed part).

#### RichText

`sealed class RichText` — Text in a cell with runs of their own font, such as a bold label followed by normal text.

- `new RichText(IEnumerable<RichTextRun> runs)` — Creates rich text from runs.
- `new RichText(params RichTextRun[] runs)` — Creates rich text from runs.
- `IReadOnlyList<RichTextRun> Runs { get; }` — The runs.
- `string ToString()` — The plain text of all runs.

#### RichTextRun

`sealed record RichTextRun` — A run of rich text.

- `new RichTextRun(string Text, CellFont? Font = null)` — A run of rich text.
- `CellFont? Font { get; init; }` — The run's font; null keeps the cell's.
- `string Text { get; init; }` — The text.

#### Section

`sealed record Section` — A document section with page settings and blocks.

- `new Section(Block block)` — Creates a section with exactly one block.
- `new Section(IEnumerable<Block> blocks)` — Creates a section with several blocks.
- `PageSize PageSize { get; init; }` — Paper size.
- `PageOrientation Orientation { get; init; }` — Orientation (portrait or landscape).
- `PageMargins Margins { get; init; }` — Page margins.
- `HeaderFooter? Header { get; init; }` — Optional header.
- `HeaderFooter? Footer { get; init; }` — Optional footer.
- `Length? HeaderDistance { get; init; }` — Distance of the header from the top edge of the sheet (Word's `w:pgMar w:header`); null means 0.5 in. Larger than the top margin, the PDF starts the header there and the body below max(top margin, distance + header height), as Word does.
- `Length? FooterDistance { get; init; }` — Distance of the footer from the bottom edge of the sheet (`w:pgMar w:footer`); null means 0.5 in.
- `bool HeaderPushesBody { get; init; }` — Whether a header taller than the top margin pushes the body down (true, the default, as in Word). False keeps the body at the top margin, and the header may overlap it (MigraDoc's behaviour). The PDF honours it; DOCX and ODT report it.
- `HeaderFooter? FirstHeader { get; init; }` — Optional header of the first page.
- `HeaderFooter? FirstFooter { get; init; }` — Optional footer of the first page.
- `HeaderFooter? EvenHeader { get; init; }` — Optional header of even pages.
- `HeaderFooter? EvenFooter { get; init; }` — Optional footer of even pages.
- `int? PageNumberStart { get; init; }` — First page number of the section; null means continuous.
- `LineNumbering? LineNumbering { get; init; }` — the line numbering of the section; null means no line numbers (phase D).
- `SectionStartType StartType { get; init; }` — How the section starts (the break before it); `SectionStartType.NextPage` is the default.
- `int? TextColumns { get; init; }` — Number of text columns (1 = single-column); null means single-column.
- `IReadOnlyList<Block> Blocks { get; init; }` — The blocks of the section. Settable via `with`.
- `PageBorders? PageBorders { get; init; }` — the border around the text area of every page of the section (phase AJ, AJ05); null means no page border. DOCX writes `w:pgBorders`, the PDF draws the border, ODT has no portable page border and reports it.

#### SectionStartType

`enum SectionStartType` — How a section starts (the break before the section).

- `NextPage = 0` — The section starts on the next page (the default).
- `Continuous = 1` — The section continues on the same page (no page break).
- `OddPage = 2` — The section starts on the next odd page (a blank page is inserted if needed).
- `EvenPage = 3` — The section starts on the next even page (a blank page is inserted if needed).

#### ShapeDash

`enum ShapeDash` — The dash pattern of the outline of a shape.

- `Solid = 0` — A continuous line.
- `Dash = 1` — Dashes.
- `Dot = 2` — Dots.

#### ShapeKind

`enum ShapeKind` — The kind of a `DrawingShape` (ADR-0022).

- `Line = 0` — A straight line from the top left to the bottom right corner of the bounding box.
- `Rectangle = 1` — A rectangle.
- `RoundRectangle = 2` — A rectangle with rounded corners.
- `Ellipse = 3` — An ellipse (a circle for equal width and height).
- `Freeform = 4` — A free path of `ShapePathStep`s.
- `TextBox = 5` — A rectangle that holds paragraphs.

#### ShapePathCommand

`enum ShapePathCommand` — The command of a `ShapePathStep`.

- `MoveTo = 0` — Starts a new sub path at the point.
- `LineTo = 1` — A straight line to the point.
- `CubicTo = 2` — A cubic Bézier curve to the point with two control points.
- `Close = 3` — Closes the current sub path; the points are ignored.

#### ShapePathStep

`readonly record struct ShapePathStep` — One step of the path of a freeform. Coordinates are measured from the top left corner of the bounding box of the shape.

- `new ShapePathStep(ShapePathCommand Command, Length X, Length Y, Length X1 = default, Length Y1 = default, Length X2 = default, Length Y2 = default)` — One step of the path of a freeform. Coordinates are measured from the top left corner of the bounding box of the shape.
- `ShapePathCommand Command { get; init; }` — The command.
- `Length X { get; init; }` — The x coordinate of the end point.
- `Length Y { get; init; }` — The y coordinate of the end point.
- `Length X1 { get; init; }` — The x coordinate of the first control point (`ShapePathCommand.CubicTo`).
- `Length Y1 { get; init; }` — The y coordinate of the first control point.
- `Length X2 { get; init; }` — The x coordinate of the second control point.
- `Length Y2 { get; init; }` — The y coordinate of the second control point.

#### SheetPageSetup

`sealed record SheetPageSetup` — How a worksheet prints: paper, orientation, scaling, margins, print area and repeated rows.

- `new SheetPageSetup()`
- `static PageMargins DefaultMargins { get; }` — Excel's default margins: 0.75 in top and bottom, 0.7 in left and right.
- `PageOrientation Orientation { get; init; }` — Portrait or landscape.
- `SheetPaperSize PaperSize { get; init; }` — The paper size.
- `int? FitToPagesWide { get; init; }` — Fits the print to this many pages wide; null prints at 100 %. With `SheetPageSetup.FitToPagesTall` 0 the sheet is one page wide and as many pages tall as it needs ("fit all columns on one page").
- `int? FitToPagesTall { get; init; }` — Fits the print to this many pages tall, 0 meaning as many as needed; null with a width means 1.
- `PageMargins Margins { get; init; }` — The page margins.
- `string? PrintArea { get; init; }` — The printed range, such as `A1:F40`; null prints the used range.
- `(int, int)? RepeatRows { get; init; }` — The first and last 1-based row repeated at the top of every printed page; null means none.

#### SheetPaperSize

`enum SheetPaperSize` — The paper a worksheet prints on.

- `A4 = 0` — A4 (210 × 297 mm).
- `Letter = 1` — US Letter (8.5 × 11 in).

#### TabStop

`sealed record TabStop` — A tab stop with position, alignment and optional leader.

- `new TabStop(Length position, TabStopAlignment alignment = TabStopAlignment.Left, TabStopLeader leader = TabStopLeader.None)` — Creates a tab stop.
- `Length Position { get; }` — The position from the left margin.
- `TabStopAlignment Alignment { get; }` — Alignment.
- `TabStopLeader Leader { get; }` — Leader.

#### TabStopAlignment

`enum TabStopAlignment` — Alignment of a tab stop.

- `Left = 0` — Text begins at the tab stop.
- `Center = 1` — Text is centered on the tab stop.
- `Right = 2` — Text ends at the tab stop.
- `Decimal = 3` — Decimal separator is placed at the tab stop.

#### TabStopLeader

`enum TabStopLeader` — Leader before a tab stop.

- `None = 0` — No leader.
- `Dot = 1` — Points.
- `Hyphen = 2` — Hyphens.
- `Underscore = 3` — Underline.

#### TableBlock

`sealed class TableBlock : Block` — A table with rows and optional column widths.

- `new TableBlock(IEnumerable<TableRow> rows)` — Creates a table from rows.
- `IReadOnlyList<TableRow> Rows { get; }` — The rows of the table (header rows are marked).
- `IReadOnlyList<Length>? ColumnWidths { get; init; }` — Optional column widths; null means automatic widths.
- `IReadOnlyList<double>? ColumnWidthPercents { get; init; }` — Column widths in percent of the table width (Word's `w:tcW w:type="pct"`), for a table whose author does not know the text width; null means none. When set, it replaces `TableBlock.ColumnWidths`: the writers turn it into widths from the section's text width (times `TableBlock.WidthPercent`). Columns past the list share what is left of 100 percent equally; a full list that sums to less than 100 is stretched in proportion, as Word does.
- `ParagraphBorder? Border { get; init; }` — Borders of the table (all grid lines); null means the default thin black grid (0.5 pt), `ParagraphBorder.None` means no borders.
- `Length? CornerRadius { get; init; }` — radius of the corners of the table outline (OF-B04); null means square corners. The PDF draws the background and the outer border of the table with rounded corners; DOCX and ODT have no rounded table corners and report the value.
- `Length? CellPadding { get; init; }` — Uniform cell padding; null means the default spacing.
- `Length? CellPaddingHorizontal { get; init; }` — Left and right padding of the cells; overrides `TableBlock.CellPadding` on those sides.
- `Length? CellPaddingVertical { get; init; }` — Top and bottom padding of the cells; overrides `TableBlock.CellPadding` on those sides.
- `Length? LeftIndent { get; init; }` — Moves the table and its borders from the left margin (Word's table indent); negative moves it into the margin, for example by the left cell padding so the text of the first column stands at the margin. Null means 0.
- `bool FixedColumnWidths { get; init; }` — Keeps a full set of `TableBlock.ColumnWidths` even when it is wider than the text area, so the table runs into the right margin (Word's fixed table layout, `w:tblLayout w:type="fixed"`); a word wider than its column breaks inside. False, the default, shrinks such a table in proportion to the text width plus the room a negative `TableBlock.LeftIndent` opens in the left margin, and, as Word's automatic layout, widens a column to its longest word, taking the room from the other columns in proportion.
- `bool AutoFit { get; init; }` — Distributes the columns by their content (Word's AutoFit to contents) when the table has neither `TableBlock.ColumnWidths` nor `TableBlock.ColumnWidthPercents`; ignored otherwise. A column gets at least its longest word and at most its text without line breaks; without `TableBlock.WidthPercent` the table is only as wide as its content needs. DOCX writes Word's automatic layout, the PDF measures the cells, ODT reports it and writes equal columns. False, the default, gives equal columns.
- `double? WidthPercent { get; init; }` — Table width in percent of the available width, up to 600 as in Word (above 100 the table runs into the right margin); null means full width.
- `TableStyle? Style { get; init; }` — reusable table style (phase Y, Y05). Its borders and cell padding apply where the table has no direct value; header and banded-row backgrounds are conditional formats.

#### TableBuilder

`sealed class TableBuilder` — Fluent builder for a table.

- `TableBlock Build()` — Creates the table.
- `TableBuilder ColumnWidths(params Length[] widths)` — Sets optional column widths.
- `TableBuilder Header(params string[] cells)` — Adds a header row with the specified cell texts.
- `TableBuilder Row(params string[] cells)` — Adds a data row with the specified cell texts.

#### TableCell

`sealed record TableCell` — A table cell.

- `new TableCell(string text)` — Creates a cell with a paragraph from the specified text.
- `new TableCell(Block block)` — Creates a cell with exactly one block.
- `new TableCell(IEnumerable<Block> blocks)` — Creates a cell with several blocks.
- `const int MaxSpan` — The largest column or row span (1000). The writers lay out a span cell by cell, so an unbounded span from a file (two billion) kept them busy indefinitely; real tables stay far below (Word has at most 63 columns).
- `IReadOnlyList<Block> Blocks { get; }` — The blocks of the cell.
- `TextAlignment Alignment { get; init; }` — Horizontal alignment of the cell content.
- `TableCellVerticalAlignment VerticalAlignment { get; init; }` — Vertical alignment of the cell content.
- `int ColumnSpan { get; init; }` — Number of spanned columns (default 1, at most `TableCell.MaxSpan`).
- `int RowSpan { get; init; }` — Number of spanned rows (default 1, at most `TableCell.MaxSpan`).
- `Color? Background { get; init; }` — Background color of the cell; null means none.
- `Length? Padding { get; init; }` — Padding of the cell; null means the table spacing.
- `Length? PaddingHorizontal { get; init; }` — Left and right padding of this cell; overrides `TableCell.Padding` on those sides.
- `Length? PaddingVertical { get; init; }` — Top and bottom padding of this cell; overrides `TableCell.Padding` on those sides.
- `ParagraphBorders? Borders { get; init; }` — Custom cell borders; null means the table borders.

#### TableCellVerticalAlignment

`enum TableCellVerticalAlignment` — Vertical alignment of the content of a table cell.

- `Top = 0` — Top.
- `Center = 1` — Centered.
- `Bottom = 2` — Bottom.

#### TableOfContents

`sealed class TableOfContents : Block` — a table of contents (phase Z, Z04). The entries are the headings of the document up to `TableOfContents.MaxLevel`; DOCX and ODT write a field so the application can fill in the page numbers, PDF renders the entries statically.

- `new TableOfContents()`
- `string? Title { get; init; }` — Optional title above the entries.
- `int MaxLevel { get; init; }` — Deepest heading level that appears (1–6; default 3).

#### TableOfFigures

`sealed class TableOfFigures : Block` — a list of the captions of one label, for example "Figure" or "Table" (a table of figures). A caption is a paragraph with a `SEQ` field of the label (`Field`, for example `SEQ Figure \* ARABIC`); its text is the entry. DOCX writes a `TOC \c` field and ODT an illustration or table index, so the application fills in the page numbers; PDF renders the entries with page numbers and links.

- `new TableOfFigures()`
- `string? Title { get; init; }` — Optional title above the entries.
- `string Label { get; init; }` — The label of the captions that are listed (default "Figure").

#### TableRow

`sealed record TableRow` — A table row.

- `new TableRow(IEnumerable<TableCell> cells)` — Creates a row from cells.
- `new TableRow(params TableCell[] cells)` — Creates a row from individual cells.
- `IReadOnlyList<TableCell> Cells { get; }` — The cells of the row.
- `bool IsHeader { get; init; }` — Marks the row as a header row.
- `Length? Height { get; init; }` — Row height; null means automatic. As in Word, a minimum height is for the content (the cells' top and bottom padding comes on top of it), an exact height (`TableRowHeightRule.Exactly`) includes the padding.
- `TableRowHeightRule HeightRule { get; init; }` — Rule for the row height.
- `bool KeepTogether { get; init; }` — Keeps the row on one page (Word's `w:cantSplit`, ODT's `fo:keep-together`): a row that does not fit at the bottom of a page moves whole to the next one. A row taller than a page is still split.

#### TableRowHeightRule

`enum TableRowHeightRule` — Rule for the row height of a table.

- `Auto = 0` — Height from the content (default).
- `AtLeast = 1` — Minimum height.
- `Exactly = 2` — Fixed height.

#### TableStyle

`sealed class TableStyle` — a reusable table style (phase Y, Y05): borders, cell padding, a header row background and a banded-row background. The direct values on `TableBlock` override the style.

- `new TableStyle(string name)` — Creates a table style.
- `string Name { get; }` — Style name; used as the DOCX style ID and the ODT style name.
- `ParagraphBorder? Border { get; init; }` — Borders of the grid; null means the default thin black grid (a border of style None removes it).
- `Length? CellPadding { get; init; }` — Cell padding; null means the default spacing.
- `Color? HeaderBackground { get; init; }` — Background of the header rows; null means none.
- `Color? BandBackground { get; init; }` — Background of the banded (alternating) rows; null means none.
- `Color? FirstColumnBackground { get; init; }` — Background of the first column; null means none.
- `Color? LastColumnBackground { get; init; }` — Background of the last column; null means none.
- `Color? LastRowBackground { get; init; }` — Background of the last row; null means none.
- `TableStyleArea? HeaderRow { get; init; }` — The conditional formatting of the header rows; null means none.
- `TableStyleArea? BandRow { get; init; }` — The conditional formatting of the banded (alternating) rows; null means none.
- `TableStyleArea? FirstColumn { get; init; }` — The conditional formatting of the first column; null means none.
- `TableStyleArea? LastColumn { get; init; }` — The conditional formatting of the last column; null means none.
- `TableStyleArea? LastRow { get; init; }` — The conditional formatting of the last row; null means none.

#### TableStyleArea

`sealed class TableStyleArea` — the conditional formatting of a table area (a header row, a banded row, the first or last column, the last row). A table style can define an area per `TableStyle`; the direct values of the table and the cells win.

- `new TableStyleArea()`
- `ParagraphBorder? Border { get; init; }` — The border of the cells in the area; null means no border.
- `bool? Bold { get; init; }` — Bold text; null means unchanged.
- `bool? Italic { get; init; }` — Italic text; null means unchanged.
- `Color? TextColor { get; init; }` — The text color; null means unchanged.

#### TemplateContent

`sealed class TemplateContent` — format-neutral content that is bound to a template placeholder when writing. content is either `TemplateContentKind.Inline` (within a paragraph) or `TemplateContentKind.Block` (its own paragraph area); the the respective other collection is empty.

- `TemplateContentKind Kind { get; }` — The kind of content.
- `IReadOnlyList<Inline> Inlines { get; }` — The inline content for `TemplateContentKind.Inline`, otherwise empty.
- `IReadOnlyList<Block> Blocks { get; }` — The block content for `TemplateContentKind.Block`, otherwise empty.
- `static TemplateContent FromBlock(Block block)` — Creates block content from exactly one element.
- `static TemplateContent FromBlocks(IEnumerable<Block> blocks)` — Creates block content from several elements (also empty).
- `static TemplateContent FromInline(Inline inline)` — Creates inline content from exactly one element.
- `static TemplateContent FromInlines(IEnumerable<Inline> inlines)` — Creates inline content from several elements (also empty).

#### TemplateContentKind

`enum TemplateContentKind` — kind of content that is bound to a template placeholder. The kind decides whether the placeholder is replaced within the surrounding paragraph (inline) or receives its own area (block).

- `Inline = 0` — Inline content such as text or a page number field; the surrounding paragraph remains.
- `Block = 1` — Block content such as paragraphs, headings, lists, tables, images or page breaks.

#### TemplatePlaceholderType

`enum TemplatePlaceholderType` — The expected value of a typed template placeholder.

- `Text = 0` — Plain text.
- `Date = 1` — An ISO date (yyyy-MM-dd) or a date field with a value.
- `Number = 2` — An invariant decimal number.
- `Image = 3` — A single image block.
- `Field = 4` — A single field inline.

#### TemplateRegionRow

`sealed class TemplateRegionRow` — A repeated-region row and its per-row nested data sets.

- `new TemplateRegionRow(IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>>? regions = null)` — Creates a row; nested lists are snapshotted and value dictionaries are revalidated when writing.
- `IReadOnlyDictionary<string, string?> Values { get; }` — Field values for this row.
- `IReadOnlyDictionary<string, IReadOnlyList<TemplateRegionRow>> Regions { get; }` — Per-row data sets for nested regions; names are case-sensitive.

#### TextAlignment

`enum TextAlignment` — Horizontal alignment of a paragraph.

- `Left = 0` — Left-aligned (default).
- `Center = 1` — Centered.
- `Right = 2` — Right-aligned.
- `Justify = 3` — Justified.

#### TextBaseline

`enum TextBaseline` — Vertical position of a text run (superscript or subscript).

- `Normal = 0` — Normal baseline.
- `Superscript = 1` — Superscript.
- `Subscript = 2` — Subscript.

#### TextEffects

`enum TextEffects` — Visual effects of the glyphs; combine with `|`.

- `None = 0` — No effect; switches inherited effects off.
- `Outline = 1` — Only the outline of the glyphs is drawn.
- `Shadow = 2` — A shadow behind the text.
- `Emboss = 4` — Raised letters (relief).
- `Engrave = 8` — Pressed-in letters (engraved).

#### TextMatch

`sealed record TextMatch` — a found occurrence of a search text (phase AH).

- `int SectionIndex { get; }` — The index of the section in which the occurrence was found.
- `Block Block { get; }` — The block that contains the occurrence.
- `TextRun Run { get; }` — The text run that contains the occurrence.
- `int Index { get; }` — The position of the occurrence inside the text of the run.
- `int Length { get; }` — The length of the found text.
- `string Text { get; }` — The found text.

#### TextQuery

`sealed record TextQuery` — the criteria of a search in the document model (phase AH). The search runs over the text of the text runs; a text that is spread over several runs is not found.

- `new TextQuery(string text)` — Creates a search query.
- `string Text { get; }` — The search text; with `TextQuery.IsRegex` a regular expression.
- `bool MatchCase { get; init; }` — Whether the search distinguishes upper and lower case (default: no).
- `bool WholeWord { get; init; }` — Whether only whole words match (default: no).
- `bool IsRegex { get; init; }` — Whether `TextQuery.Text` is a regular expression (default: no).

#### TextRun

`sealed class TextRun : Inline` — A text run with optional formatting.

- `new TextRun(string text)` — Creates a text run.
- `string Text { get; }` — The text content.
- `bool? Bold { get; init; }` — bold. `null` means "not set", so a style provides the value; `false` switches an inherited bold off.
- `bool? Italic { get; init; }` — italic; `null` means "not set" (see `TextRun.Bold`).
- `bool? Underline { get; init; }` — single underline; `null` means "not set". Corresponds to `TextRun.UnderlineStyle` = Single.
- `TextUnderlineStyle? UnderlineStyle { get; init; }` — underline style; `null` means "not set", `TextUnderlineStyle.None` switches an inherited underline off. Overrides `TextRun.Underline` when not null.
- `Color? UnderlineColor { get; init; }` — Underline color; null means the text color.
- `TextStrikethrough? Strikethrough { get; init; }` — strikethrough (single or double); `null` means "not set", `TextStrikethrough.None` switches an inherited one off.
- `TextBaseline? Baseline { get; init; }` — vertical position (superscript or subscript); `null` means "not set", `TextBaseline.Normal` switches an inherited one off.
- `bool? AllCaps { get; init; }` — all letters as uppercase; `null` means "not set" (see `TextRun.Bold`).
- `bool? SmallCaps { get; init; }` — small capitals; `null` means "not set" (see `TextRun.Bold`).
- `TextEffects? Effects { get; init; }` — Text effects (outline, shadow, relief); `null` means "not set", None switches them off. DOCX and ODT write them, the PDF does not draw them.
- `Color? Background { get; init; }` — Background color behind the text (shading).
- `Color? Highlight { get; init; }` — Text highlighter color (highlight).
- `Length? Kerning { get; init; }` — Minimum font size from which kerning is applied; null means no specification.
- `Length? CharacterSpacing { get; init; }` — Additional character spacing (positive = wider, negative = tighter).
- `double? HorizontalScaling { get; init; }` — Horizontal scaling in per cent (100 = normal).
- `Length? Position { get; init; }` — Baseline shift (positive = up).
- `string? FontFamily { get; init; }` — Font family; null means the document default.
- `Length? FontSize { get; init; }` — Font size; null means the document default.
- `bool? Hidden { get; init; }` — hidden text; `null` means "not set" (see `TextRun.Bold`).
- `ParagraphBorder? Border { get; init; }` — Border around the run (character border); null means none.
- `Color? Color { get; init; }` — Text color; null means the document default.
- `string? CharacterStyleName { get; init; }` — The name of the character style (see `CharacterStyle`); null means no style. The formatting of the run itself takes precedence over the style.
- `bool IsUnderlined { get; }` — whether the run is underlined: an underline style other than `TextUnderlineStyle.None` or, when no style is set, the `TextRun.Underline` shorthand.

#### TextStrikethrough

`enum TextStrikethrough` — Strikethrough of a text run.

- `None = 0` — No strikethrough.
- `Single = 1` — Single strikethrough.
- `Double = 2` — Double strikethrough.

#### TextUnderlineStyle

`enum TextUnderlineStyle` — Underline style of a text run.

- `None = 0` — No underline.
- `Single = 1` — Single solid line.
- `Double = 2` — Double line.
- `Dotted = 3` — Dotted line.
- `Dashed = 4` — Dashed line.
- `Wave = 5` — Wavy line.

#### TrackedChange

`sealed class TrackedChange : Inline` — the start of a tracked change (ADR-0027). The inlines up to the `TrackedChangeEnd` with the same `TrackedChange.Id` were inserted or deleted by `TrackedChange.Author`; the range may run across paragraphs.

- `new TrackedChange(int id, TrackedChangeKind kind, string author)` — Creates the start of a tracked change.
- `int Id { get; }` — The id that ties the start to its `TrackedChangeEnd`.
- `TrackedChangeKind Kind { get; }` — Whether the text was inserted or deleted.
- `string Author { get; }` — The author's name.
- `DateTimeOffset? Date { get; init; }` — When the change was made (default: unknown).

#### TrackedChangeEnd

`sealed class TrackedChangeEnd : Inline` — The end of a tracked change; the `TrackedChange` with the same id marks the start (ADR-0027).

- `new TrackedChangeEnd(int id)` — Creates the end of a tracked change.
- `int Id { get; }` — The id of the change that starts the range.

#### TrackedChangeKind

`enum TrackedChangeKind` — What a tracked change did to the text between its markers (ADR-0027).

- `Insertion = 0` — The text was inserted.
- `Deletion = 1` — The text was deleted; it is still in the document until the change is accepted.

#### Typography

`static class Typography` — central typography baseline used by all writers. Changes here affect the same across formats.

- `static readonly Length ParagraphSpaceAfter` — default spacing after a paragraph when `ParagraphStyle.SpaceAfter` is `null` (not set). List items (except the last paragraph of a list), table cells and headers/footers are compact and receive 0.
- `static readonly Length SpaceBeforeReductionAtSectionStart` — amount by which Word and LibreOffice reduce the space before of the first paragraph after a section break (new page). Measured with LibreOffice from our DOCX and observed in Word ("~5 pt" at a 12-pt heading): effectively `max(0, SpaceBefore − this amount)`. After a manual page break the space is dropped entirely (0). DOCX leaves the computation to Word/LibreOffice; ODT and PDF reproduce the reduction.
- `static readonly Length TableCellPaddingHorizontal` — Padding of a table cell left and right (Word default 108 twips).
- `static readonly Length TableBorderWidth` — Line width of the table borders (black, outer and inner).
- `static readonly Length TableCellPaddingVertical` — Padding of a table cell top and bottom.
- `static readonly Length HeaderFooterDistance` — distance of the header/footer from the sheet edge (Word default 0.5 inch). The area lies within the page margin; the body still begins at the page margin.
- `static readonly Length HeadingSpaceBefore` — Space before a heading.
- `static readonly Length HeadingSpaceAfter` — Space after a heading.
- `const double LineHeightFactor` — line height relative to the font size for the PDF default fonts (WinAnsi). default fonts have no font file with layout metrics and therefore use the usual factor 1.2. Embedded TrueType fonts compute with the metrics of the font file itself (ascender + descender + line gap from `hhea`); the matches Word and LibreOffice.
- `static Length HeadingSize(HeadingLevel level)` — Font size of a heading of the specified level (1–6).

#### UnsupportedContentBehavior

`enum UnsupportedContentBehavior` — Behavior for unsupported features.

- `Warn = 0` — Collect a warning and omit the feature (default).
- `Fail = 1` — Throw an error.

#### UnsupportedFeatureException

`sealed class UnsupportedFeatureException : NotSupportedException, IRegletError` — A feature cannot be rendered in the target format (or in version 1).

- `new UnsupportedFeatureException(string message)` — Creates the exception with a message.

#### VerticalAnchor

`enum VerticalAnchor` — What the vertical position of a floating image or shape counts from.

- `Paragraph = 0` — The top of the anchor paragraph (the default).
- `Page = 1` — The top edge of the page.
- `Margin = 2` — The top page margin.
- `Line = 3` — The anchor line; the PDF writer places a block-level object like `VerticalAnchor.Paragraph`.

#### VerticalFloatAlignment

`enum VerticalFloatAlignment` — A vertical alignment of a floating object within its `VerticalAnchor`, instead of an offset.

- `Top = 0` — At the top edge.
- `Center = 1` — Centred.
- `Bottom = 2` — At the bottom edge.

#### Watermark

`sealed record Watermark` — a text-based watermark that is placed behind the page content (phase S).

- `new Watermark(string text)` — Creates a watermark with the specified text.
- `string Text { get; }` — The displayed text.
- `double Opacity { get; init; }` — Opacity between 0 (invisible) and 1 (opaque). Default is 0.1.
- `double Rotation { get; init; }` — Rotation in degrees (counterclockwise), e.g. 45 for a diagonal. Default is 45.
- `Length FontSize { get; init; }` — Font size of the watermark. Default is 48 points.
- `Color Color { get; init; }` — Watermark color. Default is a medium gray.

#### Workbook

`sealed class Workbook` — A spreadsheet workbook (ADR-0043): named worksheets in a fixed order. Unlike the document model it is filled step by step; `XlsxWorkbookWriter` (package Reglet.Xlsx) writes it.

- `new Workbook()`
- `IReadOnlyList<Worksheet> Worksheets { get; }` — The worksheets in the order they were added.
- `Worksheet this[string name] { get; }` — The worksheet with this name, ignoring case.
- `Worksheet AddWorksheet(string name)` — Adds a worksheet at the end.
- `static bool IsValidSheetName(string? name)` — Whether Excel accepts the name for a sheet: 1 to 31 characters, none of `[ ] : * ? / \`, not starting or ending with an apostrophe. Uniqueness within a workbook is checked by `Workbook.AddWorksheet`.

#### Worksheet

`sealed class Worksheet` — A worksheet: addressed cells, merged ranges, column widths, row heights, images and the page setup.

- `string Name { get; }` — The sheet name.
- `IEnumerable<WorksheetCell> Cells { get; }` — The cells that exist, by row and then column.
- `IReadOnlyList<CellRange> MergedRanges { get; }` — Merged ranges, in the order they were merged.
- `IDictionary<int, double> ColumnWidths { get; }` — Column widths as Excel shows them (Format → Column Width): characters of the default font's digit width, by 1-based column; a column without one has the default width (8.43). The writer adds Excel's cell padding (5 px at Calibri 11) when it stores the width, and the reader takes it off again; Excel rounds widths to whole pixels, so 10.5 reads back as 10.43.
- `IDictionary<int, double> RowHeights { get; }` — Row heights in points, by 1-based row; a row without one is as high as its content.
- `IList<WorksheetImage> Images { get; }` — Images anchored at cells.
- `SheetPageSetup PageSetup { get; set; }` — Paper, orientation, scaling, margins, print area and repeated rows.
- `WorksheetCell Cell(int row, int column)` — The cell at a 1-based row and column; it is created when it does not exist yet.
- `WorksheetCell Cell(string reference)` — The cell at an A1 reference such as `B3`; it is created when it does not exist yet.
- `void Merge(string range)` — Merges a range such as `A1:C1`; the top-left cell keeps the value.
- `void SetOutsideBorder(string range, CellBorderLine line)` — Draws a border around a range: the outer side of each edge cell gets `line`.
- `void SetStyle(string range, Func<CellStyle, CellStyle> change)` — Changes the style of every cell in a range such as `A1:D4` (creating the cells).

#### WorksheetCell

`sealed class WorksheetCell` — A cell of a worksheet: a value or a formula, and a style.

- `int Row { get; }` — The 1-based row.
- `int Column { get; }` — The 1-based column.
- `string Reference { get; }` — The A1 reference, such as `B3`.
- `object? Value { get; set; }` — The value: null, a `String`, `RichText`, a number (stored as `Double`), a `DateTime` (a `DateOnly` becomes one) or a `Boolean`. Setting a value clears the formula. A read formula cell carries the value Excel cached, if any.
- `string? Formula { get; set; }` — A formula without the leading `=`, such as `SUM(A1:A3)`; null means none. It is written without a cached value and the workbook asks Excel to calculate on load. Setting a formula clears the value.
- `CellStyle Style { get; set; }` — The style; `CellStyle.Default` means none.
- `double Evaluate()` — Calculates the cell (ADR-0046): the formula when there is one, otherwise the value as a number. The formula may use numbers, cell references (also `Sheet2!B3` and `'My sheet'!$B$3`), `SUM` and `PRODUCT` over references and ranges, `+ - * / ^`, a sign, `%` and parentheses; referenced formula cells are calculated too, an empty cell counts as 0. Meant for checking a workbook in tests: Excel and LibreOffice calculate the file themselves when they open it.
- `string GetFormattedText(CultureInfo? culture = null)` — The value as Excel shows it with the cell's number format (Excel's TEXT of the value), in `culture` (default: the current culture). Rich text gives its plain text; a formula without a cached value gives an empty string.

#### WorksheetImage

`sealed record WorksheetImage` — A PNG or JPEG image whose top-left corner sits in a cell (Excel's one-cell anchor).

- `new WorksheetImage(ReadOnlyMemory<byte> data, int row, int column)` — Creates an image anchored at a 1-based row and column.
- `ReadOnlyMemory<byte> Data { get; }` — The image bytes.
- `ImageInfo Info { get; }` — The format and pixel size read from the bytes; use it to keep the aspect ratio when scaling.
- `int Row { get; }` — The 1-based row of the anchor cell.
- `int Column { get; }` — The 1-based column of the anchor cell.
- `int? WidthPixels { get; init; }` — The shown width in pixels; null keeps the image's own width.
- `int? HeightPixels { get; init; }` — The shown height in pixels; null keeps the image's own height.
- `string? AlternativeText { get; init; }` — Alternative text; null means none.

#### WriteMetrics

`sealed record WriteMetrics` — optional metrics of a write operation. They are only filled if `WriteOptions.CollectMetrics` is enabled.

- `new WriteMetrics()`
- `TimeSpan Duration { get; init; }` — Duration of the write operation (build and transfer).
- `int? Pages { get; init; }` — page count if the format knows it (PDF). DOCX and ODT flow freely and report `null`.
- `long Bytes { get; init; }` — Bytes written to the target.

#### WriteOptions

`abstract record WriteOptions` — base class for format-specific writer options. Concrete options remain optional; if none is passed, the respective writer's defaults apply.

- `UnsupportedContentBehavior UnsupportedContent { get; init; }` — behavior for unsupported features. Default is `UnsupportedContentBehavior.Warn`.
- `InvalidXmlCharacterBehavior InvalidXmlCharacters { get; init; }` — behavior for characters that XML 1.0 does not allow (DOCX/ODT). Default is `InvalidXmlCharacterBehavior.Replace`.
- `ResourceLimits Limits { get; init; }` — Configurable resource and document limits.
- `bool CollectMetrics { get; init; }` — whether `WriteResult.Metrics` (duration, pages, bytes) is collected. Default is `false`.
- `bool StreamNonSeekableOutput { get; init; }` — whether a non-seekable target is written directly instead of building the output completely in memory build-up. Benefit: no complete intermediate buffer (important for large documents). Prerequisite: the target allows synchronous write access; if the build aborts off, already-written data can remain in the target. Default is `false`.
- `IProgress<WriteProgress>? Progress { get; init; }` — optional progress receiver (phase AE, AE01); the writers report the completed sections.

#### WriteProgress

`readonly record struct WriteProgress` — Progress of a write operation (phase AE, AE01).

- `new WriteProgress(string Step, int Completed, int Total)` — Progress of a write operation (phase AE, AE01).
- `string Step { get; init; }` — The step that is being progressed (currently `section`).
- `int Completed { get; init; }` — Number of completed units.
- `int Total { get; init; }` — Total number of units.

#### WriteResult

`sealed record WriteResult` — Result of a write operation with the collected warnings.

- `new WriteResult(IEnumerable<WriteWarning> warnings)` — Creates a result from a snapshot of the warnings.
- `IReadOnlyList<WriteWarning> Warnings { get; }` — Immutable snapshot of the warnings.
- `WriteMetrics? Metrics { get; init; }` — optional metrics; `null` if `WriteOptions.CollectMetrics` is not is set.
- `static WriteResult Empty { get; }` — Result without warnings.
- `bool HasWarnings { get; }` — True if at least one warning was collected.

#### WriteWarning

`sealed record WriteWarning` — A warning that arose when writing.

- `new WriteWarning(string Message)` — A warning that arose when writing.
- `string Message { get; init; }`

## Reglet.Docx

### Reglet

#### DocxDocumentReader

`sealed class DocxDocumentReader : IDocumentReader` — reads a DOCX package (subset) into the content model: paragraphs, headings, text with formatting, (simple) lists, tables, page breaks, page number fields, sections (page size/margins/orientation) and metadata from core/settings. Text in hyperlinks, content controls and tracked insertions are read along, tabs and line breaks as `\t`/`\n`. Layout is not preserved; unmappable content (e.g. images, footnotes/endnotes, header/footer content, nested lists) is reported instead of dropped silently.

- `new DocxDocumentReader()`
- `ReadResult Read(Stream input, CancellationToken cancellationToken = default)` — Reads a DOCX from the stream into the content model.
- `ReadResult Read(Stream input, DocxReadOptions options, CancellationToken cancellationToken = default)` — Reads a DOCX from the stream; an encrypted document is opened with the password of `options` (OF-E01).

#### DocxDocumentWriter

`sealed class DocxDocumentWriter : IDocumentWriter` — writes a `PortableDocument` as DOCX directly according to ECMA-376 (OPC package with WordprocessingML). Uses only the Base Class Library.

- `new DocxDocumentWriter()`
- `Task<WriteResult> WriteAsync(PortableDocument document, Stream output, DocxWriteOptions? options = null, CancellationToken cancellationToken = default)` — Writes the document as DOCX to the stream.

#### DocxEncryptionOptions

`sealed record DocxEncryptionOptions` — the ECMA-376 agile encryption of a DOCX (OF-E01). The package is encrypted with AES-256-CBC and a password-derived key (SHA-512, iterated); the result is a compound file that Word and LibreOffice open with the password.

- `new DocxEncryptionOptions(string password)` — Creates the options with the password that protects the document.
- `string Password { get; }` — The password that opens the document.
- `int SpinCount { get; init; }` — the number of hash iterations of the key derivation. Default is 100000, the value Word writes; a higher value makes guessing the password slower.

#### DocxProtection

`enum DocxProtection` — editing protection of a DOCX document (phase AD, AD03), stored in `settings.xml` as `w:documentProtection`. This is protection without encryption; the user can remove it.

- `None = 0` — Leave any existing protection unchanged; add none to a new document.
- `Remove = 1` — Remove an existing protection.
- `ReadOnly = 2` — Read-only: editing is blocked.
- `Comments = 3` — Only comments may be added.
- `TrackedChanges = 4` — Only tracked changes are allowed.
- `Forms = 5` — Only form fields may be filled.

#### DocxReadOptions

`sealed record DocxReadOptions` — Options for the DOCX reader.

- `new DocxReadOptions()`
- `static DocxReadOptions Default { get; }` — Default options.
- `string? Password { get; init; }` — the password of an encrypted DOCX (OF-E01). A document without encryption ignores it.
- `bool KeepMacros { get; init; }` — Keeps the VBA project of a macro-enabled document in `DocumentMetadata.MacroProject` (ADR-0028). Off by default: the project is dropped and reported. It is never executed either way.

#### DocxSignatureInfo

`sealed record DocxSignatureInfo` — the result of reading one signature of a DOCX package (OF-E02).

- `new DocxSignatureInfo()`
- `X509Certificate2? Certificate { get; init; }` — The certificate of the signer from the signature (null when the signature has none).
- `string? Subject { get; }` — The subject of the certificate, for display.
- `IReadOnlyList<string> SignedParts { get; init; }` — The parts the signature covers, as they are named in the signature.
- `bool IsValid { get; init; }` — Whether the signature and every digest are valid.
- `bool? IsTrusted { get; init; }` — Whether the certificate chains to a trusted root of the read options and is valid at the signing time; null when no trusted roots were given.
- `IReadOnlyList<string> ChainProblems { get; init; }` — The problems of the chain check (expired, untrusted root, revocation, …).
- `IReadOnlyList<string> Problems { get; init; }` — What is wrong with the signature; empty when it is valid.

#### DocxSignatureOptions

`sealed record DocxSignatureOptions` — the digital signature of a DOCX package (OF-E02): an OPC signature part with an XML signature (XMLDSig) over the parts of the package.

- `new DocxSignatureOptions(X509Certificate2 certificate)` — Creates the options with the certificate of the signer.
- `X509Certificate2 Certificate { get; }` — The certificate of the signer (it must have an RSA private key).
- `IReadOnlyList<X509Certificate2> AdditionalCertificates { get; init; }` — Further certificates of the chain that are embedded in the signature.
- `IReadOnlyList<string>? Parts { get; init; }` — the parts to sign; null signs every part of the package except the signature parts. A part name is written with a leading slash (`/word/document.xml`).

#### DocxSignatureReadOptions

`sealed record DocxSignatureReadOptions` — the trust settings for reading a DOCX signature (OF-E02).

- `new DocxSignatureReadOptions()`
- `static DocxSignatureReadOptions Default { get; }` — Default options: the signature is verified, the certificate chain is not evaluated.
- `IReadOnlyList<X509Certificate2> TrustedRoots { get; init; }` — the trusted root certificates. Without them the cryptographic signature is checked, but a certificate is not judged trusted; with them the chain is built against exactly these roots.
- `X509RevocationMode RevocationMode { get; init; }` — how the chain checks revocation. Default is `X509RevocationMode.NoCheck`, because the library never calls the network; a caller that wants revocation sets `X509RevocationMode.Online` and accepts the network access.

#### DocxSignatureReader

`static class DocxSignatureReader` — reads and verifies the digital signatures of a DOCX package (OF-E02): the signature parts (`_xmlsignatures/sig*.xml`), their XML signature over the parts of the package and the signed properties of the signature.

- `static IReadOnlyList<DocxSignatureInfo> Read(Stream input)` — reads the signatures of a package. A package without a signature yields an empty list; a signature that does not verify is returned with `DocxSignatureInfo.IsValid` false and the reasons in `DocxSignatureInfo.Problems`. Input and total decompressed package data are subject to the document reader size limits.
- `static IReadOnlyList<DocxSignatureInfo> Read(Stream input, DocxSignatureReadOptions options)` — reads the signatures and also evaluates the certificate chain against the trusted roots of `options` (OF-E02).
- `static IReadOnlyList<DocxSignatureInfo> Read(byte[] package)` — Reads the signatures of a package from its bytes.
- `static IReadOnlyList<DocxSignatureInfo> Read(byte[] package, DocxSignatureReadOptions options)` — Reads the signatures and evaluates the certificate chain.

#### DocxWriteOptions

`sealed record DocxWriteOptions : WriteOptions` — Options for the DOCX writer. Deliberately kept small and optional.

- `new DocxWriteOptions()`
- `static DocxWriteOptions Default { get; }` — Default options.
- `DocumentTemplate? Template { get; init; }` — optional DOCX template. If set, the template's package is adopted, `word/document.xml` is patched and the placeholders are bound; styles of the template are preserved.
- `DocxProtection Protection { get; init; }` — editing protection written as `w:documentProtection` (phase AD, AD03). This is protection without encryption. `DocxProtection.None` leaves an existing protection from a read source unchanged; `DocxProtection.Remove` removes it.
- `string? ProtectionPassword { get; init; }` — optional password of the editing protection (phase AD, AD03). Without a password the protection can be removed without a prompt; with one Word asks for the password before the protection is removed. The password is stored as a salted hash, never in clear text.
- `bool AutoHyphenation { get; init; }` — enables the automatic hyphenation of Word (phase AJ, BJ01): the document is written with `w:autoHyphenation`, so Word hyphenates the words with its own dictionary. The library itself does not hyphenate the text of a DOCX.
- `Length? HyphenationZone { get; init; }` — the distance between the word and the hyphen (phase AJ, BJ01); the default is the Word default of 0.25 inch. Only used with `DocxWriteOptions.AutoHyphenation`.
- `DocxEncryptionOptions? Encryption { get; init; }` — optional encryption of the package (OF-E01): the DOCX is wrapped in an ECMA-376 agile encrypted container and opens with the password only.
- `DocxSignatureOptions? Signature { get; init; }` — optional digital signature (OF-E02): the finished package gets an OPC signature part with an XML signature over its parts. With `DocxWriteOptions.Encryption` the signature is added before the package is encrypted.
- `FontCollection? EmbeddedFonts { get; init; }` — fonts to embed in the file: every style of the families the document uses that this collection holds is written obfuscated (`.odttf`) with `w:embedTrueTypeFonts`, so Word shows the text with them on a computer that does not have them. Fonts whose licence forbids embedding are skipped and reported. Not applied to a document that adopts a source package.
- `IReadOnlyCollection<string>? EditableRanges { get; init; }` — names of bookmark ranges (a `Bookmark` with its `BookmarkEnd`) that stay editable for everyone while `DocxWriteOptions.Protection` is on: they are wrapped in `w:permStart` and `w:permEnd`. A name without a bookmark range is reported and ignored. Editing groups of single users are not modelled.

## Reglet.Odt

### Reglet

#### OdtDocumentReader

`sealed class OdtDocumentReader : IDocumentReader` — reads an ODT package (subset) into the content model: paragraphs, headings, text with formatting, lists (also nested), tables, page number fields, sections (page/margins/orientation) and metadata from meta.xml. Layout is not preserved; not mappable content (e.g. images, tables in headers/footers) is reported instead of dropped silently.

- `new OdtDocumentReader()`
- `ReadResult Read(Stream input, CancellationToken cancellationToken = default)` — Reads an ODT from the stream into the content model.
- `ReadResult Read(Stream input, OdtReadOptions options, CancellationToken cancellationToken = default)` — Reads an ODT from the stream; an encrypted package is opened with the password of `options` (OF-E05).

#### OdtDocumentWriter

`sealed class OdtDocumentWriter : IDocumentWriter` — writes a `PortableDocument` as ODT directly according to ODF 1.3 (ZIP package with ODF XML). Uses only the Base Class Library.

- `new OdtDocumentWriter()`
- `Task<WriteResult> WriteAsync(PortableDocument document, Stream output, OdtWriteOptions? options = null, CancellationToken cancellationToken = default)` — Writes the document as ODT to the stream.

#### OdtEncryptionOptions

`sealed record OdtEncryptionOptions` — ODF 1.3 package encryption (OF-E05). The package is encrypted per file entry with AES-256-CBC and a key derived from the password (SHA-256 start key, PBKDF2-HMAC-SHA1); the manifest carries the parameters and a checksum. The `mimetype` entry and the manifest itself stay unencrypted.

- `new OdtEncryptionOptions(string password)` — Creates the options with the password that protects the document.
- `string Password { get; }` — The password that opens the document.
- `int IterationCount { get; init; }` — the number of PBKDF2 iterations. The default is 100000; a higher value makes guessing the password slower.

#### OdtReadOptions

`sealed record OdtReadOptions` — Options for the ODT reader.

- `new OdtReadOptions()`
- `static OdtReadOptions Default { get; }` — Default options.
- `string? Password { get; init; }` — the password of an encrypted ODT (OF-E05). A document without encryption ignores it.

#### OdtSignatureInfo

`sealed record OdtSignatureInfo` — the result of reading one signature of an ODT package (OF-E05).

- `new OdtSignatureInfo()`
- `X509Certificate2? Certificate { get; init; }` — The certificate of the signer from the signature (null when the signature has none).
- `string? Subject { get; }` — The subject of the certificate, for display.
- `IReadOnlyList<string> SignedParts { get; init; }` — The parts the signature covers, as they are named in the signature.
- `bool IsValid { get; init; }` — Whether the signature and every digest are valid.
- `IReadOnlyList<string> Problems { get; init; }` — What is wrong with the signature; empty when it is valid.

#### OdtSignatureOptions

`sealed record OdtSignatureOptions` — the digital signature of an ODT package (OF-E05): an XML signature (XMLDSig, RSA/SHA-256) over every file of the package, stored in `META-INF/documentsignatures.xml`.

- `new OdtSignatureOptions(X509Certificate2 certificate)` — Creates the options with the certificate of the signer.
- `X509Certificate2 Certificate { get; }` — The certificate of the signer (it must have an RSA private key).
- `IReadOnlyList<X509Certificate2> AdditionalCertificates { get; init; }` — Further certificates of the chain that are embedded in the signature.

#### OdtSignatureReader

`static class OdtSignatureReader` — reads and verifies the digital signatures of an ODT package (OF-E05): the XML signature in `META-INF/documentsignatures.xml` over every file of the package. Binary and encrypted parts are verified as raw octets; XML parts with a C14N transform are canonicalized first. Certificate chain and trust are the caller's responsibility.

- `static IReadOnlyList<OdtSignatureInfo> Read(Stream input)` — reads the signatures of a package. A package without a signature yields an empty list; a signature that does not verify is returned with `OdtSignatureInfo.IsValid` false and the reasons in `OdtSignatureInfo.Problems`.
- `static IReadOnlyList<OdtSignatureInfo> Read(byte[] package)` — Reads the signatures of a package from its bytes.

#### OdtWriteOptions

`sealed record OdtWriteOptions : WriteOptions` — Options for the ODT writer. Deliberately kept small and optional.

- `new OdtWriteOptions()`
- `static OdtWriteOptions Default { get; }` — The default options.
- `DocumentTemplate? Template { get; init; }` — optional ODT template. If set, the template's package is adopted, `content.xml` is patched and the placeholders are bound; named styles and page design of the template are preserved.
- `bool AutoHyphenation { get; init; }` — lets the target program hyphenate the words with its own dictionary (`fo:hyphenate="true"` on the default paragraph style). The soft hyphens of the text remain break opportunities in any case.
- `OdtEncryptionOptions? Encryption { get; init; }` — optional encryption of the package (OF-E05): every file entry except the `mimetype` and the manifest is encrypted with AES-256-CBC and a password-derived key. The output is not deterministic (random salt and IV). Encryption cannot be combined with a template.
- `OdtSignatureOptions? Signature { get; init; }` — optional digital signature (OF-E05): the finished package gets `META-INF/documentsignatures.xml` with an XML signature over its parts. With `OdtWriteOptions.Encryption` the signature is added after encryption, so encrypted parts are signed in their encrypted form and the signature file stays unencrypted.
- `FontCollection? EmbeddedFonts { get; init; }` — fonts to embed in the file: every style of the families the document uses that this collection holds is written to `Fonts/` and declared with `svg:font-face-uri`, so LibreOffice shows the text with them on a computer that does not have them. Fonts whose licence forbids embedding are skipped and reported.

## Reglet.Pdf

### Reglet

#### HtmlOutput

`enum HtmlOutput` — Whether the paged HTML is a whole document or an element to embed in a host page.

- `Document = 0` — A complete HTML document.
- `Fragment = 1` — Only the root element (a `div` with the class `{ClassPrefix}document`) and its scoped styles, without `html`, `head` and `body`.

#### PdfAConformance

`enum PdfAConformance` — Desired PDF/A conformance (phase R).

- `None = 0` — No PDF/A (standard PDF 1.7).
- `PdfA2b = 1` — PDF/A-2b (ISO 19005-2, Basic).
- `PdfA3b = 2` — PDF/A-3b (ISO 19005-3, basic, allows attachments).

#### PdfAnnotation

`sealed record PdfAnnotation` — a non-link annotation on a page of the PDF (OF-F01): a sticky note, a highlight, a stamp or a free text box. The position is given in points from the lower left corner of the page.

- `new PdfAnnotation()`
- `PdfAnnotationKind Kind { get; init; }` — The kind of the annotation.
- `int Page { get; init; }` — The 1-based page the annotation is placed on.
- `double X { get; init; }` — The horizontal position of the left edge in points.
- `double Y { get; init; }` — The vertical position of the bottom edge in points.
- `double Width { get; init; }` — The width of the annotation area in points. Default is 18.
- `double Height { get; init; }` — The height of the annotation area in points. Default is 18.
- `string? Content { get; init; }` — The text of the annotation (`/Contents`); the text of a free text box is drawn on the page.
- `string? Author { get; init; }` — The author of the annotation (`/T`).
- `string? Subject { get; init; }` — The subject of the annotation (`/Subj`).
- `Color? Color { get; init; }` — The colour of the annotation (`/C`); null means the viewer default.
- `string? Icon { get; init; }` — The icon of a text annotation (`/Name`: `Note`, `Comment`, `Help`, `NewParagraph`, `Paragraph`, `Insert`) or the stamp name of a stamp (`Approved`, `Draft`, `Confidential`, …); ignored for the other kinds.

#### PdfAnnotationKind

`enum PdfAnnotationKind` — the kind of a PDF annotation (OF-F01). Only the four kinds below are written and read; a link is not an annotation of this kind (hyperlinks are written from the model).

- `Text = 0` — A sticky note at a position of the page (`/Text`).
- `Highlight = 1` — A highlighted area of the page (`/Highlight`).
- `Stamp = 2` — A stamp over an area of the page (`/Stamp`).
- `FreeText = 3` — A text box drawn on the page (`/FreeText`).

#### PdfAttachment

`sealed record PdfAttachment` — An embedded attachment for PDF/A-3 (phase R, R03).

- `new PdfAttachment()`
- `required string Name { get; init; }` — File name in the PDF (also as a Unicode name).
- `required ReadOnlyMemory<byte> Data { get; init; }` — The attachment data.
- `string MimeType { get; init; }` — MIME-Typ (z. B. `text/plain`).
- `string? Description { get; init; }` — Optional description.
- `string Relationship { get; init; }` — Relationship to the document (AFRelationship, e.g. `Data`, `Source`).

#### PdfCanvas

`sealed class PdfCanvas` — A PDF drawn page by page at fixed coordinates, without the flow layout: text, lines, rectangles and images. Units are points, the origin is the top left of the page and y points down. The file is written with the same `PdfWriteOptions` as a flow document (fonts embedded and subset, PDF/A, encryption, viewer settings), and its text is extracted by `PdfDocumentReader`.

- `new PdfCanvas(PdfWriteOptions? options = null)` — Creates an empty canvas document.
- `DocumentMetadata Metadata { get; set; }` — Title, author, creator and the other metadata of the file.
- `IReadOnlyList<PdfCanvasPage> Pages { get; }` — The pages added so far.
- `PdfCanvasPage AddPage(PageSize size, PageOrientation orientation = PageOrientation.Portrait)` — Adds a page.
- `double MeasureText(string text, PdfCanvasFont font, double size)` — The width of a text in points, exactly as `PdfCanvasPage.DrawText` draws it (shaping, kerning, same font).
- `Task<WriteResult> WriteAsync(Stream output, CancellationToken cancellationToken = default)` — Writes the pages as PDF.
- `Task<WriteResult> WriteHtmlAsync(Stream output, PdfHtmlOptions? html = null, CancellationToken cancellationToken = default)` — Writes the pages as paged HTML (ADR-0039): one element per page, every drawing at the coordinates of the PDF, with the same fonts (embedded as `@font-face`), anchors and measurement; an image drawn several times is embedded once.

#### PdfCanvasFont

`sealed record PdfCanvasFont` — A font of a `PdfCanvas`: a family of the font collection and its style.

- `new PdfCanvasFont(string Family, bool Bold = false, bool Italic = false)` — A font of a `PdfCanvas`: a family of the font collection and its style.
- `string Family { get; init; }` — The family name, as in `PdfWriteOptions.FontCollection`; without a matching font, a standard font is used.
- `bool Bold { get; init; }` — Bold style.
- `bool Italic { get; init; }` — Italic style.

#### PdfCanvasPage

`sealed class PdfCanvasPage` — A page of a `PdfCanvas`. Coordinates are points from the top left of the page, y pointing down, shifted by `PdfCanvasPage.Translate`.

- `PageSize Size { get; }` — The paper size.
- `PageOrientation Orientation { get; }` — The orientation.
- `double Width { get; }` — The width of the page in points (after the orientation).
- `double Height { get; }` — The height of the page in points (after the orientation).
- `void DrawImage(ReadOnlySpan<byte> data, double x, double y, double width, double height)` — Draws a PNG or JPEG image stretched into a rectangle. The same bytes drawn several times are embedded once.
- `void DrawLine(double x1, double y1, double x2, double y2, double width = 1, Color? color = null)` — Draws a straight line.
- `void DrawRectangle(double x, double y, double width, double height, Color? fill = null, Color? stroke = null, double strokeWidth = 1)` — Draws a rectangle, filled and/or outlined; with neither colour nothing is drawn.
- `void DrawText(string text, PdfCanvasFont font, double size, double x, double y, TextAnchor anchor = TextAnchor.BaselineLeft, Color? color = null)` — Draws a single line of text.
- `double MeasureText(string text, PdfCanvasFont font, double size)`
- `void RestoreState()` — Restores the origin saved by the matching `PdfCanvasPage.SaveState`.
- `void SaveState()` — Saves the current origin.
- `void Translate(double dx, double dy)` — Moves the origin of the following drawing.

#### PdfCommentMode

`enum PdfCommentMode` — How the comments of a document (ADR-0026) are written into a PDF.

- `None = 0` — The comments are not written (default, like a PDF exported from Word).
- `Annotations = 1` — Every comment becomes a sticky note (`/Text` annotation) at its anchor; the replies follow the text of the comment in its contents. Comments in a header, footer or note are reported and not written.

#### PdfDocumentReader

`sealed class PdfDocumentReader : IDocumentReader` — extracts text from a PDF (subset) into the content model. Supports classic xref tables, xref streams and object streams (PDF 1.5+) as well as filtered Content-Streams (FlateDecode, LZWDecode, ASCII85Decode, ASCIIHexDecode, RunLengthDecode including /DecodeParms predictor); text is decoded via the ToUnicode CMap (Type0/Identity-H) or WinAnsi (simple fonts). Position and layout are grouped into paragraphs, but not exactly preserved.

- `new PdfDocumentReader()`
- `ReadResult Read(Stream input, CancellationToken cancellationToken = default)`
- `ReadResult Read(Stream input, PdfReadOptions options, CancellationToken cancellationToken = default)` — Extracts text into the content model, opening an encrypted PDF with the password from `options` (phase AD).
- `IReadOnlyList<PdfAnnotation> ReadAnnotations(Stream input, CancellationToken cancellationToken = default)` — reads the non-link annotations of a PDF (OF-F01): sticky notes, highlights, stamps and free text boxes. A link annotation and an unknown subtype are ignored.
- `IReadOnlyList<PdfAnnotation> ReadAnnotations(Stream input, PdfReadOptions options, CancellationToken cancellationToken = default)` — Reads the non-link annotations, opening an encrypted PDF with the given options.
- `IReadOnlyList<string> ReadPageTexts(Stream input, CancellationToken cancellationToken = default)` — Extracts the text of each page of a PDF (phase AC, AC05).
- `IReadOnlyList<string> ReadPageTexts(Stream input, PdfReadOptions options, CancellationToken cancellationToken = default)` — Extracts the text of each page, opening an encrypted PDF with the given options.

#### PdfDocumentTimestamp

`static class PdfDocumentTimestamp` — adds a document timestamp to a signed PDF (OF-E03, ISO 32000-2, 12.8.5): a time-stamp authority signs the bytes of the file, which proves that the document existed at that time. The timestamp is appended as an incremental update, so the signature over the first revision stays valid.

- `static void Add(Stream source, Stream destination, PdfTimestampOptions timestamp, PdfReadOptions? options = null)` — appends a document timestamp to the PDF in `source` and writes the result to `destination`.

#### PdfDocumentWriter

`sealed class PdfDocumentWriter : IDocumentWriter` — writes a `PortableDocument` as PDF 1.7 (ISO 32000-1) with its own syntax writer. Uses only the Base Class Library.

- `new PdfDocumentWriter()`
- `Task<WriteResult> WriteAsync(PortableDocument document, Stream output, PdfWriteOptions? options = null, CancellationToken cancellationToken = default)` — Writes the document as PDF to the stream.
- `Task<WriteResult> WriteHtmlAsync(PortableDocument document, Stream output, PdfWriteOptions? options = null, PdfHtmlOptions? html = null, CancellationToken cancellationToken = default)` — Writes the document as paged HTML (ADR-0039): laid out exactly as the PDF (page breaks, line breaks, headers, footers, fields, floating objects), one element per page drawn as SVG with selectable text in reading order and the fonts of `PdfWriteOptions.FontCollection` embedded. Encryption and signature of `options` are ignored.

#### PdfEncryptionOptions

`sealed record PdfEncryptionOptions` — PDF encryption (phase AD, AD02): the standard security handler with revision 6 and AES-256-CBC (`/CFM /AESV3`, ISO 32000-2, 7.6). The document is encrypted with `PdfEncryptionOptions.UserPassword`; `PdfEncryptionOptions.OwnerPassword` may additionally be used to open and administrate it.

- `new PdfEncryptionOptions()`
- `string UserPassword { get; init; }` — User password; may be empty (the file can then be opened without a password).
- `string? OwnerPassword { get; init; }` — Owner password. If it is null or empty, a random one is used, so that `PdfEncryptionOptions.Permissions` also bind whoever can open the file; nobody can then administrate the document.
- `PdfPermissions Permissions { get; init; }` — Permissions for readers that honour them. Default: `PdfPermissions.All`.

#### PdfHtmlOptions

`sealed record PdfHtmlOptions` — Options of the paged HTML (ADR-0039): the pages of the PDF writer as HTML, one element per page, drawn as SVG with real text and the fonts of `PdfWriteOptions.FontCollection` embedded as `@font-face`.

- `new PdfHtmlOptions()`
- `static PdfHtmlOptions Default { get; }` — The default options.
- `HtmlOutput Output { get; init; }` — A complete document (default) or a fragment for a host page.
- `string ClassPrefix { get; init; }` — The prefix of every class, font family and id (default `reglet-`). The root element has the class `{prefix}document`, every page the class `{prefix}page` and a `data-page` number. The styles select only these classes, so a host page and other fragments are not changed.

#### PdfInspectedFont

`sealed record PdfInspectedFont` — A font a PDF page refers to.

- `new PdfInspectedFont(string BaseName, string Subtype, bool IsEmbedded, bool IsSubset)` — A font a PDF page refers to.
- `string BaseName { get; init; }` — The PostScript name without the subset prefix (`ABCDEF+`).
- `string Subtype { get; init; }` — The font type: TrueType, Type0, Type1, Type3.
- `bool IsEmbedded { get; init; }` — Whether the font program is in the file (FontFile, FontFile2, FontFile3, or a Type3 font).
- `bool IsSubset { get; init; }` — Whether the name carries a subset prefix.

#### PdfInspectedImage

`sealed record PdfInspectedImage` — An image XObject drawn on a page.

- `new PdfInspectedImage(int PixelWidth, int PixelHeight)` — An image XObject drawn on a page.
- `int PixelWidth { get; init; }` — Width in pixels (`/Width`).
- `int PixelHeight { get; init; }` — Height in pixels (`/Height`).

#### PdfInspectedPage

`sealed record PdfInspectedPage` — What a PDF page holds.

- `new PdfInspectedPage(double Width, double Height, IReadOnlyList<PdfInspectedFont> Fonts, IReadOnlyList<PdfInspectedImage> Images)` — What a PDF page holds.
- `double Width { get; init; }` — Width of the media box in points.
- `double Height { get; init; }` — Height of the media box in points.
- `IReadOnlyList<PdfInspectedFont> Fonts { get; init; }` — The fonts of the page's resource dictionary. Reglet shares one resource dictionary between pages, so its pages list all fonts of the document.
- `IReadOnlyList<PdfInspectedImage> Images { get; init; }` — The distinct image XObjects the page draws, also those inside form XObjects; one drawn twice counts once.

#### PdfInspection

`sealed record PdfInspection` — A read-only look at the pages, fonts and images of a PDF, for tests of written files.

- `new PdfInspection(IReadOnlyList<PdfInspectedPage> Pages)` — A read-only look at the pages, fonts and images of a PDF, for tests of written files.
- `IReadOnlyList<PdfInspectedPage> Pages { get; init; }` — The pages in document order.
- `static PdfInspection Read(Stream input, PdfReadOptions? options = null)` — Reads a PDF.

#### PdfPageLayout

`enum PdfPageLayout` — the page layout of the PDF viewer (OF-F02, ISO 32000-1 12.3.2).

- `Default = 0` — the viewer decides (the default, nothing is written).
- `SinglePage = 1` — one page at a time.
- `OneColumn = 2` — one column at a time.
- `TwoColumnLeft = 3` — two columns, the odd pages on the left.
- `TwoColumnRight = 4` — two columns, the odd pages on the right.
- `TwoPageLeft = 5` — two pages side by side, the odd pages on the left (PDF 1.5).
- `TwoPageRight = 6` — two pages side by side, the odd pages on the right (PDF 1.5).

#### PdfPageMode

`enum PdfPageMode` — the mode the viewer opens the document in (OF-F02, ISO 32000-1 12.3.3).

- `Default = 0` — no mode is requested (the default, nothing is written).
- `UseOutlines = 1` — the outline (bookmarks) of the document is shown.
- `UseThumbnails = 2` — the page thumbnails are shown.
- `FullScreen = 3` — the document is opened in full screen mode.
- `UseOptionalContent = 4` — the optional content groups of the document are shown.
- `UseAttachments = 5` — the attachments of the document are shown.

#### PdfPageOperations

`static class PdfPageOperations` — page-level operations on existing PDF files (phase AC): extract a page range or merge several PDFs. The reachable object graph is copied and the page tree rebuilt; inherited page attributes are pushed down. Outlines, forms, names and metadata are not carried over.

- `static void Extract(Stream source, int fromPage, int toPage, Stream destination)` — Extracts the 1-based page range `fromPage`..`toPage`.
- `static void Extract(Stream source, IReadOnlyList<int> pages, Stream destination)` — Extracts the given 1-based pages (in the given order).
- `static void Merge(IEnumerable<Stream> sources, Stream destination)` — Merges the pages of all sources into one PDF, in the given order.

#### PdfPermissions

`enum PdfPermissions` — Permissions of an encrypted PDF (ISO 32000-1, 7.6.3.2, the bits of the `/P` entry). They are a declaration to the reader; a reader may ignore them.

- `None = 0` — No permission (the document may only be opened).
- `Print = 4` — Printing (bit 3).
- `ModifyContents = 8` — Changing the contents (bit 4).
- `Copy = 16` — Copying text and graphics (bit 5).
- `Annotate = 32` — Annotations and form fields (bit 6).
- `FillForms = 256` — Filling in form fields (bit 9).
- `ExtractForAccessibility = 512` — Extraction for accessibility (bit 10).
- `Assemble = 1024` — Assembling the document (bit 11).
- `PrintHighQuality = 2048` — High-quality printing (bit 12).
- `All = 3900` — All permissions (default).

#### PdfReadOptions

`sealed record PdfReadOptions` — Options for reading PDF files (phase AD).

- `new PdfReadOptions()`
- `static PdfReadOptions Default { get; }` — Default options (empty password).
- `string? Password { get; init; }` — User or owner password of an encrypted PDF. If it is missing, the empty password is tried; an encrypted PDF with a different password then raises `DocumentValidationException`.
- `bool RecoverStructure { get; init; }` — Recovers headings, lists, tables, repeated headers/footers and indented paragraphs from the text positions (ADR-0033). Default false: the reader returns paragraphs of joined lines only.
- `bool RecoverFormulas { get; init; }` — With `PdfReadOptions.RecoverStructure`: turns display formulas (fractions, roots, scripts, sums, integrals) into `Equation` blocks (ADR-0034). A guess from positions and rules; default false.

#### PdfRevisionMode

`enum PdfRevisionMode` — Which state of the tracked changes (ADR-0027) a PDF shows.

- `Final = 0` — All changes accepted: insertions are plain text, deleted text is left out (default, Word's "Final" view).
- `Original = 1` — All changes rejected: inserted text is left out, deleted text is shown.
- `Markup = 2` — Insertions are underlined and deletions struck through, both in one color per author (taken from a fixed palette in the order the authors first appear).

#### PdfRewriter

`static class PdfRewriter` — rewrites a PDF while preserving the whole reachable object graph (phase AF, AF15) and optionally replaces the document metadata (AF16). Unlike `PdfPageOperations` the catalog is copied as-is, so outlines, name trees, metadata and forms survive; the output is normalised (classic xref, filters kept). An encrypted source is decrypted with the password from the options and the output is **not** re-encrypted.

- `static void Rewrite(Stream source, Stream destination, PdfReadOptions? options = null)` — Reads `source` and writes an equivalent, normalised PDF.
- `static void Rewrite(Stream source, Stream destination, DocumentMetadata metadata, PdfReadOptions? options = null)` — Reads `source` and writes a normalised PDF with the Info dictionary and the catalog's XMP metadata replaced by `metadata`; everything else is preserved.

#### PdfSignatureInfo

`sealed record PdfSignatureInfo` — the result of reading one signature or document timestamp of a PDF.

- `new PdfSignatureInfo()`
- `string? FieldName { get; init; }` — The name of the signature field.
- `string? SubFilter { get; init; }` — The sub-filter, for example `adbe.pkcs7.detached`, `ETSI.CAdES.detached` or `ETSI.RFC3161`.
- `bool IsDocumentTimestamp { get; init; }` — Whether this entry is a document timestamp and not a signature of a person.
- `X509Certificate2? Certificate { get; init; }` — The certificate of the signer (of the time-stamp authority for a document timestamp).
- `string? Subject { get; }` — The subject of the certificate, for display.
- `string? SignerName { get; init; }` — The name, reason and location the signer stated (not verified).
- `string? Reason { get; init; }` — The reason stated in the signature (not verified).
- `string? Location { get; init; }` — The location stated in the signature (not verified).
- `DateTimeOffset? SigningTime { get; init; }` — The signing time: the verified time of a document timestamp, otherwise the time the signer claims (the signed attribute or `/M`); the claim proves nothing without `PdfSignatureInfo.TimestampTime`.
- `DateTimeOffset? TimestampTime { get; init; }` — The time of a verified time-stamp token over the signature (B-T); null without one.
- `bool CoversWholeFile { get; init; }` — Whether the signature covers the file up to its end. False means later revisions follow (another signature, a document timestamp, form filling or other changes).
- `bool IsValid { get; init; }` — Whether the signature, its digest and a time-stamp token are valid.
- `bool? IsTrusted { get; init; }` — Whether the certificate chains to a trusted root of the read options; null when no trusted roots were given.
- `IReadOnlyList<string> ChainProblems { get; init; }` — The problems of the chain check (expired, untrusted root, revocation, …).
- `IReadOnlyList<string> Problems { get; init; }` — What is wrong with the signature; empty when it is valid.

#### PdfSignatureOptions

`sealed class PdfSignatureOptions` — the options of a digital signature (phase BJ, BJ02). The document is signed with the private key of the certificate (RSA); the signature is a detached CMS SignedData (RFC 5652) in the signature dictionary of the PDF. With `PdfSignatureOptions.Pades` the signature follows the PAdES baseline (ETSI EN 319 142-1, B-B): the signed attributes name the certificate of the signer (RFC 5035), the subfilter is `/ETSI.CAdES.detached` and the catalog holds a document security store (DSS) with the certificate. A timestamp (B-T/B-LT) needs a time stamp authority and is not part of it.

- `new PdfSignatureOptions(X509Certificate2 certificate)` — Creates the options with the certificate of the signer.
- `X509Certificate2 Certificate { get; }` — The certificate of the signer (with the private key).
- `string? Name { get; init; }` — The name of the signer (the reason is free text).
- `string? Reason { get; init; }` — The reason of the signature.
- `string? Location { get; init; }` — The place of the signature.
- `int ReservedSize { get; init; }` — The reserved size of the signature in bytes (default 8192, at most 1 MiB). The signature must fit into it; the rest is filled with zeros. A longer certificate chain needs more.
- `IReadOnlyList<X509Certificate2> AdditionalCertificates { get; init; }` — Further certificates of the chain (intermediate CAs, optionally the root) that are embedded with the signature, so a validator can build the path to a trusted root.
- `bool Pades { get; init; }` — Whether the signature follows the PAdES baseline (ETSI EN 319 142-1, B-B): the signed attributes carry the "signing certificate v2" (RFC 5035), the subfilter is `/ETSI.CAdES.detached` and the catalog holds a document security store with the certificate of the signer. Without a timestamp it is a B-B signature.
- `PdfValidationData? LongTermValidation { get; init; }` — optional validation material (OF-E03): certificates, OCSP responses and CRLs are written as a document security store with a VRI entry in an incremental update after the signature, which makes it a PAdES baseline B-LT signature. Requires `PdfSignatureOptions.Pades`.
- `PdfTimestampOptions? Timestamp { get; init; }` — optional time-stamp authority (OF-E03): a PAdES baseline B-T signature. The writer requests a timestamp token (RFC 3161) over the signature value and embeds it as the unsigned attribute `id-aa-signatureTimeStampToken`. The result still has the security store of B-B; the long-term validation data of B-LT is not part of it.

#### PdfSignatureReadOptions

`sealed record PdfSignatureReadOptions` — the settings for reading the signatures of a PDF.

- `new PdfSignatureReadOptions()`
- `static PdfSignatureReadOptions Default { get; }` — Default options: the signature is verified, the certificate chain is not evaluated.
- `string? Password { get; init; }` — The password of an encrypted PDF.
- `IReadOnlyList<X509Certificate2> TrustedRoots { get; init; }` — The trusted root certificates. Without them only the cryptographic signature is checked and a certificate is not judged trusted; with them the chain is built against exactly these roots.
- `X509RevocationMode RevocationMode { get; init; }` — How the chain checks revocation. Default is `X509RevocationMode.NoCheck`, because the library never calls the network.

#### PdfSignatureReader

`static class PdfSignatureReader` — reads and verifies the digital signatures and document timestamps of a PDF (ISO 32000, 12.8): the byte range, the CMS signature over its digest, an embedded time-stamp token and, with trusted roots, the certificate chain. The reader checks what the file proves; trust, revocation and the validation material stay with the caller, as for the DOCX and ODT signatures.

- `static IReadOnlyList<PdfSignatureInfo> Read(Stream input, PdfSignatureReadOptions? options = null)` — reads the signatures of a PDF. A file without a signature yields an empty list; a signature that does not verify is returned with `PdfSignatureInfo.IsValid` false and the reasons in `PdfSignatureInfo.Problems`.
- `static IReadOnlyList<PdfSignatureInfo> Read(byte[] pdf, PdfSignatureReadOptions? options = null)` — Reads the signatures of a PDF from its bytes.

#### PdfTimestampOptions

`sealed record PdfTimestampOptions` — a time-stamp authority for a PAdES baseline B-T signature (OF-E03, ETSI EN 319 142-1): the writer asks it for a timestamp token over the signature value and embeds the token as an unsigned attribute.

- `new PdfTimestampOptions()`
- `Func<byte[], byte[]>? Responder { get; init; }` — the transport of the request: it receives the DER of a `TimeStampReq` (RFC 3161) and returns the DER of the `TimeStampResp`. Use this to plug in any TSA client.
- `string? Url { get; init; }` — the URL of a TSA (RFC 3161 over HTTP). Ignored when `PdfTimestampOptions.Responder` is set.
- `string? Policy { get; init; }` — the policy the TSA should apply (RFC 3161 `reqPolicy`); null leaves the choice to the authority.

#### PdfValidationData

`sealed record PdfValidationData` — the validation material for a PAdES baseline B-LT signature (OF-E03, ETSI EN 319 142-1): the certificates, OCSP responses and CRLs that a validator needs later, when the signer's certificate has expired. It is written as a document security store in an incremental update, so the signature over the first revision stays intact.

- `new PdfValidationData()`
- `IReadOnlyList<X509Certificate2> Certificates { get; init; }` — The certificates of the chain (including the signer's).
- `IReadOnlyList<byte[]> OcspResponses { get; init; }` — The DER-encoded OCSP responses for the signer's certificate.
- `IReadOnlyList<byte[]> Crls { get; init; }` — The DER-encoded certificate revocation lists.

#### PdfViewerPreferences

`enum PdfViewerPreferences` — the window flags of the PDF viewer (OF-F02, ISO 32000-1 12.3.3).

- `None = 0` — no flag is written.
- `HideToolbar = 1` — the toolbar of the viewer is hidden.
- `HideMenuBar = 2` — the menu bar of the viewer is hidden.
- `HideWindowUi = 4` — the user interface of the viewer is hidden (only the document is shown).
- `FitWindow = 8` — the page is scaled to the window of the viewer.
- `CenterWindow = 16` — the page is centered in the window of the viewer.
- `FullScreen = 32` — the document is opened in full screen mode.

#### PdfWriteOptions

`sealed record PdfWriteOptions : WriteOptions` — Options for the PDF writer. Deliberately kept small and optional.

- `new PdfWriteOptions()`
- `static PdfWriteOptions Default { get; }` — The default options.
- `IReadOnlyDictionary<string, byte[]>? Fonts { get; init; }` — optional caller-provided TrueType fonts (font family → bytes), per family a file. If a text run's family is contained, the font is embedded (Unicode); bold and italic are produced synthetically. For real bold/italic files `PdfWriteOptions.FontCollection`. Without a matching font, a default font is used with WinAnsi are used.
- `FontCollection? FontCollection { get; init; }` — optional font collection with styles per family (normal, bold, italic, bold-italic). For a text run the matching style is embedded; only missing attributes are simulated synthetically. Is merged with `PdfWriteOptions.Fonts`; a family may appear in only one of the two specifications.
- `PdfAConformance PdfA { get; init; }` — Desired PDF/A conformance (phase R). Default: `PdfAConformance.None`.
- `IReadOnlyList<PdfAttachment>? Attachments { get; init; }` — optional attachments (phase R, R03). Require PDF/A-3 (or no PDF/A); with PDF/A-2 the writer raises an `ArgumentException`.
- `byte[]? IccProfile { get; init; }` — optional ICC output profile for PDF/A. If missing, a bundled a minimal sRGB profile is used.
- `PageTemplate? PageTemplate { get; init; }` — optional page template (phase S, S04): headers/footers, watermark and page frame. Headers/footers of the template apply to sections without their own header/footer; watermark and frame are drawn on every page.
- `PdfEncryptionOptions? Encryption { get; init; }` — optional encryption (phase AD, AD02): standard security handler with AES-256-CBC (R6). The output is not deterministic (random IVs). Encryption cannot be combined with PDF/A.
- `HyphenationPatterns? Hyphenation { get; init; }` — optional hyphenation patterns (phase AJ, BJ01). With patterns the writer breaks the words at the allowed positions and draws the hyphen; without them only the soft hyphens (U+00AD) in the text are break opportunities. The library ships no dictionary; the caller supplies the patterns of the language.
- `PdfSignatureOptions? Signature { get; init; }` — optional digital signature (phase BJ, BJ02). The document is signed with the private key of the certificate; the signature is a detached CMS SignedData in the signature dictionary of the PDF. A signed document is built in memory (the signature covers the written bytes) and cannot be encrypted.
- `IReadOnlyList<PdfAnnotation>? Annotations { get; init; }` — optional non-link annotations (OF-F01): sticky notes, highlights, stamps and free text boxes. An annotation whose page does not exist is reported and skipped.
- `PdfCommentMode Comments { get; init; }` — how the comments of the document are written (ADR-0026); `PdfCommentMode.None` by default.
- `PdfRevisionMode Revisions { get; init; }` — which state of the tracked changes the PDF shows (ADR-0027); `PdfRevisionMode.Final` by default.
- `PdfViewerPreferences ViewerPreferences { get; init; }` — optional window flags of the viewer (OF-F02). The flags are written into the `/ViewerPreferences` dictionary of the catalog.
- `PdfPageLayout PageLayout { get; init; }` — optional page layout of the viewer (OF-F02). Written as `/PageLayout` into the catalog; `PdfPageLayout.Default` writes nothing.
- `PdfPageMode PageMode { get; init; }` — optional mode the viewer opens the document in (OF-F02). Written as `/PageMode` into the catalog; `PdfPageMode.Default` writes nothing.
- `bool Kerning { get; init; }` — Applies the embedded fonts' kerning (the OpenType `kern` feature); true by default. False sets every glyph at its advance width, as PDFsharp and MigraDoc do. A run's `TextRun.Kerning` threshold still turns it off below that size.
- `bool Ligatures { get; init; }` — Applies the embedded fonts' standard ligatures (the OpenType `liga` feature, such as "fi"); true by default. The forms Arabic requires stay.

#### TextAnchor

`enum TextAnchor` — Which point of the text `PdfCanvasPage.DrawText` places at the given position. The line box runs from the ascender above the baseline to the descender below it (hhea of the font file; for a standard font the values of Arial, Times New Roman and Courier New).

- `BaselineLeft = 0` — Left end of the baseline.
- `BaselineCenter = 1` — Centre of the baseline.
- `BaselineRight = 2` — Right end of the baseline.
- `TopLeft = 3` — Top left of the line box (the baseline lies one ascender lower).
- `TopCenter = 4` — Top centre of the line box.
- `TopRight = 5` — Top right of the line box.
- `CenterLeft = 6` — Left end of the line box's vertical centre (half way between ascender and descender).
- `Center = 7` — Centre of the line box.
- `CenterRight = 8` — Right end of the line box's vertical centre.

## Reglet.Html

### Reglet

#### HtmlDocumentReader

`sealed class HtmlDocumentReader : IDocumentReader` — Reads HTML into the content model (ADR-0032): a tolerant subset reader. It maps text, headings, lists, tables, images from `data:` URIs and links; it never fetches anything and drops scripts and styles.

- `new HtmlDocumentReader()`
- `ReadResult Read(Stream input, CancellationToken cancellationToken = default)`

#### HtmlDocumentWriter

`sealed class HtmlDocumentWriter : IDocumentWriter` — Writes a `PortableDocument` as one self-contained UTF-8 HTML5 file (ADR-0032).

- `new HtmlDocumentWriter()`
- `Task<WriteResult> WriteAsync(PortableDocument document, Stream output, HtmlWriteOptions? options = null, CancellationToken cancellationToken = default)` — Writes the document as HTML to the stream.

#### HtmlWriteOptions

`sealed record HtmlWriteOptions : WriteOptions` — Options for the HTML writer (ADR-0032).

- `new HtmlWriteOptions()`
- `static HtmlWriteOptions Default { get; }` — The default options.
- `bool EmbedImages { get; init; }` — Whether images are written as `data:` URIs (default); false drops them with a report.

## Reglet.Fonts

### Reglet

#### FontCollection

`sealed class FontCollection` — an immutable collection of caller-provided fonts, grouped by family and style (normal, bold, italic, bold-italic). Family names are compared without regard case-insensitively compared; the empty name stands for the default font of text without its own family. Every change yields a new collection.

- `static FontCollection Empty { get; }` — An empty collection.
- `IReadOnlyCollection<string> Families { get; }` — The family names of the collection (the empty name stands for the default font).
- `int Count { get; }` — Number of styles across all families.
- `FontCollection Add(FontFace face)` — Adds a style under its own family name.
- `FontCollection Add(string family, FontFace face)` — Adds a style under a freely chosen family name (e.g. the name used in the document).
- `FontCollection Add(string family, FontFace face, bool bold, bool italic)` — adds a style with an explicitly specified style, for example when the information in the font file do not match.
- `FontCollection AddDefault(FontFace face)` — Sets the default font for text without its own family (family name "").
- `bool Contains(string family)` — Whether the family is contained in the collection.
- `static FontCollection FromDirectory(string directory)` — Loads all `.ttf` files of a directory (non-recursive) under their family name.
- `static FontCollection FromSystem()` — Loads the installed TrueType fonts from the standard font directories of the platform. Files that are not TrueType outlines, are corrupted or repeat an existing family/style are skipped; directories that cannot be read are ignored.
- `IReadOnlyList<FontFace> GetFaces(string family)` — The styles of a family (empty if the family is missing).
- `FontMatch? Match(string? family, bool bold, bool italic)` — chooses the best style of a family. Order: exact style; for bold-italic first bold, then italic; then normal; finally any style of the family. Missing attributes are marked as synthetic in the result.

#### FontEmbeddingPermission

`enum FontEmbeddingPermission` — embedding right of a font per OS/2 `fsType` (bits 0–3). If several bits are set, the least restrictive right applies.

- `Installable = 0` — Installable: embedding without restriction allowed.
- `Editable = 1` — Editable: embedding allowed; the document may be edited.
- `PreviewAndPrint = 2` — Preview and print: embedding allowed; the document may only be viewed and printed.
- `Restricted = 3` — Restricted license: the font must not be embedded.

#### FontFace

`sealed class FontFace` — a validated, caller-provided TrueType font file (one style, e.g. "Noto Sans Bold"). Supported are glyf-based TrueType fonts; CFF-based OpenType fonts (OTTO) and collections (TTC) are rejected. The instance is immutable and can be used in parallel.

- `string FamilyName { get; }` — Family name per font file (typographic family, otherwise name ID 1).
- `string StyleName { get; }` — Style name per font file, e.g. "Bold Italic".
- `string PostScriptName { get; }` — PostScript name per font file.
- `int Weight { get; }` — Weight (100–900; 400 = normal, 700 = bold).
- `bool IsBold { get; }` — Whether the file contains a bold style.
- `bool IsItalic { get; }` — Whether the file contains an italic or oblique style.
- `int GlyphCount { get; }` — Number of glyphs.
- `int UnitsPerEm { get; }` — Font design units per em (`head.unitsPerEm`).
- `int Ascent { get; }` — Ascender above the baseline in font units (`hhea.ascender`), as the PDF writer uses it.
- `int Descent { get; }` — Descender below the baseline in font units, as a positive value (`-hhea.descender`).
- `int LineGap { get; }` — Extra gap between lines in font units (`hhea.lineGap`).
- `FontEmbeddingPermission Embedding { get; }` — Embedding right per OS/2 `fsType`.
- `bool CanEmbed { get; }` — whether the outlines may be embedded: not for a restricted license and not if the font only allows bitmap embedding (fsType bit 9).
- `bool AllowsSubsetting { get; }` — Whether the font may be reduced to the used glyphs when embedding (fsType bit 8 not set).
- `bool CanRender(string text)` — Whether the font can render all characters of the text.
- `IReadOnlyList<int> GetMissingCodePoints(string text)` — The code points of the text without a glyph (deduplicated, in text order).
- `static FontFace Load(ReadOnlySpan<byte> data)` — Loads and validates a font from bytes. The bytes are copied.
- `static FontFace LoadFile(string path)` — Loads and validates a font from a file (`.ttf`).
- `bool Supports(int codePoint)` — Whether the font contains a glyph for the code point.
- `string ToString()`

#### FontMatch

`sealed record FontMatch` — Result of a font selection: the matching style and the attributes still to be simulated.

- `new FontMatch(FontFace Face, bool SyntheticBold, bool SyntheticItalic)` — Result of a font selection: the matching style and the attributes still to be simulated.
- `FontFace Face { get; init; }` — The chosen style.
- `bool SyntheticBold { get; init; }` — Bold is requested, but the style is not bold (is simulated).
- `bool SyntheticItalic { get; init; }` — Italic is requested, but the style is not italic (is simulated).

## Reglet.Hyphenation

### Reglet

#### HyphenationLanguages

`static class HyphenationLanguages` — the hyphenation patterns that ship with this package: the TeX patterns of the `hyph-utf8` project for several languages, unmodified. Pass the result to `PdfWriteOptions.Hyphenation`. The licences of the patterns are in `THIRD-PARTY-NOTICES.md` of the package.

- `static IReadOnlyList<string> Available { get; }` — The language keys of the package, for example `de-1996` and `en-US`.
- `static bool IsAvailable(string language)` — whether patterns exist for the language. Case does not matter; `de`, `de-DE` and `en` name the usual variant of the language.
- `static HyphenationPatterns Load(string language)` — Loads the patterns of a language (parsed once, then shared).

## Reglet.Xlsx

### Reglet

#### XlsxWorkbookReader

`sealed class XlsxWorkbookReader` — Reads an XLSX workbook into a `Workbook` (ADR-0043), narrow on purpose: sheets, cells with value, formula (and the value Excel cached for it), number format and style, merged ranges, column widths, row heights and the page setup with print area and repeated rows, PNG and JPEG images, theme and indexed colours (with tint) and the cells of shared formulas. Use `WorksheetCell.GetFormattedText` for the text Excel shows. Conditional formats, comments and the other features ADR-0043 leaves out are not read.

- `new XlsxWorkbookReader()`
- `Workbook Read(Stream input, CancellationToken cancellationToken = default)` — Reads a workbook from the stream; the stream stays open.

#### XlsxWorkbookWriter

`sealed class XlsxWorkbookWriter` — Writes a `Workbook` as an XLSX package (SpreadsheetML, ADR-0043): sheets, shared strings, styles, merged ranges, column widths, row heights, images, page setup, print area and repeated rows. Formulas are written without a cached value and Excel recalculates them on load.

- `new XlsxWorkbookWriter()`
- `Task<WriteResult> WriteAsync(Workbook workbook, Stream output, XlsxWriteOptions? options = null, CancellationToken cancellationToken = default)` — Writes the workbook to the stream.

#### XlsxWriteOptions

`sealed record XlsxWriteOptions : WriteOptions` — Options for the XLSX writer.

- `new XlsxWriteOptions()`
- `static XlsxWriteOptions Default { get; }` — Default options.

## Reglet.Validation

### Reglet

#### DocumentValidator

`sealed class DocumentValidator` — Checks a document file for what Word, Excel, LibreOffice or a PDF reader would reject or offer to repair (ADR-0044). The format is recognised from the content: DOCX, XLSX and other Office Open XML packages (package structure and the ECMA-376 Transitional schemas, with markup compatibility as Office 2019 understands it), ODF packages (mimetype, manifest, well-formed XML) and PDF (header, cross-references, trailer and catalog).

- `new DocumentValidator()`
- `const int MaxIssues` — The most findings one report collects.
- `ValidationReport Validate(Stream input, CancellationToken cancellationToken = default)` — Validates the file in the stream; the stream stays open.

#### ValidationIssue

`sealed record ValidationIssue` — One finding of the `DocumentValidator`.

- `new ValidationIssue(ValidationSeverity Severity, string Part, int? Line, string Message)` — One finding of the `DocumentValidator`.
- `ValidationSeverity Severity { get; init; }` — Error or warning.
- `string Part { get; init; }` — Where it is: a package part (`word/document.xml`), `[package]`, or a PDF object.
- `int? Line { get; init; }` — The line in the part, when it is XML and known.
- `string Message { get; init; }` — What is wrong.
- `string ToString()` — The finding as one line, such as `Error word/document.xml:12: …`.

#### ValidationReport

`sealed record ValidationReport` — The result of a validation (ADR-0044).

- `new ValidationReport(string format, IEnumerable<ValidationIssue> issues, bool truncated = false)` — Creates a report.
- `string Format { get; }` — The detected format: DOCX, XLSX, PPTX, OOXML, ODT, ODS, ODP, ODG, ODF or PDF.
- `IReadOnlyList<ValidationIssue> Issues { get; }` — The findings, in the order they were found.
- `bool Truncated { get; }` — Whether the validator stopped collecting at its limit (more findings exist).
- `bool IsValid { get; }` — Whether there is no error (warnings allowed).
- `IEnumerable<ValidationIssue> Errors { get; }` — The errors only.

#### ValidationSeverity

`enum ValidationSeverity` — How serious a validation finding is.

- `Warning = 0` — The file opens, but something may be lost or need a newer program (for example Office 2010 or later).
- `Error = 1` — A program may refuse the file or offer to repair it.
