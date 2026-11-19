# ADR-0021: Embedded fonts in DOCX and ODT

- **Status:** Implemented
- **Date:** 2026-11-13
- **Context:** Follow-up scope, closes the gap "embed fonts into DOCX/ODT"

## Context

Only the PDF writer embedded fonts. A DOCX or ODT shows its text with a substitute on a computer
that lacks the font. Embedding needs the font bytes and the licence flag (`fsType`), both owned by
`Reglet.Fonts`, which `Docx` and `Odt` were not allowed to reference.

## Decision

1. `Docx` and `Odt` may reference `Fonts` (like `Pdf`); the rule in `Directory.Build.targets`,
   `docs/build.md` and the project files changed. `Fonts` still references only `Core`.
2. New option `EmbeddedFonts` (`FontCollection?`) on `DocxWriteOptions` and `OdtWriteOptions`. Every
   style of each family the document uses (defaults, styles, runs in body, headers, footers) that
   the collection holds is embedded whole; no subsetting.
3. DOCX: `word/fontTable.xml` with `w:embedRegular/Bold/Italic/BoldItalic`, files obfuscated per
   ECMA-376 Part 2, 17.8.1 (`.odttf`, key = `w:fontKey` GUID, deterministic from family and style),
   `w:embedTrueTypeFonts` in the settings. A document that adopts a source package is not given
   embedded fonts (reported), because its own font table would have to be merged.
4. ODT: `Fonts/fontN.ttf` entries in the manifest and `svg:font-face-src` / `svg:font-face-uri` in the
   font declarations.
5. A font whose `fsType` forbids embedding is skipped with a warning.

## Consequences

- Files grow by the full font files.
- Verified with LibreOffice (PDF conversion shows the embedded font in both formats). Not verified:
  Word, and the bold/italic variants in LibreOffice.
- Runs inside notes and text boxes are not scanned, so a font used only there is not embedded.
