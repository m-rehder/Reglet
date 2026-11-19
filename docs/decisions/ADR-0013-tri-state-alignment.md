# ADR-0013: Tri-state paragraph alignment (OF-A04 remainder)

- **Status:** Implemented (slice 4)
- **Date:** 2026-11-09
- **Context:** M2 / OF-A04 remainder, builds on ADR-0012

## Context

`ParagraphStyle.Alignment` was a non-null `TextAlignment` with `TextAlignment.Left` as its
default. An explicit left alignment could therefore not be distinguished from "not set". A
paragraph that overrode a centered style with left alignment only kept the center after a style
edit, and a writer could not state the explicit left without stating it on every paragraph.

## Decision

### 1. Alignment is nullable

`ParagraphStyle.Alignment` is `TextAlignment?`:

- `null` means "not set" and inherits from the document defaults and the paragraph style;
- `TextAlignment.Left` (also `Center`, `Right`, `Justify`) is an explicit value.

Setting values stays source-compatible (`Alignment = TextAlignment.Center`); reading requires a
comparison (`style.Alignment == TextAlignment.Center`).

### 2. Readers keep the explicit value

- **DOCX:** a present `w:jc` is read into the alignment, including `w:val="left"`; an absent
  element stays `null`.
- **ODT:** a present `fo:text-align` is read, including `start`; an absent attribute stays `null`.

### 3. Writers state the difference

`ParagraphStyleResolver.ApplyDefaults` resolves defaults → style → direct formatting. The DOCX and
ODT writers compare the resolved style with the inherited base and write only the difference:

- DOCX writes `<w:jc w:val="left"/>` for an explicit left over an inherited different alignment;
- ODT writes `fo:text-align="start"` for an explicit left over an inherited different alignment;
- an absent value writes nothing and inherits.

The PDF layout coalesces `null` to `TextAlignment.Left`.

### 4. Limits of this slice

Spacing, indents, borders, tab stops and the paragraph direction are still not tri-state. A direct
value that happens to equal the inherited value remains indistinguishable for those properties.
The full model and its public API break remain the next slice.

## Consequences

- An explicit left alignment survives a write/read cycle in DOCX and ODT, also over a centered
  style.
- A style edit that changes or removes an alignment reaches a paragraph that still inherits it.
- The public API breaks for readers: `style.Alignment` is `TextAlignment?`. Setting values and the
  `with` syntax remain source-compatible.
- The PDF layout and heading defaults coalesce the nullable value to left; the appearance of an
  unchanged document stays the same.

## Alternatives rejected

- **Keep the non-null alignment and treat left as unset.** Smallest change, but the explicit left
  over a centered style stays broken.
- **Full tri-state paragraph formatting now.** Correct end state, but a much larger API break
  (spacing, indents, borders, tab stops) that this slice does not need.
