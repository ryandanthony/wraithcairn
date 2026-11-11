---
sidebar_position: 1
title: Keyboard Shortcuts
---

# Keyboard Shortcuts

Every shortcut below is also available from the menu bar (**Shell**,
**Pane**, **Edit**, **View**, **Layouts**).

| Shortcut | Action |
|---|---|
| `Ctrl+Shift+T` | New layout |
| `Ctrl+Shift+N` | New window |
| `Ctrl+Shift+W` | Close pane (closes the layout if it's the last pane) |
| `Ctrl+Shift+O` | Split right |
| `Ctrl+Shift+E` | Split down |
| `Alt+Arrows` | Move focus between panes |
| `Ctrl+Alt+Arrows` | Resize split |
| `Ctrl+Shift+Z` | Zoom pane |
| `Ctrl+Shift+=` | Equalize splits |
| `Ctrl+Shift+P` | Configure pane (folder, program, args) |
| `Ctrl+Shift+R` | Restart pane |
| `Ctrl+Shift+C` | Copy selection (also `Ctrl+Insert`) |
| `Ctrl+Shift+V` | Paste (also `Shift+Insert`) |
| `Ctrl+Shift+F` | Find (`Enter` next, `Shift+Enter` previous, `Esc` close) |
| `Ctrl+Shift+K` | Clear scrollback |
| `Ctrl+PgUp` | Previous layout |
| `Ctrl+PgDn` | Next layout |
| `Ctrl+Shift+PgUp` | Move active layout up in the list |
| `Ctrl+Shift+PgDn` | Move active layout down in the list |
| `Alt+1..9` | Switch to layout *N* |
| `Ctrl+=` | Bigger text |
| `Ctrl+-` | Smaller text |

Plain `Ctrl+C`, `Ctrl+V`, `Ctrl+X` and `Ctrl+A` are never taken by
wraithcairn: they always go to the running program (interrupt, literal-next,
and so on), even when text is selected.

## Mouse

| Action | Effect |
|--------|--------|
| Drag a pane's header bar onto another pane | Swap the two panes (`Esc` cancels) |
| Click a header bar | Focus that pane |
| Double-click a header bar | Zoom / unzoom that pane |
| Right-click a pane or its header | Pane menu |

## See Also

- [Layouts](/docs/guides/layouts)
- [Splits and Panes](/docs/guides/splits-and-panes)
- [Per-Pane Startup](/docs/guides/per-pane-startup)
