#!/usr/bin/env bash
#
# V03: extracts the changelog section of a version from docs/release-notes.md and prints
# it to standard output. Aborts if the section is missing.
#
# Usage:  build/release-notes.sh X.Y.Z > RELEASE-NOTES.md
#
set -euo pipefail

version="${1:-}"
if [ -z "$version" ]; then
  echo "Usage: build/release-notes.sh X.Y.Z" >&2
  exit 1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
notes="$root/docs/release-notes.md"

if ! grep -qxF "## ${version}" "$notes"; then
  echo "ERROR: no section '## $version' in docs/release-notes.md." >&2
  exit 1
fi

awk -v heading="## $version" '
  $0 == heading { printing = 1; next }
  printing && /^## / { exit }
  printing { print }
' "$notes"
