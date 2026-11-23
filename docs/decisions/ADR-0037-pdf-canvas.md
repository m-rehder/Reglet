# ADR-0037: PdfCanvas, a PDF drawn at fixed coordinates

Status: Accepted (2026-11-23)

## Context

Technical data sheets are drawn, not laid out: text, thin lines, filled cells and drawings at fixed
coordinates, with line breaks and right alignment computed by the caller from measured widths. Expressing
them as floating shapes in the flow model is fragile (offsets count from column and paragraph, shapes in
table cells are not supported). The PDF writer already has every building block internally.

## Decision

- **A thin public layer over the flow renderer.** `PdfCanvas` records the drawing of each page; writing turns
  every page into a margin-less section holding one internal `CanvasBlock`, which the block renderer draws
  with the existing page renderer, font catalog and image manager. Metadata, font embedding and subsetting,
  PDF/A, encryption, signature, viewer settings and the `WriteResult` therefore work as for any document.
- **Coordinates as in XGraphics:** points, origin top left, y down. Only translation; no rotation or clipping.
- **Measurement goes through the writer's own font catalog** (built once on a null stream), so the measured
  width is the drawn width: same font resolution, shaping, kerning and standard-font fallback.
- **Anchors use the hhea line box**: TopLeft puts the baseline one ascender below the point, CenterLeft
  (ascender − descender) / 2 below it, BaselineLeft on it. Standard fonts use the hhea values of Arial,
  Times New Roman and Courier New.
- **Images are deduplicated by content (SHA-256)** and stretched into the rectangle.
- **Text is tagged as `P`, graphics as artifacts**, so the tagged PDF stays consistent and text extraction works.
- `PdfInspection` reports the fonts of a page's resource dictionary; Reglet shares one dictionary between
  pages, so each page lists all fonts of the document. Images are the distinct XObjects a page draws.
- `DocumentMetadata.Creator` is PDF-only; DOCX and ODT keep writing their own application name.

## Consequences

- A font handle registers its resource on first use; a standard font that is only measured is no longer
  written (a document with only embedded fonts no longer lists Helvetica). Resource names (`F1`, `F2`) can
  number differently than before.
- `FontFace.MeasureText` does not exist: shaping lives in `Reglet.Pdf`; the canvas measures before any page exists.
