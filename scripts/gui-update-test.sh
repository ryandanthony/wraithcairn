#!/usr/bin/env bash
# End-to-end test of Help > Check for Updates... on an isolated X server:
# an old single-file build opens the dialog (--check-updates), clicks
# Update Now, then Restart Now, and we check the binary was replaced and
# the new version came back up with the saved layout.
#   scripts/gui-update-test.sh OLD_BINARY RELEASES_DIR [DISPLAY]
# RELEASES_DIR is laid out like GitHub releases:
#   latest/download/SHA256SUMS, download/vX.Y.Z/<asset>
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

old="$1"
releases="$2"
display="${3:-:99}"
work="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-gui-update.XXXXXX")"
mkdir -p "$work/app" "$work/cfg/wraithcairn"
cp "$old" "$work/app/wraithcairn"
bin="$work/app/wraithcairn"
printf '{ "restoreLayout": true, "showSplash": false, "useGhosttyConfig": false }\n' >"$work/cfg/wraithcairn/config.json"
before="$("$bin" --version)"
echo "before: $before"

shot() { DISPLAY="$display" python3 scripts/x-shot.py "$work/$1.png"; echo "screenshot: $work/$1.png"; }
click() { DISPLAY="$display" python3 scripts/xtest-type.py "click:$1"; }

(cd /tmp && DISPLAY="$display" XDG_CONFIG_HOME="$work/cfg" WRAITHCAIRN_NO_DESKTOP=1 WRAITHCAIRN_RELEASES_URL="$releases" \
  setsid -f "$bin" --check-updates >"$work/run1.log" 2>&1 </dev/null)
sleep "${WAIT:-9}"
shot 1-dialog
# Coordinates are passed in by the caller after looking at the screenshot
# when not given: UPDATE_XY / RESTART_XY.
if [ -z "${UPDATE_XY:-}" ]; then echo "set UPDATE_XY and RESTART_XY from $work/1-dialog.png"; exit 2; fi
click "$UPDATE_XY"
sleep "${INSTALL_WAIT:-25}"
shot 2-installed
after_install="$("$bin" --version)"
echo "after install: $after_install"
click "$RESTART_XY"
sleep 10
shot 3-restarted
pgrep -af "$work/app/wraithcairn" || true
echo "workspace: $(test -f "$work/cfg/wraithcairn/workspace.json" && echo saved || echo missing)"
echo "WORK=$work"
