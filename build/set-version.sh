#!/usr/bin/env bash
#
# V03: sets the version centrally in Directory.Build.props and in the package consumer
# smoke/Smoke.Package. Accepts SemVer (MAJOR.MINOR.PATCH with an optional suffix).
#
# Usage:  build/set-version.sh X.Y.Z
#
set -euo pipefail

version="${1:-}"
if [ -z "$version" ]; then
  echo "Usage: build/set-version.sh X.Y.Z" >&2
  exit 1
fi

if ! printf '%s' "$version" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'; then
  echo "ERROR: '$version' is not a valid SemVer version." >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

perl -pi -e "s{(<Version>)[^<]+(</Version>)}{\${1}$version\${2}}" Directory.Build.props
perl -pi -e "s{(<PackageReference Include=\"Reglet\" Version=\")[^\"]+(\")}{\${1}$version\${2}}" smoke/Smoke.Package/Smoke.Package.csproj

echo "Version set to $version (Directory.Build.props, Smoke.Package)."
