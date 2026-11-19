#!/usr/bin/env bash
#
# L09: publish the beta package (locally) and test it from independent consumers.
# Packs the library into a local feed and builds/runs the package consumer
# smoke/Smoke.Package, which references only the NuGet.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

rm -rf artifacts/packages
dotnet pack Reglet.sln --configuration Release --output artifacts/packages --verbosity quiet --nologo

dotnet run --project smoke/Smoke.Package --configuration Release --verbosity quiet --nologo

echo "Beta package OK."
