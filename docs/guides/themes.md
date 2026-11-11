---
sidebar_position: 6
title: Themes
---

# Themes

Wraithcairn ships the same color themes that come built into
[Ghostty](https://ghostty.org): 800+ of them, from Dracula and Nord to
GitHub, Catppuccin, Gruvbox, and Tokyo Night. The theme files live on this
site and the app downloads them on demand, so the app itself stays small.

Browse every theme with a preview on the [Themes gallery](/theme-gallery) page.

## Picking a Theme

Open **View → Themes…**:

- **Search** by name, or filter to **Dark**, **Light**, or **Downloaded**
- **Select** a theme to preview it on every open pane right away (turn off
  *Preview on open panes* to preview only inside the dialog)
- **Apply** (or double-click) to switch. If the theme isn't downloaded yet
  it is fetched first, so the button reads **Download & Apply**
- **Close** without applying puts your previous theme back
- **Use Ghostty Config Theme** removes Wraithcairn's own setting and goes
  back to the `theme` in `~/.config/ghostty/config`

Switching never needs a restart: every pane, pane header, and window in
every open Wraithcairn window repaints immediately, and panes you open
later use the new theme too.

## Downloading All Themes

**Download All** in the Themes dialog fetches every theme in one small zip
(about 250 KB) so they all work offline. Downloaded themes show a ✓.

From a shell:

```bash
wraithcairn --download-themes        # all themes
wraithcairn --list-themes            # every theme; * = downloaded
wraithcairn --set-theme "Dracula"    # download if needed, then select
wraithcairn --set-theme ghostty      # back to the Ghostty config's theme
```

These commands don't open a window. A running Wraithcairn notices the
change and repaints right away.

## Where It's Stored

The chosen theme is saved as `theme` in
`~/.config/wraithcairn/config.json`:

```json
{
  "theme": "Dracula"
}
```

Only that one line is changed. Your comments and the rest of the file are
left as they were. `"theme": null` (or no `theme` key) means "use the
Ghostty config's theme".

Downloaded theme files go in `~/.config/wraithcairn/themes/`, one
Ghostty-format file per theme, named exactly as the theme. A theme is
looked up by name in these folders, first match wins:

1. `~/.config/ghostty/themes/`
2. `~/.config/wraithcairn/themes/`
3. Ghostty's system theme folders (`/usr/share/ghostty/themes`, the macOS
   app bundle, and so on)

So Ghostty themes you already have work without downloading, and you can
drop your own Ghostty-format theme file in either user folder.

## Live Reload

Wraithcairn watches `config.json`, `~/.config/ghostty/config`, and the
user theme folder. Edit `theme` by hand (or with another tool, or sync it
from another machine) and save: the new theme is applied in about a
third of a second.

## Theme File Format

Themes use Ghostty's format, one `key = value` per line:

```
palette = 0=#21222c
palette = 1=#ff5555
...
palette = 15=#ffffff
background = #282a36
foreground = #f8f8f2
cursor-color = #f8f8f2
cursor-text = #282a36
selection-background = #44475a
selection-foreground = #ffffff
```

## For Maintainers

The published catalog is generated from Ghostty's built-in themes by
`scripts/sync-ghostty-themes.py`, which writes `website/static/themes/`:

| File | Contents |
|---|---|
| `index.json` | Every theme's name, dark/light flag, and colors (used by the app's picker and the gallery) |
| `ghostty/<slug>` | One Ghostty-format file per theme |
| `wraithcairn-themes.zip` | All themes, for **Download All** |
| `NOTICE.txt` | Attribution |

Run it after a Ghostty upgrade (it finds Ghostty's themes in the system
folders or extracts them from the AppImage), commit, and the docs deploy
publishes the update. `WRAITHCAIRN_THEMES_URL` points the app at another
catalog, an http(s) URL or a local folder, for testing.

The themes come from the
[iTerm2-Color-Schemes](https://github.com/mbadolato/iTerm2-Color-Schemes)
project (MIT), as bundled by Ghostty. Each theme's colors belong to its
original author.
