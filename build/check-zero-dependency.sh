#!/usr/bin/env bash
#
# C07/C09: zero-dependency check against the machine-checkable statement
# build/zero-dependency.json.
#
# Checks:
#   1. The project list in the manifest matches the projects under src/.
#   2. MSBuild finds no forbidden references, even in imported files.
#   3. No bundled binary or font files lie under src/.
#
# Usage:  build/check-zero-dependency.sh
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$root/build/zero-dependency.json"
status=0

if [ ! -f "$manifest" ]; then
  echo "ERROR: manifest missing: build/zero-dependency.json"
  exit 1
fi

declared="$(grep -oE '"src/[^"]+\.csproj"' "$manifest" | tr -d '"' | sort)"
actual="$(cd "$root" && find src -name '*.csproj' | sort)"

if [ "$declared" != "$actual" ]; then
  echo "ERROR: manifest and actual projects under src/ differ:"
  diff <(printf '%s\n' "$declared") <(printf '%s\n' "$actual") || true
  status=1
fi

while IFS= read -r rel; do
  [ -z "$rel" ] && continue
  proj="$root/$rel"

  if ! dotnet msbuild "$proj" -target:ValidateZeroDependency -verbosity:quiet -nologo; then
    status=1
  fi
done <<< "$actual"

bundled_files="$(cd "$root" && find src \( -name bin -o -name obj \) -prune -o -type f \
  \( -iname '*.dll' -o -iname '*.so' -o -iname '*.dylib' -o -iname '*.a' -o -iname '*.lib' \
     -o -iname '*.ttf' -o -iname '*.otf' -o -iname '*.ttc' -o -iname '*.woff' -o -iname '*.woff2' \) \
  -print)"

if [ -n "$bundled_files" ]; then
  echo "ERROR: bundled binary/font file(s) under src/:"
  echo "$bundled_files"
  status=1
fi

if [ "$status" -eq 0 ]; then
  echo "Zero-Dependency OK (manifest build/zero-dependency.json respected)."
fi

exit "$status"
