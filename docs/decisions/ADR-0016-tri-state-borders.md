# ADR-0016: Tri-state paragraph borders (OF-A04 remainder)

- **Status:** Implemented (slice 7)
- **Date:** 2026-11-09
- **Context:** M2 / OF-A04 remainder, builds on ADR-0011 to ADR-0015

## Context

`ParagraphStyle.Borders` was a nullable `ParagraphBorders`: `null` meant both "not set" and "no
borders". A paragraph that removed the borders of a bordered named style therefore kept the
inherited borders, and a reader could not tell an absent `w:pBdr`/`fo:border-*` from an explicit
`w:val="nil"`/`fo:border-top="none"`.

## Decision

### 1. Borders have three states

- `null` means "not set" and inherits from the document defaults and the paragraph style;
- `ParagraphBorders.None` is the explicit empty border set (no borders on any side);
- any other `ParagraphBorders` is an explicit set; a side that is `null` or has
  `BorderStyle.None` is not drawn.

`ParagraphBorders.None` is a static empty instance; a `new ParagraphBorders()` compares equal to
it, so both spell the same state.

### 2. Readers keep the explicit empty set

- **DOCX:** an absent `w:pBdr` stays `null`; a present `w:pBdr` whose sides are all absent,
  `w:val="none"` or `w:val="nil"` is read as `ParagraphBorders.None`.
- **ODT:** an absent `fo:border-top`/`-right`/`-bottom`/`-left` stays `null`; present attributes
  that are all `none` are read as `ParagraphBorders.None`.

The per-side `none` from a mixed set is kept as an absent side. A per-side explicit `none` over an
inherited side is therefore still not distinguishable; whole-set tri-state is this slice.

### 3. Writers state the difference

`ParagraphStyleResolver.ApplyDefaults` resolves defaults → style → direct formatting. The DOCX and
ODT writers compare the resolved style with the inherited base and write only the difference:

- DOCX writes `<w:pBdr>` with `w:top w:val="nil"` (and the other sides) for an explicit empty set;
- ODT writes `fo:border-top="none"` (and the other sides);
- an absent value writes nothing and inherits.

The PDF layout draws nothing for an empty set, which is the effective result after the style
resolution.

### 4. Limits of this slice

Tab stops and the paragraph direction are still not tri-state; they remain the next slice. The
DOCX and ODT writers can only clear a whole border set, not an individual inherited side.

## Consequences

- An explicit "no borders" over a bordered style survives a write/read cycle in DOCX and ODT.
- A style edit that adds or removes borders reaches a paragraph that still inherits them.
- The public API stays source-compatible (`Borders = ParagraphBorders.All(...)`); the new state is
  `ParagraphBorders.None`. Reading code that tested `Borders is null` must now also handle the
  empty set for an explicit none.
- Existing documents without border overrides are written unchanged.

## Alternatives rejected

- **Keep the binary nullable border set and an internal explicit flag.** Smallest change, but it
  repeats the hidden state that ADR-0015 just removed for spacing.
- **Tri-state per side.** More precise, but a larger public model (four tri-state border values)
  that this slice does not need; the whole-set state covers the common "remove the style borders".
