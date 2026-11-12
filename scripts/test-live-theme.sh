#!/usr/bin/env bash
# Live theme change, observed on screen: runs wraithcairn on an isolated X
# server, changes the theme from OUTSIDE the app, and samples the pane's
# pixels each time. No restart in between.
#   1. --set-theme Dracula           (CLI writes config.json)
#   2. --set-theme "iTerm2 Solarized Light" (light theme)
#   3. config.json theme -> null, Ghostty config theme = Nord (inherit)
#   4. hand edit of the Ghostty config: Nord -> Gruvbox Dark
#   5. hand edit of config.json back to Dracula (overrides Ghostty)
# Needs an X server on $DISPLAY_TEST (default :99), e.g. `Xwayland :99 &`.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

display="${DISPLAY_TEST:-:99}"
work="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-live-theme.XXXXXX")"
mkdir -p "$work/wraithcairn" "$work/ghostty"
printf '{\n  // test config\n  "restoreLayout": false,\n  "showSplash": false\n}\n' >"$work/wraithcairn/config.json"
printf 'font-size = 11\n' >"$work/ghostty/config"
bin=bin/Release/net10.0/wraithcairn
export WRAITHCAIRN_THEMES_URL="$PWD/website/static/themes" XDG_CONFIG_HOME="$work" WRAITHCAIRN_NO_DESKTOP=1
cli() { "$bin" "$@" >/dev/null; }
# Pre-install the themes the Ghostty layer will name (it only looks locally).
cli --set-theme Nord; cli --set-theme "Gruvbox Dark"; cli --set-theme ghostty

abs_bin="$PWD/$bin"
(cd /tmp && DISPLAY="$display" setsid -f "$abs_bin" >"$work/app.log" 2>&1 </dev/null)
sleep 8

fail=0
sample() { # label expected-hex
  sleep "${SETTLE:-1.5}"
  DISPLAY="$display" python3 scripts/x-shot.py "$work/$1.png"
  python3 - "$work/$1.png" "$2" "$1" <<'EOF' || fail=1
import sys
from collections import Counter
from PIL import Image
img = Image.open(sys.argv[1]).convert("RGB")
# Pane body area (below menu + pane header, away from the prompt line).
box = img.crop((200, 300, 1000, 650))
(r, g, b), n = Counter(box.getdata()).most_common(1)[0]
got = f"#{r:02x}{g:02x}{b:02x}"
want = sys.argv[2].lower()
ok = got == want
# Menu bar text must contrast with the window: darkest vs lightest pixel
# in the "Shell Pane Edit View Layouts" strip.
menu = img.crop((15, 15, 280, 36)).convert("L")
lo, hi = menu.getextrema()
bgl = (0.2126 * r + 0.7152 * g + 0.0722 * b)
text_far = (lo if bgl > 127 else hi)
contrast = abs(text_far - bgl)
ok = ok and contrast > 110
print(f"{'ok  ' if ok else 'FAIL'} {sys.argv[3]:<34} pane bg {got} (expected {want}, {n * 100 // len(box.getdata())}% of area) menu contrast {contrast:.0f}")
sys.exit(0 if ok else 1)
EOF
}
bg() { python3 -c "import json,sys;print(next(t['background'] for t in json.load(open('website/static/themes/index.json'))['themes'] if t['name']==sys.argv[1]))" "$1"; }

sample "0-start (built-in default)" "#1e1e1e"
cli --set-theme Dracula;              sample "1-cli Dracula" "$(bg Dracula)"
cli --set-theme "iTerm2 Solarized Light";    sample "2-cli Solarized Light" "$(bg 'iTerm2 Solarized Light')"
printf 'font-size = 11\ntheme = Nord\n' >"$work/ghostty/config"
cli --set-theme ghostty;              sample "3-inherit Ghostty theme (Nord)" "$(bg Nord)"
printf 'font-size = 11\ntheme = Gruvbox Dark\n' >"$work/ghostty/config"
                                      sample "4-hand edit ghostty config" "$(bg 'Gruvbox Dark')"
python3 - "$work/wraithcairn/config.json" <<'EOF'
import sys, re
p = sys.argv[1]; s = open(p).read()
s = re.sub(r'"theme":\s*null', '"theme": "Dracula"', s)
open(p, "w").write(s)
EOF
                                      sample "5-hand edit config.json" "$(bg Dracula)"
pkill -f "$work" 2>/dev/null
pgrep -f "$abs_bin" | while read -r pid; do
  if tr '\0' '\n' </proc/"$pid"/environ 2>/dev/null | grep -q "XDG_CONFIG_HOME=$work"; then kill "$pid"; fi
done
echo "WORK=$work"
exit $fail
