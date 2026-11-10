---
sidebar_position: 5
title: Shell Integration
---

# Shell Integration

Wraithcairn injects shell-integration support — OSC 7 (current directory
reporting) and OSC 133 (prompt/command boundary markers) — for the shells
it recognizes, **without editing your dotfiles**. This is what lets a
pane's `workingDirectory` stay accurate for save/restore, and gives the
terminal a sense of where each prompt and command starts.

## Supported Shells

| Shell | Mechanism |
|---|---|
| `bash` | Launched as `bash --rcfile <generated file>`. The generated file sources your normal `~/.bashrc` first, then appends the integration snippet. |
| `zsh` | Launched with `ZDOTDIR` pointed at a generated directory. Its `.zshrc` restores your real `ZDOTDIR` (or `$HOME`) and sources your normal `.zshrc`, then appends the snippet. |
| `fish` | Launched as `fish --init-command 'source <snippet.fish>'`. |
| `pwsh` | Launched as `pwsh -NoExit -Command '. <snippet.ps1>'`; your normal PowerShell profile still loads. |
| anything else (e.g. `cmd`) | Launched plainly, with no integration. Working-directory restore falls back to whatever was last observed. |

In every integrated case, your existing shell startup files run exactly as
they normally would; the integration snippet is appended after them, not
instead of them.

## Which Shell Is Used

By default Wraithcairn uses `$SHELL` (or the platform default on
Windows). Set `shell` in `config.json` to force a specific one, e.g.
`"/usr/bin/fish"` (see [Configuration](/docs/guides/configuration)).

## Why It Matters

- Accurate `workingDirectory` tracking, so layout/workspace restore
  reopens each pane where you actually left it (as a fallback under
  `startup.directory`)
- Smoother prompt/command awareness, which the underlying `libghostty-vt`
  engine can use for features like jumping between command boundaries

If integration can't be set up for a given shell (or fails for any
reason), the pane still launches — it just runs that shell without the
extra reporting.
