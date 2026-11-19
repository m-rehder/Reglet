#!/usr/bin/env bash
#
# T03/T05: native AOT publish test. Builds the library, publishes the smoke app
# Smoke.Aot natively (no JIT) and runs it.
#
# Usage:  build/run-aot.sh
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet build Reglet.sln --configuration Release --verbosity quiet --nologo
dotnet publish smoke/Smoke.Aot --configuration Release --verbosity quiet --nologo

# Determine the RID robustly from the publish project (no CRLF issue as with `dotnet --info`).
rid="$(dotnet msbuild smoke/Smoke.Aot/Smoke.Aot.csproj -getProperty:NETCoreSdkRuntimeIdentifier -nologo 2>/dev/null | tr -d '[:space:]')"
if [ -z "$rid" ]; then
  rid="$(dotnet --info | awk -F': *' '/RID:/{print $2; exit}' | tr -d '[:space:]')"
fi

app="$root/smoke/Smoke.Aot/bin/Release/net10.0/$rid/publish/Smoke.Aot"
if [ ! -x "$app" ]; then
  app="$app.exe"
fi

if [ ! -x "$app" ]; then
  echo "ERROR: AOT binary not found ($app)." >&2
  exit 1
fi

"$app"
echo "AOT publish test OK."
