# ADR-0022: Drawing layer (vector shapes, text boxes)

- **Status:** Accepted (2026-11-13, product owner); not yet implemented
- **Date:** 2026-11-13
- **Context:** First item of "Out of scope" in the feature matrix; charts, barcodes and equations
  depend on it

## Context

The model has no drawing objects. A DOCX with `wps:` shapes or an ODT with `draw:custom-shape` keeps
them only through the preserving round trip; a document built from the model cannot contain a line, a
rectangle or a text box. The PDF writer already draws vectors, but only as the result of converting an
SVG image (a documented subset), and DOCX/ODT embed that SVG as a picture.

## Decision proposed

1. **Smallest useful model, not "all of DrawingML".** New block/inline type `DrawingShape` in `Core`
   with: kind (`Line`, `Rectangle`, `RoundRectangle`, `Ellipse`, `Polygon`/`Freeform` as a path of
   move/line/cubic segments, `TextBox`), size and position (inline or anchored like `ImageBlock`, same
   wrap kinds), fill (colour, opacity, optional gradient later), outline (colour, width, dash), rotation,
   alt text. A `TextBox` holds paragraphs and headings in version 1 (no lists, tables or images; the constructor rejects other blocks).
2. **Formats.** DOCX: `wps:wsp` written directly in `a:graphicData` without `mc:AlternateContent` (Word 2010+ and LibreOffice read it; Word 2007 does not);
   ODT: `draw:custom-shape` / `draw:line` / `draw:path` with `draw:text-box`; PDF: path operators and a
   clipped text area, reusing the existing SVG path renderer and the paragraph layout of table cells.
3. **Reuse before new code.** Paths share the path type of the SVG converter (moved to `Core` as an
   internal-to-public model type); reusable XObjects are a PDF-internal optimisation, not API.
4. **Reading.** Shapes written by the library are read back; foreign shapes stay preserved by the
   round trip and are reported as before.
5. **Boundaries.** No new project and no dependency; `Core` gets the model, `Docx`/`Odt`/`Pdf` the
   writers. Charts, barcodes, equations and connectors stay out until their own ADRs.

## Scope change needed

`scope.md` "Outside the current scope" and the feature matrix move *Vector drawing foundation* from
"Out of scope" to a new section "Drawing", and the Content row loses "shapes, text boxes". The
"Small formatting details" row (drawing the text effects in the PDF) becomes possible afterwards.

## Decisions of the product owner

- Text boxes are allowed in headers and footers from version 1 (letterheads). The PDF band renderer
  and the DOCX/ODT header parts must therefore handle `DrawingShape`.
- Version 1 has no gradients and no tables in text boxes (as proposed; the proposal was approved
  without change).
- Swift port: follows after the .NET acceptance gate (default of the proposal; not yet discussed).

## Consequences

- Estimate: L, not XL, because the SVG path renderer and the cell layout exist; the cost is the three
  writers and the readers, plus tests per format and a LibreOffice check for each shape kind.
- Word is not automated; DrawingML output is checked against the schema and LibreOffice only.
