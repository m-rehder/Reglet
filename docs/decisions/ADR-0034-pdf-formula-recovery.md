# ADR-0034: Formula recovery from PDF

Status: Accepted (2026-11-19)

## Context

ADR-0033 recovers paragraphs, lists, tables and images from PDF. Display formulas stay text lines in reading order: a
fraction becomes three lines, a sum its limits as separate lines. The document model has real formulas since ADR-0031
(`Equation`, DOCX OMML, ODT MathML), so a PDF formula can become one.

## Decision

- Opt-in `PdfReadOptions.RecoverFormulas` (default false; only with `RecoverStructure`). Heuristic, so never silent.
- New input: rules. The content interpreter records thin filled rectangles (`re ... f`) and horizontal strokes (`m l S`)
  as positioned pseudo segments (fraction bars, radical overbars). Text recovery ignores them.
- Recognised, display equations only: a cluster of lines around a rule, or a short indented line with a relation sign
  or an n-ary operator. Inline formulas stay text.
- Tree building from geometry:
  - fraction: a rule with text above (numerator) and below (denominator) inside its horizontal span;
  - radical: a root glyph directly before a rule, radicand = the text under the rule;
  - script: a smaller piece above or below the baseline attaches to the piece to its left (superscript above, subscript
    below);
  - n-ary: sum/product with limits above and below, integral with scripts; the body is the rest of the row up to the
    next relation or additive operator;
  - text: single letters italic, digits, operators and function names upright; parentheses stay characters.
- An equation number at the right margin stays in the row as trailing text.
- Reported: `PDF: n formula(s) were recovered; the structure is a guess.` Not recognised: matrices, aligned multi-line
  equations, stretchy delimiters, accents, formulas drawn as images or Type 3 glyphs without names.

## Consequences

- Risk: prose with a centred "=" line becomes an equation; a table of formulas is not a table any more. Hence opt-in.
- Swift port with cross-checks of the recovered tree.

## Findings from the implementation (2026-11-19)

- Measured on a PDF of `x = (-b ± √(b²-4ac))/2a` (first noted here as written by LibreOffice; it was written by this library's own PDF writer via `EQUATION_OUT`): fraction, radical (overbar rule), scripts and the minus signs (drawn as rules, so a short rule in line with text and with nothing above or below becomes a "−") are recovered.
- Real arXiv paper (dvips/CM fonts): the flush-left numbered equations come back as equations with scripts (`|m_ee^(1)| = |U_e1|² m_1, (1)` and others).
- False positives that had to be fenced: link underlines and table borders (bar must sit 0.25 em above the baseline of a line, ±0.2 em), prose clusters (no letter run of 6+, at most 120 characters, 60 pieces, 6 numbers, balanced parentheses), figure axis labels (two remain in the arXiv paper). Wikipedia (74 pages), the Drucksache, the BITV report and the Berkeley guide give no equation.
- CMEX (TeX extension font, no ToUnicode, no Differences) gets a small code table for sums, products, integrals and roots.
- Swift port with the same tests; the cluster rules are the same code, checked by tests, not by a byte comparison of real PDFs.

### Bars as images and operators as outlines (2026-11-19)

- Fractions "without a bar" in the arXiv paper had a bar after all: dvips draws fraction bars and root overbars as thin image masks (inline images 0.48 pt high). An image whose CTM makes it at most 2 pt high and at least 2 pt wide is now also a rule (inline images and image XObjects; the XObject stays an image for the structure recovery). Equation (4) comes back as `⟨m⟩ ≃ m_ee^(2) = tan²θ/(1+tan²θ) √(Δm²⊙)`.
- Operators drawn as outlines (this library's PDF writer, and producers without a math font): stroked subpaths are classified by shape: sum (corners and a notch), integral (tall S-curve), union and intersection (U shapes), root sign (hook ending top right), vertical strokes (legs of a product sign under a rule) and closed loops (ring of a contour integral). Neighbouring integral outlines make ∬/∭. A shape becomes the operator text only in a formula: a root sign needs an overbar starting at its right end, every other operator a relation sign within 8 heights on its line. Check marks in tables (the shape of a root sign) and zigzags in charts stay out: with formulas on, only the arXiv paper changes among the five foreign PDFs.
- Rules come only from subpaths of exactly one horizontal segment, so the edges of an outline are not taken for fraction bars.
- Round trip: every big operator (∑ ∏ ∫ ∬ ∮ ⋃ ⋂) and a root with degree written by this library come back with their limits.
- Not recoverable still: fractions that are drawn with no bar at all (rare), operators as filled glyph outlines of an embedded font without names, matrices, aligned multi-line equations.

### Labels inside charts (2026-11-19)

- The two remaining false positives of the arXiv paper were axis labels of charts (`<m>=0.001 eV`, `<m>=0.2 eV`). Every painted path that is neither a rule nor an operator outline is now kept as "ink" (its bounding box; at most 4000 per content stream). A formula candidate is dropped when at least three pieces of graphics touch its area widened by one text height: ink, vertical strokes, images, and rules outside the candidate's own width (its own bars and minus signs lie within it; the ticks beside a label do not).
- arXiv: 6 equations, all real; the four other foreign PDFs unchanged.
- Risk: a formula inside a table cell with drawn borders, or set right next to a figure, is now missed (stays text).

### A real Word PDF (2026-11-19)

- `equation.docx` exported to PDF by Word (Microsoft 365, Cambria Math, Identity-H with ToUnicode): both formulas come back complete, `x = (−b ± √(b² − 4ac))/2a` and `∑_{i=1}^{n} x_i = ∫_0^1 f(t)dt + ∛y`. Word draws the bars as rectangles and everything else as text.
- Word writes variables as mathematical italic letters (𝑥 = U+1D465) and upright text as plain letters. When a formula contains such letters, they decide the style: mathematical italic letters (Latin, Greek, ℎ) become plain letters with italic style, plain letters stay upright (so "sin" is upright). Without them the old rule stays (a single letter is italic, as TeX means it). The recovered equation goes to DOCX and back.
- The Word PDF is not a test fixture (it embeds subsets of Microsoft fonts); a hand-made PDF with a ToUnicode map onto U+1D465 tests the rule.
