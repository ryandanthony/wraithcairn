---
sidebar_position: 3
title: Command Line
---

# Command Line

| Flag | Meaning |
|---|---|
| `--workspace NAME` / `-w NAME` | Start this instance bound to `layouts/NAME.json` instead of the default `workspace.json`. Layouts opened and saved by this instance go to that named workspace file. See [Layouts](/docs/guides/layouts#named-workspaces). |
| `--selftest` | Headless self-test: builds a layout, checks the Ghostty engine, GPU, search, splits, layout switching, and save/open, then saves and exits. Running it a second time additionally checks restore. Prints `SELFTEST key=value` lines. **Overwrites `workspace.json`.** Used by CI (see `.github/workflows/ci.yml`), run twice headless. |
| `--dump-panes` | Restores the current layout, prints each pane's startup configuration, working directory, and last screen lines, then exits **without saving**. Useful for inspecting saved state without risking a write. |
| `--configure-pane` | Opens straight to the **Configure Pane** dialog for the focused/primary pane on launch. |
| `--version` / `-v` | Prints `wraithcairn <version>` and exits. |

## Examples

```bash
# Open (or create) a separate named workspace for a project
wraithcairn --workspace my-project

# Same, short form
wraithcairn -w my-project

# Inspect the current saved layout without touching it
wraithcairn --dump-panes

# Headless sanity check, e.g. in CI
wraithcairn --selftest

# Print the version and exit
wraithcairn --version
```

## See Also

- [Layouts](/docs/guides/layouts)
- [Files and Paths](/docs/reference/files-and-paths)
