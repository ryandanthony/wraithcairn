#!/usr/bin/env bash
# Finder/Dock launch on macOS, reproduced anywhere: wraithcairn starts with
# launchd's bare environment (PATH=/usr/bin:/bin:/usr/sbin:/sbin, no LANG)
# and the user's PATH only comes from login files, as with Homebrew's
# `eval "$(brew shellenv)"` in ~/.zprofile. Checks that a zsh shell pane and
# an exec pane (runInShell=false) both find a tool from that PATH, and that
# ~/.zshenv, ~/.zprofile, ~/.zshrc, ~/.zlogin each run once and ZDOTDIR ends
# up back at the user's.
#   scripts/test-login-path.sh            (zsh from PATH)
#   ZSH_BIN=/path/to/zsh scripts/test-login-path.sh
# WRAITHCAIRN_LOGIN=1 forces the macOS behaviour on Linux. Uses a throwaway
# HOME, never the real one. Needs a display (xvfb-run on Linux CI; macOS
# runners have a GUI session). Exit 0 = all checks passed.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

zsh_bin="${ZSH_BIN:-$(command -v zsh || true)}"
[ -x "$zsh_bin" ] || { echo "zsh not found (set ZSH_BIN)"; exit 2; }
bin="${WRAITHCAIRN_BIN:-$PWD/bin/Release/net10.0/wraithcairn}"

home="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-login-path.XXXXXX")"
# Where wraithcairn keeps config.json/workspace.json (.NET ApplicationData).
if [ "$(uname)" = Darwin ]; then cfgdir="$home/Library/Application Support/wraithcairn"
else cfgdir="$home/.config/wraithcairn"; fi
mkdir -p "$home/brew/bin" "$cfgdir"
cat >"$home/brew/bin/wc-fake-tool" <<'EOF'
#!/bin/sh
echo "FAKE_TOOL_OK:$1:ZDOTDIR=${ZDOTDIR:-unset}"
EOF
chmod +x "$home/brew/bin/wc-fake-tool"

# Each file logs that it ran; .zprofile is the only place PATH gains brew/bin.
# ZSH_MODULE_DIR: for a zsh unpacked outside / (zsh won't take MODULE_PATH
# from the environment).
cat >"$home/.zshenv" <<EOF
${ZSH_MODULE_DIR:+module_path=("$ZSH_MODULE_DIR")}
EOF
cat >>"$home/.zshenv" <<'EOF'
# Ubuntu's /etc/zsh/zshrc runs compinit, which stops at an "insecure
# directories" prompt on CI runners (and eats the typed command).
skip_global_compinit=1
echo zshenv >>"$HOME/ran.log"
EOF
cat >"$home/.zprofile" <<'EOF'
echo zprofile >>"$HOME/ran.log"
export PATH="$HOME/brew/bin:$PATH"
EOF
cat >"$home/.zshrc" <<'EOF'
echo "zshrc ZDOTDIR=${ZDOTDIR:-unset}" >>"$HOME/ran.log"
EOF
cat >"$home/.zlogin" <<'EOF'
echo zlogin >>"$HOME/ran.log"
EOF

cat >"$cfgdir/config.json" <<EOF
{ "shell": "$zsh_bin", "useGhosttyConfig": false, "showSplash": false }
EOF
pane() { # id, startup json or empty
  printf '{ "id": "%s", "title": null, "profileId": null, "workingDirectory": null, "transportId": "pty", "transportProfileId": null, "split": null%s }' \
    "$1" "${2:+, \"startup\": $2}"
}
cat >"$cfgdir/workspace.json" <<EOF
{ "formatVersion": 1, "selectedWindowId": "window-1", "windows": [ {
  "id": "window-1", "title": "t", "selectedTabId": "tab-1", "widthPixels": 1200, "heightPixels": 700,
  "isMaximized": false, "tabsInTitleBar": false,
  "tabs": [ { "id": "tab-1", "profileId": "default", "title": "main", "workingDirectory": null,
    "transportId": "pty", "transportProfileId": null, "renderMode": "Default",
    "rootPane": { "id": "pane-1", "title": null, "profileId": null, "workingDirectory": null,
      "transportId": null, "transportProfileId": null,
      "split": { "orientation": "Vertical", "ratio": 0.5,
        "firstPane": $(pane pane-2 '{ "command": "wc-fake-tool", "args": ["shell"], "runInShell": true }'),
        "secondPane": $(pane pane-3 '{ "command": "wc-fake-tool", "args": ["exec"], "runInShell": false }') } } } ] } ] }
EOF

out="$home/out.txt"
# macOS has no `timeout`; perl's alarm does the same job.
if command -v timeout >/dev/null; then limit=(timeout 60); else limit=(perl -e 'alarm 60; exec @ARGV'); fi
env -i HOME="$home" USER="${USER:-user}" PATH=/usr/bin:/bin:/usr/sbin:/sbin \
  SHELL="$zsh_bin" WRAITHCAIRN_LOGIN="${WRAITHCAIRN_LOGIN:-1}" WRAITHCAIRN_NO_DESKTOP=1 \
  ${DISPLAY:+DISPLAY="$DISPLAY"} ${XAUTHORITY:+XAUTHORITY="$XAUTHORITY"} \
  ${DOTNET_ROOT:+DOTNET_ROOT="$DOTNET_ROOT"} ${FPATH:+FPATH="$FPATH"} \
  "${limit[@]}" "$bin" --dump-panes >"$out" 2>&1 || true

cat "$out"
echo "--- ran.log"
cat "$home/ran.log" 2>/dev/null || true
echo "---"

fail=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi; }
check "shell pane finds tool from ~/.zprofile PATH" 'grep -q "PANE pane-2 screen:.*FAKE_TOOL_OK:shell" "$out"'
check "exec pane finds tool from login PATH" 'grep -q "PANE pane-3 screen:.*FAKE_TOOL_OK:exec" "$out"'
check "no 'command not found'" '! grep -q "command not found" "$out"'
# Two zsh processes: the shell pane and the login-env capture for the exec pane.
for f in zshenv zprofile zshrc zlogin; do
  check "~/.$f ran once per zsh (2)" '[ "$(grep -c "^$f" "$home/ran.log")" -eq 2 ]'
done
# The pane's zsh goes through our ZDOTDIR proxy (user's files see their own
# ZDOTDIR); the capture zsh doesn't (ZDOTDIR unset). Neither may see ours.
check "~/.zshrc ran with the user's ZDOTDIR" 'grep -qx "zshrc ZDOTDIR=$home" "$home/ran.log" && ! grep -q wraithcairn/zdotdir "$home/ran.log"'
check "shell pane: ZDOTDIR handed back (unset)" 'grep -q "FAKE_TOOL_OK:shell:ZDOTDIR=unset" "$out"'
check "exec pane: no ZDOTDIR leak" 'grep -q "FAKE_TOOL_OK:exec:ZDOTDIR=unset" "$out"'

rm -rf "$home"
exit "$fail"
