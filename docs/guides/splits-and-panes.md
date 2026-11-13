---
sidebar_position: 3
title: Splits and Panes
---

# Splits and Panes

A layout is a tree of **panes**, split horizontally or vertically. There
is exactly one active layout per window, shown full-size; splits divide
that area.

## Splitting

- `Ctrl+Shift+O` splits the focused pane **right**
- `Ctrl+Shift+E` splits the focused pane **down**
- `Ctrl+Alt+Shift+O` splits the focused pane **left**
- `Ctrl+Alt+Shift+E` splits the focused pane **up**

The new pane starts as a plain shell unless you configure its startup (see
[Per-Pane Startup](/docs/guides/per-pane-startup)).

## Focus and Navigation

- `Alt+Arrows` moves focus to the neighboring pane in that direction
- Clicking a pane also focuses it

## Resizing

- `Ctrl+Alt+Arrows` resizes the split adjacent to the focused pane
- `Ctrl+Shift+=` equalizes every split ratio in the current layout back to
  even

## Pane Header Bar

Every pane has a narrow bar along its top edge showing:

- **Title**: the title the program set (for example vim or htop), or
  the folder name when nothing set one
- **Folder**: the pane's current directory, shortened with `~`
- **Running command**: `▶ npm run dev` while a command runs (needs
  [shell integration](/docs/guides/shell-integration))
- **Last exit code**: `✗ 2` when the last command failed, or
  `■ exited 0` when a directly-run program has exited
- **Startup program**: `⚙ npm run dev` when the pane has a configured
  [startup](/docs/guides/per-pane-startup)

The focused pane's bar is highlighted.

### Moving Panes

Drag a pane's header bar onto another pane to **swap** them. The
drop target is outlined while you drag, and `Esc` cancels. Each pane keeps
its process, scrollback and startup settings, and split sizes stay with
their positions, so a swap only moves where the panes are.

Clicking the bar focuses the pane, double-clicking it toggles zoom, and
right-clicking opens the pane menu.

## Zooming

`Ctrl+Shift+Z` zooms the focused pane to temporarily fill the whole
layout, hiding its siblings without closing them. Press it again to
restore the split view. Only one pane per layout can be zoomed at a time.

## Closing

`Ctrl+Shift+W` closes the focused pane. If it's the last pane in the
layout, the layout itself closes.

## Right-Click Menu

Right-clicking a pane opens a context menu with the same core pane
actions available from the keyboard or **Pane** menu:

- **Copy** (`Ctrl+Shift+C`, enabled when text is selected)
- **Paste** (`Ctrl+Shift+V`)
- **Configure Pane…**
- **Restart Pane**
- **Split Right**
- **Split Down**
- **Split Left**
- **Split Up**
- **Close Pane**

## See Also

- [Keyboard Shortcuts](/docs/reference/keyboard-shortcuts) — every
  shortcut in one table
- [Layouts](/docs/guides/layouts) — how panes group into layouts and how
  layouts are switched

:::tip
The **Help** menu has an **About wraithcairn...** item showing the app
icon and the current version. The same version is printed by
[`--version`](/docs/reference/command-line).
:::
