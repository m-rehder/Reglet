#!/usr/bin/env bash
#
# U06: benchmarks and regression test. Builds the library, runs the benchmarks and
# compares throughput and memory against the thresholds in build/benchmarks.txt.
# An exceedance aborts with exit code 1.
#
# Usage:  build/run-benchmarks.sh
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

dotnet build Reglet.sln --configuration Release --verbosity quiet --nologo
dotnet run --project benchmarks/Reglet.Benchmarks --configuration Release --verbosity quiet --nologo
