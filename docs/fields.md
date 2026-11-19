# Fields: calculation and caching (OF-C04)

One policy for DOCX, ODT and PDF. The rule is short: **a cached result is shown as it is, and a
field without a cached result is calculated when the file is written.** Page-dependent fields are
the only exception, because only the PDF lays out pages.

## The three states of a field

| State | Model | DOCX | ODT | PDF |
| --- | --- | --- | --- | --- |
| Cached result | `Field.Result` (or the typed field's value) | The code *and* the result are written; Word may recalculate with F9 | `SEQ`, `REF`, document information and page fields are written as ODF fields with the result; any other field keeps only its result as text and is reported (OF-C02, [feature matrix](feature-matrix.md#text-structure)) | The result is drawn |
| No cached result | `Field.Result is null` | The code is written after it is calculated; the calculated text is the cached result in the file | The calculated text is written | The calculated text is drawn |
| No value | The calculation returns `null` | The code is written without a result | Nothing is written | Nothing is drawn and it is reported |

`FieldResolver.Resolve(document, context)` applies the same rule inside the model: it returns a new
document whose fields carry the calculated text, and the input document stays unchanged. That is
the explicit recalculation step for a caller who wants the values in the model.

## What the calculation knows

The writers build the `FieldContext` themselves:

- **document properties** (`DOCPROPERTY`): title, subject, author, keywords, creation time and the
  custom properties of `DocumentMetadata`;
- **document variables** (`DOCVARIABLE`) from `DocumentMetadata.Variables`;
- **bookmark text** (`REF`) from the bookmarks of the document;
- **creation date** (`CREATEDATE`) from `DocumentMetadata.Created`;
- **sequence counters** (`SEQ`): they belong to the context, so the second `SEQ Figure` on the
  page is `2`, also across table cells and notes;
- **`IF`**, `QUOTE`, `MERGEFIELD` and nested fields (`FieldEvaluator`). The value of a nested field
  is one piece of data: its spaces and quotes do not split or end an operand. Fields nested deeper
  than 64 levels are not calculated. `ROMAN` above 3999 is written as a decimal number.
- **Date pictures** (`\@`): `y`, `M`, `d`, `H`, `h`, `m`, `s`, `t`; text in apostrophes (`'Uhr'`) is
  literal.

`FieldContext.MergeValues`, `PageNumber`, `TotalPages`, `SectionPages`, `Saved` and `Printed` are
caller-supplied: mail merge fills `MergeValues`, and only the PDF knows page numbers.

## Page-dependent fields

`PAGE`, `NUMPAGES`, `SECTIONPAGES`, `PAGEREF` and the table of contents need the layout:

- **PDF** lays the document out up to four times (scratch passes) until the page numbers are stable
  and draws them.
- **DOCX and ODT** do not paginate. They write what the context provides; without a value the field
  stays without a result. Word fills `PAGE` and `NUMPAGES` in headers and footers by itself, but
  in the body, and `PAGEREF` anywhere, only after an update (F9, printing); LibreOffice leaves
  `PAGEREF` empty until the fields are updated. Marking the fields dirty does not help: Word then
  calculates them before it has laid out the pages (checked with Word 16.114: "Page 1 of 1" in a
  two-page document). A caller who wants numbers in those files writes general fields
  (`new Field("PAGE")`) and resolves them with `FieldResolver` and a context that has the page
  values.

That is the documented boundary: the same field shows the same text in all formats **except** for
page-dependent fields, where only the PDF knows the layout.

## Tested

`tests/Reglet.CompatibilityTests/FieldPolicyChecks.cs` writes one document with the fields of
all three states through DOCX, ODT and PDF:

- a cached `DOCPROPERTY` result wins over the metadata in all three formats;
- calculated `DOCPROPERTY`, `DOCVARIABLE`, `CREATEDATE`, `SEQ` and `IF` fields appear in the same
  order in all three formats;
- a field without a value stays empty and is reported by the PDF;
- `FieldResolver` keeps a cached result and does not modify the input document.

`FieldCalculationChecks` covers the individual field codes (conditions, sequences, references,
dates, nested fields) and the typed fields are tested in `FieldChecks`.
