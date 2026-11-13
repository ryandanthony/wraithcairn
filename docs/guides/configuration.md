---
sidebar_position: 4
title: Configuration
---

# Configuration

Settings are layered, with each later layer overriding the one before it:

1. **Built-in defaults**
2. `~/.config/ghostty/config` — Ghostty's own config file, read for
   `font-family`, `font-size`, `theme`, and color keys
   (`background`/`foreground`/`palette`/`cursor-color`/`selection-*`) and
   `copy-on-select`
3. `~/.config/wraithcairn/config.json` — Wraithcairn's own overrides

This means an existing Ghostty setup is picked up automatically, and
`config.json` only needs to hold the handful of settings you want to
differ from it (or everything, if you don't use Ghostty's config file at
all).

## config.json Keys

| Key | Type | Default | Meaning |
|---|---|---|---|
| `fontFamily` | string | from Ghostty / built-in | Terminal font family. |
| `fontSize` | number | `11` (or from Ghostty) | Terminal font size. |
| `theme` | string or `null` | from Ghostty / built-in | A Ghostty theme name, e.g. `"GitHub Dark Dimmed"`. Set from **View → Themes…** or `--set-theme`; applied live. `null` = use Ghostty's. See [Themes](/docs/guides/themes). |
| `copyOnSelect` | boolean | `false` (or from Ghostty) | Copy a selection to the clipboard automatically. |
| `restoreLayout` | boolean | `true` | Restore the saved workspace on launch. `false` always starts fresh. |
| `restoreScrollback` | boolean | `false` | Also save/replay each pane's scrollback (see below). |
| `useGhosttyConfig` | boolean | `true` | Read `~/.config/ghostty/config` as a base layer at all. |
| `showSplash` | boolean | `true` | Show a splash window at startup while the saved layout loads (status line plus a progress bar as panes start). |
| `shell` | string or `null` | `$SHELL` | Explicit shell to launch, e.g. `"/usr/bin/fish"`. `null` uses `$SHELL`. |
| `voiceModelsDir` | string or `null` | `models` next to `config.json` | Where [voice input](/docs/guides/voice-input) keeps its speech model. Relative paths are relative to the config folder; `~` and environment variables expand. |
| `voiceInputDevice` | string or `null` | system default | Microphone for voice input, by name or part of one (`wraithcairn voice --devices`). |
| `voiceIdleStopSeconds` | number | `30` | Voice input stops after this many seconds without speech. `0` = never. |
| `voiceLiveTyping` | boolean | `true` | Voice input types words as they are recognized and fixes them up as the phrase firms up. `false` = each phrase is typed once, when you pause. |

All keys are optional; omit anything you want left at its default or
inherited from the Ghostty layer.

## Themes by Name

Setting `theme` (in either the Ghostty config or `config.json`) looks the
name up in the usual Ghostty theme directories (user and system) plus
`~/.config/wraithcairn/themes/`, where **View → Themes…** downloads them.
So a Ghostty theme name such as `"GitHub Dark Dimmed"` just works.

The theme is the one setting that applies live: change it in the Themes
dialog, with `--set-theme`, or by editing `config.json` (or the Ghostty
config) and every open pane repaints without a restart. See
[Themes](/docs/guides/themes).

## Copy on Select

When `copyOnSelect` is on, selecting text in a pane copies it to the
system clipboard immediately, without needing `Ctrl+Shift+C`.

## Restoring Scrollback

With `restoreScrollback: true`, each pane's visible screen plus
scrollback is exported (with styling/colors preserved) and written to
`~/.config/wraithcairn/scrollback/{paneId}.vt` whenever the workspace is
saved. On the next restore, that output is replayed into the pane before
its shell starts, so the previous session's output appears above the new
prompt. Scrollback files are capped per pane and pruned for panes that no
longer exist. This is off by default; turning it on means more written to
disk on every save.

## See Also

- [Files and Paths](/docs/reference/files-and-paths) — exact locations
  per OS
- [Shell Integration](/docs/guides/shell-integration) — how `shell` is
  used to inject OSC 7/133 support
