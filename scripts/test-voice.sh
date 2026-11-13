#!/usr/bin/env bash
# Voice input end to end, offline after the first step:
#   1. `wraithcairn voice --download` fetches the pinned speech model into an
#      isolated config dir ("models" next to config.json) and verifies it.
#   2. `voice --transcribe` recognizes the model's sample WAV locally.
#   3. voiceModelsDir in config.json moves the model folder (relative path).
#   4. Optional (Linux with PulseAudio/PipeWire): a virtual sink's monitor
#      stands in for the microphone, so real OpenAL capture is exercised.
#   5. The GUI selftest: Ctrl+Shift+I dictates the WAV into a pane.
#
#   scripts/test-voice.sh [path/to/wraithcairn]   (default: Release build)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

bin="${1:-bin/Release/net10.0/wraithcairn}"
[ -x "$bin" ] || bin="$bin.exe"
base="$(mktemp -d "${JCODE_SCRATCH_DIR:-${RUNNER_TEMP:-/tmp}}/wc-voice.XXXXXX")"
export WRAITHCAIRN_CONFIG_DIR="$base/cfg"
mkdir -p "$WRAITHCAIRN_CONFIG_DIR"
model_id=sherpa-onnx-streaming-zipformer-en-kroko-2025-08-06
fail=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi; }

"$bin" voice --status | tee "$base/status.txt"
check "status: not installed yet" 'grep -q "installed: no" "$base/status.txt"'
check "status: default dir next to config.json" 'grep -q "$model_id" "$base/status.txt" && grep -q "/cfg/models/" "$base/status.txt"'

"$bin" voice --download 2>&1 | tail -3
check "download into config dir" '[ -s "$WRAITHCAIRN_CONFIG_DIR/models/$model_id/encoder.onnx" ]'
check "no .part leftovers" '! ls "$WRAITHCAIRN_CONFIG_DIR/models/$model_id/"*.part >/dev/null 2>&1'

# Sample speech: the model repo's own test clip (JFK), pinned revision.
wav="$base/jfk.wav"
curl -fsSL -o "$wav" "https://huggingface.co/csukuangfj/$model_id/resolve/572aaf4e2e0c603c3fc2a574d096e755a178faa1/test_wavs/0.wav"

text="$("$bin" voice --transcribe "$wav" 2>/dev/null | tr -d '\r')"
echo "transcribed: $text"
check "offline transcription" 'echo "$text" | grep -qi "what your country can do for you"'

# Moved model dir via config (relative to the config folder).
printf '{ "voiceModelsDir": "speech" }\n' >"$WRAITHCAIRN_CONFIG_DIR/config.json"
"$bin" voice --status | tee "$base/status2.txt"
check "voiceModelsDir respected" 'grep -q "/cfg/speech/$model_id" "$base/status2.txt" && grep -q "installed: no" "$base/status2.txt"'
mv "$WRAITHCAIRN_CONFIG_DIR/models" "$WRAITHCAIRN_CONFIG_DIR/speech"
"$bin" voice --status | grep -q "installed: yes" && echo "ok   moved model found" || { echo "FAIL moved model found"; fail=1; }

# Real capture through OpenAL from a virtual microphone (Linux desktop only).
if command -v pactl >/dev/null && command -v paplay >/dev/null && pactl info >/dev/null 2>&1; then
  module=$(pactl load-module module-null-sink sink_name=wcvoicetest sink_properties=device.description=wcvoicetest)
  trap 'pactl unload-module "$module" 2>/dev/null || true' EXIT
  sleep 1
  printf '{ "voiceModelsDir": "speech", "voiceInputDevice": "Monitor of wcvoicetest" }\n' >"$WRAITHCAIRN_CONFIG_DIR/config.json"
  ( sleep 2; paplay --device=wcvoicetest "$wav" ) &
  heard="$("$bin" voice --listen 9 2>/dev/null | tr -d '\r')"
  wait
  echo "heard via microphone: $heard"
  check "microphone capture (OpenAL)" 'echo "$heard" | grep -qi "your country"'
else
  echo "skip microphone capture (no PulseAudio/PipeWire)"
fi

# GUI: Ctrl+Shift+I in a pane, WAV standing in for the microphone.
if [ -n "${DISPLAY:-}" ] || command -v xvfb-run >/dev/null; then
  run=(); [ -z "${DISPLAY:-}" ] && run=(xvfb-run -a)
  out="$(cd "$base" && WRAITHCAIRN_VOICE_SELFTEST_MODEL="$WRAITHCAIRN_CONFIG_DIR/speech/$model_id" \
    WRAITHCAIRN_VOICE_SELFTEST_WAV="$wav" timeout 150 "${run[@]}" "$OLDPWD/$bin" --selftest 2>&1 || true)"
  echo "$out" | grep "SELFTEST voice" || true
  check "selftest voice_download" 'echo "$out" | grep -q "SELFTEST voice_download=True"'
  check "selftest voice_retry" 'echo "$out" | grep -q "SELFTEST voice_retry=True"'
  check "selftest voice_cancel" 'echo "$out" | grep -q "SELFTEST voice_cancel=True"'
  check "selftest voice_live_edits" 'echo "$out" | grep -q "SELFTEST voice_live_edits=True"'
  check "selftest voice_dictate (live)" 'echo "$out" | grep -q "SELFTEST voice_dictate=True.*live_before_final=True"'
fi

exit $fail
