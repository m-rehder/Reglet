# ADR-0012: Tri-state paragraph break flags (OF-A04 remainder)

- **Status:** Implemented (slice 3)
- **Date:** 2026-11-09
- **Context:** M2 / OF-A04 remainder, builds on ADR-0011

## Context

`ParagraphStyle.KeepWithNext`, `KeepTogether` and `PageBreakBefore` were `bool`. That cannot
distinguish "not set" from an explicit off. A source that switched an inherited `keepNext` off
(`w:keepNext w:val="0"`, `fo:keep-with-next="auto"`) therefore either lost the off or forced the
writers to state a value on every paragraph. The same gap affected an edited style that removes a
flag.

## Decision

### 1. The flags are nullable

`KeepWithNext`, `KeepTogether` and `PageBreakBefore` are `bool?`:

- `null` means "not set" and inherits from the document defaults and the paragraph style;
- `true` switches the flag on;
- `false` is an explicit off.

Setting the values stays source-compatible (`KeepWithNext = true`); reading requires a comparison
(`style.KeepWithNext == true`).

### 2. Readers keep the explicit off

- **DOCX:** a present `w:keepNext`, `w:keepLines` or `w:pageBreakBefore` is read as `true` or
  `false` according to its `w:val`; an absent element stays `null`.
- **ODT:** `fo:keep-with-next` and `fo:keep-together` read `always` as `true` and `auto` as `false`;
  `fo:break-before` reads `page` as `true` and `auto` as `false`.

### 3. Writers state the difference

`ParagraphStyleResolver.ApplyDefaults` resolves defaults → style → direct formatting. The DOCX and
ODT writers compare the resolved style with the inherited base and write only the difference:

- DOCX writes `<w:keepNext/>` for on and `<w:keepNext w:val="0"/>` for off;
- ODT writes `fo:keep-with-next="always"` or `"auto"` (and the same for keep-together and
  break-before).

The PDF layout treats `true` as set and `null`/`false` as not keeping.

### 4. Limits of this slice

Alignment, spacing, indents, borders, tab stops and the paragraph direction are still not
tri-state. A direct value that happens to equal the inherited value remains indistinguishable for
those properties. The full model and its public API break remain the next slice.

## Consequences

- An explicit off of a break flag survives a write/read cycle in DOCX and ODT.
- A style edit that removes a flag now reaches a paragraph that still inherits it.
- The public API breaks for readers: `style.KeepWithNext` is `bool?`. Setting values and the
  `with` syntax remain source-compatible.
- PDF layout and heading spacing coalesce the nullable values to `false`; the appearance of an
  unchanged document stays the same.

## Alternatives rejected

- **Keep `bool` and flatten the off.** Smallest change, but the reported gap (explicit offs,
  removed style flags) stays.
- **Full tri-state paragraph formatting now.** Correct end state, but a much larger API break
  (alignment, spacing, indents, borders, tab stops) that this slice does not need.
