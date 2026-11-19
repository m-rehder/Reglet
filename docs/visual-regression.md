# Visual regression (K11)

The image comparison `ImageDiff` decodes two PNGs and returns the fraction of clearly deviating
channel values (default tolerance 8/255). A self-test is part of the compatibility checks and
always runs.

The render step is **optional** and needs external tools. If the environment variables are set,
the check renders every fixture (DOCX → PDF → PNG) and compares it against approved baselines
under `tests/Reglet.CompatibilityTests/fixtures/baselines/`:

| Variable | Meaning |
| --- | --- |
| `REGLET_SOFFICE` | path to `soffice` (LibreOffice headless) |
| `REGLET_PDFTOPPM` | path to `pdftoppm` (poppler) |
| `UPDATE_BASELINES=1` | create/update baselines once |

If the renderer variables are not set, the render step is skipped with a message (exit code 0).
The tolerance is 1 % deviating channels of the drawn area (white in both images does not count).

## Limitations

- The baselines are checked in, but they depend on the LibreOffice version and the installed
  fonts (e.g. DejaVu on Linux). They apply only to the environment in which they were created; a
  different renderer produces expected deviations. For CI a fixed renderer state per platform is
  needed. If a baseline is missing, the check writes the current output as a new baseline and
  passes, so a baseline proves stability, not correctness. New baselines must be reviewed
  before checking them in.
- Only DOCX is rendered, and only the first page (`pdftoppm -singlefile`). ODT and the own PDF
  are not compared visually.
