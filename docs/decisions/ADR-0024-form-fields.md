# ADR-0024: Form fields (AcroForm, content controls, form controls)

- **Status:** Accepted (2026-11-17, product owner); implemented in .NET for PDF, DOCX and ODT (Swift port open)
- **Date:** 2026-11-17
- **Context:** "Forms and embedded objects" row of "Out of scope" in the feature matrix.

## Decision

1. **One model type.** `FormField` is a block in `Core` (placed in the text flow like a shape, also in
   table cells): kinds `Text` (single or multi line), `CheckBox`, `DropDown` and `RadioButton`, with name,
   size, value or checked state, options, export value (radio), maximum length, read-only, required,
   font size and tool tip. The model rejects an empty name, a name with a period, a drop-down without
   options and a value that is not one of them.
2. **Three stages, one per format.**
   - **2a PDF (done):** one widget annotation per field with appearance streams (no
     `NeedAppearances`), `/AcroForm` in the catalog next to a signature field, a parent field for each
     group of radio buttons (`/Kids`, `/Ff` radio and no-toggle-off). Only new fields are written.
   - **2b DOCX (done):** content controls (`w:sdt`) for text (`w:text`, also multi-line), drop-down
     (`w:dropDownList`) and check box (`w14:checkbox`), written as a paragraph that holds the control and
     read back as a form field. The name is the tag, the tool tip the alias, read-only a content lock, an
     empty text or drop-down shows its tool tip (or name) as placeholder (`w:showingPlcHdr`). A radio
     button has no Word control: it is written as a check box with the tag `pd-radio:name=value`, reported,
     and restored when reading. A date control is not modelled and stays text.
   - **2c ODT (done):** form controls in `office:forms` (`form:text`, `form:textarea`, `form:checkbox`,
     `form:listbox` with `form:dropdown`, `form:radio`), one `draw:control` per field in its own paragraph.
     ODF has real radio buttons: the buttons of a group share the control name, the export value is
     `form:value`. Name, tool tip (`form:title`), read-only, maximum length, value, size and the
     check/selection state are written and read; `Required` and `FontSize` have no counterpart and are
     reported. Fields in headers and footers are not written (the controls live in `content.xml`) and are
     reported. Reading accepts the file LibreOffice saves (`form:text` with line breaks in the value is a
     multi-line field); a control kind the model does not have is reported.
3. **Writing first, filling later.** Reading existing AcroForms and filling their values (with new
   appearance streams) is a later step with its own decision. Flattening is not part of 2a either.
4. **PDF/A is allowed.** Fields are written with an appearance in the embedded document font, the
   print flag, no actions and no JavaScript; `NeedAppearances` is never set. The AcroForm default
   appearance names Helvetica (`Helv`) outside PDF/A so a viewer that edits a value has a font, and the
   embedded document font in PDF/A (its resources are the default resources).
5. **Reports instead of silent loss.** A field name used twice (a second field would share the value of
   the first), a radio button without export value and a repeated radio value are reported and the field
   is skipped. A value with characters the font cannot show is drawn empty and reported. Until 2b and
   2c exist DOCX and ODT report a form field as not written.
6. **Out of scope here:** OLE objects (preserve, never render), VBA and macros, JavaScript and actions,
   submit buttons, signature fields as a model type (`PdfWriteOptions.Signature` stays), push buttons,
   list boxes, fields inside headers and footers (a repeated name is reported).

## Limits accepted in 2c

- `form:control-implementation` is left out on purpose: LibreOffice drops every control that carries it
  (checked 2026-11-17: with the attribute no control survived a load and save, without it all nine did).
- No control style is written, so LibreOffice uses its defaults. In a headless PDF export on a Mac with a
  dark appearance the multi-line text area came out with a dark background; the other controls were
  normal. Whether the same happens in the LibreOffice window is not checked.
- The placement is a paragraph per field (`as-char` anchor), as for DOCX; a field next to text needs a
  table.

## Limits accepted in 2b

- A content control has no size, so width and height are not stored (a field read from DOCX has the
  default size); `Required` and `MaxLength` have no counterpart and are reported when set.
- Reading takes controls inside a paragraph: the field becomes a block before the rest of the
  paragraph (a check box with a label in the same paragraph comes back as the field above the label). A
  block-level control and any other control type are read as text, as before.
- The check box and its states use the `w14` namespace (Word 2010 and later, LibreOffice). Word is not
  automated: that it opens the file and shows the text was checked, the click behaviour of the controls
  was not.

## Limits accepted in 2a

- The widgets are not part of the structure tree; the tool tip (`TU`) is the accessible name. Tagged
  form fields (PDF/UA) need an own step.
- Appearance streams show the value drawn once; a viewer redraws them when the user edits a field.
- A drop-down is a combo box without free text; a text field has no comb or password variant.

## Scope change

`scope.md` and the feature matrix move *Forms* from "Out of scope" to a section "Form fields"; the row
"Forms and embedded objects" shrinks to OLE objects.

## Consequences

- Estimate XL over three formats; 2a is M because the annotation, appearance and AcroForm code exists.
- Tests: model rules, the written structure, reported problems, a signed document with a form field,
  PDF/A-2b with veraPDF and a read-back of types, values and radio groups with Apple PDFKit (by hand).
- Swift port at the end together with barcodes and charts.
