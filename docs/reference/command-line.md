---
sidebar_position: 3
title: Command Line
---

# Command Line

| Flag | Meaning |
|---|---|
| `--workspace NAME` / `-w NAME` | Start this instance bound to `layouts/NAME.json` instead of the default `workspace.json`. Layouts opened and saved by this instance go to that named workspace file. See [Layouts](/docs/guides/layouts#named-workspaces). |
| `--selftest` | Headless self-test: builds a layout, checks the Ghostty engine, GPU, search, splits, layout switching, and save/open, then saves and exits. Running it a second time additionally checks restore. Prints `SELFTEST key=value` lines. **Overwrites `workspace.json`.** Used by CI (see `.github/workflows/ci.yml`), run twice headless. |
| `--dump-panes` | Restores the current layout, prints each pane's startup configuration, working directory, and last screen lines, then exits **without saving**. Useful for inspecting saved state without risking a write. |
| `--configure-pane` | Opens straight to the **Configure Pane** dialog for the focused/primary pane on launch. |
| `update` | Downloads and installs the latest release if it is newer than this one (SHA-256 verified), then tells you to restart. `update --check` only reports (exit code `0` up to date, `10` update available); `update --force` reinstalls. See [Updating](/docs/getting-started/installation#updating). |
| `--list-themes` | Lists every available theme (online catalog plus local ones); `*` marks downloaded ones. No window. |
| `--download-themes` | Downloads every theme to `themes/` so they all work offline. No window. |
| `--set-theme NAME` | Downloads `NAME` if needed and saves it as `theme` in `config.json`. `--set-theme ghostty` clears it (inherit Ghostty's). Running instances repaint immediately. No window. |
| `--check-updates` | Opens **Check for Updates…** right after launch. |
| `voice --status` / `--download` / `--devices` / `--transcribe FILE` / `--listen [SECS]` | Voice input helpers, no window: show the model folder, download the speech model, list microphones, transcribe a WAV file or the microphone offline. See [Voice Input](/docs/guides/voice-input#command-line). |
| `--version` / `-v` | Prints `wraithcairn <version>` and exits. |
| `--install-desktop` | Linux: writes `~/.local/share/applications/wraithcairn.desktop` and icons pointing at this binary, so it gets its own task manager entry and appears in the launcher. Normal launches do this automatically when there is no entry yet, or when the entry's binary no longer exists; a copy run from elsewhere leaves a working entry alone. Set `WRAITHCAIRN_NO_DESKTOP=1` to skip it. |
| `--uninstall-desktop` | Linux: removes that desktop entry and its icons. |

## Examples

```bash
# Open (or create) a separate named workspace for a project
wraithcairn --workspace my-project

# Same, short form
wraithcairn -w my-project

# Inspect the current saved layout without touching it
wraithcairn --dump-panes

# Headless sanity check, e.g. in CI
wraithcairn --selftest

# Switch every open window to a new theme
wraithcairn --set-theme "Catppuccin Mocha"

# Update to the latest release
wraithcairn update

# Print the version and exit
wraithcairn --version
```

## See Also

- [Layouts](/docs/guides/layouts)
- [Files and Paths](/docs/reference/files-and-paths)
