# ADR-0032: HTML as a document format (reader and writer)

- **Status:** Accepted (2026-11-18, product owner: "conversion engine: only HTML", details chosen by the implementer: recommended defaults)
- **Date:** 2026-11-18
- **Context:** "Conversion engine and formats" row of "Out of scope" (HTML, Markdown, TXT, RTF, XPS, DOC, flat OPC, images,
  layout-faithful conversions, HTML→PDF; XL). The product owner narrowed it to **HTML**. The library converts between its
  formats through the content model (`DocumentReader` → `PortableDocument` → `DocumentWriter`), so HTML as a *format* gives
  HTML→PDF, HTML→DOCX, HTML→ODT and back without a separate engine.

## Decision

1. **A new format `DocumentFormat.Html`** (`.html`, `.htm`) with a reader and a writer in a new project `Reglet.Html`.
   No dependency: the tokenizer and tree builder are written here. The facade (`DocumentReader`, `DocumentWriter`,
   `DocumentFormatExtensions`) learns the format; the existing formats do not change.
2. **Reader: tolerant, subset, semantic.** It reads HTML5 as a browser tokenizes it for the supported elements (implied end
   tags of `p`, `li`, `td`, `tr`; void elements; character references: numeric and the common named ones; attribute values
   quoted or not; comments, doctype and processing instructions skipped). It builds a tree without recursion and maps it:
   - blocks: `p`, `div` (as a paragraph when it holds text), `h1`–`h6` (headings), `ul`/`ol`/`li` (lists, nested up to the
     model limit; `start`, `type` of `ol` ignored), `table`/`tr`/`td`/`th` (`colspan`, `rowspan`; a `th` row in `thead` or a
     first row of only `th` is a header row), `pre` (a paragraph with line breaks and a monospaced font), `blockquote` (an indented
     paragraph), `hr` (a paragraph border is **not** modelled: it is skipped), `img` (a block after its paragraph);
   - inline: `b`/`strong`, `i`/`em`/`cite`, `u`, `s`/`strike`/`del`, `sup`/`sub`, `code`/`kbd`/`samp`/`tt` (monospaced), `a href`,
     `br` (a line break), `span` and `font` with the style properties `color`, `background-color`, `font-size` (pt, px, em
     against the default size), `font-family`, `font-weight`, `font-style`, `text-decoration`; `style` on block
     elements: `text-align`, `margin-left` (as indent);
   - ignored with their content: `script`, `style`, `head` (except `title`, read as the document title), `noscript`,
     `template`, `iframe`, `object`, `embed`, `video`, `audio`, `canvas`, `svg` (reported once per kind), form controls
     (reported once); unknown elements are transparent (their content is read).
   - Whitespace is collapsed as in a browser (outside `pre`).
3. **Security.** The reader never fetches anything (no network, no files): an `img` is read only from a `data:` URI of a
   supported type (PNG, JPEG, SVG within the existing limits); any other `src` is reported and replaced by its `alt` text.
   Links keep their target only when `ReaderGuard.IsAllowedLinkTarget` allows it (no `javascript:`). Event handler
   attributes and `style` content other than the properties above are ignored. Limits: input size, element depth 256
   (further nesting is flattened), text length and block count as for the other formats.
4. **Encoding.** A byte order mark wins; otherwise a `<meta charset>` or `http-equiv` in the first 1,024 bytes (UTF-8,
   ISO-8859-1/Windows-1252, UTF-16 are known; another name is reported and UTF-8 is used); the default is UTF-8.
5. **Writer: one self-contained, valid HTML5 file.** UTF-8, `<meta charset>`, `<title>` from the metadata, a small `<style>`
   (body font, table borders, page break class), then the body. Supported: paragraphs with alignment and indent, headings,
   lists (nested, bullet/ordered), tables (spans, header rows, cell background), images as `data:` URIs with `alt`,
   hyperlinks, text formatting (bold, italic, underline, strike, sup/sub, color, background, font family and size, small
   caps, all caps), line breaks, tabs (as spaces in a `white-space: pre` span), page break (`<div style="page-break-after:always">`),
   bookmarks (`id`), the fields that have a text (number, date) as their current text. Everything else is **reported and
   omitted**: charts, shapes, diagrams, equations, forms, OLE objects, barcodes (shapes), comments, tracked changes (the
   inserted text stays, deleted text is dropped), footnotes (written as a numbered list at the end), headers and footers
   (dropped), a table of contents (written as its entries). Equations as MathML are possible later.
6. **Options.** Reader: none in version 1. Writer: `HtmlWriteOptions` with `EmbedImages` (default true; false drops
   images with a report).
7. **Swift port.** Same model, parser and writer; HTML written by .NET and Swift is byte-identical in the cross-parity
   job; the reader parity compares models read from a small HTML corpus.

## Consequences

- HTML→PDF/DOCX/ODT is a two-line call; the quality is that of the subset above, **not** a browser: no CSS layout, no
  floats, no `display`, no external style sheets, no classes.
- Reading HTML written by a word processor ("Save as web page") gives text, headings, lists, tables and images but not the page
  layout.
- The writer's output is meant to be read and edited as simple HTML, not as a faithful rendering.

## Not decided here

Markdown, TXT, RTF, XPS, DOC, flat OPC, image formats (EMF/WMF/TIFF/GIF/BMP), CSS classes and style sheets, external
resources, MathML and SVG output, HTML templates, a layout-faithful conversion.
