#!/usr/bin/env bash
# Reproduces keyboard input end to end with real X11 key events (XTest) on
# an isolated X server, so nothing reaches the user's desktop:
#   scripts/key-repro.sh            types Shift+1..Shift+0 (US layout)
#   scripts/key-repro.sh SPEC...    see scripts/xtest-type.py for SPEC
# Starts wraithcairn on $KEY_REPRO_DISPLAY (default :99, must already run,
# e.g. `Xwayland :99 &` or `Xvfb :99 &`) with an isolated config, runs
# `od` in the pane, types the keys, and prints the bytes the program got.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

display="${KEY_REPRO_DISPLAY:-:99}"
work="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-keys.XXXXXX")"
mkdir -p "$work/wraithcairn"
out="$work/od.txt"
printf '{ "restoreLayout": false, "showSplash": false, "useGhosttyConfig": false }\n' >"$work/wraithcairn/config.json"

DISPLAY="$display" XDG_CONFIG_HOME="$work" WRAITHCAIRN_TRACE="${WRAITHCAIRN_TRACE:-0}" \
  bin/Release/net10.0/wraithcairn >"$work/app.log" 2>&1 &
app=$!
trap 'kill $app 2>/dev/null || true' EXIT
sleep "${KEY_REPRO_WAIT:-6}"

if [ $# -eq 0 ]; then
  set -- shift+1 shift+2 shift+3 shift+4 shift+5 shift+6 shift+7 shift+8 shift+9 shift+0
fi

# No window manager on a bare X server: click into the pane to focus it.
# Raw mode so every byte arrives as typed. cat writes each read at once
# (od/head would lose their buffer when timeout kills them).
# KEY_REPRO_PRE/POST wrap it, e.g. to turn on the Kitty keyboard protocol:
#   KEY_REPRO_PRE="printf '\\e[>1u'; " KEY_REPRO_POST="; printf '\\e[<u'"
DISPLAY="$display" python3 scripts/xtest-type.py click:400,300 "text:${KEY_REPRO_PRE:-}stty raw -echo; timeout 4 cat > $out; stty sane${KEY_REPRO_POST:-}" Return
sleep 0.8
DISPLAY="$display" python3 scripts/xtest-type.py "$@"
sleep 4.5
echo "keys: $*"
echo "got:  $(od -An -c "$out" 2>/dev/null | tr -s ' ' | tr -d '\n' || echo '(nothing)')"
if [ "${WRAITHCAIRN_TRACE:-0}" = 1 ]; then
  grep TRACE "$work/app.log" | tail -n 40
fi
