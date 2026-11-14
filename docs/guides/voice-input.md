---
sidebar_position: 7
title: Voice Input
---

# Voice Input

Press **`Ctrl+Shift+I`** in a pane and talk. What you say is typed into
that pane as you say it. Press **`Ctrl+Shift+I`** again to stop. On macOS
**`Cmd+Shift+I`** works too.

It also lives in **Pane → Voice Input** and in the pane's right-click menu.

## Private and Offline

Speech recognition runs **entirely on your computer** with
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) and a small streaming
model. No audio and no text is sent anywhere, and there is no account or
API key. It works the same on Linux, macOS and Windows (x64 and arm64).

The only network use is a **one-time model download** the first time you
use it (next section). After that, voice input works with no network at
all.

## First Use: the Model Download

The first `Ctrl+Shift+I` asks to download the speech model, the
[Kroko English streaming model](https://huggingface.co/Banafo/Kroko-ASR)
(about 71 MB, CC-BY-SA 4.0). The dialog shows:

- a **progress bar** with size, speed and time left
- **automatic retries**: if the connection drops, it waits a moment and
  resumes where it stopped (up to 4 attempts per file)
- **Try Again** if it still fails, which also resumes, so nothing is
  downloaded twice
- **Cancel** at any time; the partial download is kept for next time

Every file is pinned to one model revision and checked against its
SHA-256 before it is used. When the download finishes, dictation starts
right away.

You can also download it ahead of time (for example on a machine you set
up over SSH):

```bash
wraithcairn voice --download
```

## Where the Model Is Stored

By default the model goes in a `models` folder **next to `config.json`**:

| OS | Default model folder |
|---|---|
| Linux | `~/.config/wraithcairn/models/` |
| macOS | `~/Library/Application Support/wraithcairn/models/` |
| Windows | `%APPDATA%\wraithcairn\models\` |

To keep it somewhere else, set `voiceModelsDir` in `config.json`:

```json
{
  "voiceModelsDir": "~/.cache/wraithcairn-models"
}
```

Absolute paths, `~`, and environment variables (`%LOCALAPPDATA%\models`)
work. A relative path is relative to the config folder. If you move an
existing model folder there, it is used as is. `wraithcairn voice
--status` shows the folder in use and whether the model is installed.

## While Dictating

- The pane header shows a red **🎤** plus what it is hearing right now.
- Words are **typed into the pane as you speak**. While a phrase is in
  progress the recognizer may change its mind about the last word or two.
  When it does, only the changed part is erased and retyped, so what you
  see always matches the best guess so far. A short pause finishes the
  phrase. Phrases after the first are separated by a space.
- Don't type into the pane while a phrase is in progress: the corrections
  backspace from the cursor. Set `"voiceLiveTyping": false` if you would
  rather have each phrase typed in one go when you pause.
- It **never presses Enter**. Control characters are removed, so dictated
  text can't run a command or send escape sequences. Read it, fix it, and
  press Enter yourself.
- Only one pane listens at a time. Starting voice input in another pane
  moves it there.
- It stops by itself after 30 seconds without speech
  (`voiceIdleStopSeconds`), or when the pane closes.

The model writes English with punctuation and capital letters. It is a
dictation model, so it writes "slash" as a word rather than `/`. It works
best for prompts, commit messages and comments, and less well for shell
syntax.

## Choosing a Microphone

Voice input uses the system's default microphone. To pick another one,
list the devices and put a name (or any part of one) in `config.json`:

```bash
wraithcairn voice --devices
```

```json
{
  "voiceInputDevice": "USB"
}
```

Audio capture goes through OpenAL Soft (bundled): WASAPI on Windows,
CoreAudio on macOS, PipeWire, PulseAudio or ALSA on Linux.

**macOS** asks for microphone permission the first time. If you said
no, turn it back on in **System Settings → Privacy & Security →
Microphone**. **Windows** has the same switch under **Settings → Privacy
& security → Microphone**.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `voiceModelsDir` | `models` next to `config.json` | Where the speech model is downloaded and loaded from. |
| `voiceInputDevice` | system default | Microphone name, or part of one (see `voice --devices`). |
| `voiceIdleStopSeconds` | `30` | Stop listening after this many seconds without speech. `0` = never. |
| `voiceLiveTyping` | `true` | Type words as they are recognized. `false` = type each phrase only when you pause. |

## Command Line

| Command | What it does |
|---|---|
| `wraithcairn voice --status` | Model folder, whether it is installed, and the microphone setting. |
| `wraithcairn voice --download` | Download the model now (same resume and checksum checks). |
| `wraithcairn voice --devices` | List microphones (`*` = default). |
| `wraithcairn voice --transcribe FILE.wav` | Transcribe a 16-bit PCM WAV file, offline. |
| `wraithcairn voice --listen [SECONDS]` | Transcribe the microphone for a few seconds (default 5), a quick mic check. |

## Memory

The model loads the first time you dictate (about 2 seconds, about 190
MB of RAM). It stays loaded for 5 minutes after you stop, so starting
again is instant, and then it is freed. Speech you start while it is still
loading is buffered, not lost.

## If the Shortcut Does Nothing

If **Pane → Voice Input** works but `Ctrl+Shift+I` does nothing, start
wraithcairn with tracing on and press the shortcut once:

```bash
# macOS
WRAITHCAIRN_TRACE=1 /Applications/wraithcairn.app/Contents/MacOS/wraithcairn
# Linux
WRAITHCAIRN_TRACE=1 wraithcairn
```

- **No `TRACE key I` line:** the key never reached wraithcairn. Another
  app or a system shortcut took it first. On macOS, check **System
  Settings → Keyboard → Keyboard Shortcuts** and any hotkey or window
  manager apps (Raycast, Alfred, BetterTouchTool, Rectangle, Karabiner).
  On macOS, `Cmd+Shift+I` does the same thing and may be free.
- **`TRACE key I mods=Control, Shift` but no `TRACE voice toggle` line:**
  wraithcairn saw the key but did not act on it. That is a bug; please
  report it with the trace lines.
- **`TRACE voice toggle` then nothing in the pane:** the shortcut worked.
  Check the pane header for the red mic, and the microphone permission.
