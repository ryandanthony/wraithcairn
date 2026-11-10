---
sidebar_position: 1
title: Layouts
---

# Layouts

A **layout** is a named tree of split panes. A window can hold several
layouts, but shows only one at a time — there is no tab strip; everything
is driven from the **Layouts** menu and its shortcuts.

## Switching Layouts

- Open layouts are listed at the top of the **Layouts** menu; click one, or
  press `Alt+1..9` to jump straight to layout *N*
- **Next Layout** / **Previous Layout** (`Ctrl+PgDn` / `Ctrl+PgUp`) cycle
  through open layouts
- `Ctrl+Shift+PgUp` / `Ctrl+Shift+PgDn` move the active layout up or down in
  the list (its position, not its content)

A layout you switch away from **keeps running in the background**: any
process inside its panes keeps executing. Switching back shows it exactly
as you left it.

## Creating and Managing Layouts

- **New Layout…** (`Ctrl+Shift+T`) adds a new, empty layout with one pane
- **Rename Layout…** renames the active layout
- **Close Layout** closes the active layout (and every pane in it)

## Saved Layouts

- **Save Layout As…** writes the current layout's pane tree, splits, and
  per-pane startup configuration to `layouts/NAME.json`
- **Open Saved Layout** loads a saved layout back in as a new, running
  layout in the current window
- **Delete Saved Layout** removes a `layouts/NAME.json` file after asking
  for confirmation (there's no undo)

Saved layouts are independent snapshots: editing (and resaving) one does
not affect any layout already open from it, and vice versa.

## Named Workspaces

By default Wraithcairn reads and writes `workspace.json`, the one
automatically-restored workspace for this machine. Launching with
`--workspace NAME` (or `-w NAME`) starts a separate instance bound to its
own `layouts/NAME.json`-style workspace file instead, so you can keep
entirely separate sets of layouts — for example, one workspace per
project — running side by side, each saved and restored independently.

## See Also

- [Per-Pane Startup](/docs/guides/per-pane-startup) — what each pane in a
  layout runs
- [Files and Paths](/docs/reference/files-and-paths) — where
  `workspace.json` and `layouts/` live
- [Layout File Format](/docs/reference/layout-file-format) — the JSON
  structure of a saved layout
