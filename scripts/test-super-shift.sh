#!/usr/bin/env bash
# Linux: Super+Shift+letter must NOT act as a window shortcut (the Cmd+Shift
# alias is macOS-only), while Ctrl+Shift+letter still does. Real XTest keys
# into a binary on its own X server; reads the app's trace.
#   scripts/test-super-shift.sh BIN [DISPLAY]
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bin="$(realpath "$1")"; display="${2:-:95}"
work="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-super.XXXXXX")"
mkdir -p "$work/cfg/wraithcairn"
printf '{ "restoreLayout": false, "showSplash": false, "useGhosttyConfig": false, "shell": "/bin/bash" }\n' >"$work/cfg/wraithcairn/config.json"
if command -v Xvfb >/dev/null; then Xvfb "$display" -screen 0 1200x800x24 >/dev/null 2>&1 &
else Xwayland "$display" -geometry 1200x800 >/dev/null 2>&1 &
fi
xpid=$!
app=""
trap '[ -n "$app" ] && kill "$app" 2>/dev/null; kill "$xpid" 2>/dev/null; true' EXIT
sleep 2
export DISPLAY="$display"
XDG_CONFIG_HOME="$work/cfg" WRAITHCAIRN_NO_DESKTOP=1 WRAITHCAIRN_TRACE=1 "$bin" >"$work/app.log" 2>&1 &
app=$!
sleep 8
k() { python3 "$repo/scripts/xtest-type.py" "$@"; }
k click:400,300
sleep 0.5
mark() { echo "MARK $1" >>"$work/marks"; grep -c "" "$work/app.log" >>"$work/marks"; }
# Super+Shift+Z / O: no zoom, no split.
k super+shift+z; sleep 0.8
k super+shift+o; sleep 1.2
k super+shift+i; sleep 1.2
super_lines=$(grep -E "TRACE (key (Z|O|I) mods=Shift, Meta|voice toggle)" "$work/app.log" || true)
# Positive control: Ctrl+Shift+O splits.
k ctrl+shift+o; sleep 1.5
kill "$app"; app=""
sleep 1
echo "$super_lines"
super_seen=$(echo "$super_lines" | grep -c "mods=Shift, Meta" || true)
toggles=$(grep -c "TRACE voice toggle" "$work/app.log" || true)
panes=$(grep -oE "[0-9]+ pane\(s\)" "$work/app.log" | tail -1 || true)
split_ok=$(grep -c "TRACE key O mods=Control, Shift" "$work/app.log" || true)
echo "super_shift_keys_seen=$super_seen voice_toggles=$toggles ctrl_shift_o_seen=$split_ok"
saved="$work/cfg/wraithcairn/workspace.json"
leaves=$( [ -f "$saved" ] && grep -o '"split": null' "$saved" | wc -l || echo "?")
echo "panes_in_saved_layout=$leaves (1 pane + Ctrl+Shift+O = 2; a Super+Shift+O split would make 3)"
if [ "$toggles" = 0 ] && [ "$leaves" = 2 ]; then echo "RESULT super_shift_inert=PASS"; else echo "RESULT super_shift_inert=FAIL"; fi
