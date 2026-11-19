#!/usr/bin/env bash
#
# V02: creates the Gitea release for a tag and attaches the packed artifacts
# (artifacts/packages: *.nupkg, *.snupkg, SHA256SUMS). The release text is the changelog
# section of the version (build/release-notes.sh). If the release already exists (e.g. created
# by hand), only the missing assets are uploaded, so the step can be repeated.
# Prerequisite: build/release.sh has packed and produced the checksums.
#
# Usage:  build/gitea-release.sh X.Y.Z
# Configuration:
#   GITEA_API_URL   repository API, e.g. https://gitea.example/api/v1/repos/owner/repo
#   GITEA_TOKEN     token with write access to the repository's releases
#
set -euo pipefail

version="${1:-}"
if [ -z "$version" ]; then
  echo "Usage: build/gitea-release.sh X.Y.Z" >&2
  exit 1
fi

: "${GITEA_API_URL:?GITEA_API_URL is not set}"
: "${GITEA_TOKEN:?GITEA_TOKEN is not set}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
packages="$root/artifacts/packages"
tag="v$version"

shopt -s nullglob
assets=("$packages"/*.nupkg "$packages"/*.snupkg)
if [ ${#assets[@]} -eq 0 ] || [ ! -f "$packages/SHA256SUMS" ]; then
  echo "ERROR: no packages or SHA256SUMS under artifacts/packages; run build/release.sh first." >&2
  exit 1
fi
assets+=("$packages/SHA256SUMS")

# JSON string of a text file (backslash, quote, tab and line breaks escaped).
json_string() {
  awk 'BEGIN { ORS = ""; print "\"" }
       { gsub(/\\/, "\\\\"); gsub(/"/, "\\\""); gsub(/\t/, "\\t"); gsub(/\r/, "");
         if (NR > 1) print "\\n"; print }
       END { print "\"" }' "$1"
}

api() {
  curl --silent --show-error --fail-with-body \
    --header "Authorization: token $GITEA_TOKEN" \
    --header "Accept: application/json" \
    "$@"
}

notes="$(mktemp)"
trap 'rm -f "$notes"' EXIT
"$root/build/release-notes.sh" "$version" > "$notes"

status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  --header "Authorization: token $GITEA_TOKEN" "$GITEA_API_URL/releases/tags/$tag")"

case "$status" in
  200)
    echo "Release $tag exists; adding missing assets."
    release="$(api "$GITEA_API_URL/releases/tags/$tag")"
    ;;
  404)
    prerelease=false
    case "$version" in *-*) prerelease=true ;; esac
    echo "Creating release $tag."
    release="$(api --request POST --header "Content-Type: application/json" \
      --data-binary @- "$GITEA_API_URL/releases" <<EOF
{"tag_name": "$tag", "name": "Reglet $version", "body": $(json_string "$notes"), "draft": false, "prerelease": $prerelease}
EOF
)"
    ;;
  *)
    echo "ERROR: querying release $tag returned HTTP $status." >&2
    exit 1
    ;;
esac

# The release id is the first "id" of the response (nested objects follow later).
id="$(printf '%s' "$release" | grep -oE '"id": ?[0-9]+' | head -n 1 | grep -oE '[0-9]+')"
if [ -z "$id" ]; then
  echo "ERROR: no release id in the response." >&2
  exit 1
fi

for asset in "${assets[@]}"; do
  name="$(basename "$asset")"
  if printf '%s' "$release" | grep -qF "\"name\":\"$name\""; then
    echo "  $name already attached."
    continue
  fi

  api --request POST --form "attachment=@$asset" \
    "$GITEA_API_URL/releases/$id/assets?name=$name" > /dev/null
  echo "  $name attached."
done

echo "Gitea release $tag ready (${#assets[@]} assets)."
