# ADR-0031: Equations (an `Equation` block of a math tree)

- **Status:** Accepted (2026-11-18, product owner: "danach Gleichungen", details chosen by the implementer: recommended defaults); implemented in .NET
- **Date:** 2026-11-18
- **Context:** "Equations" row of "Out of scope" (formula model, OMML and MathML/ODF, vector rendering; XL). The model has no
  formulas. A DOCX with `m:oMath` keeps it only through the preserving round trip; a document built from the model cannot
  contain one, and the PDF writer cannot draw one.

## Decision

1. **A display block, not an inline.** `Equation` is a `Block` (own paragraph, centred by default, optional left or right
   alignment). Inline equations inside a line of text are **not** in version 1: they need baseline metrics in every
   writer and in the PDF line breaker.
2. **A small math tree, not "all of OMML".** `MathNode` with these kinds, enough for school and university formulas:
   `MathText` (a run: `Text`, `Style` = `Italic` for variables (default for letters), `Normal` for numbers, operators
   and function names), `MathRow` (a sequence), `MathFraction` (numerator, denominator), `MathScript` (base, optional
   superscript, optional subscript), `MathRadical` (radicand, optional degree), `MathNary` (a big operator `∑ ∏ ∫ ∬ ∭ ∮ ⋃ ⋂`
   with optional lower and upper limit and a body), `MathDelimiter` (opening and closing character, one or more
   comma-separated items; `( ) [ ] { } | ‖ ⌊ ⌋ ⌈ ⌉ ⟨ ⟩` or none). **Matrices, accents, equation arrays and
   limits under a function (`lim`) are not in version 1** and are reported when read.
3. **Formats.**
   - **DOCX:** native `m:oMathPara` / `m:oMath` (Word, LibreOffice and others edit it).
   - **ODT:** a formula object: `draw:frame` with `draw:object` and the sub document `Object N/content.xml` holding
     MathML (`math:math`, media type `application/vnd.oasis.opendocument.formula`), numbered after charts and OLE
     objects. No replacement picture (LibreOffice renders the MathML itself; other readers show nothing).
   - **PDF:** the library lays the formula out and draws it itself (text glyphs of the document font for letters,
     digits and delimiters; fraction bars and radical signs as vector lines; big operators as glyphs of the document
     font, or as vector shapes when the font has no glyph for them, see below). A simple TeX-like layout: script size
     70 %, fraction axis at 0.25 em above the baseline, limits above/below for `∑ ∏` and beside for integrals.
4. **Reading.** DOCX `m:oMath` of the supported kinds and ODT formula objects with MathML of the supported kinds
   come back as `Equation`. Anything else in a formula is reported and the equation is read as far as it goes (an
   unsupported element becomes a `MathText` with its text content), never dropped silently.
5. **Limits.** At most 2,000 nodes and nesting depth 40 per equation (the limit and the recursion guard are checked
   before any writer runs, as for charts and diagrams).
6. **Fonts.** Math glyphs outside the document font are not substituted. Big operators and the radical are drawn as
   vector shapes so no math font is needed; the other characters follow the usual font fallback and its report.
7. **Equations in headers, footers, notes, table cells and text boxes.** Version 1: body and table cells only; others are
   reported and omitted in all three formats (as for charts).
8. **Swift port.** Same model and layout; the DOCX and ODT packages are byte-identical in the cross-parity job.

## Consequences

- Equations stay editable in Word (OMML) and LibreOffice (Math); the PDF is a faithful picture, not editable.
- The layout is deliberately simpler than TeX (no kerning tables, no italic correction, no stretchy delimiters
  beyond scaling the parenthesis glyph to the height of its content).
- No parser for LaTeX or linear format in version 1; the caller builds the tree (builder helpers for common cases
  may follow).

## Findings from the implementation (2026-11-18)

- The minus sign and the big operators (`∑ ∏ ∫ ∬ ∭ ∮ ⋃ ⋂`) are drawn as vector strokes in the PDF; the standard fonts have
  no glyph for them. Greek letters and other math symbols (`≤ ≥ ≠ α β`) are text and need an embedded font that has
  them; otherwise the PDF reports it and shows `?` (as for any text).
- ODT ignores the alignment of the `Equation` (the frame sits at the start of its line; the paragraph has no style of its own);
  the frame size is an estimate of the layout, LibreOffice redraws the formula from the MathML.
- DOCX and ODT read round trip: a `m:func` is read as a row (name, argument), `mfenced` and an `mrow` with matching bracket
  operators as a delimiter, an `m:oMath` inside a line of text as a display equation (reported).
- A formula that comes back from a file may have fewer rows than the one written (a row of one part is read as that part).
- ODT writes an equation in a table cell (the placement is generic); in headers, footers and notes it is reported and omitted,
  DOCX likewise. Text boxes: the DOCX writer writes it (the box holds paragraphs only in the model, so none can contain one).
- DOCX (2026-11-19): in a table cell the equation paragraph starts with an empty run: LibreOffice drops an `m:oMathPara` that is the only content of a cell paragraph (tested; in the body it shows it). Word shows the same either way; the reader ignores the empty run.
- ODT (2026-11-19): the alignment goes into the paragraph of the formula frame (automatic styles `MathLeft`, `MathCenter`, `MathRight`) and is read back from those names; before, every formula was left-aligned in LibreOffice.
- PDF (2026-11-19): an equation in a table cell is drawn, scaled down to the cell, left-aligned unless it asks for centre or right, tagged as Formula; before, it was left out with a warning.
- The italic font of the PDF may look upright in viewers that replace the standard fonts (poppler); the file asks for Helvetica-Oblique.

## Not decided here

Inline equations, matrices, accents, equation arrays, numbering/captions of equations, a LaTeX or linear-format
parser, MathML import from HTML, a math font for the PDF.
