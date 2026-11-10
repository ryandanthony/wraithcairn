---
sidebar_position: 4
title: Layout File Format
---

# Layout File Format

A workspace file (`workspace.json`, or `layouts/NAME.json`) holds one or
more windows, each holding one or more layouts, each a tree of panes.
Below is an annotated example of a single window with one layout that has
been split into two panes, one of them configured with startup.

```json
{
  "windows": [
    {
      "layouts": [
        {
          "name": "main",
          "rootPane": {
            "id": "pane-1",
            "split": {
              "orientation": "vertical",
              "ratio": 0.6,
              "first": {
                "id": "pane-2",
                "workingDirectory": "/home/ryan/code/app",
                "startup": {
                  "directory": "~/code/app",
                  "command": "npm",
                  "args": ["run", "dev"],
                  "runInShell": true
                }
              },
              "second": {
                "id": "pane-3",
                "workingDirectory": "/home/ryan/code/app",
                "startup": {
                  "directory": "~/code/app",
                  "command": "tail",
                  "args": ["-f", "logs/app.log"],
                  "runInShell": false
                }
              }
            }
          }
        }
      ]
    }
  ]
}
```

## Field Notes

- **`windows`** – one entry per open window. Each window owns its own list
  of layouts.
- **`layouts`** – named layouts within a window. The active one is shown;
  the rest keep running in the background (see
  [Layouts](/docs/guides/layouts)).
- **`rootPane`** – either a leaf pane (has `id`, `workingDirectory`,
  optionally `startup`) or a `split` node with `orientation`
  (`vertical`/`horizontal`), a `ratio`, and `first`/`second` child panes,
  recursively.
- **`workingDirectory`** – the pane's last-seen current directory,
  tracked via [shell integration](/docs/guides/shell-integration). This
  is informational and used as a restore fallback.
- **`startup`** – the pane's configured startup (see
  [Per-Pane Startup](/docs/guides/per-pane-startup)):
  - `directory` – where the pane starts; wins over `workingDirectory` on
    restore
  - `command` / `args` – the program and arguments
  - `runInShell` – `true` types the command into the shell (default);
    `false` execs it directly as the pane's process

A pane with no `startup` (or an empty one) simply opens a plain shell at
`workingDirectory`.

:::note
This is a representative, hand-annotated example, not a machine-generated
schema dump. Field names and nesting follow the real save/restore model in
`NamedLayouts.cs` and `PaneStartup.cs`, but exact internal property names
may differ slightly between versions.
:::

## See Also

- [Files and Paths](/docs/reference/files-and-paths) – where these files
  live
- [Layouts](/docs/guides/layouts)
- [Per-Pane Startup](/docs/guides/per-pane-startup)
