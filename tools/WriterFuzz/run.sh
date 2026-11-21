#!/bin/zsh
# run.sh <first seed> <last seed>: runs the writer fuzzer and starts it again after a hang at the next seed.
set -u
cd "$(dirname "$0")/../.."
dotnet build -c Release tools/WriterFuzz -v q > /dev/null || exit 1
seed=$1
while (( seed < $2 )); do
  out=$(dotnet tools/WriterFuzz/bin/Release/net10.0/WriterFuzz.dll $seed $(( $2 - seed )))
  print -r -- "$out"
  next=$(print -r -- "$out" | grep '^NEXT' | tail -1 | cut -d' ' -f2)
  [[ -z $next ]] && break
  seed=$next
done
