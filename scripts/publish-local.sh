#!/usr/bin/env bash
# Publish a self-contained linux-x64 build of wraithcairn.
#
#   scripts/publish-local.sh            single-file exe in ~/temp/wraithcairn-single
#   scripts/publish-local.sh --folder   classic folder build in ~/temp/wraithcairn
#   OUT=/some/dir scripts/publish-local.sh [--folder]
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ "${1:-}" == "--folder" ]]; then
  OUT="${OUT:-$HOME/temp/wraithcairn}"
  dotnet publish -c Release -r linux-x64 --self-contained true -o "$OUT"
else
  OUT="${OUT:-$HOME/temp/wraithcairn-single}"
  # Don't drop a single-file exe on top of an old folder build: the loose
  # DLLs would sit next to it and confuse things.
  if [[ -f "$OUT/wraithcairn.deps.json" ]]; then
    echo "error: $OUT holds a folder build; pick another OUT or clear it first" >&2
    exit 1
  fi
  dotnet publish -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -o "$OUT"
fi

echo "Published to $OUT"
