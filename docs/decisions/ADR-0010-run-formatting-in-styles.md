# ADR-0010: Run formatting of styles in the model (OF-A04)

- **Status:** Implemented (slices 1 and 2)
- **Date:** 2026-11-05
- **Context:** M2 / OF-A04 (rest of AC01), prepares OF-B01 follow-ups and the template work

## Context

A paragraph style and a character style in Word and ODF carry run formatting:

- **DOCX:** `w:style` has `w:rPr` next to `w:pPr` (paragraph style) or alone (character style),
  inherited through `w:basedOn`, plus `w:docDefaults/w:rPrDefault`. A run's own `w:rPr` wins, and a
  toggle can be switched **off** (`<w:b w:val="0"/>`, `<w:u w:val="none"/>`,
  `<w:vertAlign w:val="baseline"/>`).
- **ODF:** a style of family `paragraph` or `text` has `style:text-properties`; the parent chain
  (`style:parent-style-name`) inherits per attribute, and `normal`/`none` switches an inherited
  toggle off. A `text:span` with an automatic style overrides per attribute.

Today the readers **bake** that formatting into the runs instead of modelling it:

- `DocxDocumentReader` merges `w:style/w:rPr` of the paragraph style and of the run's character
  style into every run (`ReadTextRun(text, rPr, baseFormat)`), and `OdtDocumentReader` resolves
  `style:text-properties` into a `TextRun` that becomes the base of the span formatting
  (`shortcut:` at both readers).
- `ParagraphStyle` has **no** run formatting, so the DOCX writer writes `w:style` for a named
  paragraph style without `w:rPr` and the ODT writer writes no `style:text-properties` for it.
  A styled "Normal: Calibri 12 pt bold" therefore loses its definition on a read/write cycle.
- The model's run formatting is **not tri-state**: `TextRun.Bold` is a `bool`, `UnderlineStyle` and
  `Strikethrough` default to the "off" values. "Not set" and "explicitly off" cannot be told apart,
  so the readers have to bake and cannot express `<w:b w:val="0"/>` in a bold style.

Consequence: a model change to a paragraph or character style after a read does not reach the
runs, PDF (which does resolve character styles at draw time, `CharacterStyleResolver`) and
DOCX/ODT (which rely on the baked values) disagree, and style definitions lose their formatting.

The obvious fix noted in the former working notes — "a property on `ParagraphStyle` plus use in the three writers" —
is necessary but not sufficient: while the readers bake, the baked run value always wins over the
style, so an edited style still has no effect. And as soon as the readers stop baking, a run that
switches an inherited toggle off must be representable, which needs tri-state formatting in the
model. `TextRun.Bold = false` cannot mean both "no opinion" and "not bold".

## Decision

### 1. Tri-state run formatting

`TextRun` and `CharacterStyle` keep their property names but make the toggles and enumerations
nullable; `ParagraphStyle` gains the same set as its run formatting. `null` means "not set",
`true`/`false` (and `None`/`Normal`/…) mean "explicitly on/off".

```csharp
public sealed class TextRun          // direct formatting of the run
{
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Underline { get; init; }                 // single underline shorthand
    public TextUnderlineStyle? UnderlineStyle { get; init; }
    public TextStrikethrough? Strikethrough { get; init; }
    public TextBaseline? Baseline { get; init; }
    public bool? AllCaps { get; init; }
    public bool? SmallCaps { get; init; }
    public bool? Hidden { get; init; }
    // unchanged (already nullable): UnderlineColor, Color, Background, Highlight, FontFamily,
    // FontSize, Border, Kerning, CharacterSpacing, HorizontalScaling, Position, CharacterStyleName
}

public sealed class CharacterStyle   // named character style, same nine become nullable
{
    // ... same properties as the run ...
}

public sealed record ParagraphStyle  // paragraph style, new run formatting
{
    // unchanged paragraph properties ...
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public bool? Underline { get; init; }
    public TextUnderlineStyle? UnderlineStyle { get; init; }
    public Color? UnderlineColor { get; init; }
    public TextStrikethrough? Strikethrough { get; init; }
    public TextBaseline? Baseline { get; init; }
    public bool? AllCaps { get; init; }
    public bool? SmallCaps { get; init; }
    public bool? Hidden { get; init; }
    public Color? TextColor { get; init; }        // run color; `Background` stays the shading
    public Color? TextBackground { get; init; }   // run background
    public Color? Highlight { get; init; }
    public string? FontFamily { get; init; }
    public Length? FontSize { get; init; }
    public ParagraphBorder? TextBorder { get; init; } // run border; `Borders` stays the paragraph box
    public Length? Kerning { get; init; }
    public Length? CharacterSpacing { get; init; }
    public double? HorizontalScaling { get; init; }
    public Length? Position { get; init; }
}
```

The three names `TextColor`/`TextBackground`/`TextBorder` avoid the clash with the paragraph's
`Background` (shading) and `Borders` (page-visible box); the other names are unique already.

### 2. Resolution

One resolver in `Reglet.Core` (today `CharacterStyleResolver`) computes the effective run
formatting of a run from **document defaults → paragraph style → character style → direct run
formatting**, later wins:

- toggles (`Bold`, `Italic`, `AllCaps`, `SmallCaps`, `Hidden`) and enumerations
  (`Strikethrough`, `Baseline`): the first non-`null` value wins;
- underline: a level sets it when `UnderlineStyle` or `Underline` is non-`null`; `UnderlineStyle`
  wins over the shorthand, `None`/`false` switch it off, `true` means single;
- `Position` wins over `Baseline` (as today);
- color, background, highlight, font family, size, border, kerning, spacing, scaling: the first
  non-`null` value wins.

Document defaults stay limited to the three fields the model already has (`FontFamily`,
`FontSize`, `Color`); extending `DocumentDefaults` is a separate step (OF-A04 remainder).

`NamedStyleComparer` compares the new properties too, so a changed style invalidates the adopted
`styles.xml`.

### 3. Readers stop baking

- **DOCX:** the paragraph's `Style` carries the run formatting of its paragraph style (resolved
  `w:style/w:rPr` over `w:basedOn`); named styles in `PortableDocument.Styles` carry theirs as
  well. `w:pPr/w:rPr` describes the paragraph mark, not the text, so it does not become the base
  of the runs (it stays unmodelled and is reported as before). A run carries only its own `w:rPr`
  and the `CharacterStyleName` from `w:rStyle`. `w:b`, `w:i`, `w:caps`, `w:smallCaps`, `w:vanish`,
  `w:strike`/`w:dstrike`, `w:u` (`none`), `w:vertAlign` (`baseline`), `w:color`, `w:highlight`,
  `w:shd`, `w:sz`, `w:rFonts`, `w:spacing`, `w:w`, `w:position`, `w:kern`, `w:bdr` are read
  tri-state.
- **ODT:** a paragraph's `Style` carries the `style:text-properties` of its named paragraph style
  (parent chain resolved); a span carries only the attributes it sets. `fo:font-weight="normal"`,
  `fo:font-style="normal"`, `style:text-underline-style="none"`,
  `style:text-line-through-style="none"`, `style:text-position="0% 0%"`/`baseline`,
  `fo:text-transform="none"`, `fo:font-variant="normal"` and `text:display` read as explicit off.
- Automatic text styles and `text:span` still exist; only the *base* moves from the run into the
  style.

### 4. Writers resolve and write the definitions

- **DOCX:** named paragraph styles get a `w:rPr`; character styles keep theirs. A run writes its
  direct formatting and, where it overrides a base that is on, the explicit off
  (`<w:b w:val="0"/>`, `<w:u w:val="none"/>`, `<w:vertAlign w:val="baseline"/>`, …).
- **ODT:** named paragraph styles get `style:text-properties`; character styles keep theirs with
  `normal`/`none` for explicit offs; automatic span styles are keyed on the **resolved** run, so
  PDF and ODT show the same.
- **PDF:** the draw path resolves defaults → paragraph style → character style → run (the
  character-style step exists today), and `IsUnderlined` becomes tri-state as well.

### 5. API break and migration

`TextRun.Bold` and the other toggles become `bool?`, `CharacterStyle`'s become `bool?` as well.
Code that only *sets* values keeps working (`Bold = true` still compiles); code that reads them
compares (`run.Bold == true`, `run.IsUnderlined`). The 1.x line may break here (same decision as
ADR-0009); the release notes list the changed signatures and the resolution order.

## Slices

1. **Model and writers (done).** Tri-state types, `ParagraphStyle` run formatting, resolver with
   tests, writers resolving and writing style definitions. The readers only changed in the
   mechanical part: an element the source does not set becomes `null` instead of the "off" value,
   while the merge of style and run formatting stays as it is. The appearance of a read document
   is unchanged; a source's explicit off (`<w:b w:val="0"/>`,
   `style:text-underline-style="none"`) is now *kept* in the output instead of being lost, and an
   absent property still writes nothing. `RunFormatChecks` covers the resolution order, the three
   writers and an explicit off for a document built from the model.
2. **Readers (done).** The runs keep the source's own formatting only; the style's run formatting
   moves into `PortableDocument.Styles`/`CharacterStyles` and into the paragraphs' `Style` (the
   paragraph style of a note, a table cell, a band or a list item included). The writers resolve
   defaults → paragraph style → character style → run when they write and state only what the
   inherited base does not already provide, so the output and appearance of a read document stay
   as they were. Golden and corpus fixtures did not change. Tests: the readers keep the formatting
   on the style (`NamedStyleChecks`), a character style edit after a read reaches the runs in
   DOCX, ODT and PDF, and an explicit off survives a rewritten body (`RunFormatChecks`).

## Consequences

- A change to a *character style* in the model reaches the runs in all three formats (the writers
  resolve it by name); a paragraph style's run formatting reaches the paragraphs the caller points
  at the style. A paragraph read earlier keeps the resolved formatting, because the model flattens
  paragraph formatting (unchanged by this ADR) — the style reference is only kept for headings.
  The style definitions keep their run formatting (before, the DOCX wrote named paragraph styles
  without `w:rPr` and ODT without `style:text-properties`).
- PDF, DOCX and ODT resolve the same chain, so the baked-value divergence disappears.
- Cost: resolution runs per run in the writers (the ODT automatic-style key and the PDF text
  atoms), plus a public API break. The ODT writer's automatic-style collection must resolve like
  the writer, otherwise span styles and text disagree.
- `CharacterStyleResolver` no longer "applies a style to a run" but computes the effective
  formatting; `DocumentSearch` keeps copying the direct formatting unchanged.
- Out of scope: `w:pPr/w:rPr` (the formatting of the paragraph mark, which Word does not apply to
  the text), `w:rPrChange`, conditional (`w:cnfStyle`) run formatting, theme fonts and
  `docDefaults` beyond the three modelled fields.

## Alternatives rejected

- **Additive `ParagraphStyle` property with baked runs.** Smallest change, but a style edit still
  never reaches the runs (the baked value wins) and the style definitions keep losing their
  `w:rPr`/`style:text-properties`. It would only fix the appearance of newly built documents.
- **Hidden "explicit" flags on `TextRun`** (like the internal `ParagraphStyle.SpaceAfterExplicit`).
  Keeps the public `bool` but every copy path (search, template binding, nested tables, notes) must
  carry the flags, and `new TextRun("x") { Bold = false }` would still be ambiguous for a caller.
- **Per-writer resolution without a model change.** DOCX and ODT could resolve style definitions
  when writing, but the readers cannot express what the source had, so a read document would keep
  losing the information.
