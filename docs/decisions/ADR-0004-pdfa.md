# ADR-0004: PDF/A archive formats (phase R)

- **Status:** Accepted
- **Date:** 2026-10-01
- **Context:** Phase R

## Context

Besides the standard PDF 1.7, the writer should optionally produce standards-conformant archive
PDFs (PDF/A). PDF/A forbids, among other things, the standard 14 fonts and requires an
OutputIntent with an ICC profile, XMP metadata and a document ID in the trailer. PDF/A-3
additionally allows embedded attachments.

## Decision

1. **Opt-in via `PdfWriteOptions.PdfA`** (`None`, `PdfA2b`, `PdfA3b`). Without a specification
   the output remains unchanged (PDF 1.7 without PDF/A components).
2. **Embedded fonts are mandatory.** In PDF/A mode every standard font (Helvetica/Times/Courier)
   triggers an `UnsupportedFeatureException`. A standard font can be provided via the key `""`
   in `PdfWriteOptions.Fonts`; it then also applies to headers/footers, list markers and page
   numbers.
3. **ICC profile generated at runtime.** `PdfIccProfile.Srgb()` builds a minimal sRGB profile
   (ICC v2, RGB display) from code. This way no binary file lies in the repository
   (zero dependency); an own profile can be passed via `PdfWriteOptions.IccProfile`.
4. **Trailer `/ID`** from a SHA-256 of the document metadata (stable for the same metadata).
5. **XMP (R04)** contains `pdfaid:part` (2/3) and `pdfaid:conformance` (B) and is consistent
   with the document metadata in title, author and language.
6. **Attachments (R03)** are written as `/EmbeddedFile`/`/Filespec` with `/AFRelationship`,
   linked in the catalog via `/AF` and `/Names /EmbeddedFiles`. PDF/A-2 rejects attachments
   (ArgumentException); PDF/A-3 is intended for this.
7. **Validator (R01/R05)** is optional: if `REGLET_VERAPDF` is set to the veraPDF CLI, the
   `PDF/A` suite validates the produced fixtures; otherwise the step is skipped (as with the
   visual regression).

## Consequences

- PDF/A is a deliberate output option; the standard output and the golden tests remain
  unaffected.
- The public API grows by `PdfAConformance`, `PdfAttachment` and the options `PdfA`,
  `Attachments`, `IccProfile`.
- The validator is not part of the offline build; the structural check is done in the own test.

## Limits

- The supplied ICC profile is a minimal sRGB profile (gamma approximation), not a certified
  profile. For the highest demands an own profile can be passed.
- PDF/A-1 (based on PDF 1.4) and the Unicode variants (2u/3u) are not offered.
