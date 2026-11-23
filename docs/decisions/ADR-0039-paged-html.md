# ADR-0039: Paged HTML from the PDF writer's pages

Status: Accepted (2026-11-23)

## Context

A desktop application previews its documents in a web view while the user edits them and wants the preview to look like
the saved PDF: headers and footers with page fields, landscape sections, tables over several pages with repeated header
rows, page-anchored pictures, and data sheets drawn with `PdfCanvas`. The HTML writer of ADR-0032 is a simple editable
flow; making it page-faithful would mean a second layout engine that drifts from the PDF's.

## Decision

- **The PDF writer lays out, the HTML shows its pages.** `PdfDocumentWriter.WriteHtmlAsync` builds the PDF in memory
  (encryption and signature dropped) and converts each page's content stream into an SVG page. Page count, line and
  page breaks, headers, footers, fields and floating objects are therefore the PDF's by construction; the cost is one PDF
  write plus a linear pass over its content.
- **SVG per page**, because its model is the PDF's: paths with fill, stroke, dash, caps and joins, clip paths (intersected
  through `clip-path` on `clipPath`), images placed by a matrix, transparency. Gradients (`sh`) and pattern fills are
  approximated and reported; inline images are not shown.
- **Real text.** Each shown string becomes an SVG `text` in its font and size, with `textLength` set to the PDF's advance
  (`lengthAdjust="spacingAndGlyphs"`), so it ends exactly where it ends in the PDF even if a font differed. The text is
  written after the graphics in the reading order of `PdfDocumentReader` (lines top to bottom, left to right), with an
  invisible space where the reader inserts one, so selection, search and copy follow it. Text therefore lies on top of
  a picture placed in front of it.
- **Fonts**: a PDF font whose PostScript name is a face of the resolved font collection is embedded once as
  `@font-face` with a `data:` URI (the whole file; no subsetting yet); standard fonts map to Helvetica/Arial, Times and
  Courier. A face whose licence forbids embedding is already left out and reported by the PDF writer.
- **Images** are decoded back from the XObject (PNG or JPEG) and stored once in a shared `defs`, drawn with `use`.
- **Embedding (fragment)**: one root `div`, styles that select only `{prefix}` classes, ids and font families with a
  token from the document's hash, so two fragments and the host page do not affect each other.
- **Package boundary kept.** `Reglet.Html` stays dependent on Core only; the paged writer lives in `Reglet.Pdf` with its
  own `PdfHtmlOptions`, and `HtmlWriteOptions` gains nothing. The flow writer (ADR-0032) is unchanged, so the "more
  faithful flow HTML" request is not needed.

## Consequences

- Form fields and comment annotations (drawn by viewers from their appearance streams) and links are not in the HTML yet.
- Right-to-left text is written in the PDF's visual order with `textLength`; joining scripts may render unjoined.
