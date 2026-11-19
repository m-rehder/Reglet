#!/usr/bin/env bash
#
# Blazor WebAssembly smoke test. Builds the library, publishes smoke/Smoke.Wasm, serves it
# locally and loads it in headless Chrome/Chromium; the app writes its result into the DOM.
#
# Usage:  build/run-wasm.sh        (browser override: REGLET_CHROME=/path/to/chrome)
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

chrome="${REGLET_CHROME:-}"
if [ -z "$chrome" ]; then
  for candidate in google-chrome chromium chromium-browser \
      "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"; do
    if command -v "$candidate" >/dev/null 2>&1 || [ -x "$candidate" ]; then
      chrome="$candidate"
      break
    fi
  done
fi
if [ -z "$chrome" ]; then
  echo "ERROR: no Chrome/Chromium found (set REGLET_CHROME)." >&2
  exit 1
fi

dotnet build Reglet.sln --configuration Release --verbosity quiet --nologo
publish="$root/smoke/Smoke.Wasm/bin/Release/net10.0/publish/wwwroot"
rm -rf "$publish"
dotnet publish smoke/Smoke.Wasm --configuration Release --verbosity quiet --nologo

port="${REGLET_WASM_PORT:-8765}"
profile="$(mktemp -d)"
python3 -m http.server "$port" --bind 127.0.0.1 --directory "$publish" >/dev/null 2>&1 &
server=$!
trap 'kill "$server" 2>/dev/null || true; wait "$server" 2>/dev/null || true; rm -rf "$profile"' EXIT
sleep 1

dom="$("$chrome" --headless=new --disable-gpu --no-sandbox --disable-dev-shm-usage --user-data-dir="$profile" \
  --virtual-time-budget=60000 --dump-dom "http://127.0.0.1:$port/" 2>/dev/null)"

if ! grep -q "Smoke.Wasm OK" <<<"$dom"; then
  echo "ERROR: Blazor WebAssembly smoke test failed:" >&2
  grep -A20 'id="result"' <<<"$dom" >&2 || echo "$dom" | tail -20 >&2
  exit 1
fi
echo "Blazor WebAssembly smoke test OK."
