# ADR-0038: Floating objects anchored to page and margin

Status: Accepted (2026-11-23)

## Context

A floating `ImageBlock` or `DrawingShape` was placed only by offsets from its column and paragraph. A
letterhead logo or a stamp belongs to the page: it should land at the same place whatever paragraph carries it,
and is usually aligned (right edge, centre) rather than offset.

## Decision

- **Four properties on both types**: `HorizontalRelativeTo` (`Column` by default, `Page`, `Margin`,
  `Character`), `VerticalRelativeTo` (`Paragraph` by default, `Page`, `Margin`, `Line`), and nullable
  `HorizontalAlignment` / `VerticalAlignment`, which replace the offset when set. The defaults keep the old behaviour.
- The alignment enums are `HorizontalFloatAlignment` and `VerticalFloatAlignment`, not `HorizontalAlignment`, which
  would clash with WPF and WinForms types in a caller's desktop application.
- **DOCX** writes `relativeFrom` and `wp:align` or `wp:posOffset`. The inner, outer, left and right margins and the
  inside/outside alignments have no model value; they are read as column/paragraph with a warning.
- **ODT** writes `style:horizontal-rel`/`vertical-rel` (`page`, `page-content`, `paragraph-content`, `char`,
  `paragraph`, `line`) and `style:horizontal-pos`/`vertical-pos` in the frame's graphic style.
- **PDF**: page and margin positions are computed from the page box, without reserving space at the cursor. The
  margin is the area between the page margins over all columns. A block-level object has no character or line of
  its own, so `Character` and `Line` place like `Column` and `Paragraph`; a vertical alignment relative to the
  paragraph or line puts the object at the cursor. A page-relative top-and-bottom object moves the text only when it
  covers the cursor.

## Consequences

- A DOCX image with `relativeFrom="page"` is no longer reported as approximated (test changed accordingly).
