# ADR-0008: Converter and fidelity boundaries (phases AC and beyond)

- **Status:** Accepted
- **Date:** 2026-10-01
- **Context:** Phases AC and the remaining roadmap items

## Context

Several requested features are either out of the model's scope or need machinery that the
zero-dependency, format-neutral core cannot carry:

- **SVG in DOCX/PDF:** ODT embeds SVG natively (`image/svg+xml`). DOCX and PDF have no SVG
  container; a complete SVG renderer needs a rasterizer (texts, gradients, patterns, clipping
  paths, filters) that the BCL does not provide.
- **DOCX↔PDF and HTML/RTF→PDF:** a faithful conversion needs a shared layout engine and
  font metrics that reproduce the source's line breaking and pagination. This is a conversion
  engine with quality promises; [scope.md](../scope.md) already excludes one.
- **Lossless round trip:** reading reconstructs a subset of the model; unknown constructs are
  preserved only through templates, not normalised.
- **PDF page extraction/merge:** the writer builds a fresh object graph; extracting a page
  range from an existing PDF needs deep copying of the page tree, content streams and shared
  resources (fonts, images) — a PDF manipulation library, not a text/extraction reader.
## Decision

1. **SVG is supported in all three formats within a documented subset (amended 2026-11-03 and 2026-11-05).**
   ODT embeds it natively. DOCX embeds the SVG as a media part and references it through the
   SVG extension of the picture. The caller may supply a PNG or JPEG raster fallback; without
   it, older Word versions may show nothing (reported). PDF converts the supported subset (paths,
   basic shapes, simple text, transformations, colors, element and group opacity, stroke dash
   patterns with offsets, line caps, joins and miter limits, `use` references, rules of a `style`
   element whose selector names an element, a class or an id, selected gradients,
   patterns and clipping, base64-embedded PNG/JPEG images, masks as luminance soft masks, markers
   at the start and end of a path, and strokes painted with a gradient or a pattern) into vector
   graphics; complex text, unsupported gradients and patterns, complex clipping paths, conditional
   style groups (`@media`), filters and markers at inner vertices are reported, never silently
   dropped. A group with its own opacity is painted as a transparency group (form XObject), so its
   children composite as one unit. A caller who needs the full SVG can rasterise it outside the library and
   pass the raster image.
2. **DOCX↔PDF, HTML/RTF→PDF stay out of scope** (see [scope.md](../scope.md)): the supported
   conversion path is through the format-neutral model (DOCX↔ODT, PDF→DOCX/ODT text).
3. **A lossless round trip is not promised.** The feature matrix names the fidelity limits;
   preservation is available via templates.
4. **PDF page extraction/merge is implemented** (`PdfPageOperations.Extract`/`Merge`). The
   reachable object graph is copied, inherited page attributes (`/Resources`, `/MediaBox`,
   `/CropBox`, `/Rotate`) are pushed down and the page tree is rebuilt; outlines, forms,
   names and metadata are not carried over.
5. **Output streaming is implemented (U01).** Seekable targets (files, memory streams) are
   written without a full intermediate buffer and `WriteOptions.StreamNonSeekableOutput` writes
   directly to non-seekable targets; the *document model* is built in memory, which is inherent
   to the build-then-write API.

## Consequences

- The feature matrix names these as boundaries instead of "open"; no
  false completeness is claimed.
- The library stays dependency- and data-free; callers can pre-process (rasterise, merge) with
  external tools.
- The boundaries are reversible: they are converter/fidelity features, not data losses in the
  supported model.
