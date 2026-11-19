#!/usr/bin/env bash
#
# Builds and runs the example gallery (examples/Gallery) into a temporary directory. The gallery reads every file it
# writes back, so a broken example fails here. The browser demo (examples/LivePreview) is only compiled.
#
# Usage:  build/run-examples.sh
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

dotnet build Reglet.sln --configuration Release --verbosity quiet --nologo

# Not run here (it needs a browser), but it has to keep compiling.
dotnet build examples/LivePreview --configuration Release --verbosity quiet --nologo

dotnet run --project examples/Gallery --configuration Release --verbosity quiet -- --out "$out"

# Spot checks: the first and the most complete examples exist in every format.
for file in basics-hello-world.docx basics-hello-world.odt basics-hello-world.pdf basics-one-model-four-formats.html \
            showcase-invoice.pdf showcase-report.docx showcase-business-letter.odt pdf-digital-signature.pdf; do
  if [ ! -f "$out/$file" ]; then
    echo "ERROR: example output missing: $file" >&2
    exit 1
  fi
done

count="$(find "$out" -type f | wc -l | tr -d ' ')"
echo "Examples OK ($count files)."
