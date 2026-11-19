# ADR-0014: Tri-state paragraph indents (OF-A04 remainder)

- **Status:** Implemented (slice 5)
- **Date:** 2026-11-09
- **Context:** M2 / OF-A04 remainder, builds on ADR-0012 and ADR-0013

## Context

`ParagraphStyle.LeftIndent`, `RightIndent` and `FirstLineIndent` were `Length` with `Length.Zero`
as their default. An explicit zero indent could therefore not be distinguished from "not set". A
paragraph that removed the indent of an indented style kept the inherited indent after a style
edit, and a writer could not state the explicit zero without repeating it on every paragraph.

## Decision

### 1. The indents are nullable

`LeftIndent`, `RightIndent` and `FirstLineIndent` are `Length?`:

- `null` means "not set" and inherits from the document defaults and the paragraph style;
- `Length.Zero` is an explicit zero;
- every other length is an explicit indent (a negative first-line indent is a hanging indent).

Setting values stays source-compatible (`LeftIndent = Length.FromPoints(12)`); reading requires a
null check (`style.LeftIndent?.Points`).

### 2. Readers keep the explicit values

- **DOCX:** a present `w:ind/@w:left` (or `start`), `@w:right` (or `end`) and
  `@w:firstLine`/`@w:hanging` is read, including an explicit zero; an absent attribute stays
  `null`.
- **ODT:** a present `fo:margin-left`, `fo:margin-right` and `fo:text-indent` is read, including
  `0pt`; an absent attribute stays `null`.

### 3. Writers state the difference

`ParagraphStyleResolver.ApplyDefaults` resolves defaults → style → direct formatting. The DOCX and
ODT writers compare the resolved style with the inherited base and write only the difference:

- DOCX writes `w:ind` with `w:left="0"`, `w:right="0"` and `w:firstLine="0"` for explicit zeros
  over an inherited indent;
- ODT writes `fo:margin-left="0pt"`, `fo:margin-right="0pt"` and `fo:text-indent="0pt"`;
- an absent value writes nothing and inherits.

The PDF layout coalesces `null` to zero.

### 4. Limits of this slice

Spacing, borders, tab stops and the paragraph direction are still not tri-state. A direct value
that happens to equal the inherited value remains indistinguishable for those properties. The full
model and its public API break remain the next slice.

## Consequences

- An explicit zero indent survives a write/read cycle in DOCX and ODT, also over an indented style.
- A style edit that changes or removes an indent reaches a paragraph that still inherits it.
- The public API breaks for readers: the three indents are `Length?`. Setting values and the `with`
  syntax remain source-compatible.
- The PDF layout coalesces the nullable values to zero; the appearance of an unchanged document
  stays the same.

## Alternatives rejected

- **Keep the non-null indents and treat zero as unset.** Smallest change, but the explicit zero
  over an indented style stays broken.
- **Full tri-state paragraph formatting now.** Correct end state, but a much larger API break
  (spacing, borders, tab stops) that this slice does not need.
