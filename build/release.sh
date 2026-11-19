#!/usr/bin/env bash
#
# V02/V01/V04: release process. Checks version and changelog, builds, tests, respects the
# zero-dependency gates, packs, signs, produces checksums and release notes and optionally
# uploads the packages to a NuGet registry.
#
# Usage:  build/release.sh X.Y.Z
# Configuration (optional):
#   REGLET_SIGN_CERT / _SIGN_PASSWORD / _TIMESTAMPER   optional author signature (V01)
#   REGLET_NUGET_SOURCE / _NUGET_TOKEN                 publication (V02)
#
set -euo pipefail

version="${1:-}"
if [ -z "$version" ]; then
  echo "Usage: build/release.sh X.Y.Z" >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
  echo "WARNING: working tree is not clean; the release contains uncommitted changes." >&2
fi

declared="$(grep -oE '<Version>[^<]+' Directory.Build.props | sed 's/<Version>//')"
if [ "$declared" != "$version" ]; then
  echo "ERROR: Directory.Build.props has version '$declared', expected '$version'." >&2
  echo "       Run first: build/set-version.sh $version" >&2
  exit 1
fi

echo "== Check changelog =="
build/release-notes.sh "$version" > /dev/null

echo "== Build and tests =="
dotnet build Reglet.sln --configuration Release --verbosity quiet --nologo
dotnet run --project tests/Reglet.CompatibilityTests --configuration Release --verbosity quiet --nologo

echo "== Zero-dependency and AOT safety (V04) =="
build/check-zero-dependency.sh
build/check-architecture.sh
build/check-aot-safety.sh

echo "== Pack =="
rm -rf artifacts/packages
dotnet pack Reglet.sln --configuration Release --output artifacts/packages --verbosity quiet --nologo

echo "== Package audit (V04) =="
build/check-package.sh

echo "== Sign (V01) =="
# The author signature is optional: nuget.org adds its repository signature to every package anyway.
if [ -n "${REGLET_SIGN_CERT:-}" ]; then
  build/sign-packages.sh
else
  echo "No signing certificate (REGLET_SIGN_CERT): packages stay unsigned."
fi

echo "== Checksums (L10) =="
build/checksums.sh
build/release-notes.sh "$version" > artifacts/packages/RELEASE-NOTES.md

echo "== Publish (V02) =="
if [ -n "${REGLET_NUGET_SOURCE:-}" ]; then
  shopt -s nullglob
  for package in artifacts/packages/*.nupkg artifacts/packages/*.snupkg; do
    dotnet nuget push "$package" \
      --source "$REGLET_NUGET_SOURCE" \
      --api-key "${REGLET_NUGET_TOKEN:-}" \
      --skip-duplicate
  done
else
  echo "Note: REGLET_NUGET_SOURCE not set; packages are in artifacts/packages."
fi

echo "Release $version done (artifacts/packages)."
