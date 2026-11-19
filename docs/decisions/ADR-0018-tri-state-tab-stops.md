# ADR-0018: Tri-state tab stops (OF-A04 remainder)

- **Status:** Implemented (slice 9)
- **Date:** 2026-11-09
- **Context:** M2 / OF-A04 remainder, builds on ADR-0011 to ADR-0017

## Context

`ParagraphStyle.TabStops` was a nullable list where `null` meant both "not set" and "no custom
tab stops". A paragraph that removed the tab stops of a styled paragraph therefore inherited them,
and the readers only stored a list when at least one stop was present.

## Decision

### 1. Tab stops have three states

- `null` means "not set" and inherits from the document defaults and the paragraph style;
- an empty list is an explicit "no tab stops";
- a non-empty list is an explicit set of tab stops.

### 2. Readers keep the empty list

- **DOCX:** an absent `w:tabs` stays `null`; a present `w:tabs` without a usable `w:tab` (including
  only `w:val="clear"` entries) is read as an empty list. Clear entries are not model stops.
- **ODT:** an absent `style:tab-stops` stays `null`; a present element without `style:tab-stop`
  children is read as an empty list.

### 3. Writers state the difference

`ParagraphStyleResolver.ApplyDefaults` resolves defaults → style → direct formatting. The DOCX and
ODT writers compare the resolved style with the inherited base and write only the difference:

- DOCX writes `w:tabs` with `w:tab` entries for the new stops and additionally a
  `w:tab w:val="clear" w:pos="…"` for every inherited position that the new set no longer contains;
- for an explicit empty set it writes an empty `w:tabs` and the clear entries for the inherited
  positions it knows;
- ODT writes `style:tab-stops`; an explicit empty set is an empty element, a non-empty set keeps
  the existing `style:tab-stop` children;
- an absent value writes nothing and inherits.

Tab stops are compared by position for the clear entries; the model keeps the order of the list.

### 4. Limits of this slice

ODF has no per-stop "clear" value. An empty `style:tab-stops` overrides the inherited list in the
model and round-trips through Reglet; whether every ODF consumer treats it as "clear" is
renderer-dependent and is reported as a known limit. Clearing an inherited set in a document built
from the model only knows the inherited positions when the paragraph carries a source base; without
one the empty `w:tabs`/`style:tab-stops` is what the model can state.

## Consequences

- An explicit "no tab stops" over a styled paragraph survives a write/read cycle in DOCX and ODT.
- A style edit that changes or removes tab stops reaches a paragraph that still inherits them.
- Removing one stop from an inherited set now also clears that position in DOCX instead of leaving
  it inherited.
- The public API stays source-compatible (`TabStops = [new TabStop(...)]`); reading code that
  tested `TabStops is null` must now also handle the empty list for an explicit none.

## Alternatives rejected

- **Keep the binary nullable list.** Smallest change, but the explicit "none" over a styled
  paragraph stays broken.
- **A separate `TabStopsExplicit` flag.** Repeats the hidden state that ADR-0015 removed.
- **Per-stop tri-state.** Not needed: the list as a whole is the inherited property; a missing stop
  in an explicit list already means "not present", and DOCX expresses that with a clear entry.
