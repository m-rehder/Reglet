#!/usr/bin/env bash
#
# K13: consumer smoke tests. Builds the library and runs three small
# applications that reference only the built DLLs.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet build Reglet.sln --configuration Release --verbosity quiet --nologo

for name in Docx Odt Pdf; do
  dotnet run --project "smoke/Smoke.$name" --configuration Release --verbosity quiet --nologo
done

echo "Smoke tests OK."
