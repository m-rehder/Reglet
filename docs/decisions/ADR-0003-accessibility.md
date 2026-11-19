# ADR-0003: Accessibility and tagged PDF (phase Q)

- **Status:** Accepted
- **Date:** 2026-10-01
- **Context:** Phase Q

## Context

Resources should be usable for screen readers and assistive technology. For this, produced
documents must be structured and marked up:

- **PDF** needs a tagged PDF structure tree (`/StructTreeRoot`), marked content with `/MCID`,
  alternative texts for figures and metadata (title, language).
- **DOCX** and **ODT** need heading outline, marked header rows, alternative texts and language
  markup.

## Decision

1. **Own structure tree builder in the PDF writer** (`PdfStructureTree`). It produces
   `/StructTreeRoot` with a `/Document` element and the roles `P`, `H1`–`H6`, `Table`, `TR`,
   `TH`/`TD` and `Figure`. The content is enclosed with `/MCID` marked content; the
   `/ParentTree` links marked content and structure elements. Pages carry `/StructParents`.
2. **Container vs. content:** containers (table, row, cell) carry child elements, content
   elements (paragraph, heading, figure) carry an MCR reference to their marked-content
   sequence. Headers and footers are marked as `/Artifact`.
3. **Paragraphs across page boundaries:** marked content must not span a page boundary; a
   paragraph is therefore continued per page as its own `P` element.
4. **Metadata:** catalog with `/MarkInfo <</Marked true>>`, `/ViewerPreferences
   <</DisplayDocTitle true>>`, `/Lang` and an XMP package (title, author, language, producer).
5. **DOCX/ODT** remain without their own structure engine: Word and LibreOffice produce the
   structure from the style and language information. Added were `w:lang` (DOCX) and
   `fo:language`/`fo:country` (ODT); headings, header rows (`w:tblHeader`,
   `table:table-header-rows`) and alternative texts were already present.

## Consequences

- The public API does not change; accessibility is part of the output.
- The PDF content stream contains additional marked-content operators; the text content remains
  unchanged.
- The accessibility audit (`AccessibilityChecks`, Q05) runs in CI via the compatibility checks on
  all three operating systems.

## Limits

- Full **PDF/UA** conformance (e.g. `pdfuaid:part`, `Table` `/Headers`, `Scope`) is not assured;
  the check is done offline-structurally, not with an external PDF/UA validator (e.g. veraPDF).
  The metadata needed for this (title, language, display title) is set.
- Alternative texts are output for figures; there is not yet an artifact marking for decorative
  images.
