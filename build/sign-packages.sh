#!/usr/bin/env bash
#
# V01: signs the release packages with an author signature (dotnet nuget sign).
# Prerequisite: REGLET_SIGN_CERT points to a .pfx file; optionally
# REGLET_SIGN_PASSWORD and REGLET_TIMESTAMPER.
#
# Usage:  build/sign-packages.sh
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
packages="$root/artifacts/packages"

cert="${REGLET_SIGN_CERT:-}"
if [ -z "$cert" ]; then
  echo "ERROR: no signing certificate set (REGLET_SIGN_CERT)." >&2
  exit 1
fi

if [ ! -f "$cert" ]; then
  echo "ERROR: certificate '$cert' not found." >&2
  exit 1
fi

shopt -s nullglob
files=("$packages"/*.nupkg "$packages"/*.snupkg)
if [ ${#files[@]} -eq 0 ]; then
  echo "ERROR: no packages under artifacts/packages; pack first." >&2
  exit 1
fi

args=(--certificate-path "$cert" --overwrite)
if [ -n "${REGLET_SIGN_PASSWORD:-}" ]; then
  args+=(--certificate-password "$REGLET_SIGN_PASSWORD")
fi

if [ -n "${REGLET_TIMESTAMPER:-}" ]; then
  args+=(--timestamper "$REGLET_TIMESTAMPER")
fi

for file in "${files[@]}"; do
  dotnet nuget sign "$file" "${args[@]}"
done

echo "Packages signed (${#files[@]} files)."
