#!/usr/bin/env bash
# Exercises the theme CLI against an isolated config dir (never the real one).
#   scripts/test-theme-cli.sh [themes-url]
# themes-url defaults to the repo's website/static/themes (offline).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

cfg="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-theme-cli.XXXXXX")"
mkdir -p "$cfg/wraithcairn"
cat >"$cfg/wraithcairn/config.json" <<'EOF'
{
  // a comment that must survive
  "fontSize": 11,
  "restoreLayout": true,
}
EOF

bin=bin/Release/net10.0/wraithcairn
run() { XDG_CONFIG_HOME="$cfg" WRAITHCAIRN_THEMES_URL="${1}" "$bin" "${@:2}"; }
url="${1:-$PWD/website/static/themes}"
fail=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi; }

run "$url" --list-themes >"$cfg/list.txt" 2>"$cfg/list.err"
check "list-themes lists 800+" '[ "$(wc -l <"$cfg/list.txt")" -ge 800 ]'

run "$url" --set-theme dracula
check "set-theme downloads (case-insensitive name)" '[ -f "$cfg/wraithcairn/themes/Dracula" ]'
check "set-theme saves theme" 'grep -q "\"theme\": \"Dracula\"" "$cfg/wraithcairn/config.json"'
check "comment kept" 'grep -q "a comment that must survive" "$cfg/wraithcairn/config.json"'

run "$url" --set-theme "Rose Pine Moon"
check "set-theme replaces value" '[ "$(grep -c "\"theme\"" "$cfg/wraithcairn/config.json")" -eq 1 ] && grep -q "Rose Pine Moon" "$cfg/wraithcairn/config.json"'

run "$url" --set-theme ghostty
check "set-theme ghostty clears" 'grep -q "\"theme\": null" "$cfg/wraithcairn/config.json"'

if run "$url" --set-theme "No Such Theme" 2>/dev/null; then check "unknown theme fails" false; else check "unknown theme fails" true; fi

run "$url" --download-themes
check "download-themes writes all" '[ "$(ls "$cfg/wraithcairn/themes" | wc -l)" -ge 800 ]'
check "config still valid json5" 'python3 -c "import re,json,sys;t=open(sys.argv[1]).read();t=re.sub(r\"//.*\",\"\",t);t=re.sub(r\",(\s*[}\]])\",r\"\1\",t);json.loads(t)" "$cfg/wraithcairn/config.json"'

echo "--- config.json"
cat "$cfg/wraithcairn/config.json"
exit $fail
