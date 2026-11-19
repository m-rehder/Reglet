# ADR-0028: Preserve VBA projects in DOCX (opt-in, never executed)

- **Status:** Accepted (2026-11-18, product owner: preservation yes, default off)
- **Date:** 2026-11-18
- **Context:** "VBA" row of "Out of scope" in the feature matrix. A macro-enabled Word file (`.docm`) holds its
  macros in the binary part `word/vbaProject.bin`. The body-unchanged path copies every part, but a document
  whose body changed is rebuilt from the model and loses the project (and the macro-enabled content type).
  Macros are also the classic carrier of malicious code, so keeping them must be a choice of the caller.

## Decision

1. **Preserve, never execute.** The library does not run, parse, create or change VBA. `vbaProject.bin` is an
   opaque block of bytes. Out of scope stays: executing macros, editing or generating VBA code, signing a
   project, `vbaData.xml` and macro-sheet content.
2. **Model.** `MacroProject` (record, `Data` = the bytes of `vbaProject.bin`, non-empty, copied and immutable),
   carried by `DocumentMetadata.MacroProject` like the custom XML parts. Only DOCX stores it; ODT and PDF ignore it.
3. **Default off.**
   - **Reading:** `DocxReadOptions.KeepMacros` (default `false`). Off: the project is not read into the model and
     is dropped, with the warning "DOCX: the macro project is not read; set KeepMacros to keep it." This holds on
     the body-unchanged path as well: the part, its relationship and the macro-enabled content type are removed
     from the copied package, so a `.docm` read and written with the defaults comes out as a plain `.docx`
     package. On: the bytes go into `Metadata.MacroProject`.
   - **Writing:** a document with `MacroProject` writes the part, the relationship from the document part
     (`…/relationships/vbaProject`), the default type for `.bin` and the macro-enabled main content type
     (`application/vnd.ms-word.document.macroEnabled.main+xml`). Setting `MacroProject` is the explicit act; no
     writer option is needed, and without it nothing macro-related is written. `vbaData.xml` is not carried.
4. **Limit.** No limit of its own: the part is read through the reader's general decoded-size bound
   (`ReaderGuard.OpenBounded`), like every other part.
5. **Reporting.** Only the drop is reported. A kept project is visible in `Metadata.MacroProject`. The file
   extension is the caller's concern (`.docm` for a package with a project; Word refuses a project in a `.docx`).
   The three write paths agree: an untouched document with the same project keeps its package byte for byte; a
   document whose model has no project drops the part, relationship and macro types; a model project the source
   did not have (or has with other bytes) is written from the model.
6. **Swift port.** Same model and option (`MacroProject`, `keepMacros`), byte-identical
   packages in the cross-parity job.

## Consequences

- A `.docm` survives read, edit and write when the caller opts in; nothing changes for every other caller.
- Dropping by default is a behaviour change only for the body-unchanged path of `.docm` files, which used to keep
  the project by accident. It is reported, so it is not silent.
- The library cannot say whether a project is harmful. The opt-in and the limit are the whole defence; opening
  the file in Word stays under Word's macro security.
- A project belongs to the document it came from: names in it may refer to bookmarks, styles or controls that an
  edit removed. This is not checked.

## Not decided here

Reading VBA source for display or audit, signing projects, `.dotm` templates as a separate format value
(a template with a project is the same package with another main content type; the first version keeps the
type of the source and writes the document type otherwise).
