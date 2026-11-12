#!/usr/bin/env bash
# Keyboard check for the opt-in native Wayland backend (WRAITHCAIRN_WAYLAND=1)
# on a nested, isolated KWin (--virtual, own socket), so nothing touches the
# user's session. Types with wtype and prints the bytes the program receives.
#   scripts/key-repro-wayland.sh [wtype args...]
# Default: Shift+1..0 as text "!@#$%^&*()" plus "a1".
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

sock="wc-key-wl-$$"
work="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-keys-wl.XXXXXX")"
mkdir -p "$work/wraithcairn"
printf '{ "restoreLayout": false, "showSplash": false, "useGhosttyConfig": false }\n' >"$work/wraithcairn/config.json"
out="$work/out.bin"

env -u DISPLAY kwin_wayland --virtual --socket "$sock" --width 1200 --height 800 \
  --no-lockscreen --no-global-shortcuts >"$work/kwin.log" 2>&1 </dev/null &
kwin=$!
sleep 4
env -u DISPLAY WAYLAND_DISPLAY="$sock" WRAITHCAIRN_WAYLAND=1 WRAITHCAIRN_NO_DESKTOP=1 \
  WRAITHCAIRN_TRACE="${WRAITHCAIRN_TRACE:-0}" XDG_CONFIG_HOME="$work" \
  bin/Release/net10.0/wraithcairn >"$work/app.log" 2>&1 </dev/null &
app=$!
trap 'kill $app $kwin 2>/dev/null || true' EXIT
sleep "${KEY_REPRO_WAIT:-9}"

wt() { WAYLAND_DISPLAY="$sock" wtype "$@"; }
wt "${KEY_REPRO_PRE:-}stty raw -echo; timeout 4 cat > $out; stty sane${KEY_REPRO_POST:-}" -k Return
sleep 0.8
if [ $# -eq 0 ]; then
  # Real Shift + digit key presses (not just the characters).
  set -- -M shift -k 1 -k 2 -k 3 -k 4 -k 5 -k 6 -k 7 -k 8 -k 9 -k 0 -m shift -k a -k 1
fi
wt "$@"
sleep 4.5
echo "got:  $(od -An -c "$out" 2>/dev/null | tr -s ' ' | tr -d '\n' || echo '(nothing)')"
echo "WORK=$work"
