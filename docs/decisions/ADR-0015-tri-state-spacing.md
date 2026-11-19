# ADR-0015: Tri-state paragraph spacing (OF-A04 remainder)

- **Status:** Implemented (slice 6)
- **Date:** 2026-11-09
- **Context:** M2 / OF-A04 remainder, builds on ADR-0012, ADR-0013 and ADR-0014

## Context

`ParagraphStyle.SpaceBefore` and `SpaceAfter` were non-null `Length` values with
`Length.Zero` as their default. For `SpaceAfter`, an internal `SpaceAfterExplicit` flag
distinguished the built-in default (0, resolved to `Typography.ParagraphSpaceAfter`) from an
explicit zero. `SpaceBefore` had no such flag. A paragraph that removed the spacing of a spaced
style could therefore not be distinguished from a paragraph that inherited it, and the internal
flag was not part of the public model.

## Decision

### 1. The spacings are nullable

`SpaceBefore` and `SpaceAfter` are `Length?`:

- `null` means "not set" and inherits from the document defaults and the paragraph style;
- `Length.Zero` is an explicit zero;
- every other length is an explicit spacing (negative values are rejected as before).

Setting values stays source-compatible (`SpaceAfter = Length.FromPoints(6)`); reading requires a
null check (`style.SpaceAfter?.Points`). The internal `SpaceAfterExplicit` flag is removed.

### 2. The built-in default spacing stays in the layout

The built-in default after a normal paragraph is still `Typography.ParagraphSpaceAfter`; compact
contexts (lists, table cells, headers/footers) stay at 0. `ParagraphSpacing` resolves:
`SpaceAfter is { } value ? value : compact ? 0 : Typography.ParagraphSpaceAfter`. `SpaceBefore`
resolves `null` to 0. This keeps the appearance of an unchanged document.

### 3. Readers keep the explicit values

- **DOCX:** a present `w:spacing/@w:before` or `@w:after` is read, including an explicit zero; an
  absent attribute stays `null`.
- **ODT:** a present `fo:margin-top` or `fo:margin-bottom` is read, including `0pt`; an absent
  attribute stays `null`.

### 4. Writers state the difference

`ParagraphStyleResolver.ApplyDefaults` resolves defaults → style → direct formatting. The DOCX and
ODT writers compare the resolved style with the inherited base and write only the difference:

- DOCX writes `w:spacing w:before="0"` and `w:after="0"` for explicit zeros over an inherited
  spacing, and `before="0"`/`after="0"` for compact contexts that must override the 6 pt default;
- ODT writes `fo:margin-top="0pt"` and `fo:margin-bottom="0pt"`;
- an absent value writes nothing and inherits.

The PDF layout coalesces `null` to zero before and to the compact/default rule after.

### 5. Limits of this slice

Borders, tab stops and the paragraph direction are still not tri-state. A direct value that
happens to equal the inherited value remains indistinguishable for those properties. They remain
the next slice and keep their own ADR.

## Consequences

- An explicit zero spacing survives a write/read cycle in DOCX and ODT, also over a spaced style.
- A style edit that changes or removes a spacing reaches a paragraph that still inherits it.
- The public API breaks for readers: both spacings are `Length?` and the internal explicit flag is
  gone. Setting values and the `with` syntax remain source-compatible.
- `Typography.ParagraphSpaceAfter` stays the built-in default, so the appearance of an unchanged
  document is the same.

## Alternatives rejected

- **Keep the non-null spacings and add a `SpaceBeforeExplicit` flag.** Smallest change and no
  public API break, but it keeps three different representations for one concept and leaves the
  flag internal, so callers still cannot tell "unset" from an explicit zero.
- **Full tri-state paragraph formatting now.** Correct end state, but a much larger API break
  (borders, tab stops, direction) that this slice does not need.
