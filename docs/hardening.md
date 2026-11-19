# Hardening and operational safety (phase J)

## Package paths (J04)

Internal ZIP entry names are checked via `PackagePath.Validate`. Absolute paths, drive
specifiers and parent traversal (`..`) are rejected (`DocumentValidationException`) so that a
package cannot break out of the target tree.

## Resource limits (J05/J06)

`WriteOptions.Limits` (`ResourceLimits`) limits image size (`MaxImageBytes`,
`MaxImagePixels`), blocks (`MaxBlocks`), table cells (`MaxTableCells`), text length
(`MaxTextLength`) and the nesting depth of tables, lists and notes (`MaxNestingDepth`, 256 by
default; deeper content would overflow the stack and end the process). Headers and footers count.
Exceedances lead to an `ImageException` or `DocumentValidationException` before appreciable memory
is consumed.

## Foreign input when reading

The readers treat every file as untrusted:

- **Compression bombs:** source files are limited to 512 MiB (seekable) or 256 MiB
  (non-seekable) before parsing; ZIP entries
  (DOCX/ODT), decoded PDF streams and the joined content of a PDF page are limited to
  256 MiB; a text run in ODT to `ResourceLimits.Default.MaxTextLength` characters. ToUnicode
  ranges are trimmed to the readable 2-byte codes.
- **Cycles and depth:** PDF page trees are checked for cycles and a depth of 256; object
  streams must not themselves be compressed. A stack overflow, which would terminate the
  process, is thereby excluded.
- **Style chains:** DOCX `w:basedOn` and ODT `style:parent-style-name` chains are resolved once
  per style, iteratively and memoized, so a file with tens of thousands of chained styles reads in
  linear time without a deep recursion; a missing or cyclic parent ends the chain.
- **PDF object streams:** all object streams of a file may decode to at most 512 MiB in total.
- **Infinite loops:** the PDF lexer advances by at least one byte per token.
- **Error types:** damaged files lead to a `DocumentValidationException` (or
  `UnsupportedFeatureException` for deliberately unsupported features), not to arbitrary
  runtime exceptions such as `IndexOutOfRangeException` or `XmlException`.
- **XML:** DTDs and external entities are disabled (no XXE). Foreign XML may be nested at most
  1024 levels deep, because the cost of loading grows with the square of the depth.
- **Hyperlinks:** DOCX and ODT readers adopt only `http`, `https`, `mailto`, `#bookmark` and relative
  targets; any other scheme or network path is dropped with a warning and the text stays.

**Templates:** binding may add at most `ResourceLimits.MaxTemplateCharacters` characters (256 MiB by
default), counted over bound values and repeated regions or rows; invalid XML characters in row values
follow `InvalidXmlCharacters`. Conditions, regions and placeholders are resolved in linear time.

`DocumentSearch` limits a regular expression to one second per match step, so a backtracking pattern
ends with a `DocumentValidationException`.

## Cancellation (J07)

The `CancellationToken` is checked at defined points: DOCX per section and per image, ODT per
section and per image, PDF per section and per block. A cancelled job writes no output into a
stream.

## Temporary files (J08)

For a stream, output is written directly into the stream. For a path, the document is written
into a temporary file next to the target (`.<name>.<random>.tmp`) and moved over the target at
the end. A failed write (validation error, cancellation, full disk) therefore neither leaves a
half-written file behind nor destroys an existing file at the target path; the temporary file
is deleted.

## Non-seekable targets

Seekable targets (files, `MemoryStream`) are written directly; a PDF may thereby also begin
after existing data, the xref offsets count from the start of the PDF. Non-seekable targets
(e.g. an HTTP response body) are built in all three formats in a **memory buffer**
(`MemoryStream`, no temporary file) and then transferred with `CopyToAsync`. This also works
with streams that forbid synchronous writes (ASP.NET Core, `AllowSynchronousIO = false`). An
error or cancellation during the build leaves the target stream unchanged.

## Concurrency (J09)

The writers hold **no global state**; object registries, resource management, font caches and
image management are per call. Several documents can therefore be written in parallel and even
simultaneously to different formats; the output is identical to the sequential case.

## Error texts (J10)

Error messages and warnings name the affected format (prefix `DOCX:`, `ODT:`, `PDF:`) and the
feature or the reason, but contain **no** document content (no texts, no metadata values).
Example: `PDF: characters outside WinAnsi are replaced with '?'.`
