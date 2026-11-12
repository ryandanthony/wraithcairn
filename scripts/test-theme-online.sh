#!/usr/bin/env bash
# Theme catalog client against the REAL docs site, then offline.
# Isolated config dir; never touches ~/.config/wraithcairn.
#   scripts/test-theme-online.sh
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

cfg="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-theme-online.XXXXXX")"
mkdir -p "$cfg/wraithcairn"
printf '{\n  // keep me\n  "fontSize": 11\n}\n' >"$cfg/wraithcairn/config.json"
bin=bin/Release/net10.0/wraithcairn
fail=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi; }
run() { XDG_CONFIG_HOME="$cfg" "$bin" "$@"; }

# 1. Online (default URL = the published docs site).
run --list-themes >"$cfg/list.txt" 2>"$cfg/list.err"
check "online list: 819 themes" '[ "$(wc -l <"$cfg/list.txt")" -eq 819 ]'
check "online list: no offline warning" '! grep -q offline "$cfg/list.err"'
check "catalog cached for offline use" '[ -s "$cfg/wraithcairn/theme-catalog.json" ]'

run --set-theme "Black Metal (Bathory)" >/dev/null
f="$cfg/wraithcairn/themes/Black Metal (Bathory)"
check "download name with spaces/parens" '[ -f "$f" ]'
check "downloaded file = published file" 'cmp -s "$f" website/static/themes/ghostty/black-metal-bathory'
run --set-theme "Dracula+" >/dev/null
check "download name with +" 'cmp -s "$cfg/wraithcairn/themes/Dracula+" website/static/themes/ghostty/dracula-plus'
check "config keeps comment + has theme" 'grep -q "keep me" "$cfg/wraithcairn/config.json" && grep -q "\"theme\": \"Dracula+\"" "$cfg/wraithcairn/config.json"'

start=$(date +%s)
run --download-themes >"$cfg/dl.txt"
took=$(( $(date +%s) - start ))
check "download all over HTTPS: 819 files (${took}s)" '[ "$(ls "$cfg/wraithcairn/themes" | wc -l)" -eq 819 ]'
check "every downloaded file = published file" '
  ok=1; while IFS= read -r line; do
    name=$(printf "%s" "$line" | python3 -c "import json,sys;print(json.loads(sys.stdin.read())[\"name\"])")
    slug=$(printf "%s" "$line" | python3 -c "import json,sys;print(json.loads(sys.stdin.read())[\"slug\"])")
    : ; done < /dev/null
  python3 - "$cfg/wraithcairn/themes" <<EOF
import json,sys,os
d=sys.argv[1]; idx=json.load(open("website/static/themes/index.json"))
bad=[t["name"] for t in idx["themes"] if open(os.path.join(d,t["name"]),"rb").read()!=open("website/static/themes/ghostty/"+t["slug"],"rb").read()]
print("mismatched:",len(bad),bad[:3]); sys.exit(1 if bad else 0)
EOF'

# 2. Offline: unreachable site -> cached catalog + installed themes still work.
export WRAITHCAIRN_THEMES_URL="https://127.0.0.1:9/nothing/"
run --list-themes >"$cfg/off.txt" 2>"$cfg/off.err"
check "offline list falls back to cache (819)" '[ "$(wc -l <"$cfg/off.txt")" -eq 819 ]'
check "offline is reported" 'grep -q offline "$cfg/off.err"'
run --set-theme "Nord" >/dev/null 2>&1
check "offline set-theme of installed theme works" 'grep -q "\"theme\": \"Nord\"" "$cfg/wraithcairn/config.json"'
rm -f "$cfg/wraithcairn/theme-catalog.json" "$cfg/wraithcairn/themes/Zenburn"
if run --set-theme "Zenburn" >/dev/null 2>"$cfg/z.err"; then check "offline unknown theme fails" false; else check "offline unknown theme fails cleanly" 'grep -q "unknown theme" "$cfg/z.err"'; fi

echo "WORK=$cfg"
exit $fail
