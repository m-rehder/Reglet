# Packages and release (phase L)

## Packages (L01/L02)

One package per project. Users normally install only the facade `Reglet`; it pulls in the
writers as a dependency. Advanced users can reference individual packages.

| Package | Content |
| --- | --- |
| `Reglet` | Facade and builder (recommended entry) |
| `Reglet.Core` | Model, value types, writer contracts |
| `Reglet.Fonts` | Caller-provided fonts: `FontFace`, `FontCollection` (depends on `Core`; `Pdf` depends on `Fonts`) |
| `Reglet.Docx` | DOCX writer |
| `Reglet.Odt` | ODT writer |
| `Reglet.Pdf` | PDF writer |
| `Reglet.Hyphenation` | Optional: TeX hyphenation patterns of 13 languages (depends on `Core` only; the facade does not pull it in) |

```csharp
var document = PortableDocument.Create()
    .Metadata(title: "Report")
    .Heading("Title", level: 1)
    .Paragraph("Text")
    .Build();

await DocumentWriter.WriteAsync(document, "report.docx");
await DocumentWriter.WriteAsync(document, "report.odt");
await DocumentWriter.WriteAsync(document, "report.pdf");
```

## Package validation (L03)

`EnablePackageValidation` is enabled; during packing the public API and the supported target
frameworks are checked.

## Sources and symbols (L04)

Every package contains repository metadata (URL and commit; the URL is the public GitHub remote
`https://github.com/m-rehder/Reglet`), a
`.snupkg` with symbols and embedded sources (`EmbedUntrackedSources`), and the build is
deterministic. An automatic Source Link mapping to online sources would require a build-time
package (`Microsoft.SourceLink.*`); that violates the zero-dependency rule and is therefore
deliberately not used.

## Zero-dependency check of the package (L08)

`build/check-package.sh` packs the library and checks that no package contains foreign
dependencies, foreign DLLs or font files.

## Checksums (L10)

`build/checksums.sh` produces `artifacts/packages/SHA256SUMS` for all `.nupkg`/`.snupkg` after
packing and immediately checks the file against the packages. The file is published together
with the packages; consumers verify with `sha256sum --check SHA256SUMS` (macOS:
`shasum -a 256 --check SHA256SUMS`). No checksums were produced for 1.0.0.

## Beta and release (L09/L10)

`build/check-beta.sh` packs the library into a local feed and builds/runs `smoke/Smoke.Package`,
which references only the NuGet package `Reglet` (local `NuGet.config`). This proves
package consumption without third-party sources; the two examples under `examples/` additionally
serve as independent consumers. A version is released once all gates are green; the package
version lives centrally in `Directory.Build.props`, and the package consumer
`smoke/Smoke.Package` references the same version.

## Automated release (phase V)

The steps above are bundled in scripts; details in [versioning.md](versioning.md):

| Step | Script | Content |
| --- | --- | --- |
| Version (V03) | `build/set-version.sh X.Y.Z` | sets `Directory.Build.props` and `Smoke.Package` |
| Changelog (V03) | `build/release-notes.sh X.Y.Z` | extracts the section from `release-notes.md` |
| Release (V02) | `build/release.sh X.Y.Z` | checks, builds, tests, packs, signs, checksums, optional push |
| Signing (V01) | `build/sign-packages.sh` | `dotnet nuget sign` (needs `REGLET_SIGN_CERT`) |

`build/release.sh` runs **every** release through the zero-dependency audit
(`check-zero-dependency.sh` and `check-package.sh`, V04) and aborts without a signing
certificate (dry run: `REGLET_ALLOW_UNSIGNED=1`). Registry publication happens via
`REGLET_NUGET_SOURCE`/`REGLET_NUGET_TOKEN`. The tag workflow
[.gitea/workflows/release.yml](../.gitea/workflows/release.yml) invokes `build/release.sh`.

## Remotes and publication (L10)

The repository has exactly one remote per server:

| Remote | Server |
| --- | --- |
| `github` | `https://github.com/m-rehder/Reglet.git` (public) |
| `gitea` | a private Forgejo mirror that runs the CI and release workflows |

`main` tracks `github/main`. A release tag `vX.Y.Z` is pushed to both servers:

```bash
build/set-version.sh X.Y.Z        # set the version
build/release.sh X.Y.Z            # check, pack, sign, checksums
git tag -a vX.Y.Z -m "Version X.Y.Z"
git push github main vX.Y.Z
git push gitea main vX.Y.Z
```

Pushing the tag triggers the release workflow, which uploads the packages to the Gitea package
registry. A release is then created on both servers with the artifacts from
`artifacts/packages/` (one `.nupkg` and one `.snupkg` per package, `SHA256SUMS`, `RELEASE-NOTES.md`):

* GitHub: `gh release create vX.Y.Z artifacts/packages/* --notes-file <notes>`
* Gitea: `POST /api/v1/repos/{owner}/{repo}/releases` and per artifact
  `POST /api/v1/repos/{owner}/{repo}/releases/{id}/assets?name=<name>` (authentication via token;
  `tea` is not installed).

The mirror runs Forgejo (Gitea-compatible API, same endpoints as above).

## PDF/A validation (R01)

Archive PDFs can optionally be checked with the external validator **veraPDF**. It is not part
of the offline build; the `PDF/A` suite runs it only when the environment variable
`REGLET_VERAPDF` points to the CLI:

```bash
REGLET_VERAPDF=/path/to/verapdf \
  dotnet run --project tests/Reglet.CompatibilityTests --configuration Release
```

Without the variable the validation step is skipped; CI (`.gitea/workflows/ci.yml`) installs
veraPDF 1.30.4 and sets it. The structural check (OutputIntent, `/ID`,
XMP, embedded fonts, attachments) always runs.

## Optional external checks

The compatibility suite runs further independent checks only when the matching variable is set:

| Variable | Tool | Check |
| --- | --- | --- |
| `REGLET_SOFFICE` | LibreOffice `soffice` | office smoke, LibreOffice cross-format import and LibreOffice-produced PDF preservation |
| `REGLET_PDFTOPPM` | poppler `pdftoppm` | visual regression (also needs `REGLET_SOFFICE`) |
| `REGLET_VERAPDF` | veraPDF | PDF/A-2b/3b |
| `REGLET_MSOFFCRYPTO` | Python with `msoffcrypto-tool` | DOCX encryption |
| `REGLET_XMLDSIG` | Python with `lxml` and `cryptography` | DOCX XML signature |
| `REGLET_PYHANKO` | Python with `pyHanko` | PDF signatures and timestamps |

The Python variables point to the interpreter, for example
`REGLET_PYHANKO=/path/to/venv/bin/python`.
