# ADR-0026: Comments (modelled in DOCX and ODT, annotations in PDF)

- **Status:** Accepted (2026-11-18, product owner: comments first, then revisions); accepted and implemented in all three formats
- **Date:** 2026-11-18
- **Context:** "Comments, change tracking, VBA" row of "Out of scope" in the feature matrix. Before this ADR, a comment is reported when
  reading ("a comment is not mapped") and its anchor is lost when the body changes.

## Decision

1. **Model.** `Comment` is an inline at the start of the commented range, like a `Bookmark`: integer `Id`
   (unique within the document), author, optional initials and date, the text as a list of paragraphs
   (plain text), a `Resolved` flag and nested `Replies` (one level, a reply has no replies and no anchor).
   `CommentEnd(id)` ends the range; without it the comment is a point. There is no document-wide list
   (footnotes work the same way). The model checks the single comment (id, author, text, replies);
   a missing or doubled end is the writers' concern and reported there. No builder methods, as for
   bookmarks and footnotes.
2. **Formats.**
   - **DOCX:** `word/comments.xml` with `w:comment`, `w:commentRangeStart/End` and `w:commentReference` in the
     body; replies and `Resolved` through `word/commentsExtended.xml` (`w15:commentEx`, `paraIdParent`, `done`).
   - **ODT:** `office:annotation` (point) and `office:annotation-end` (range end) with `dc:creator`, `dc:date`;
     a reply is an annotation that names its parent by `loext:parent-name`; `Resolved` as `loext:resolved`.
   - **PDF:** opt-in `PdfWriteOptions.Comments = PdfCommentMode.Annotations` (`None` default, like a PDF exported
     from Word): one sticky note (`/Subtype /Text`, icon `Comment`, `/T` author, `/Contents` the text) per anchored
     comment, in the right margin level with its anchor, notes of one line side by side so the text stays readable.
     The replies follow the text in the contents (`Bob: Agreed`); the done flag and the date are not written (a
     text annotation has no field for the first, and `/IRT` threads and `/M` are not worth the objects). The
     notes use the existing annotation writer: print flag set, and with PDF/A an appearance stream
     (veraPDF PDF/A-2b: compliant). A comment in a header, footer or note is reported and not written.
3. **Reading and preserving.** DOCX and ODT comments are read into `Comment` and the markers, replacing the current
   warning. A comment whose anchor is damaged (range end without start, unknown id) is kept as a point
   at the nearest position and reported. A comment without a range (LibreOffice writes a bare `w:commentReference`) is a point. The comments part of a source file is replaced by the generated one; anchors whose comment the reader cannot map keep their preserved XML and the source part.
4. **Limits of version 1:** plain text body (character formatting is dropped and reported), no images or
   tables in a comment, no comment in a header, footer, text box or table-of-contents entry (reported),
   no `w:commentsIds` / `w16cid` durable ids, no modern threaded-comment parts (`w16cex`), no author
   photo.
5. **Reports instead of silent loss:** every dropped detail is a warning, never silent.
6. **Templates and `DocxProtection`.** A template placeholder inside a commented range keeps the
   comment; the `Comments` protection kind of `DocxProtection` stays as it is.

## Scope change

`scope.md` and the feature matrix move *Comments* from "Out of scope" to a section "Comments"; the row
"Comments, change tracking, VBA" shrinks to revisions (next ADR) and preserved VBA.

## Consequences

- Estimate L over three formats; the model and the DOCX pair is the larger half.
- Tests: model rules, structure of the written parts, round trip through DOCX and ODT, LibreOffice
  round trip (ODT and DOCX), PDF annotations read back with the own reader, PDF/A check with veraPDF.
- Swift port at the end of the comments-and-revisions block, with a cross-parity document `comments`.
