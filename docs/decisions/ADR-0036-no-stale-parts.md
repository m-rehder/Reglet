# ADR-0036: No stale source parts in a changed document

Status: Accepted (2026-11-21)

## Context

The DOCX and ODT writers keep a read document's package parts they do not generate themselves (AF05, AF11), so
unmodelled content survives an edit. The fifth audit found that this also kept what the user had removed: a deleted
header, footnote, image or chart stayed in the package as an orphan part with its old content; a DOCX written from a
read ODT contained the whole ODT package (its `content.xml` with the original text); a changed ODT kept its Basic and
Python macros and a signature that no longer covered it. Someone who removes a paragraph and saves expects it gone.

## Decision

- **Only the own format is reused.** The DOCX writer copies source parts only from a DOCX source (the ODT writer
  already did so for ODT).
- **Parts of the model's kinds travel only while something points to them.** DOCX: headers, footers, footnotes,
  endnotes, media and charts (and their relationship parts) are copied only when a relationship of the written package
  reaches them, directly or through the source relationships of another copied part (a block written from its source
  XML). ODT: pictures, object replacement images and embedded objects only while written source XML (a block, an
  automatic style or the adopted `styles.xml`) links to them. Unknown parts stay as before.
- **Macros never travel into a changed document unasked.** ODT `Basic/` and `Scripts/` are dropped with a warning, as
  the VBA project in DOCX (ADR-0028); there is no option to keep them.
- **A signature is dropped when the document changes**, also for a metadata-only edit, with a warning. Open and save
  without a change still writes the package back byte for byte, signature and macros included.

## Consequences

- A replaced picture's source part is no longer kept (test OF-A01 changed accordingly).
- A metadata-only ODT edit rewrites the manifest when it drops a part.
