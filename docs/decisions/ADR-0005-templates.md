# ADR-0005: Templates and placeholders (phase S)

- **Status:** Accepted
- **Date:** 2026-09-25
- **Context:** Phase S

## Context

Phase S adds templates: design and placeholders should come from an existing file without
weakening the express no to a lossless round trip. A template is an existing DOCX or ODT file
with named markers; the writer should fill it while preserving styles and layout. PDF knows no
template file, but needs recurring page design (header/footer, watermark, page frame).

At the same time, the architecture forbids backend types in the public API (E09) and the
zero-dependency promise forbids packages. Core must therefore carry neither OOXML/ODF/PDF nor
ZIP/XML types outward.

## Decision

1. **Templates are format-specific and file-based, not model-based.** DOCX and ODT receive their
   template as immutable bytes (`DocumentTemplate.FromBytes`). The respective writer patches the
   contained `word/document.xml` or `content.xml` in place and writes a new package. The template
   is **not** read into the document model; this avoids a round-trip promise.
2. **Placeholders are named `{{Name}}`.** The delimiters (`{{`, `}}`) and `DocumentTemplate.Marker`
   live in Core so that the DOCX and ODT writers use the same syntax. Names are compared ordinally
   (case-sensitive) and must not contain braces.
3. **Bindings are format-neutral model content.** `TemplateContent` is either inline content
   (stays in the surrounding paragraph, e.g. `{{Date}}`) or block content (its own paragraph area,
   e.g. `{{Items}}`). Both are adopted as a snapshot. The document itself provides its body via an
   optional body placeholder (`DocumentTemplate.Body`).
4. **Page design is its own, model-based template.** `PageTemplate` optionally bundles header,
   footer, `Watermark` and `PageFrame`. It is not file-based and is attached via the options of the
   PDF writer (S04).
5. **No backend types.** Both template kinds use only BCL primitives (`ReadOnlyMemory<byte>`,
   strings, `Length`, `Color`); the API surface check remains green unchanged.
6. **Optional typed placeholders.** A template may declare text, ISO date, invariant decimal
   number, image or field for a name. Bindings and repeated-row values are checked when supplied,
   including when a type is declared after a binding. Undeclared names retain the existing behavior;
   missing values keep the existing diagnostics. A body placeholder cannot be typed.
7. **Repeated regions.** `{{Repeat:Name}}` and `{{EndRepeat}}` as standalone paragraphs or
   table rows delimit a region expanded once per row supplied through `RepeatRegion`. Regions
   may nest and use either document-wide named data sets or per-parent data in
   `TemplateRegionRow`. A per-parent data set overrides a shared one, including when empty;
   an absent data set falls back to the shared one. Inserted values remain literal.
   Simple embedded images and local bookmarks can be duplicated with distinct identifiers;
   links within each copy point to that copy's bookmarks. Unsupported content whose
   duplication cannot be made safe is reported instead of cloned.

## Staged implementation

| Stage | Content | Status |
| --- | --- | --- |
| S01 | Contracts in Core (`DocumentTemplate`, `TemplateContent`, `PageTemplate`, `Watermark`, `PageFrame`) | implemented |
| S02 | DOCX: bind placeholders in `word/document.xml`, preserve styles | implemented |
| S03 | ODT: bind placeholders in `content.xml`, preserve styles | implemented |
| S04 | PDF: `PageTemplate` for header/footer, watermark and page frame | implemented |
| S05 | Golden tests for styles and binding | implemented |

## Consequences

- The writers gain an additional, opt-in input path. Without a template the output remains
  unchanged.
- The public API grows by `DocumentTemplate`, `TemplateContent`, `TemplateContentKind`,
  `PageTemplate`, `Watermark` and `PageFrame`.
- The template is a deliberate design source per format; responsibility for matching styles
  remains with the caller or the author of the template.

## Limits
- A template is not an arbitrary import format. Unsupported template content is reported by the
  respective writer (warning or error depending on `UnsupportedContent`), not silently adopted.
- There is no automatic merging of template styles into the document model; template styles
  remain a matter of the backend file and are carried forward unchanged.
- **DOCX (S02):** `word/document.xml` (body and table cells) and the header/footer parts it
  references are bound. Placeholders in footnotes are not yet bound. If a template has a section break in the
  placeholder paragraph, it is preserved and the content is inserted before it.
- **ODT (S03):** `content.xml` (body and table cells) and the header/footer styles of the master
  pages in `styles.xml` are bound. Automatic styles are added for inserted content; colliding
  style names are renamed. Unformatted inserted text inherits the text style of the template
  span; formatted inserted text uses the generated style and does not additionally inherit the
  remaining template properties.
- **Several sections:** the body placeholder receives the blocks of all sections merged (with a
  warning); the template's section layout is thereby not applied per section.
