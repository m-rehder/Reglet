# Reglet – Release notes

## 1.0.0

The first public release. Reglet creates, reads and converts DOCX, ODT, PDF and HTML documents in .NET 10 with no
dependencies beyond the base class library.

### Documents

- One format-neutral model (`PortableDocument`) with a fluent builder; DOCX, ODT, PDF and HTML writers and readers.
- Text: character formatting (fonts, sizes, colours, highlight, underline styles, strikethrough, sub- and superscript,
  small capitals, spacing, effects), paragraph formatting (alignment, indents, spacing, line spacing, borders, shading,
  tab stops, keep rules), named paragraph and character styles, lists with numbering definitions, links and bookmarks,
  right-to-left scripts (Unicode bidirectional algorithm), hyphenation (optional package).
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
