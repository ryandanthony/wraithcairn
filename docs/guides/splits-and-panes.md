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

The new pane starts as a plain shell unless you configure its startup (see
[Per-Pane Startup](/docs/guides/per-pane-startup)).

## Focus and Navigation

- `Alt+Arrows` moves focus to the neighboring pane in that direction
- Clicking a pane also focuses it

## Resizing

- `Ctrl+Alt+Arrows` resizes the split adjacent to the focused pane
- `Ctrl+Shift+=` equalizes every split ratio in the current layout back to
  even

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

- **Configure Pane…**
- **Restart Pane**
- **Split Right**
- **Split Down**
- **Close Pane**

## See Also

- [Keyboard Shortcuts](/docs/reference/keyboard-shortcuts) — every
  shortcut in one table
- [Layouts](/docs/guides/layouts) — how panes group into layouts and how
  layouts are switched

:::tip
The **View** menu has an **About wraithcairn...** item showing the app
icon and the current version. The same version is printed by
[`--version`](/docs/reference/command-line).
:::
