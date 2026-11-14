#!/usr/bin/env bash
# End-to-end acceptance of voice input against a real wraithcairn binary
# (e.g. the released one), with real keys and a real capture path:
#   - its own X server (Xvfb, or Xwayland) so nothing reaches your desktop
#   - a PulseAudio/PipeWire null sink whose monitor is the "microphone"
#     (OpenAL capture, exactly as with a real mic)
#   - Ctrl+Shift+I typed with XTest, what a keyboard sends
#   - the pane runs a raw-mode logger that timestamps every byte the
#     program receives, so we see when words arrived relative to the end
#     of the speech, every correction (DEL), and that no Enter was sent.
#   scripts/e2e-voice.sh BIN MODEL_DIR WAV [DISPLAY]
#   E2E_LIVE=false checks the voiceLiveTyping=false path (text only after
#   the phrase ends).
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bin="$(realpath "$1")"; model="$(realpath "$2")"; wav="$(realpath "$3")"; display="${4:-:97}"
work="$(mktemp -d "${JCODE_SCRATCH_DIR:-/tmp}/wc-e2e.XXXXXX")"
mkdir -p "$work/cfg/wraithcairn/models"
cp -r "$model" "$work/cfg/wraithcairn/models/"
module=$(pactl load-module module-null-sink sink_name=wce2e sink_properties=device.description=wce2e)
xpid=""; app=""
cleanup() {
  if [ -n "$app" ]; then kill "$app" 2>/dev/null || true; fi
  if [ -n "$xpid" ]; then kill "$xpid" 2>/dev/null || true; fi
  pactl unload-module "$module" 2>/dev/null || true
}
trap cleanup EXIT
printf '{ "restoreLayout": false, "showSplash": false, "useGhosttyConfig": false, "voiceInputDevice": "Monitor of wce2e", "shell": "/bin/bash", "voiceIdleStopSeconds": 0, "voiceLiveTyping": %s }\n' "${E2E_LIVE:-true}" \
  >"$work/cfg/wraithcairn/config.json"

# Logger the pane runs: raw tty, one line per read: <unix time> <repr bytes>.
cat >"$work/log.py" <<'EOF'
import os, sys, termios, time, tty
fd = sys.stdin.fileno()
old = termios.tcgetattr(fd)
tty.setraw(fd)
out = open(sys.argv[1], "w", buffering=1)
end = time.time() + float(sys.argv[2])
try:
    while time.time() < end:
        import select
        r, _, _ = select.select([fd], [], [], 0.2)
        if r:
            out.write(f"{time.time():.3f} {os.read(fd, 4096)!r}\n")
finally:
    termios.tcsetattr(fd, termios.TCSADRAIN, old)
EOF

if command -v Xvfb >/dev/null; then Xvfb "$display" -screen 0 1200x800x24 >/dev/null 2>&1 &
else Xwayland "$display" -geometry 1200x800 >/dev/null 2>&1 &
fi
xpid=$!
sleep 2
export DISPLAY="$display"
XDG_CONFIG_HOME="$work/cfg" WRAITHCAIRN_NO_DESKTOP=1 WRAITHCAIRN_TRACE=1 WRAITHCAIRN_VOICE_SNAPSHOT="$work/inapp-stopped.png" \
  "$bin" >"$work/app.log" 2>&1 &
app=$!
sleep 8
echo "binary: $("$bin" --version)"
shot() {  # pointer nudge first: a bare Xwayland root can hand back a stale frame
  python3 - <<'PY'
import ctypes, ctypes.util, time
x = ctypes.CDLL(ctypes.util.find_library("X11")); t = ctypes.CDLL(ctypes.util.find_library("Xtst"))
x.XOpenDisplay.restype = ctypes.c_void_p; x.XOpenDisplay.argtypes = [ctypes.c_char_p]
t.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
x.XFlush.argtypes = [ctypes.c_void_p]
d = x.XOpenDisplay(None)
for px in (300, 700, 600):
    t.XTestFakeMotionEvent(d, -1, px, 52 if px != 600 else 500, 0); x.XFlush(d); time.sleep(0.15)
PY
  sleep 0.4
  python3 "$repo/scripts/x-shot.py" "$work/$1" >/dev/null 2>&1 || true
}
keys() { python3 "$repo/scripts/xtest-type.py" "$@"; }
keys click:400,300
sleep 0.5
keys "text:python3 $work/log.py $work/bytes.txt 30" Return
sleep 1.5
t_key=$(date +%s.%N)
keys ctrl+shift+i
sleep 4                                   # model load + mic open
t_play=$(date +%s.%N)
shot listening.png
( sleep 2.5; shot listening-late.png ) &
late=$!
paplay --device=wce2e "$wav"
wait "$late"
t_speech_end=$(date +%s.%N)
sleep 3                                   # trailing pause ends the phrase
keys ctrl+shift+i
for s in 1 2 4 6; do
  sleep $(( s == 1 ? 1 : 2 ))
  shot "stopped-${s}s.png"
done
sleep 16                                  # let the logger exit


python3 - "$work/bytes.txt" "$t_play" "$t_speech_end" "${E2E_LIVE:-true}" <<'EOF'
import ast, sys
path, t_play, t_end = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])
rows = []
for line in open(path):
    t, b = line.split(" ", 1)
    rows.append((float(t), ast.literal_eval(b)))
data = b"".join(b for _, b in rows)
line = []
for ch in data.decode("utf-8", "replace"):
    if ch == "\x7f":
        if line: line.pop()
    else:
        line.append(ch)
final = "".join(line)
before = [(t, b) for t, b in rows if t < t_end]
text_before = b"".join(b for _, b in before).decode("utf-8", "replace").replace("\x7f", "")
first = rows[0][0] - t_play if rows else None
print(f"reads={len(rows)} first_bytes_after_audio_start={first and round(first, 2)}s speech_len={t_end - t_play:.2f}s")
print(f"reads_before_speech_ended={len(before)} chars_before_speech_ended={len(text_before)}")
print(f"deletes={data.count(b'\x7f')} enter_sent={b'\r' in data or b'\n' in data} escapes={b'\x1b' in data}")
print(f"final_line={final!r}")
live = len(text_before) >= 10
expect_live = sys.argv[4] == "true"
ok = live == expect_live and "country" in final.lower() and b"\r" not in data and b"\n" not in data and b"\x1b" not in data
print(f"E2E live_typing={live} expected={expect_live} final_ok={'country' in final.lower()} result={'PASS' if ok else 'FAIL'}")
EOF
grep -c "TRACE key I mods=Control, Shift" "$work/app.log" | sed 's/^/ctrl+shift+i key events seen by app: /'
# The red mic indicator (#f38ba8) in the pane header. On: an X capture
# taken while the speech plays (screen captures of a bare X server can trail
# the window by a frame or two, so this is sampled well into listening).
# Off: the app's own render of its window 1 s after dictation ended
# (WRAITHCAIRN_VOICE_SNAPSHOT), plus the trace saying the session ended.
python3 - "$work" <<'EOF'
import sys
from PIL import Image
w = sys.argv[1]
def red(name, top):
    try:
        im = Image.open(f"{w}/{name}").convert("RGB").crop((0, top, 1200, top + 24))
    except FileNotFoundError:
        return -1
    return sum(1 for p in im.getdata() if abs(p[0] - 0xf3) < 24 and abs(p[1] - 0x8b) < 24 and abs(p[2] - 0xa8) < 24)
on = max(red("listening.png", 40), red("listening-late.png", 40))
off = red("inapp-stopped.png", 30)
ended = "TRACE voice ended reason=stopped" in open(f"{w}/app.log").read()
print(f"indicator_pixels listening={on} after_stop_inapp={off} session_ended={ended}")
print(f"E2E indicator={'PASS' if on > 30 and off == 0 and ended else 'FAIL'}")
EOF
echo "work: $work"
