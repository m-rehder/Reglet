#!/usr/bin/env bash
#
# T02: AOT/trimming safety at the source level. Ensures that the shipped projects
# under src/ use no reflection, serialization or dynamic code generation. Complements
# the manual inventory in docs/aot-trimming.md.
#
# Usage:  build/check-aot-safety.sh
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

# Forbidden APIs. The diagnostic call block.GetType().Name is deliberately allowed;
# forbidden is type resolution at runtime (Type.GetType, Assembly.Load, Activator, ...).
# Note: the search is text-based (heuristic). It catches the usual patterns, but does
# not replace the SDK analyzer; authoritative are the inventory in
# docs/aot-trimming.md and the AOT publish.
pattern='System\.Reflection|\bActivator\.|\bType\.GetType\b|\bAssembly\.(Load|LoadFrom|LoadFile)\b|MakeGenericType|MakeGenericMethod|System\.Text\.Json|\bXmlSerializer\b|\bDataContract\b|\bBinaryFormatter\b|System\.Linq\.Expressions|\bDllImport\b|\bLibraryImport\b|RuntimeHelpers\.GetUninitializedObject|\bFormatterServices\b|\bdynamic([^[:alnum:]_-]|$)'

matches="$(grep -rnE "$pattern" src --include='*.cs' | grep -vE '/obj/|/bin/' || true)"

if [ -n "$matches" ]; then
  echo "ERROR: forbidden reflection/serialization/AOT API in src/:"
  echo "$matches"
  exit 1
fi

echo "AOT safety OK (no reflection, serialization or dynamic code generation in src/)."
