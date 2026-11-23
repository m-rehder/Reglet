# Architecture Decision Records

The individual records in this directory are the binding source for each decision: context,
decision, consequences and the limits the decision accepts. This page is only the index. What is
implemented, the known limits, the open work and what is out of scope are kept in one place, the
[feature matrix](../feature-matrix.md).

| ADR | Phase | Decision |
| --- | --- | --- |
| [0001](ADR-0001-architecture.md) | A | One immutable, format-neutral model, a facade without backend types, three separate writers, zero dependencies; fonts as their own package |
| [0002](ADR-0002-text-shaping-bidi.md) | P | Own OpenType shaper in the PDF writer, UAX #9 bidi in Core, CJK subsetting |
| [0003](ADR-0003-accessibility.md) | Q | Tagged PDF with an own structure tree; DOCX/ODT carry language and structure through styles |
| [0004](ADR-0004-pdfa.md) | R | PDF/A-2b and PDF/A-3b as an opt-in, runtime ICC profile, embedded fonts mandatory |
| [0005](ADR-0005-templates.md) | S | File-based DOCX/ODT templates with `{{Name}}` placeholders, model-based `PageTemplate` for PDF |
| [0006](ADR-0006-encryption-signatures.md) | AD | Protection, encryption and signatures with BCL cryptography only; trust and validation material stay with the caller |
| [0007](ADR-0007-layout-boundaries.md) | AA/AE | PDF image wrap and hyphenation without shipped data; the caller supplies patterns |
| [0008](ADR-0008-converter-boundaries.md) | AC+ | SVG as a documented subset, no layout-faithful conversion, no fully modelled lossless round trip |
| [0009](ADR-0009-numbering-definitions.md) | M2 | Shared, named numbering definitions; one definition is one continuation |
| [0010](ADR-0010-run-formatting-in-styles.md) | M2 | Tri-state run formatting and one resolution chain: defaults → paragraph style → character style → run |
| [0011](ADR-0011-paragraph-style-reference.md) | M2 | Body paragraphs keep their paragraph style reference; writers resolve style edits and state only direct formatting |
| [0012](ADR-0012-tri-state-break-flags.md) | M2 | Keep-with-next, keep-together and page-break-before are tri-state; explicit offs survive DOCX/ODT |
| [0013](ADR-0013-tri-state-alignment.md) | M2 | Paragraph alignment is tri-state; an explicit left overrides an inherited centered/right style |
| [0014](ADR-0014-tri-state-indents.md) | M2 | Paragraph left/right/first-line indents are tri-state; an explicit zero overrides an inherited indent |
| [0015](ADR-0015-tri-state-spacing.md) | M2 | Paragraph space before/after are tri-state; an explicit zero overrides an inherited spacing |
| [0016](ADR-0016-tri-state-borders.md) | M2 | Paragraph borders are tri-state; `ParagraphBorders.None` overrides inherited borders |
| [0017](ADR-0017-tri-state-direction.md) | M2 | Paragraph direction is tri-state; an explicit left-to-right overrides an inherited RTL style |
| [0018](ADR-0018-tri-state-tab-stops.md) | M2 | Paragraph tab stops are tri-state; an empty list clears inherited stops |
| [0019](ADR-0019-odt-encryption.md) | FU | ODT package encryption with ODF 1.3 AES-256-CBC, PBKDF2 and manifest encryption data |
| [0020](ADR-0020-odt-signatures.md) | FU | ODT package signatures as XMLDSig in `META-INF/documentsignatures.xml` |
| [0021](ADR-0021-embedded-fonts.md) | FU | Fonts embedded in DOCX and ODT; `Docx` and `Odt` may reference `Fonts` |
| [0022](ADR-0022-drawing-layer.md) | – | Accepted: drawing layer (shapes, text boxes, also in headers and footers) |
| [0023](ADR-0023-barcodes.md) | – | Accepted: barcodes as freeform vector shapes from own encoders (Code 128, EAN, QR) on the drawing layer |
| [0024](ADR-0024-form-fields.md) | – | Accepted: form fields (PDF AcroForm first, then DOCX content controls and ODT form controls) |
| [0025](ADR-0025-charts.md) | – | Accepted: charts (own PDF vector rendering, native DOCX chart part without workbook, ODT chart object) |
| [0026](ADR-0026-comments.md) | – | Accepted: comments modelled in DOCX and ODT (`Comment` with start/end markers, replies, resolved), opt-in PDF text annotations |
| [0027](ADR-0027-revisions.md) | – | Accepted: revisions as marker pairs (`TrackedChange`, insertions and deletions), accept/reject API, PDF modes `Final`/`Original`/`Markup` |
| [0028](ADR-0028-vba-preservation.md) | – | Accepted: VBA projects preserved in DOCX as opaque bytes (`MacroProject`), read only with `KeepMacros` (default off), never executed |
| [0029](ADR-0029-embedded-objects.md) | – | Accepted: embedded OLE objects as an inline `EmbeddedObject` (opaque bytes, PNG/JPEG preview) in DOCX and ODT, never opened or run |
| [0030](ADR-0030-flow-diagrams.md) | – | Accepted: flow diagrams as a `Diagram` block (nodes with fixed positions, drawn connectors with arrowheads, no glue, no auto layout), expanded to `DrawingShape`s |
| [0031](ADR-0031-equations.md) | – | Accepted: equations as a display `Equation` block of a math tree (text, row, fraction, script, radical, n-ary, delimiter); DOCX OMML, ODT MathML object, PDF own layout |
| [0032](ADR-0032-html.md) | – | Accepted: HTML as a format (`DocumentFormat.Html`, new project `Reglet.Html`): tolerant subset reader, self-contained writer, no network, no dependency |
| [0033](ADR-0033-docx-pdf-conversion.md) | – | Accepted: DOCX↔PDF: opt-in structure recovery in the PDF reader (paragraphs, headings, lists, tables, images, headers/footers), measured DOCX→PDF fidelity against LibreOffice |
| [0034](ADR-0034-pdf-formula-recovery.md) | – | Accepted: formula recovery from PDF (opt-in): fractions, radicals, scripts, n-ary operators from geometry and rules |
| [0035](ADR-0035-reglet-1.0.md) | – | Accepted: publication as Reglet 1.0.0 (name, logo, own examples, clean history) |
| [0036](ADR-0036-no-stale-parts.md) | – | Accepted: a changed document carries no removed content, no foreign package, no macros and no stale signature of its source |
| [0037](ADR-0037-pdf-canvas.md) | – | Accepted: PdfCanvas draws PDF pages at fixed coordinates over the flow renderer; measurement through the writer's font catalog |
| [0038](ADR-0038-float-anchors.md) | – | Accepted: floating images and shapes anchored to page, margin, column or paragraph, with an alignment instead of an offset |
| [0039](ADR-0039-paged-html.md) | – | Accepted: paged HTML converts the PDF writer's pages to SVG with real text and embedded fonts; lives in Reglet.Pdf |
| [0040](ADR-0040-migradoc-parity.md) | – | Accepted: fields and empty lines take their paragraph's font, floats can do without an anchor line, cell padding per axis and a table indent |
| [0041](ADR-0041-migradoc-paging.md) | – | Accepted: rows kept on one page, inline images, breaks after hyphens, fixed column widths, header distance and placement, kerning and ligatures off |

A new ADR is added to this table; a change of a status goes into the feature matrix, not here.
