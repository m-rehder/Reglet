# Zero-dependency statement

Reglet is built using nothing but the .NET Base Class Library. No shipped project
contains package references (`PackageReference`), external assembly or framework references,
native libraries or bundled font files. Build tools that would implicitly pull in a NuGet
package (e.g. the Roslyn analyzers from `Microsoft.NET.ILLink.Tasks`) are deliberately not
enabled, so that even the build stays package-free. AOT/trimming safety is instead verified
package-free via the source guard `build/check-aot-safety.sh` and additionally the AOT
publish (`build/run-aot.sh`).
At runtime no Office programs, browsers or external processes are started.

## Shipped projects

- `src/Reglet.Core`
- `src/Reglet` (facade)
- `src/Reglet.Docx`
- `src/Reglet.Odt`
- `src/Reglet.Pdf`
- `src/Reglet.Html`
- `src/Reglet.Fonts`

## Machine-readable statement

The statement lives at a fixed location: `build/zero-dependency.json`. It lists the shipped
projects and forbids additional references as well as bundled font files.

## Verification

- Every build enforces the rule via the MSBuild target `ValidateZeroDependency`.
- `build/check-zero-dependency.sh` additionally checks that the project list in the manifest
  matches reality and that no binary or font files lie under `src/`.

```
build/check-zero-dependency.sh
```

## Fonts

No font files are shipped. If a caller provides a TrueType file for Unicode, the license of
that file is the caller's sole responsibility. `FontFace.Embedding` and `FontFace.CanEmbed`
show the embedding rights stored in the file (OS/2 `fsType`); the PDF writer reports fonts
that must not be embedded.
