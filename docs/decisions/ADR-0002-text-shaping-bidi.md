# ADR-0002: Text shaping for complex scripts and bidirectional text (phase P)

- **Status:** Accepted
- **Date:** 2026-10-01
- **Context:** Phase P

## Context

Phase P requires correct composition of Arabic, Hebrew, CJK and bidirectional text. This is
significantly more involved than the previous text processing: the PDF writer currently encodes
text character by character via the `cmap` and measures widths via `hmtx`. That suffices for
Latin but is wrong for complex scripts:

- **Ligatures and contextual forms** (Arabic `init`/`medi`/`fina`/`isol`, Latin `liga`) replace
  or merge glyphs. This is in the OpenType tables `GSUB`/`GPOS`.
- **Kerning and mark positioning** shift glyphs and are in `GPOS`.
- **Bidirectional text** (UAX #9) must be wrapped and drawn in the correct direction.
- **Zero dependency** forbids HarfBuzz, ICU or other third-party libraries.

## Decision

1. **Own, lean OpenType shaper in the PDF writer** (`Reglet.Pdf`). It reads `GSUB`, `GPOS`
   and `GDEF` itself and applies the lookup types needed for the target fonts. Other writers are
   unaffected: Word and LibreOffice shape text from DOCX/ODT themselves; there only direction
   attributes are set.
2. **Bidirectional algorithm (UAX #9)** lives as an internal helper in `Reglet.Core` so
   that all writers can determine the same base direction. It works with a compiled bidi class
   table (generated from the Unicode data, as C# source in the repository — no runtime
   dependency, no bundled binary).
3. **Fallback instead of silent data loss:** if an embedded font lacks a needed table or glyph,
   this is reported via the existing `UnsupportedFeatureHandler` (warning or error depending on
   the option).
4. **Standard fonts remain unchanged** (WinAnsi, no shaping tables). Shaping applies only to
   embedded TrueType fonts with matching tables; golden tests with standard fonts thereby remain
   stable.

## Staged implementation

| Stage | Content | Status |
| --- | --- | --- |
| P-1 | Parse and validate the OpenType tables `GSUB`/`GPOS`/`GDEF` (P01) | implemented |
| P-2 | GSUB single/ligature substitution, GPOS kerning (P02) | implemented |
| P-3 | Arabic joining and contextual forms (P03) | implemented |
| P-4 | Bidi UAX #9 with a compiled class table (P04) | implemented |
| P-5 | CJK font subsetting (P05) | implemented |
| P-6 | Composition comparison with tolerance (P06) | implemented (K11: `ImageDiff`/`VisualRegressionChecks`) |

## Consequences

- The PDF text path gains a stage between `cmap` mapping and `Tj`: a **glyph sequence with
  advances and offsets**. Measurement and drawing share it so that wrapping and output agree.
- Text with kerning/shift is written as a `TJ` array instead of a single string.
- The shaping logic is fully testable through synthetic fonts (own `GSUB`/`GPOS` in the test
  font builder), without real font files.
- Subsetting (P05) changes the embedded font file and is separate from this; it can be added
  later without changing the shaping interface.

## Limits and known deviations

- The bidi class table is generated from UCD 16.0.0 (`tools/generate-bidi-data.py`).
- Against `BidiCharacterTest.txt`, all cases without explicit direction control characters pass
  (an excerpt in the test; locally the full file with 91,597 cases). Rare combinations of the
  explicit format codes `LRE`/`RLE`/`LRO`/`RLO`/`PDF` in nested overrides deviate in about
  0.06 % of the UCD cases; Arabic and Hebrew text without these (obsolete) codes is composed
  completely correctly.
- The Arabic joining analysis covers the Arabic basic block (U+0600–U+06FF); extended Arabic
  blocks are treated as non-joining.
- **Font subsetting (P05)** is applied only to fonts **without** `GSUB`/`GPOS`/`GDEF`; otherwise
  the shaping would break. The result is parsed again after construction and checked against the
  used code point mappings; if the check fails, the complete file is embedded (no risk to the
  document).
