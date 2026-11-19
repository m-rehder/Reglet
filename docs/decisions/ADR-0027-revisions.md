# ADR-0027: Revisions (insertions and deletions modelled; accept and reject)

- **Status:** Accepted (2026-11-18, product owner: marker pairs, PDF default `Final`)
- **Date:** 2026-11-18
- **Context:** "Revisions, VBA" row of "Out of scope" in the feature matrix. Before this ADR, the DOCX reader reads a tracked
  insertion (`w:ins`) as plain text, drops a deletion (`w:del`, `w:moveFrom`) with a warning, and the ODT reader
  reports tracked changes; a changed document loses the tracking.

## Decision

1. **Model.** Marker pairs, as for comments. `TrackedChange` is an inline at the start of the changed range:
   `Kind` (`Insertion` | `Deletion`), integer `Id` (unique among the tracked changes of the document), `Author`,
   optional `Date`. `TrackedChangeEnd(id)` ends it. The content between the markers is ordinary inlines and may
   run across paragraphs. A start without an end (or an end without a start) is the writers' concern: reported
   and omitted. Ranges of different changes may nest or overlap in the text (a deletion inside an insertion);
   the readers produce only well-nested ranges. The model checks the single marker (id, author). No builder
   methods, as for comments and bookmarks.
2. **Version 1 covers** insertions and deletions of inline content (text, breaks, tabs, fields, images, notes).
   **Not in version 1** (read, reported, content kept as it stands): formatting changes (`w:rPrChange`,
   `w:pPrChange`, `text:format-change`), moves (`w:moveFrom`/`w:moveTo`: read as a deletion and an insertion,
   reported), changes to a paragraph mark, table rows, cells and section properties, and tracked changes in a
   header, footer or note (reported).
3. **Formats.**
   - **DOCX:** `w:ins` and `w:del` around the runs (`w:id`, `w:author`, `w:date`); deleted text as `w:delText`.
     `w:trackRevisions` in the settings is not written (it only switches tracking on for later edits). A range across
     paragraphs is written as one `w:ins`/`w:del` per paragraph with fresh `w:id`s (Word has no element across
     paragraphs).
   - **ODT:** an insertion is `text:change-start` / `text:change-end` around the text, a deletion a `text:change`
     point in the body; both have a `text:changed-region` in `text:tracked-changes` (`text:insertion` or
     `text:deletion` with `office:change-info`: `dc:creator`, `dc:date`). The deleted content lives in the region, so a deletion across paragraphs is written per paragraph (one region
     each); an insertion keeps its range across paragraphs (`change-start` and `change-end` may lie in different
     paragraphs).
   - **PDF:** `PdfWriteOptions.Revisions` = `PdfRevisionMode`: `Final` (default: insertions shown as plain text,
     deletions omitted; the default, as Word's "Final" view), `Original` (insertions omitted, deletions shown) or `Markup` (insertions underlined,
     deletions struck through, both in one colour per author from a fixed palette). No annotations.
4. **Accept and reject.** `DocumentRevisions.AcceptAll(document)`, `RejectAll(document)` and
   `Accept(document, id)` / `Reject(document, id)` return a new document (the model is immutable): accepting an
   insertion keeps its content, accepting a deletion removes it, rejecting does the opposite. The markers of the ids
   touched are removed. Paragraph marks stay: a paragraph left empty by a deletion across paragraphs is not
   merged with its neighbour (version 1).
5. **Reading and preserving.** The DOCX and ODT readers produce the markers instead of the current warning (every `w:ins`/`w:del` piece
   becomes its own pair with a fresh model id);
   a `w:del` run inside `w:ins` (insert, then delete by another author) is read as a deletion and reported. As for
   comments, a change the reader cannot map keeps its preserved XML (anchors of unmodelled changes stay).
6. **Reports instead of silent loss** for everything in point 2 that is not modelled.
7. **Templates.** A placeholder inside a tracked change keeps the change (existing cloning of tracked changes in
   the template binders stays as it is).

## Scope change

`scope.md` and the feature matrix move *Revisions* from "Out of scope" to a section "Revisions"; the row shrinks
to preserved VBA.

## Consequences

- Estimate L: the model and DOCX are small, ODT (regions outside the text flow) and the PDF markup are the work.
- Tests: model rules, structure of the written parts, round trips, accept/reject, LibreOffice round trip
  (DOCX and ODT: LibreOffice shows the changes in its "Manage Changes" list), PDF text per mode via the own text
  extraction.
- Swift port after the .NET block, with a cross-parity document `revisions`.
