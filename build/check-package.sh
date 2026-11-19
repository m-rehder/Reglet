#!/usr/bin/env bash
#
# L08: zero-dependency check of the packed NuGet. Packs the library and checks per
# package that it contains no foreign dependencies, no foreign binaries and no
# font files.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

dotnet pack Reglet.sln --configuration Release --output "$out" --verbosity quiet --nologo
status=0
count=0

for pkg in "$out"/*.nupkg; do
  [ -e "$pkg" ] || continue
  count=$((count + 1))
  name="$(basename "$pkg")"

  # 1. Only own packages as a dependency.
  if unzip -p "$pkg" '*.nuspec' | grep -oE '<dependency id="[^"]+"' | grep -vE 'id="Reglet(\.|")' | grep -q .; then
    echo "ERROR: foreign dependency in $name"
    status=1
  fi

  # 2. lib/ contains only own DLLs.
  if unzip -Z1 "$pkg" | grep '^lib/' | grep -E '\.(dll|so|dylib|a|lib)$' | grep -vE 'Reglet[^/]*\.dll$' | grep -q .; then
    echo "ERROR: foreign binary in $name"
    status=1
  fi

  # 3. No font files.
  if unzip -Z1 "$pkg" | grep -qiE '\.(ttf|otf|ttc|woff2?)$'; then
    echo "ERROR: font file in $name"
    status=1
  fi
done

if [ "$status" -eq 0 ]; then
  echo "Package zero-dependency OK ($count packages checked)."
fi

exit "$status"
