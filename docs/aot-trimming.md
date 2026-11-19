# Native AOT, trimming and further target frameworks (phase T)

This page records the analysis and the measures of phase T: reflection and serialization
inventory (T01), trimming safety (T02), the AOT publish test (T03), further target frameworks
(T04) and the CI (T05).

## T01 – Reflection and serialization inventory

All shipped projects under `src/` were examined.

| Category | Finding | Location | Assessment |
| --- | --- | --- | --- |
| Reflection for type resolution | none | – | No `Type.GetType`, `Assembly.Load`, `Activator`, `MakeGenericType`. |
| Diagnostic `GetType()` | `block.GetType().Name` only in warning text | `PdfDocumentBuilder`, `PdfBlockRenderer` | Uncritical: the type is used statically; `Type.Name` is trim-safe. |
| Serialization | none | – | No `System.Text.Json`, `XmlSerializer`, `DataContract`, `BinaryFormatter`. |
| `dynamic` / `System.Linq.Expressions` | none | – | – |
| Enum reflection | `Enum.IsDefined(value)` (generic) | `Reglet.Core` | AOT-safe; no `Enum.Parse`/`GetValues`/`GetNames`. |
| Native interop | none | – | No `DllImport`/`LibraryImport`, no native libraries. |
| `Convert` | `Convert.ToString/ToByte/ToHexString` | `Reglet.Pdf` | AOT-safe. |
| `Regex` | only in tests | `tests/` | Not shipped. |

**Result:** apart from the diagnostic `GetType().Name` call, the library is free of reflection
and serialization. The BCL areas used — `System.Xml`, `System.Xml.Linq`,
`System.IO.Compression` and `System.Globalization` — are AOT-compatible.

## T02 – Trimming safety

The output paths use neither reflection nor dynamic code generation. Two package-free proofs
secure this:

1. **Automated source guard** `build/check-aot-safety.sh`: it aborts as soon as a forbidden API
   appears under `src/` (`System.Reflection`, `Activator`, `Type.GetType`, `Assembly.Load`,
   `MakeGeneric*`, `System.Text.Json`, `XmlSerializer`, `DataContract`, `BinaryFormatter`,
   `System.Linq.Expressions`, `DllImport`/`LibraryImport`, `dynamic`, …). The diagnostic call
   `block.GetType().Name` is deliberately allowed. The search is a text-based heuristic; the
   inventory (T01) and the functional AOT publish remain authoritative.
2. **Functional AOT publish** (T03): the whole application is natively compiled and executed;
   this proves the paths used under AOT.

`IsAotCompatible`/`IsTrimmable` are **deliberately not** enabled on the shipped projects: they
would pull in the NuGet package `Microsoft.NET.ILLink.Tasks` for every build and thereby weaken
the zero-dependency promise. Without `IsTrimmable`, `ilc` does not analyze the referenced
libraries, so the source guard takes over this task package-free.

## T03 – AOT publish test

`smoke/Smoke.Aot` publishes with `PublishAot=true` and produces DOCX, ODT and PDF from a model;
the header bytes are checked. `build/run-aot.sh` builds the library, publishes natively and runs
the application.

```
build/run-aot.sh
```

`Smoke.Aot` — like the other smoke apps — is deliberately not part of `Reglet.sln`; it is
built and run via the scripts that the CI also invokes.

## T04 – Further target frameworks

`net10.0` remains the only target framework (enforced by `ValidateTargetFramework`). `net8.0`
and `net9.0` are deliberately not added: according to the official lifecycle policy (as of
8 September 2026), their support ends **on 10 November 2026** — net9 as STS (extended from 18
to 24 months), net8 as LTS. A bridge for about six weeks justifies neither the triple
build/test matrix nor the package maintenance.

The next step is `net12.0` as the next LTS; the target is added once the SDK builds it without
extra packages and it passes all checks.

## T05 – CI

The CI runs the source guard (`build/check-aot-safety.sh`) on all matrix platforms and
additionally builds the native AOT publish (`build/run-aot.sh`) on `ubuntu-latest`. This is
representative because the library uses only the BCL and AOT compilation is platform-independent;
on all three platforms the step would be significantly slower and more error-prone. The further
TFMs are added with T04.

## Sources

[.NET Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[Trim warnings and annotations](https://learn.microsoft.com/dotnet/core/deploying/trimming/fixing-warnings)
[.NET releases and support](https://learn.microsoft.com/dotnet/core/releases-and-support)
