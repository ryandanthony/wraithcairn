#!/usr/bin/env bash
# Run wraithcairn --selftest N times against an isolated XDG config dir so the
# user's real ~/.config/wraithcairn/workspace.json is never touched.
#   scripts/selftest-isolated.sh [runs] [restore]
# Writes logs to $JCODE_SCRATCH_DIR (or /tmp) as selftest-<n>.log.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

runs="${1:-1}"
restore="${2:-}"
base="${JCODE_SCRATCH_DIR:-/tmp}/wc-selftest"
mkdir -p "$base/seed/wraithcairn" "$base/seed/ghostty"
cp -r "$HOME/.config/ghostty/." "$base/seed/ghostty/" 2>/dev/null || true
cp "$HOME/.config/wraithcairn/config.json" "$base/seed/wraithcairn/" 2>/dev/null || true
cp -r "$HOME/.config/wraithcairn/themes" "$base/seed/wraithcairn/" 2>/dev/null || true

for n in $(seq 1 "$runs"); do
  cfg="$base/run-$n-$$"
  mkdir -p "$cfg"
  cp -r "$base/seed/." "$cfg/"
  log="$base/selftest-$n.log"
  XDG_CONFIG_HOME="$cfg" timeout 100 dotnet run -c Release --no-build -- --selftest >"$log" 2>&1 || true
  if [[ "$restore" == "restore" ]]; then
    XDG_CONFIG_HOME="$cfg" timeout 100 dotnet run -c Release --no-build -- --selftest >"$log.restored" 2>&1 || true
  fi
  echo "== run $n: $log"
done
