#!/usr/bin/env bash
#
# L10: checks artifacts with SHA-256 checksums. Produces artifacts/packages/SHA256SUMS
# for all .nupkg/.snupkg and then checks the file against the packages.
# Prerequisite: build/check-package.sh or build/check-beta.sh has packed.
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
packages="$root/artifacts/packages"

if command -v sha256sum >/dev/null 2>&1; then
    hash() { sha256sum "$@"; }
    check() { sha256sum --check --quiet "$@"; }
elif command -v shasum >/dev/null 2>&1; then
    hash() { shasum -a 256 "$@"; }
    check() { shasum -a 256 --check --quiet "$@"; }
else
    echo "Neither sha256sum nor shasum found." >&2
    exit 1
fi

cd "$packages"
shopt -s nullglob
files=(*.nupkg *.snupkg)
if [ ${#files[@]} -eq 0 ]; then
    echo "No packages under artifacts/packages; pack first." >&2
    exit 1
fi

hash "${files[@]}" > SHA256SUMS
check SHA256SUMS
echo "Checksums OK (${#files[@]} files, artifacts/packages/SHA256SUMS)."
