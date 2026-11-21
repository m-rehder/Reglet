# Contributing to Reglet

Thanks for your interest. This guideline describes the process and the quality gates.

## Principles

- **Zero dependency:** shipped projects use only the .NET Base Class Library. No new
  package, native or framework references (see [ZERO-DEPENDENCY.md](ZERO-DEPENDENCY.md)).
- **No reflection/dynamic code generation** in `src/` (see
  [docs/aot-trimming.md](docs/aot-trimming.md)).
- **Handling:** warnings are errors (`TreatWarningsAsErrors`); public members need XML
  documentation.
- **Language:** **English** for all code, messages, XML documentation and Markdown. Use
  English (US) spelling and wording.

## Development

The project structure, the target framework, the compiler settings and the enforced dependency
boundaries are described in [docs/build.md](docs/build.md).

```bash
dotnet build Reglet.sln --configuration Release
dotnet run --project tests/Reglet.CompatibilityTests --configuration Release
```

## Quality gates

A contribution counts as green when all scripts under `build/` pass (the CI runs them):

```bash
build/check-zero-dependency.sh   # no packages/binaries
build/check-architecture.sh      # dependency boundaries
build/check-aot-safety.sh        # no reflection/serialization/dynamic
build/check-package.sh           # package without foreign dependencies
build/run-smoke.sh               # consumer against the built DLLs
build/run-benchmarks.sh          # no performance regression
```

Golden tests are updated deliberately with `UPDATE_GOLDEN=1 dotnet run --project
tests/Reglet.CompatibilityTests`.

A change to the public API (or its XML comments) also regenerates the API reference; the CI
fails while it is stale:

```bash
dotnet run --project tools/ApiReference -- docs/api-reference.md
```

## Process

1. Describe an issue (goal, affected formats) or pick up an existing one.
2. Branch `phase-…` or `feature-…`, small, thematically separate commits.
3. Pull request against `main`; the text references the issue.
4. Review: at least one review along the project standards and the specification.
5. Merge only when all gates are green.

## Commits

Short, imperative subject line with a prefix (`feat`, `fix`, `docs`, `test`, `refactor`),
reference to the plan ID in the text. Larger phases are tagged and released at the end
(see [docs/versioning.md](docs/versioning.md)).

## License

By contributing you agree that your contribution is published under the
[MIT license](LICENSE).
