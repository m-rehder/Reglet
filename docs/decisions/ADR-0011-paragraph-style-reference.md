# ADR-0011: Body paragraphs reference their paragraph style (OF-A04 remainder)

- **Status:** Implemented (slice 1)
- **Date:** 2026-11-06
- **Context:** M2 / OF-A04 remainder, builds on ADR-0010

## Context

ADR-0010 moved the **run** formatting of a paragraph style into the style: the readers stop
baking it into the runs and the writers resolve defaults → paragraph style → character style →
run. The same split is still missing for **paragraph** formatting (alignment, spacing, indents,
keep flags, borders, tab stops).

Today the DOCX and ODT readers merge the `w:pPr`/`style:paragraph-properties` of the referenced
style into `Paragraph.Style` and drop the style reference for body paragraphs (only headings keep
`StyleName`). The writers therefore state the inherited values as direct `w:pPr`/automatic style:

- an edit of `PortableDocument.Styles` after reading does not reach the paragraph;
- the output has no `w:pStyle`/`text:style-name` reference and repeats the style on every
  paragraph;
- DOCX and ODT disagree with PDF, which already resolves the effective style for drawing.

The public model has no tri-state paragraph formatting: `Alignment` is a non-null enum,
`SpaceBefore`/`SpaceAfter`/the indents are `Length` with a zero default and the flags are `bool`.
"Not set" and "explicitly left / zero / off" cannot be told apart. A full reference-only model
therefore needs a public API break. This ADR takes the smaller step that removes the flattening
without breaking the API, and records the remaining work.

## Decision

### 1. Readers keep the reference and the inherited base

A body paragraph whose `w:pStyle`/`text:style-name` names a style that exists in
`PortableDocument.Styles` keeps `ParagraphStyle.StyleName`. The resolved formatting stays in
`Paragraph.Style` (the public model is unchanged); in addition the paragraph stores the formatting
it inherited from the style, without direct formatting, in the internal
`Paragraph.SourceStyleBase`:

- **DOCX:** `SourceStyleBase` is the style + document-defaults `pPr` resolved over `w:basedOn`,
  plus the style's run formatting (`w:style/w:rPr`). The paragraph's own `pPr` is not part of it.
- **ODT:** `SourceStyleBase` is `style:paragraph-properties`/`text-properties` resolved over
  `style:parent-style-name` and the default style. Direct paragraph formatting on the element is
  not read by the ODT reader (unchanged), so both values are equal today.
- A style the model does not keep (a built-in, an automatic style from `content.xml`, or a
  filtered style) is not referenced; such a paragraph behaves as before.
- Notes, list items and headings keep their previous behaviour in this slice.

### 2. Writers resolve the effective style

`ParagraphStyleResolver.TryResolve` finds the old definition in `PortableDocument.SourceStyles`
and the current one in `Styles`. It applies the difference between the two definitions to every
property the paragraph still inherits (value equals `SourceStyleBase`); a property that differs is
direct formatting and wins. This keeps the appearance of an unchanged document byte-for-byte at
the model level and lets a later style edit reach the paragraph.

- **DOCX:** the paragraph writes `w:pStyle` plus only the direct `w:pPr` properties. Inherited
  properties are left to Word, which resolves the style. For a built paragraph (no
  `SourceStyleBase`) the previous "state everything" behaviour is kept.
- **ODT:** a paragraph without direct formatting writes `text:style-name` of the named style and
  gets no automatic style. A paragraph with direct formatting gets an automatic style whose
  `style:parent-style-name` is the named style; the automatic style states every property that
  differs from the parent, including a value that equals the built-in default (for example
  `fo:text-align="start"` under a right-aligned parent).
- **PDF:** layout and drawing use the resolved style, so an edited style reaches the pages.
- **Tables:** a cell paragraph is resolved before the cell padding/indent is applied; the cell
  adjustment is direct formatting and is written. Derived paragraphs (list flattening, search,
  field resolution) keep the style reference when they copy the paragraph unchanged.

### 3. Limits of this slice

- The public `Paragraph.Style` remains the resolved snapshot. A caller that inspects it after
  editing `Styles` sees the old values; an existing `Paragraph` instance is not mutated.
- Paragraph formatting is not tri-state. An explicit direct value that happens to equal the
  inherited value is indistinguishable from "not set", so a later edit of that style property
  changes the paragraph too. Making alignment, spacing, indents and flags nullable and resolving
  defaults → style → direct in the model is the next slice and a public API break.
- The readers still do not put `w:docDefaults/w:pPrDefault` or the ODT default style into
  `DocumentDefaults.ParagraphStyle`. A style property that an edited definition *removes* therefore
  falls back to the value the source style had, not to the document default.
- `w:pPr/w:rPr` (the paragraph mark), conditional `w:cnfStyle` formatting, note paragraphs and
  heading definitions are out of scope, as in ADR-0010.

## Consequences

- An edit of a paragraph style after reading reaches body paragraphs in DOCX, ODT and PDF; the
  appearance of an unchanged read/write cycle stays the same.
- DOCX and ODT output keeps the named style reference instead of repeating it on every paragraph.
  A paragraph with direct formatting states only the deviation (DOCX) or an automatic child style
  with the deviation (ODT).
- Golden and corpus fixtures do not change.
- Cost: one resolution per paragraph in the writers, an internal property on `Paragraph`, and one
  extra style lookup per automatic ODT style. `TextRun`/`ParagraphStyle` records still carry the
  resolved snapshot, so the model is not yet a pure reference graph.

## Alternatives rejected

- **Bake and let a style edit do nothing.** Smallest change, but the reported problem (later
  edits of paragraph styles, flattening) stays.
- **Full tri-state paragraph formatting now.** Correct end state, but a large public API break
  (alignment, spacing, indents, keep flags and every writer/test touched) that this slice does not
  need to fix the reported behaviour. It remains the next step.
- **Store a direct-formatting delta in the model and drop the resolved snapshot.** Cleaner, but it
  changes `Paragraph.Style` semantics and every copy path at once; the internal base is enough
  while the public model stays resolved.
