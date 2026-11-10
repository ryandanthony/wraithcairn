---
sidebar_position: 1
title: What is Wraithcairn?
---

# What is Wraithcairn?

Wraithcairn is a **cross-platform split-pane terminal** for Linux, Windows,
and macOS, built with [Avalonia](https://avaloniaui.net/) on the
[RoyalTerminal](https://www.nuget.org/packages/RoyalApps.RoyalTerminal.Avalonia)
NuGet packages, which wrap Ghostty's native `libghostty-vt` VT engine.

## Layouts, not tabs

Instead of tabs, a window holds named **layouts** — each a set of split
panes — and shows one at a time. Switch between them from the **Layouts**
menu, with `Alt+1..9`, or with `Ctrl+PgUp` / `Ctrl+PgDn`. A layout you
switch away from keeps running in the background, so a long build or dev
server in one layout keeps going while you work in another.

## Per-pane startup

Each pane can start in its own folder and run its own program with its own
arguments — a shell, a dev server, a REPL, anything. This is configured per
pane (**Pane > Configure Pane**, `Ctrl+Shift+P`, or right-click) and stored
alongside the pane in the layout file, so it survives restarts and
`Ctrl+Shift+R` restarts.

## Everything restores

Windows, layouts, splits, split ratios, and each pane's working directory
and startup configuration are saved automatically — 5 seconds after a
change, on window close, and on exit — and restored the next time you
launch. Optionally, each pane's scrollback can be saved and replayed too.

## Built on Ghostty

Wraithcairn's terminal rendering and VT handling come from Ghostty's
`libghostty-vt` engine via RoyalTerminal. Wraithcairn also understands
Ghostty's own config file and theme catalog, so a lot of your existing
Ghostty setup carries over without any extra configuration.

## Next steps

- [Installation](/docs/getting-started/installation) — download a release or build from source
- [Quick Start](/docs/getting-started/quick-start) — first launch, your first split, your first saved layout
- [Layouts](/docs/guides/layouts) — switching, creating, saving, and reopening layouts
- [Keyboard Shortcuts](/docs/reference/keyboard-shortcuts) — the full shortcut table
