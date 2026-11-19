#!/usr/bin/env bash
# C08: MSBuild also evaluates imported and multi-line ProjectReference entries.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

for project in "$root"/src/*/*.csproj; do
  dotnet msbuild "$project" -target:ValidateProjectDependencyBoundaries -verbosity:quiet -nologo
done

echo "Architecture OK: dependency boundaries respected."
