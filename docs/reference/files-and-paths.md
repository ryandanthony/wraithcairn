---
sidebar_position: 2
title: Files and Paths
---

# Files and Paths

All of Wraithcairn's own state lives under one config directory per OS:

| OS | Config directory |
|---|---|
| Linux | `~/.config/wraithcairn/` (or `$XDG_CONFIG_HOME/wraithcairn/`) |
| macOS | `~/Library/Application Support/wraithcairn/` |
| Windows | `%APPDATA%\wraithcairn\` |

Set `WRAITHCAIRN_CONFIG_DIR` to use a different directory (handy for
throwaway test runs; on macOS `XDG_CONFIG_HOME` and `$HOME` don't move
Application Support).

## Files and Folders

| Path | Contents |
|---|---|
| `workspace.json` | The default workspace: open windows, layouts, splits, ratios, and per-pane working directory + startup. |
| `layouts/NAME.json` | Saved layouts, written by **Save Layout As...** and loaded by **Open Saved Layout**. Also the workspace file used by `--workspace NAME`. |
| `config.json` | App settings (see [Configuration](/docs/guides/configuration)). Watched: theme changes apply live. |
| `themes/` | Downloaded color themes, one Ghostty-format file per theme (see [Themes](/docs/guides/themes)). |
| `theme-catalog.json` | Cached copy of the online theme list, so the Themes dialog works offline. |
| `scrollback/` | Per-pane scrollback files (`{paneId}.vt`), only written when `restoreScrollback` is enabled. Scoped under a per-workspace subfolder when running with `--workspace NAME`. |

## Save Timing

The active workspace is saved:

- 5 seconds after any layout change
- when a window closes
- on exit, `SIGTERM`, or `SIGINT`

## Ghostty Config

Wraithcairn also reads (but never writes) Ghostty's own config file as a
base settings layer:

| OS | Path |
|---|---|
| Linux / macOS | `~/.config/ghostty/config` |

See [Configuration](/docs/guides/configuration) for how it layers with
`config.json`.

## App Icon

The window/taskbar icon comes from `assets/icon-256.png` in the
repository, and the Windows executable icon from `assets/icon.ico`. These
are build-time assets, not user-configurable.
