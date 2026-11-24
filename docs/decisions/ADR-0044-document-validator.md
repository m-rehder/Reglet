# ADR-0044: A document validator for all formats

Status: Accepted (2026-11-24); stages 1 and 2 implemented

## Context

A program that writes documents needs to know whether Word, Excel, LibreOffice or a PDF reader will open them without
complaint. For Office Open XML the usual tool is the OpenXML SDK's validator, which brings the SDK in as a dependency;
for ODF and PDF there are separate tools, mostly in Java. Reglet's own tests check its output part by part, but a user
cannot run those checks on a file. A validator belongs beside the readers and writers, and it should cover every
format Reglet handles, not only one.

## Decision

- **Package `Reglet.Validation`** with `DocumentValidator.Validate(stream)`, returning a `ValidationReport`: the
  detected format, findings (`ValidationIssue`: severity, part, line, message) and whether it is valid (no error). The
  format is recognised from the content. At most 1000 findings are collected. It depends on Core and Pdf; the facade
  does not reference it.
- **Severity** follows what a program does: an **error** is something a program refuses or offers to repair; a
  **warning** opens but may lose something or need a newer program.
- **Office Open XML (stage 1)**: part names (valid, unique ignoring case), `[Content_Types].xml` (every part has a
  content type, overrides name existing parts), relationships (a main part, unique ids, every internal target exists),
  well-formed XML, and the **ECMA-376 5th edition Transitional schemas** (part 4) plus the OPC schemas of part 2,
  embedded unmodified and compiled once with `System.Xml.Schema`. The schemas compile without a change.
- **Markup compatibility as Office 2019 reads it**: `mc:AlternateContent` becomes its first understood choice or its
  fallback; namespaces in `mc:Ignorable` that the schemas do not know are dropped; Microsoft's Office 2010+ extension
  namespaces (`http://schemas.microsoft.com/office/…`: `w14`, `w15`, `wps`, `wpg`, `x14`, …) are understood but not
  schema-checked. One used without being declared ignorable is a warning (Office 2007 cannot read it). This matches the
  target of the OpenXML SDK validator set to Office 2019, without its extension schemas.
- **Core properties** (`docProps/core.xml`) are checked for well-formedness only: their schema imports the Dublin Core
  schemas from the web, which a validator must not fetch.
- **ODF (stage 1)**: `mimetype` is the first entry, stored, without an extra field (an extra field is a warning), with
  an ODF media type; `META-INF/manifest.xml` exists, its `/` entry has the package's media type, it lists every file
  (except `mimetype` and `META-INF/`) and only existing ones; every XML part is well-formed. Encrypted parts are skipped.
- **PDF (stage 1)**: the `%PDF-` header (later than byte 0 is a warning), `%%EOF` in the last 1024 bytes, the
  cross-references read as the file states them (`PdfFile.ReadCrossReferencesStrict`, without the reader's repair),
  every offset landing on its `N G obj` header, every compressed object's stream listed, a trailer `/Size` covering
  every object and `/Root` the catalog (not checked for an encrypted file, which needs the password).
- **ODF schemas (stage 2)**: content, styles, meta and settings parts and the manifest are validated against the OASIS
  ODF RELAX NG schemas, 1.4 for parts that declare version 1.4 and 1.3 for all others (LibreOffice 26.8 writes 1.4).
  .NET has no RELAX NG validator, so Reglet.Validation has its own: James Clark's derivative algorithm over the
  simplified schema (shared patterns, cached derivatives), the XML Schema datatypes the ODF schemas use (facets
  included; XSD regular expressions translated), one finding per element that does not fit and then on with its
  parent, so a fault does not cascade. Markup in namespaces the schema does not know (LibreOffice's `loext`, …) is
  removed first: ODF calls such a document an extended conforming one. ID uniqueness and IDREF targets are not checked.
- **Stage 3**: deeper PDF structure (page tree, resources, fonts, streams). **PDF/A conformance stays with veraPDF**: its rule set is a project of its
  own. Microsoft's Office extension schemas could later be added under the Open Specifications terms to check the
  extension markup too.
- **Licence**: the Ecma schemas are used under Ecma's default copyright notice, which allows implementing the
  specification in conformant products by copying it; the notice is in THIRD-PARTY-NOTICES.md and ships in the package.
  The files are not modified.

## Consequences

- The validator found faults in Reglet's own output, fixed with it: DOCX story parts now declare the Word 2010
  extensions ignorable; ODT heading styles order paragraph before text properties, tracked-change regions carry
  `xml:id`, a pie chart keeps its categories in an x axis, and the table's border and cell padding (which ODF has no
  attribute for on a table; the cells draw the grid) are kept as `reglet:*` attributes instead of `fo:*` outside the
  schema (the reader still reads the old ones). Reglet's DOCX, XLSX, ODT and PDF output is valid.
- The package carries about 1 MB of schemas (compressed in the NuGet package).
- Line numbers are reported for XML parts; `ReaderGuard`'s depth limiter now passes line information through.
