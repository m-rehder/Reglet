# ADR-0029: Embedded OLE objects (preserved as bytes, with a preview image)

- **Status:** Accepted (2026-11-18, product owner: DOCX and ODT, bytes only)
- **Date:** 2026-11-18
- **Context:** "Embedded objects" row of "Out of scope" in the feature matrix ("preserve, never render"). A Word
  document can hold an object another program owns (an Excel sheet, a drawing): `w:object` with a shape for the
  preview picture and an `o:OLEObject` that points to the data in `word/embeddings/`. An ODT holds it as
  `draw:object-ole` in a frame, with the picture in `ObjectReplacements/`. Today the readers report the object
  ("the embedded object '…' is not mapped") and a changed document loses it (the body is regenerated; the
  embedding part stays behind unreferenced). An untouched document keeps it because its package is adopted.

## Decision

1. **Preserve, never open, never run.** The data is opaque bytes. The library does not read the content of an
   embedded workbook, activate, update or render the object. Out of scope stays: reading the embedded content
   (also of an embedded `.xlsx`), linked objects (`o:OLEObject Type="Link"`, `xlink:href` outside the package),
   creating new objects from application data, OLE automation.
2. **Model.** `EmbeddedObject` is a block, like `ImageBlock`, `Chart` and `FormField` (the DOCX reader hoists it after the
   paragraph it stood in; the writers put it in a paragraph of its own): `ProgId` (string, may be empty), `Kind` (`OleStorage` = a compound
   file such as `oleObject1.bin`, `Package` = an Open XML package such as `Microsoft_Excel_Sheet.xlsx`), `Data`
   (non-empty bytes, copied), `FileExtension` (of a `Package`, `bin` by default), `Width` and `Height` (`Length`, the size on the page), optional `Preview` (an
   `Image`). Checked at construction: data not empty, size positive, preview a PNG or JPEG (SVG is not accepted: `v:imagedata` does not take it). Like the other object blocks it has no builder method.
3. **Preview image.** The preview is a picture. Word writes EMF or WMF previews, which the library does not
   decode (out of scope: EMF/WMF). Such a preview is **dropped and reported**; the object is kept, and the writers
   then draw an empty placeholder (a one-pixel transparent PNG) at the size of the object so the file stays valid.
   Word and LibreOffice redraw the preview when the object is activated.
4. **Formats.**
   - **DOCX:** `w:object` in a run of its own paragraph: `v:shape` (size, `o:ole=""`) with `v:imagedata` for the preview, and
     `o:OLEObject` (`Type="Embed"`, `ProgID`, `ShapeID`, `DrawAspect="Content"`, `ObjectID`, `r:id`). The data goes
     to `word/embeddings/oleObjectN.bin` (relationship type `…/oleObject`, content type `application/vnd.openxmlformats-officedocument.oleObject`)
     or `word/embeddings/PackageN.<ext>` (relationship `…/package`). Read back in both kinds. A linked object, or one
     whose data part is missing, is reported and omitted.
   - **ODT:** `draw:frame` (as-char) with `draw:object-ole` (`xlink:href="./Object N"`) and `draw:image` for the
     preview (`./ObjectReplacements/Object N`), both parts listed in the manifest (`application/vnd.sun.star.oleobject`
     for the data). Only `OleStorage` is written; a `Package` object cannot be stored as an ODF object-ole and is
     **reported and omitted** (no conversion between the kinds). Read back: `draw:object-ole` becomes `OleStorage`;
     `draw:object` that is not a chart stays reported as before.
   - **PDF:** the preview image is drawn at the object size; without a preview nothing is drawn and the object is
     reported once. No annotation, no attachment.
5. **Where the object may stand.** In body paragraphs, headings, list items and table cells. In headers, footers,
   notes and comments it is reported and omitted (as for tracked changes).
6. **Limits.** The data is read through the reader's general decoded-size bound; no limit of its own. The preview
   follows the image limits.
7. **Preserving paths.** The untouched-document path keeps the package as it is (as today). The rebuild path
   writes the object from the model and no longer copies the orphaned embedding part for an object the reader
   mapped; parts of objects the reader could not map keep being copied (as other unknown parts).
8. **Swift port.** Same model and the same packages, byte-identical in the cross-parity job.

## Consequences

- A document with an embedded workbook survives edit and save in DOCX and ODT. Before, it lost the object.
- A DOCX → ODT conversion keeps an `OleStorage` object and drops a `Package` object with a report. The reverse
  (ODT → DOCX) keeps what ODT holds.
- Without an EMF decoder the preview of most Word objects is replaced by an empty placeholder until the object is
  activated in the editor. That is a visible loss and reported; it is the price of the image scope.
- The library cannot say whether an object is harmful. Nothing is executed or opened; Word's own security applies
  when the file is opened.

## Not decided here

Decoding EMF/WMF previews; linked objects; an object in a header, footer or note; the `DrawAspect="Icon"` view
(read as an ordinary object, the aspect is not kept); embedded objects in PDF as attachments.

## Word check (2026-11-19)

- An embedded workbook written without `FileExtension` became `Package1.bin` (`application/octet-stream`); Word showed
  the preview but a double click failed ("server application not found"). The DOCX writer now takes the extension from
  the ProgID when none is given (Excel.Sheet → xlsx, Word.Document → docx, PowerPoint → pptx).
