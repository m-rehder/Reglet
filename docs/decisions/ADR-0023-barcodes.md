# ADR-0023: Barcodes (vector symbols on the drawing layer)

- **Status:** Accepted (2026-11-17, product owner); implemented in .NET (Swift port open)
- **Date:** 2026-11-17
- **Context:** "Barcodes" row of "Out of scope" in the feature matrix; depends on the drawing layer
  ([ADR-0022](ADR-0022-drawing-layer.md))

## Decision

1. **No new block type.** A barcode is a `DrawingShape` of kind `Freeform` whose path is the dark
   modules (rectangles), filled with the foreground colour. DOCX, ODT and PDF already write and
   render it; no writer changes, no new format code.
2. **Factory in `Core`:** `Barcode.Create(BarcodeSymbology symbology, string content, ...)` returns the
   shape. `BarcodeSymbology`: `Code128`, `Ean13`, `Ean8`, `QrCode`. Options: module width (default
   0.33 mm), bar height for 1D codes (default 15 mm), colour (default black), quiet zone in modules
   (default 10 for 1D, 4 for QR), error-correction level for QR. The shape's bounding box includes the
   quiet zone; `AltText` carries the content unless the caller sets another text.
3. **Encoders are our own** (zero dependencies): Code 128 (sets A, B, C with automatic switching,
   check digit), EAN-13 and EAN-8 (check digit computed or verified), QR (versions 1–40, modes numeric,
   alphanumeric and byte, levels L/M/Q/H, Reed-Solomon, mask choice by penalty). Invalid content
   throws `ArgumentException` (EAN: digits and length; Code 128: characters 0–127; QR: capacity).
4. **Reading.** A barcode comes back from a file as an ordinary freeform with its alt text, not as a
   barcode (as for any shape); the content is not decoded.
5. **Version 1 limits:** no human-readable text line under 1D codes (the caller adds a paragraph), no
   background colour (the quiet zone is transparent; place the code on a light surface), QR with one
   segment per code (no mixing of modes), without ECI and structured append, no Data Matrix (later, same factory).

## Scope change

`scope.md` "Outside the current scope" and the feature matrix move *Barcodes* from "Out of scope" to a
new section "Barcodes". The Content row of the comparison loses "barcodes".

## Consequences

- Estimate M: the three writers are reused; the cost is the encoders and their tests.
- Tests: check digits, Code 128 and QR reference vectors, module-grid properties (quiet zone, finder
  patterns), and a round trip through the three formats. No decoder is installed here; an optional
  smoke check with `zbarimg` can follow.
- Swift port after the .NET acceptance, with a cross-parity document `barcodes`.
