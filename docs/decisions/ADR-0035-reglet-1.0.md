# ADR-0035: Publication as Reglet 1.0.0

Status: Accepted (2026-11-19)

## Context

The library was developed as "Reglet" in a private repository. It is complete for its scope (ADR-0001 to
ADR-0034) and has no users yet. Before the first public release it gets its own name, a logo, examples of its own and a
clean history; documentation and examples refer to no other library.

## Decision

- **Name:** Reglet (a reglet is the strip of wood that sets the space between paragraphs in metal type). Checked on
  2026-11-19: free on NuGet, no document, PDF or typesetting software of that name on the web, reglet.dev/.io unused.
- **Code:** the namespace `Reglet` becomes `Reglet`, projects and packages `Reglet`, `Reglet.Core`,
  `Reglet.Docx`, `Reglet.Odt`, `Reglet.Pdf`, `Reglet.Html`, `Reglet.Fonts`, `Reglet.Hyphenation`; `IRegletError`
  becomes `IRegletError`; environment variables `REGLET_*` become `REGLET_*`; files written name "Reglet" as
  producer/application. The model type `PortableDocument` (and its builder) keeps its name: it says what it is (a
  document that is not tied to one format) and does not clash with the many `Document` types of applications.
- **Version:** 1.0.0. The history of Reglet (tags v1.2.0–v1.9.0) stays in the private archive repository.
- **Repositories:** a new repository `Reglet` with one initial commit, on GitHub and on the home Gitea; the old
  repository is kept as an archive. Only the .NET library now; the Swift port follows later under the same name.
- **Examples:** a gallery of examples of its own, from "Hello World" to complete documents (invoice, report, letter
  with mail merge, form, PDF tools), each a small program that writes its output files; no example copies another
  library's sample.
- **No references to other libraries** in code, documentation and examples. Tests may keep neutral words.
- **Licence:** MIT, as before (Maik, 2026-11-19: a copyleft licence such as MPL 2.0 was discussed and not chosen).
  `THIRD-PARTY-NOTICES.md` carries the notices the shipped data needs: the Unicode License V3 for the bidi tables
  (Reglet.Core), Adobe's AFM licence for the core-14 character widths (Reglet.Pdf), and points to the hyphenation
  pattern notices (Reglet.Hyphenation); the packages ship the file.
- **Logo:** a petrol page with a dog-ear, a white round "R" and an orange full stop (`docs/logo/`); package icon of the
  NuGet packages.

## Consequences

- Every public name changes once; since there are no users this costs nothing outside the repository.
- The Swift port's parity tools point at the .NET project paths and namespace; they are updated when the Swift port is
  published.
