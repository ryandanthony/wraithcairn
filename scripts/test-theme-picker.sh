#!/usr/bin/env bash
# The Themes picker driven like a person would: real mouse clicks and key
# presses (XTest) on an isolated X server, screenshots checked by pixel.
#   open View > Themes..., type a search, arrow to a theme (live preview),
#   Escape (preview reverts), reopen, search + Enter (applies), verify
#   config.json + pixels, then Download All and check the files.
# Needs an X server on $DISPLAY_TEST (default :99).
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

display="${DISPLAY_TEST:-:99}"
work="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-picker.XXXXXX")"
mkdir -p "$work/wraithcairn"
printf '{\n  // picker test\n  "restoreLayout": false,\n  "showSplash": false,\n  "useGhosttyConfig": false\n}\n' >"$work/wraithcairn/config.json"
abs_bin="$PWD/bin/Release/net10.0/wraithcairn"
export WRAITHCAIRN_THEMES_URL="${THEMES_URL:-$PWD/website/static/themes}" XDG_CONFIG_HOME="$work" WRAITHCAIRN_NO_DESKTOP=1
(cd /tmp && DISPLAY="$display" setsid -f "$abs_bin" >"$work/app.log" 2>&1 </dev/null)
sleep 8

fail=0
x() { DISPLAY="$display" python3 scripts/xtest-type.py "$@"; }
shot() { sleep "${2:-1.2}"; DISPLAY="$display" python3 scripts/x-shot.py "$work/$1.png"; }
pane_bg() { # image -> most common colour in the terminal area left of where the dialog sits
  python3 - "$work/$1.png" <<'EOF'
import sys
from collections import Counter
from PIL import Image
img = Image.open(sys.argv[1]).convert("RGB")
(r, g, b), _ = Counter(img.crop((20, 100, 150, 600)).getdata()).most_common(1)[0]
print(f"#{r:02x}{g:02x}{b:02x}")
EOF
}
expect() { # label got want
  if [ "$2" = "$3" ]; then echo "ok   $1 ($2)"; else echo "FAIL $1: got $2, want $3"; fail=1; fi
}
bg() { python3 -c "import json,sys;print(next(t['background'] for t in json.load(open('website/static/themes/index.json'))['themes'] if t['name']==sys.argv[1]))" "$1"; }

shot 0-start 0
start_bg=$(pane_bg 0-start)

open_picker() { x click:186,25; sleep 0.6; x click:200,119; sleep 2.5; }
cfg_theme() { python3 - "$work/wraithcairn/config.json" <<'EOF'
import json, re, sys
t = re.sub(r"//[^\n]*", "", open(sys.argv[1]).read()); t = re.sub(r",(\s*[}\]])", r"\1", t)
print(json.loads(t).get("theme"))
EOF
}

# 1. Open View > Themes... with the mouse.
open_picker; shot 1-open 0
expect "picker opens (status line drawn)" "$(python3 -c "
from PIL import Image; im=Image.open('$work/1-open.png').convert('L'); print('yes' if im.crop((160,680,500,705)).getextrema()[1]>100 else 'no')")" yes

# 2. Type a search, arrow down onto the first hit: live preview on the pane.
x "text:dracula"; sleep 0.8; x Down; shot 2-preview
expect "arrow onto Dracula previews it on the pane" "$(pane_bg 2-preview)" "$(bg Dracula)"
expect "preview does not save" "$(cfg_theme)" "None"

# 3. Escape closes and reverts the preview.
x Escape; shot 3-escaped
expect "Escape reverts the pane" "$(pane_bg 3-escaped)" "$start_bg"
expect "still nothing saved" "$(cfg_theme)" "None"

# 4. Reopen, search a light theme, Enter applies (downloads it first).
open_picker; x "text:iTerm2 Solarized Light"; sleep 0.8; x Down; sleep 0.5; x Return; shot 4-applied 2.5
expect "Enter applies + saves" "$(cfg_theme)" "iTerm2 Solarized Light"
expect "theme file downloaded" "$([ -f "$work/wraithcairn/themes/iTerm2 Solarized Light" ] && echo yes || echo no)" yes
expect "pane repainted while dialog open" "$(pane_bg 4-applied)" "$(bg 'iTerm2 Solarized Light')"

# 5. Close with Escape after applying: the applied theme stays.
x Escape; shot 5-closed
expect "applied theme stays after closing" "$(pane_bg 5-closed)" "$(bg 'iTerm2 Solarized Light')"
expect "comment kept in config.json" "$(grep -c 'picker test' "$work/wraithcairn/config.json")" 1

# 6. Download All from the dialog (button at 620,128).
open_picker; x click:620,128; shot 6-download-all 4
expect "Download All writes every theme" "$(ls "$work/wraithcairn/themes" | wc -l)" 819
x Escape; sleep 1

for pid in $(pgrep -f "$abs_bin"); do
  tr '\0' '\n' </proc/"$pid"/environ 2>/dev/null | grep -q "XDG_CONFIG_HOME=$work" && kill "$pid"
done
echo "WORK=$work"
exit $fail
