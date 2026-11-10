# spike-royalterminal

A cross-platform, tabbed and split terminal emulator built with Avalonia on
top of the [RoyalTerminal](https://www.nuget.org/packages/RoyalApps.RoyalTerminal.Avalonia)
NuGet packages, which wrap Ghostty's native `libghostty-vt` VT engine. It
restores its window/tab/pane layout (splits, ratios, per-pane working
directory) from a JSON file on startup.

## Build & run

Requires the .NET 10 SDK.

```bash
dotnet run
```

## Self-test mode

Running with `--selftest` exercises save/restore without manual interaction.
The app launches, saves its layout, then on a second invocation restores it
and reports status via `SELFTEST` lines on stdout, e.g.:

```
SELFTEST echo_output_visible=True
SELFTEST ghostty=True
SELFTEST phase=restored
```

CI runs this twice in a row (see `.github/workflows/ci.yml`) to validate
both the save and restore paths headlessly.

## Layout persistence

The workspace (tabs, splits, ratios, per-pane cwd) is saved to:

- Linux: `~/.config/spike-royalterminal/workspace.json`
- Windows: `%APPDATA%\spike-royalterminal\workspace.json`
- macOS: `~/.config/spike-royalterminal/workspace.json` (Avalonia's
  `ApplicationData` special folder)

It's saved on a 5-second debounce after layout changes, on window close, and
on process exit/SIGTERM/SIGINT.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+T` | New tab (or click `+`) |
| `Ctrl+Shift+N` | New window |
| `Ctrl+Shift+W` | Close pane (closes the tab if it's the last pane) |
| `Ctrl+Shift+O` | Split right |
| `Ctrl+Shift+E` | Split down |
| `Alt+Arrows` | Move focus between panes |
| `Ctrl+Alt+Arrows` | Resize split |
| `Ctrl+Shift+Z` | Zoom pane |
| `Ctrl+Shift+=` | Equalize splits |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | Copy selection / paste |
| `Ctrl+Shift+F` | Find (Enter next, Shift+Enter prev, Esc close) |
| `Ctrl+Shift+K` | Clear scrollback |
| `Ctrl+PgUp` / `Ctrl+PgDn` | Previous / next tab |
| `Ctrl+Shift+PgUp` / `Ctrl+Shift+PgDn` | Move tab left / right |
| `Ctrl+=` / `Ctrl+-` | Bigger / smaller text |

These are also available from the menu bar (Shell, Pane, Edit, View).

## Publishing

See `scripts/publish-local.sh` for a local self-contained Linux build, and
`.github/workflows/release.yml` for tagged (`v*`) cross-platform releases
(linux-x64, win-x64, osx-arm64, osx-x64). Builds are self-contained but
**not** single-file: bundling as a single file breaks `System.Native`
native-library loading in this app.

## Status

This is a spike/prototype; the project name is not final.
