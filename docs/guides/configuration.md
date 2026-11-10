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
| `theme` | string | from Ghostty / built-in | A Ghostty theme name, e.g. `"GitHub Dark Dimmed"`. |
| `copyOnSelect` | boolean | `false` (or from Ghostty) | Copy a selection to the clipboard automatically. |
| `restoreLayout` | boolean | `true` | Restore the saved workspace on launch. `false` always starts fresh. |
| `restoreScrollback` | boolean | `false` | Also save/replay each pane's scrollback (see below). |
| `useGhosttyConfig` | boolean | `true` | Read `~/.config/ghostty/config` as a base layer at all. |
| `shell` | string or `null` | `$SHELL` | Explicit shell to launch, e.g. `"/usr/bin/fish"`. `null` uses `$SHELL`. |

All keys are optional; omit anything you want left at its default or
inherited from the Ghostty layer.

## Themes by Name

Setting `theme` (in either the Ghostty config or `config.json`) looks the
name up in the usual Ghostty theme directories — user, system, and an
extracted AppImage copy — so a Ghostty theme name such as
`"GitHub Dark Dimmed"` just works without copying any files.

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
