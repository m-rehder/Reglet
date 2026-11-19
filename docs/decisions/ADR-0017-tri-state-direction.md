# ADR-0017: Tri-state paragraph direction (OF-A04 remainder)

- **Status:** Implemented (slice 8)
- **Date:** 2026-11-09
- **Context:** M2 / OF-A04 remainder, builds on ADR-0012 to ADR-0016

## Context

`ParagraphStyle.RightToLeft` was a non-null `bool` with `false` as its default. A paragraph that
forced left-to-right over a right-to-left style therefore could not be distinguished from a
paragraph that simply inherited the default. The DOCX and ODT writers also wrote only the RTL
case, so an explicit LTR was lost on a style edit.

## Decision

### 1. The direction is nullable

`ParagraphStyle.RightToLeft` is `bool?`:

- `null` means "not set" and inherits from the document defaults and the paragraph style;
- `true` is an explicit right-to-left paragraph;
- `false` is an explicit left-to-right paragraph.

Setting values stays source-compatible (`RightToLeft = true`); reading requires a comparison
(`style.RightToLeft == true`). The PDF layout coalesces `null` to `false`.

### 2. Readers keep the explicit value

- **DOCX:** a present `w:bidi` is read with its on/off value, so `w:bidi w:val="0"` becomes
  `false`; an absent element stays `null`.
- **ODT:** a present `style:writing-mode` of `rl-tb` becomes `true` and `lr-tb`/`lr` becomes
  `false`; an absent attribute stays `null`.

### 3. Writers state the difference

`ParagraphStyleResolver.ApplyDefaults` resolves defaults → style → direct formatting. The DOCX and
ODT writers compare the resolved style with the inherited base and write only the difference:

- DOCX writes `<w:bidi/>` for an explicit RTL and `<w:bidi w:val="0"/>` for an explicit LTR over an
  inherited RTL style;
- ODT writes `style:writing-mode="rl-tb"` or `style:writing-mode="lr-tb"`;
- an absent value writes nothing and inherits.

### 4. Limits of this slice

Tab stops are still not tri-state; they remain the next slice. The direction of a single run is
still governed by the bidirectional algorithm and is not part of this property.

## Consequences

- An explicit left-to-right direction over a right-to-left style survives a write/read cycle in
  DOCX and ODT.
- A style edit that changes or removes the direction reaches a paragraph that still inherits it.
- The public API breaks for readers: `RightToLeft` is `bool?`. Setting values and the `with` syntax
  remain source-compatible.
- The PDF layout coalesces `null` to left-to-right; the appearance of an unchanged document stays
  the same.

## Alternatives rejected

- **Keep the non-null bool and treat `false` as unset.** Smallest change, but the explicit LTR
  over an RTL style stays broken.
- **A separate `Direction` enum.** Clearer in isolation, but the rest of the paragraph model uses
  nullable values for "not set"; a `bool?` matches `KeepWithNext` and the other flags.
