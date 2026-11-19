# Build and project basis

## Project structure (C01)

| Project | Responsibility |
| --- | --- |
| `src/Reglet.Core` | Format-neutral document model and writer contracts. |
| `src/Reglet` | Facade, builder and format selection. |
| `src/Reglet.Docx` | DOCX writer (ECMA-376). |
| `src/Reglet.Odt` | ODT writer (ODF 1.3). |
| `src/Reglet.Pdf` | PDF 1.7 writer. |
| `src/Reglet.Fonts` | Caller-provided TrueType fonts: `FontFace` (validation, font information, embedding rights), `FontCollection` (families with bold/italic variants) and the TrueType parser. |
| `tests/Reglet.CompatibilityTests` | Non-shipped tests (own runner, phase K). |

The consumer smoke tests under `smoke/` reference the built DLLs; `Smoke.Aot` additionally
publishes with Native AOT (see [aot-trimming.md](aot-trimming.md)).

## Target framework (C02)

All projects build exclusively for **`net10.0`** (current LTS line, supported until November
2028). Further target frameworks are added only when they can be built and tested without
additional packages. The target framework is set centrally in `Directory.Build.props`; the
build target `ValidateTargetFramework` in `Directory.Build.targets` aborts the build as soon as
a project deviates from it.

## Compiler settings (C03/C04)

| Setting | Value | Reason |
| --- | --- | --- |
| `Nullable` | `enable` | Nullable reference types everywhere; no nullable warnings in the build. |
| `ImplicitUsings` | `enable` | Uniform in all projects; covers only the SDK default scope. |

Both values live centrally in `Directory.Build.props` so that all projects use the same
documented setting. Explicit `using` directives remain required for everything beyond the
implicit namespaces.

## Warnings and deterministic builds (C05/C06)

- `TreatWarningsAsErrors` is enabled: the build fails on any warning.
- `Deterministic` is enabled so that equal inputs produce equal binary data.
- `ContinuousIntegrationBuild` is set as soon as `CI=true` is set (Gitea Actions, GitHub
  Actions, Azure Pipelines). In CI this uses reproducible source paths in PDBs and packages.

## Zero dependency (C07)

Shipped projects (the six projects under `src/`, marked with `IsShippedLibrary=true`) must not
contain package or native references. This is enforced in two places:

1. **Build target `ValidateZeroDependency`** (`Directory.Build.targets`): aborts every build on
   all platforms as soon as package, native, external assembly or additional framework
   references appear in a shipped project. Bundled binary and font files inside a project are
   likewise blocked.
2. **`build/check-zero-dependency.sh`**: checks the project list in the manifest and searches
   the whole `src/` tree for binary and font files. It evaluates the references via MSBuild,
   even if they come from imported files.

```
build/check-zero-dependency.sh
```

## Dependency boundaries (C08)

- `Reglet.Core` is the root without project dependencies.
- `Reglet.Fonts` references only `Core` (error types).
- The writers `Docx`, `Odt` and `Pdf` reference `Core` and `Fonts`, because they embed the
  provided fonts (ADR-0021).
- The facade `Reglet` references `Core`, `Fonts` and the three writers.

Enforced by the build target `ValidateProjectDependencyBoundaries` for all shipped projects.
`build/check-architecture.sh` invokes the same MSBuild check without a full build:

```
build/check-architecture.sh
```

## License and zero-dependency statement (C09)

- Project license: **MIT** (`LICENSE` in the root directory).
- The zero-dependency promise is written human-readably in `ZERO-DEPENDENCY.md` and
  machine-readably in `build/zero-dependency.json` (fixed locations).
  `build/check-zero-dependency.sh` checks the statement against the actual projects and files.
- Font licenses are the caller's sole responsibility; no font files are shipped.
