# ADR-0025: Charts (own vector rendering in PDF, native chart parts in DOCX and ODT)

- **Status:** Accepted (2026-11-17, product owner); 3a PDF, 3b DOCX and 3c ODT written
- **Date:** 2026-11-17
- **Context:** "Charts and diagrams" row of "Out of scope" in the feature matrix.

## Decision

1. **One model type.** `Chart` is a block in `Core` (inline in the text flow, also in table cells): a
   `ChartKind` (`Column`, `Bar`, `Line`, `Area`, `Pie`, `Scatter`), series (`ChartSeries`: name, values, for a
   scatter also x values, optional color), optional category labels, a size, and the look: title, legend
   position, axis titles, value axis minimum and maximum, stacking, a palette. The model rejects empty
   series, non-finite values, a pie with more than one series and a scatter without x values.
2. **Stages, one per format.**
   - **3a PDF (done):** the chart is drawn as vectors by our own renderer with fixed, documented rules (no layout
     engine): title, legend, axes with ticks from a 1-2-5 scale, light grid lines, series in the palette,
     text in the font of the document (embedded in PDF/A). It is a figure with alt text in the tag tree.
   - **3b DOCX:** a native DrawingML chart (`word/charts/chartN.xml`, `c:chartSpace`) in an inline
     `a:graphicData`, with the values cached in the part. **No embedded workbook** (decision of the product
     owner): Word shows the chart but cannot edit its data; the data are edited through the library.
   - **3c ODT:** an embedded chart object (`draw:object` with a `chart:chart` sub document that carries its
     values in a local table), checked against LibreOffice.
3. **Reading.** An existing chart stays preserved by the round trip as before; modelling it comes later
   with its own decision. A chart written by the library is not read back as a `Chart` in the first
   version.
4. **Version 1 limits:** one chart type per chart (no combinations), no secondary axis, no 3D, no trend
   lines, no error bars, no data labels, no logarithmic scale, no category axis labels longer than the
   column width (they are shortened with an ellipsis in the PDF), no per-point colors.
5. **Reports instead of silent loss:** a feature a format cannot show is reported; values the font cannot
   show are replaced by a question mark and reported.

## Scope change

`scope.md` and the feature matrix move *Charts* from "Out of scope" to a section "Charts"; the row
"Charts and diagrams" shrinks to connectors and flow diagrams (second stage, not planned yet).

## Consequences

- Estimate XL over three formats; 3a is the largest single stage because the renderer is new.
- Tests: model rules, axis scale helper (nice ticks), structure of the written parts, a LibreOffice
  smoke check per kind, a visual check of the PDF.
- Swift port at the end together with barcodes and forms.
