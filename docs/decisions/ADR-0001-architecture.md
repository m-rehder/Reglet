# ADR-0001: Shared document model with a facade and three separate writers

- **Status:** Accepted
- **Date:** 2026-09-24
- **Context:** Phase A / first implementation day

## Context

Reglet is to produce DOCX, ODT and PDF — cross-platform and without third-party
dependencies. The three formats have very different technical foundations:

- **DOCX** is an OPC/ZIP package with WordprocessingML (ECMA-376).
- **ODT** is an ODF/ZIP package with ODF 1.3 XML.
- **PDF** is a binary object stream with its own cross-reference table (ISO 32000-1) and
  additionally needs its own page layout and font code.

## Decision

1. A **format-neutral, immutable document model** (`Reglet.Core`) is the single source of
   truth.
2. A small **facade/builder API** (`Reglet`) selects the format and registers the writers.
   The public API contains **no** backend types (no OOXML, ODF or PDF).
3. **Three separate writers** (`Reglet.Docx`, `Reglet.Odt`, `Reglet.Pdf`)
   produce each format directly according to its standard.
4. **Zero dependency:** no shipped project contains `PackageReference`, native libraries or
   third-party programs started at runtime; only the .NET Base Class Library is used. Optional
   `Reglet.Fonts` contracts allow caller-provided TrueType bytes — without bundled font
   files.
5. **Fonts as a separate package (addendum N11):** `Reglet.Fonts` contains the public font
   types (`FontFace`, `FontCollection`) and the TrueType parser. Dependencies:
   `Fonts → Core`, `Pdf → Core, Fonts`; DOCX and ODT remain without a font dependency, since
   they only name fonts. This way the parser exists only once, and real bold/italic files can be
   passed to the PDF writer without `Core` containing font code.

## Consequences

- The public API stays small and format-independent; a built document can be written to several
  formats in parallel.
- The **PDF writer** is the most involved part (own syntax writer, page layout, TrueType parser)
  and thus the biggest project risk; it is secured early via a spike (phase B).
- Format details stay internal, which makes breaking changes in the API rarer.
- Test frameworks must not be used via packages → own test runner (phase K).
