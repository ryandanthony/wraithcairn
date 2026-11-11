---
sidebar_position: 2
title: Quick Start
---

# Quick Start

This walks through a first session: launching Wraithcairn, splitting a
pane, pointing a pane at a project folder, and saving the result as a
named layout.

## First Launch

Run the binary (see [Installation](/docs/getting-started/installation)).
On first launch there is no saved workspace yet, so Wraithcairn opens a
single window with one layout containing a single pane running your
default shell.

If `~/.config/ghostty/config` exists, its font, theme, and color settings
are picked up automatically (see
[Configuration](/docs/guides/configuration)).

## Split the Window

- `Ctrl+Shift+O` splits the focused pane to the right
- `Ctrl+Shift+E` splits the focused pane down
- `Ctrl+Alt+Shift+O` / `Ctrl+Alt+Shift+E` split left / up instead
- `Alt+Arrows` moves focus between panes
- `Ctrl+Alt+Arrows` resizes the focused split
- `Ctrl+Shift+=` equalizes all splits in the layout

Try splitting right, then down, to build an L-shaped layout with three
panes.

## Configure a Pane

Focus a pane and press `Ctrl+Shift+P` (or right-click it and choose
**Configure Pane...**) to open the **Configure Pane** dialog. Set:

- **Directory** — where the pane starts (supports `~`)
- **Command** and **Args** — a program to launch
- **Run in shell** — on to type the command into the shell, off to exec it
  directly as the pane's process

See [Per-Pane Startup](/docs/guides/per-pane-startup) for the full
behavior and the JSON it produces. `Ctrl+Shift+R` restarts a pane from its
saved startup configuration at any time.

## Save a Layout

Once you like the arrangement, open the **Layouts** menu and choose
**Save Layout As…** to write the current layout to
`layouts/NAME.json`. Use **Open Saved Layout** later (in this window or a
fresh launch) to load it back as a new, running layout.

You don't have to save manually for day-to-day use: the whole workspace
(open windows, layouts, splits, ratios, and per-pane state) is saved
automatically and restored the next time you launch. Saved layouts under
**Save Layout As…** are for layouts you want to reopen deliberately, by
name, including from a new window or instance (`--workspace NAME`).

## Next Steps

- [Layouts](/docs/guides/layouts) — the full layout model
- [Splits and Panes](/docs/guides/splits-and-panes) — zoom, focus, and the
  pane context menu
- [Keyboard Shortcuts](/docs/reference/keyboard-shortcuts) — everything in
  one table
