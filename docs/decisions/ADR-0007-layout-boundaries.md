# ADR-0007: Layout boundaries (phases AA and AE)

- **Status:** Accepted
- **Date:** 2026-10-01
- **Context:** Phases AA and AE

## Context

Two remaining roadmap points would need machinery that the zero-dependency, data-free
constraint makes disproportionate:

- **AA01 (PDF floating images with text wrap):** DOCX anchors images (`wp:anchor`) and ODT
  wraps (`draw:frame`); the PDF renderer places images in the block flow. Real text wrap
  needs a per-line *exclusion zone* inside the central paragraph layout: every line would
  query the available width and its left edge at its own vertical band, re-wrap, and feed the
  result back into pagination, background boxes and justified line positions. The line height
  itself depends on the atoms in the line, so the band and the wrap are mutually dependent.
- **AE03 (automatic hyphenation):** line breaking with hyphenation needs language-specific
  hyphenation dictionaries (patterns) and an algorithm (Liang). The dictionaries are data
  that the library must ship or download; the library is dependency- and data-free. Soft
  hyphens (`U+00AD`) are already preserved verbatim, so callers can pre-hyphenate.

## Decision

1. **AA01-PDF is partially implemented.** Floating raster and SVG images use their offsets from
   the column and paragraph. `Square` reserves space beside the image, including multiple images;
   `TopAndBottom` advances past it. Every paragraph recalculates its width per line, also one
   with notes, keep constraints, borders or a list marker, and lays out again where it really
   stands, so a paragraph that breaks in the middle flows over the full width on the next page;
   the box of a bordered paragraph spans all its lines, as Word draws it. ODT stores
   wrapping and offsets in a graphic style and frame.
2. **AE03 is partially lifted (phase BJ01).** The library still ships no dictionary (the
   zero-dependency, data-free rule stays), but:
   - the soft hyphen (`U+00AD`) is a break opportunity in every format: the PDF breaks the
     line there and draws the hyphen; DOCX and ODT pass the character through, so Word and
     LibreOffice break there as well;
   - `HyphenationPatterns` implements the Liang algorithm for the patterns that the caller
     supplies (TeX format, for example the `hyph-en-us` patterns); `PdfWriteOptions.Hyphenation`
     uses them, so the PDF hyphenates without a dictionary in the library;
   - `DocxWriteOptions.AutoHyphenation` writes `w:autoHyphenation`, so Word hyphenates with
     its own dictionary.
   - *Revised 2026-11-13:* the optional package `Reglet.Hyphenation` (only `Core` as a
     dependency, not referenced by the facade) embeds the unmodified `hyph-utf8` patterns of 13
     languages as text resources (`HyphenationLanguages.Load`); only patterns that are MIT or
     freely redistributable are included, with their notices in `THIRD-PARTY-NOTICES.md`. The other
     packages still ship no pattern data.
   - *Revised 2026-11-19 (Reglet 1.0):* eleven more European languages, 24 in all: Slovak, Lithuanian,
     Albanian and Belarusian (MIT), Estonian, Ukrainian, Slovenian and Modern Greek (monotonic) (MIT
     chosen of their alternatives), Galician (Unlicense), Bulgarian (a BSD-like permission) and Basque (a
     permissive notice). Still left out: LPPL-only (Swedish, Russian, Catalan, Croatian, Turkish, Thai),
     GPL/LGPL (Czech, Latvian, Indonesian, Irish, Serbian Cyrillic, Hungarian), and patterns without a
     clear licence (Finnish, Romanian). The hyphen minimums come from the `typesetting` (else
     `generation`) values of each file's header. Adding Ukrainian found a parser bug: pattern characters
     other than letters and the point (hyphen, apostrophe) were skipped, which made other patterns that
     suppressed breaks; as in TeX, every character but a digit now belongs to the pattern.
   What stays a boundary: the quality of the hyphenation depends on the patterns; the library
   downloads none and the core packages ship none.
3. **AD04 follows [ADR-0006](ADR-0006-encryption-signatures.md):** PDF signing is
   implemented through the PAdES baselines B-B, B-T and B-LT, and a document timestamp can be
   appended; the caller supplies the certificate, TSA and validation material. PDF AES
   encryption is also implemented.

## Consequences

- The feature matrix names the boundaries instead of leaving them open.
- No data, no packages and no layout rewrite are introduced; the model already carries the
  DOCX/ODT image-layout attributes for a future PDF implementation.
- The boundaries are reversible: they are layout features, not data losses.
- With BJ01 the hyphenation is no longer a pure boundary, but it stays a cooperation with the
  caller: without patterns and without soft hyphens the documents are not hyphenated.
