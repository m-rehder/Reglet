# Reglet guide

Writer options per format and document templates in detail. For a first overview see the
[README](../README.md) and the [example gallery](examples.md).

## Format options

`DocumentWriter.WriteAsync` accepts optional, format-specific options
(`DocxWriteOptions`, `OdtWriteOptions`, `PdfWriteOptions`) based on `WriteOptions`:

- `UnsupportedContent` (`Warn`/`Fail`): unsupported features are collected as warnings or
  reported as errors — never silent data loss.
- `InvalidXmlCharacters` (`Replace`/`Fail`): handling of characters that XML 1.0 forbids.
- `Limits` (`ResourceLimits`): limits for image size, blocks, table cells, text length.
- `CollectMetrics`: collects `WriteResult.Metrics` (duration, pages, bytes); default is `false`.
- `StreamNonSeekableOutput`: also writes directly to a non-seekable target without building
  the output completely in memory (requires synchronous write access).

For Unicode in PDF the caller provides TrueType fonts (glyf-based `.ttf`). With
`FontCollection` (package `Reglet.Fonts`) real bold/italic files are embedded per
family; missing styles are reproduced synthetically:

```csharp
var fonts = FontCollection.Empty
    .Add(FontFace.LoadFile("NotoSans-Regular.ttf"))
    .Add(FontFace.LoadFile("NotoSans-Bold.ttf"))
    .AddDefault(FontFace.LoadFile("NotoSans-Regular.ttf")); // text without its own family

await DocumentWriter.WriteAsync(document, "report.pdf", new PdfWriteOptions { FontCollection = fonts });
```

`FontFace` validates the file on load and reports family, style, embedding rights and missing
characters (`GetMissingCodePoints`). The simpler variant `PdfWriteOptions.Fonts`
(family → bytes, synthetic styles) is retained. `WriteAsync` returns a `WriteResult` with the
collected warnings.

Further PDF options are optional:

- `Hyphenation` (`HyphenationPatterns`): the words are broken at the positions that the
  patterns of the caller allow (Liang algorithm, TeX format). The optional package
  `Reglet.Hyphenation` ships the TeX patterns of 24 languages (`HyphenationLanguages.Load("de")`);
  the other packages ship none. The soft hyphen (`U+00AD`) in the text is a break opportunity in every format.
- `Signature` (`PdfSignatureOptions`): the document is signed with the private key of a
  certificate (RSA); the PDF carries a detached CMS SignedData (RFC 5652) in its signature
  dictionary. With `Pades` the signature follows the PAdES baseline B-B (ETSI EN 319 142): the
  signed attributes name the certificate and the catalog holds a document security store.
  `Timestamp` (`PdfTimestampOptions`, a TSA URL or a responder delegate) adds an RFC 3161
  timestamp (B-T); `LongTermValidation` (`PdfValidationData`) appends the validation material as
  a document security store with a VRI entry in an incremental update (B-LT), and
  `PdfDocumentTimestamp.Add` appends a document timestamp. The caller supplies the certificate,
  the TSA and the validation data; trust and revocation are not evaluated.
- `Annotations` (`PdfAnnotation`): sticky notes, highlights, stamps and free text boxes;
  `PdfDocumentReader.ReadAnnotations` reads them back with their page.
- `ViewerPreferences`, `PageLayout` and `PageMode`: window flags and opening mode of the viewer;
  `PageTemplate.PictureWatermark` (PNG/JPEG) draws a picture watermark behind every page.

The DOCX and PDF writers can protect a document:

- `DocxWriteOptions.Encryption` (`DocxEncryptionOptions`) wraps the package in the ECMA-376 agile
  container; `DocxReadOptions.Password` or `DocumentReader.Read(stream, format, password)` opens
  it. `PdfWriteOptions.Encryption` writes R6/AES-256.
- `DocxWriteOptions.Signature` (`DocxSignatureOptions`) adds an OPC/XML signature over the parts
  of the package; `DocxSignatureReader.Read` verifies it and can check the certificate chain
  against caller-supplied trusted roots. `PdfSignatureReader.Read` does the same for PDF
  signatures and document timestamps (byte range, CMS signature, time-stamp token).

## Templates

A DOCX template supplies the design and named placeholders `{{Name}}`. When writing, the
template's package is adopted and its body and referenced headers/footers are patched; styles
and other parts are preserved:

```csharp
var template = DocumentTemplate.FromBytes(File.ReadAllBytes("letterhead.docx"))
    .Body("Content")                                 // the document's paragraphs land here
    .BindText("Date", DateTime.Today.ToString("d"));

await DocumentWriter.WriteAsync(document, "letter.docx", new DocxWriteOptions { Template = template });
```

The same applies to ODT via `OdtWriteOptions.Template` (patch of `content.xml`). For PDF,
`PdfWriteOptions.PageTemplate` instead defines headers/footers, watermarks and page frames
from the model.

A placeholder that stands alone in a paragraph is replaced by the bound blocks (headings,
lists, tables, images); a placeholder inside a paragraph by inline text. The paragraph and run
styles at the placeholder apply as the basis for the inserted content. If the body placeholder
is missing, the content is appended at the end; unbound placeholders remain and are reported.
The same bindings also replace placeholders in headers and footers. The body placeholder
applies only to the document body.

DOCX templates can also use Word `MERGEFIELD` fields with inline bindings such as `BindText`.
Simple fields and ordinary begin/separate/end fields are supported, including quoted names and
`\* MERGEFORMAT`, `\* Upper`, `\* Lower`, and conditional `\b` / `\f` text. The latter four
require text-only bindings; case switches cannot be combined with `\b` or `\f`.
Names are matched without regard to case. Nested fields, other switches,
block bindings and unbound fields remain unchanged and are reported; Word merge ranges are not
expanded by this feature.

Optionally declare a placeholder type before binding it:

```csharp
var checkedTemplate = template
    .Declare("Amount", TemplatePlaceholderType.Number)
    .BindText("Amount", "1234.50");
```

Types include text, ISO dates (`yyyy-MM-dd`), invariant decimal numbers, images and fields.
Invalid values throw `ArgumentException`; repeated-row values are checked before writing too.

To repeat several paragraphs or table rows, place `{{Repeat:People}}` and `{{EndRepeat}}` in
their own paragraphs or rows and provide the data with `template.RepeatRegion("People", rows)`.
Regions may nest using shared named data sets. Source content that cannot safely be duplicated
is reported and left unexpanded.

For different inner rows per outer row, use `RepeatNestedRegion` with `TemplateRegionRow`:

```csharp
var rows = new[]
{
    new TemplateRegionRow(new Dictionary<string, string?> { ["Customer"] = "Ada" },
        new Dictionary<string, IReadOnlyList<TemplateRegionRow>>
        {
            ["Items"] = [new TemplateRegionRow(new Dictionary<string, string?> { ["Item"] = "Book" })],
        }),
};
var nestedTemplate = template.RepeatNestedRegion("Customers", rows);
```

The corresponding `{{Repeat:Items}}` region uses that customer's rows. An explicitly empty inner
set produces no content; an absent set falls back to a shared set registered with `RepeatRegion`.

A rectangular named range in an Excel workbook can supply the rows. Its first row contains the
placeholder names:

```csharp
var customerRows = MergeDataSource.FromExcelFile("customers.xlsx", "Customers");
var customerTemplate = template.RepeatRegion("Customers", customerRows);
```

For DOCX files that also need to display in older Word versions, supply a raster preview of an SVG:

```csharp
var logo = new ImageBlock(svgBytes, ImageMediaType.Svg)
{
    RasterFallback = new ImageBlock(pngBytes, ImageMediaType.Png),
};
```

