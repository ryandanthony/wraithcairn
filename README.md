# wraithcairn

Docs: https://ryandanthony.github.io/wraithcairn/

A cross-platform split-pane terminal built with Avalonia on top of the
[RoyalTerminal](https://www.nuget.org/packages/RoyalApps.RoyalTerminal.Avalonia)
NuGet packages, which wrap Ghostty's native `libghostty-vt` VT engine.

Instead of tabs, a window holds named **layouts** (a set of split panes) and
shows one at a time. Each pane can start in its own folder and run its own
program with arguments. Everything restores on the next launch.

> [!NOTE]
> **This project was developed 100% by AI.** Every line of code, the docs,
> the build and release pipelines were written by AI coding agents, directed
> and reviewed for behaviour by a human. The goal was a working tool I
> actually wanted to use, so the outcome took priority over code quality:
> expect pragmatic, unpolished code rather than an example of best practice.

## Build & run

Requires the .NET 10 SDK.

```bash
dotnet run
```

Local self-contained Linux build, a single `wraithcairn` executable in
`~/temp/wraithcairn-single` (`--folder` for the classic folder build in
`~/temp/wraithcairn`):

```bash
./scripts/publish-local.sh
```

## Layouts

Switch from the **Layouts** menu:

- open layouts are listed at the top; click one or press `Alt+1..9`
- **Next / Previous Layout** (`Ctrl+PgDn` / `Ctrl+PgUp`)
- **New Layout** (`Ctrl+Shift+T`), **Rename Layout**, **Close Layout**
- **Save Layout As** writes the current layout to `layouts/NAME.json`;
  **Open Saved Layout** loads it back as a new layout in this window

Layouts you switch away from keep running in the background.
`--workspace NAME` starts an instance bound to a named workspace file.

## Per-pane startup

**Pane > Configure Pane** (`Ctrl+Shift+P`, or right-click a pane) sets a
pane's start folder, program and arguments, and whether the program runs
inside the shell or as the pane's own process. Stored on the pane in the
layout JSON:

```json
"startup": {
  "directory": "~/code/app",
  "command": "npm",
  "args": ["run", "dev"],
  "runInShell": true
}
```

`Ctrl+Shift+R` restarts a pane from its startup config.

## Configuration

Settings layer in this order (later wins):

1. built-in defaults
2. `~/.config/ghostty/config`: font-family, font-size, theme, colours,
   copy-on-select (Ghostty themes are found by name)
3. `~/.config/wraithcairn/config.json`: `fontFamily`, `fontSize`, `theme`,
   `copyOnSelect`, `shell`, `restoreLayout`, `restoreScrollback`,
   `useGhosttyConfig`

Shell integration (OSC 7 cwd, OSC 133 prompts) is injected for bash, zsh,
fish and PowerShell without editing your dotfiles.

## Files

Under `~/.config/wraithcairn/` (Linux/macOS) or `%APPDATA%\wraithcairn\`
(Windows):

| File | Contents |
|---|---|
| `workspace.json` | open windows, layouts, splits, ratios, per-pane cwd + startup |
| `layouts/NAME.json` | saved layouts |
| `config.json` | app settings |
| `scrollback/` | per-pane scrollback (only with `restoreScrollback`) |

Saved 5 s after layout changes, on window close, and on exit/SIGTERM/SIGINT.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+T` | New layout |
| `Ctrl+Shift+N` | New window |
| `Ctrl+Shift+W` | Close pane (closes the layout if it's the last pane) |
| `Ctrl+Shift+O` / `Ctrl+Shift+E` | Split right / down |
| `Alt+Arrows` | Move focus between panes |
| `Ctrl+Alt+Arrows` | Resize split |
| `Ctrl+Shift+Z` | Zoom pane |
| `Ctrl+Shift+=` | Equalize splits |
| `Ctrl+Shift+P` | Configure pane (folder, program, args) |
| `Ctrl+Shift+R` | Restart pane |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | Copy selection / paste |
| `Ctrl+Shift+F` | Find (Enter next, Shift+Enter prev, Esc close) |
| `Ctrl+Shift+K` | Clear scrollback |
| `Ctrl+PgUp` / `Ctrl+PgDn` | Previous / next layout |
| `Alt+1..9` | Switch to layout N |
| `Ctrl+=` / `Ctrl+-` | Bigger / smaller text |

All of these are also in the menu bar (Shell, Pane, Edit, View, Layouts).

## Self-test

`--selftest` builds a layout, checks the Ghostty engine, GPU, search,
splits, layout switching and save/open, saves, and exits. A second run
checks restore. Output is `SELFTEST key=value` lines. CI runs it twice
headless (see `.github/workflows/ci.yml`). Note: it overwrites
`workspace.json`.

`--dump-panes` restores the layout, prints each pane's startup, cwd and
last screen lines, and exits without saving.

## Publishing

`.github/workflows/release.yml` publishes on every push to `main` for
linux-x64, win-x64, osx-arm64 and osx-x64. Each build is one
self-contained executable. Native libraries (libghostty-vt, Skia,
HarfBuzz) are bundled and self-extract on first run to the .NET bundle
cache (`~/.net/wraithcairn/`, or `$DOTNET_BUNDLE_EXTRACT_BASE_DIR`).

The SDK normally leaves `libSystem.Native` out of single-file bundles
because the host links it statically, but RoyalTerminal's Unix PTY code
P/Invokes it by name. The `KeepSystemNativeInSingleFile` target in
`wraithcairn.csproj` puts it back. CI checks the single-file build with
`--selftest`.

## License

MIT
