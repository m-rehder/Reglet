# Performance, memory and diagnostics (phase U)

This page documents throughput and memory consumption (U03/U04) as well as the regression
thresholds of the CI (U06). U01 (streaming) and U05 (metrics) are described in the
[README](../README.md) and in the [release notes](release-notes.md).

## Measurement method

`benchmarks/Reglet.Benchmarks` runs fixed scenarios and measures per scenario:

- **Duration** with `Stopwatch` (a warm-up run before; JIT and caches are warm).
- **Allocated bytes** as the difference of `GC.GetTotalAllocatedBytes(precise: true)`.
- **Retained bytes** as the difference of `GC.GetTotalMemory(forceFullCollection: true)`.
- **Output bytes** (size of the produced file).

Peak consumption is represented via the **allocations**. The working-set peak of a
long-lived measurement process is dominated by garbage collection and runtime and cannot be
measured reliably per scenario; the allocations are more deterministic and more meaningful for
regressions.

## Streaming (U01)

For **seekable** targets (files, memory streams) DOCX, ODT and PDF write directly and without a
complete intermediate buffer — this is the normal case for large files. For **non-seekable**
targets the buffered, async-safe path remains the default (no partial output on errors, works
with `AllowSynchronousIO = false`); `WriteOptions.StreamNonSeekableOutput` writes directly and
requires synchronous write access.

## Reference profile

Measured on macOS (arm64), .NET 10, release. The numbers are reference points; the thresholds
are a multiple of them.

| Scenario | Duration | Allocated | Output | Output bytes |
| --- | ---: | ---: | ---: | ---: |
| `pdf-1000-pages` | ~15 ms | ~9.5 MB | 1,000 pages | ~0.5 MB |
| `pdf-10000-pages` | ~120 ms | ~100 MB | 10,000 pages | ~5.3 MB |
| `docx-10000-blocks` | ~8 ms | ~4 MB | 10,000 paragraphs | ~30 KB |
| `odt-10000-blocks` | ~9 ms | ~6 MB | 10,000 paragraphs | ~27 KB |
| `pdf-large-image` | ~33 ms | ~37 MB | 2000×2000 PNG | ~54 KB |
| `parallel-8x1000-pages` | ~44 ms | ~74 MB | 8×1,000 pages | ~4 MB |

**Assessment:** PDF allocates a content buffer per page and compresses it; the allocations
therefore grow linearly with the page count (~10 KB per page). DOCX and ODT write their parts
directly into the ZIP package and allocate significantly less. The large image is decoded when
embedding (2000×2000 = 4 megapixels), hence the ~37 MB. Parallel writes scale without shared
state (no locking); the eight runs take about as long as a single run of the same size plus
load.

## Thresholds and CI (U06)

The limits are in `build/benchmarks.txt` (duration and allocations per scenario) and are
deliberately generous so that slower CI machines do not flake; coarse regressions are still
detected (an exceedance aborts the run with exit code 1).

```
build/run-benchmarks.sh
```

The CI runs this on `ubuntu-latest`.
