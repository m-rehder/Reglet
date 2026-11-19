# Versioning and changelog (phase V)

## Scheme (V03)

Reglet follows **SemVer** (`MAJOR.MINOR.PATCH`):

- **MAJOR** for incompatible changes of the public API.
- **MINOR** for backward-compatible features.
- **PATCH** for backward-compatible fixes.

The 1.x line is the **development line**: the public API changes there without a major bump and
without compatibility shims. The public release is a fresh **1.0.0** under a new name after the
repository cleanup; that release starts the SemVer promise.

Pre-release versions carry a suffix (`1.9.0-rc.1`).

## Maintaining the version

The version lives centrally in `Directory.Build.props` (`<Version>`); the package consumer
`smoke/Smoke.Package` references the same version. A script sets both:

```bash
build/set-version.sh 1.9.0
```

## Changelog

The changelog is [docs/release-notes.md](release-notes.md). During development changes are
collected under `## Unreleased`; at release the section is renamed to `## X.Y.Z`. For the
release the section is extracted:

```bash
build/release-notes.sh 1.9.0 > RELEASE-NOTES.md
```

The script prints the content of the `## 1.9.0` section to standard output and aborts if the
section is missing.

## Release process (V02)

1. Set the version: `build/set-version.sh X.Y.Z`.
2. `docs/release-notes.md`: `## Unreleased` → `## X.Y.Z`.
3. Green gates: `build/release.sh X.Y.Z` builds, tests, checks, packs, signs and produces
   checksums (see [packaging.md](packaging.md)).
4. Push the tag `vX.Y.Z`; the release workflow
   ([.gitea/workflows/release.yml](../.gitea/workflows/release.yml)) runs `build/release.sh`,
   which uploads the packages to the Gitea package registry, and then `build/gitea-release.sh`,
   which creates the Gitea release with the extracted changelog and attaches the packages and
   `SHA256SUMS`. If the release already exists (e.g. created by hand), only missing assets are
   attached, so the step can be repeated. The workflow needs an online Actions runner with the
   label `ubuntu-latest`; without one the run stays queued.

The tag workflow expects the following configuration in the repository:

| Name | Type | Purpose |
| --- | --- | --- |
| `SIGNING_CERT_BASE64` | Secret | base64-encoded signing certificate (`.pfx`) |
| `SIGNING_CERT_PASSWORD` | Secret | certificate password |
| `TIMESTAMPER` | Variable | optional timestamp service (RFC 3161) |
| `NUGET_SOURCE` | Variable | URL of the NuGet/Gitea package registry |
| `NUGET_TOKEN` | Secret | API token for the registry |
| `GITEA_TOKEN` | automatic | job token for the release; the job requests `contents: write` |

## Package signing (V01)

Release packages carry an author signature (`dotnet nuget sign`); checksums alone (L10) do not
suffice. The signature needs a certificate:

```bash
REGLET_SIGN_CERT=/path/to/certificate.pfx \
REGLET_SIGN_PASSWORD=… \
REGLET_TIMESTAMPER=http://timestamp.digicert.com \
  build/sign-packages.sh
```

`build/release.sh` invokes signing automatically and **aborts** if no certificate is
configured. For dry runs without a certificate, `REGLET_ALLOW_UNSIGNED=1` can be set.

## Zero-dependency audit (V04)

Every release confirms the zero-dependency statement: `build/release.sh` runs
`build/check-zero-dependency.sh` (source) and `build/check-package.sh` (packed NuGet). Both must
be green; the result goes into the release check.
