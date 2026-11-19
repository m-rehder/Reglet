# ADR-0009: Shared numbering definitions across lists (OF-B01)

- **Status:** Accepted, implemented
- **Date:** 2026-11-04
- **Context:** M2 / OF-B01

## Context

Today every `ListBlock` carries its own kind, number format, start value, number colour and
number weight. The DOCX writer gives every list its own `w:abstractNum`/`w:num` pair, even when
several lists came from one Word list. After a read/write cycle, lists that continued in Word
restart at 1, and Word shows different list identities.

Word and ODF share a multi-level definition:

- **DOCX:** `w:numbering` holds `w:abstractNum` (nine `w:lvl` with format, start, text and run
  properties) and `w:num` entries that reference an abstract definition. Several paragraphs
  with the same `w:numId` are one list; numbering continues across paragraphs that are not
  part of the list. `w:lvlOverride`/`w:startOverride` restarts a level per `w:num`.
- **ODT:** `text:list-style` holds up to ten `text:list-level-style-*` entries; every list with
  the same `text:style-name` shares the level definitions. Continuation across separate
  `text:list` elements needs `text:continue-list` (or `text:continue-numbering`).

The DOCX reader already reads every present `w:lvl` of an `w:abstractNum` (format, start,
colour, weight). What is missing is a place in the model that holds the definition itself and a way
for a list to reference it.

## Decision

### 1. Model

New public types in `Reglet.Core`:

```csharp
public sealed record NumberingLevel
{
    public ListKind Kind { get; init; } = ListKind.Number;
    public ListNumberFormat Format { get; init; } = ListNumberFormat.Decimal;
    public int Start { get; init; } = 1;
    public Color? NumberColor { get; init; }
    public bool NumberBold { get; init; }
}

public sealed record NumberingDefinition
{
    public NumberingDefinition(string name, IEnumerable<NumberingLevel> levels);
    public string Name { get; }        // non-empty, unique, a valid NCName
    public IReadOnlyList<NumberingLevel> Levels { get; } // always nine, level 1 first
}
```

- A definition accepts 1–9 levels and stores nine: levels above the given ones repeat the last
  one, so a definition with one level uses it at every depth and every writer materializes the
  same numbering (Word needs a `w:lvl` for each depth).
- The name must be a valid NCName (no spaces, no leading digit) because it is also the ODT
  `style:name` (the writer generates no other list styles, so the name is free); it must be
  unique. The constructor rejects anything else with `DocumentValidationException`.
- `PortableDocument.Numbering` holds the definitions.
- `ListBlock` loses its list formatting:

```csharp
public ListBlock(string numberingName, IEnumerable<ListItem> items);
public string NumberingName { get; }    // required, must resolve in PortableDocument.Numbering
public int NestingLevel { get; init; }  // 1..9, selects the level
```

- `Kind`, `NumberFormat`, `Start`, `NumberColor`, `NumberBold` and the `(ListKind, items)`
  constructor are removed; that data exists once, in `NumberingLevel`. A writer that cannot
  resolve `NumberingName` throws `DocumentValidationException` instead of inventing a
  definition.
- `PortableDocumentBuilder.BulletList`/`NumberedList` keep their signature and add one
  definition per call (`Numbering1`, `Numbering2`, …), so each call restarts. Sharing a
  definition means `Numbering(definition…)` plus `List(name, items…)`, whose lists continue
  each other.

### 2. No backward compatibility

This is an incompatible model change and ships as such. The 1.x line is the development line
(`docs/versioning.md`): no major bump, no migration shims, no second source of truth, no unnamed
fallback in readers or writers, no obsolete properties kept around. The changelog names the
change; the public 1.0.0 follows later under a new name.

### 3. DOCX mapping

- **Reader:** one definition per `w:num`, named `Numbering{numId}` (e.g. `Numbering1`) so the
  name is a valid ODT style name. Levels come from the referenced `w:abstractNum`, with
  `w:lvlOverride`/`w:startOverride` applied per `w:num`. A list block gets the same
  `NumberingName` and `NestingLevel = w:ilvl + 1`. A list without `w:numPr` gets a synthesized
  one-level definition (`numbering` default: decimal, start 1) so every block resolves.
- **Writer:** one `w:abstractNum` and one `w:num` per definition; all lists with the same
  `NumberingName` reuse that `w:numId`, so Word continues the numbering. Each definition has its
  own abstract definition, so `w:lvl/w:start` carries the start;
  `w:lvlOverride`/`w:startOverride` is neither needed nor written.

### 4. ODT mapping

- **Reader:** each `text:list-style` becomes a definition named after its `style:name`; each
  `text:list-level-style-*` becomes a level (format, `text:start-value`, colour and weight from
  `style:text-properties`). Every `text:list` with that `text:style-name` references the
  definition. A nested `text:list` without a `text:style-name` continues the style of its parent
  (that is how LibreOffice writes nested lists); a list with a style name the package does not
  define gets a synthesized definition from the built-in list typography.
- **Writer:** one `text:list-style` per definition a list uses, named after the definition, with
  all nine levels (kind, `style:num-format`, `text:start-value`, colour and weight in the
  list-level `style:text-properties`). Lists reference it by `NumberingName`, so a definition
  keeps its levels across a read/write cycle.
- **Continuation (OF-B02):** a list carries an `xml:id` (`List1`, `List2`, …) and every top-level
  list after the first one of a definition writes `text:continue-list` with the id of the previous
  list of that definition, so LibreOffice and Word continue the numbering instead of restarting at
  `text:start-value`. The `text:start-value` is written only on the first list. The attribute is
  limited to top-level lists, because a `text:list` and its sub-lists form one counter domain.
  Because the model says “one definition = one continuation”, a document that uses one style for
  two lists that restart in LibreOffice reads as continuing; that deviation is accepted and
  matches the DOCX side, where the same definition is one `w:numId`.

## Consequences

- Read → write keeps a Word numbered list continuing after an intervening paragraph or table.
- The model gains one public type pair and loses five `ListBlock` properties plus the
  `(ListKind, items)` constructor; `ListKind` of a list now comes from its level.
- Renderers, scans, search and the field resolver resolve the level once from
  `PortableDocument.Numbering` instead of reading block properties.
- ODT writes and reads the continuation of a definition across top-level lists.
- `NumberingDefinition` names are public and used as ODT list-style names, so validation must
  reject empty, duplicate, non-NCName and generated-name collisions.

## Implementation slices

1. **Model:** types, validation (names as NCName, 1–9 levels), `PortableDocument.Numbering`,
   `ListBlock` without list formatting, builder creates a definition per call; update every
   call site (renderers, scans, search, field resolver).
2. **DOCX:** reader fills the definitions and references; writer emits one definition per
   name and continues by `w:numId`; roundtrip test with two lists around an intervening
   paragraph.
3. **ODT:** reader/writer map definitions to `text:list-style`; test that two lists share one
   style; document the restart limitation.
4. **Docs and goldens:** feature gaps, roadmap, roundtrip, golden and corpus tests, changelog
   entry (no migration section).

## Open questions

- **Name source:** `w:numId` is used because `w:startOverride` is a property of the `w:num`,
  not of the abstract definition. Two `w:num` entries that share an `abstractNum` therefore
  become two definitions with different start values. The name is prefixed so it stays a valid
  ODT style name.
- **Nested lists in the model:** `ListItem.Blocks` holds nested `ListBlock`s; a nested list
  with the same `NumberingName` uses the next `NestingLevel` and therefore the next level of
  the same definition. A nested list with a different name restarts numbering, as Word does
  for a new `w:numId`.
