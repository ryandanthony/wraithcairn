---
sidebar_position: 2
title: Per-Pane Startup
---

# Per-Pane Startup

Every pane can be given its own starting folder, program, and arguments.
This is what lets a layout double as a small dashboard: one pane in
`~/code/app` running `npm run dev`, another tailing logs, another sitting
at a plain shell.

## Configure Pane

Open the dialog with `Ctrl+Shift+P`, from **Pane > Configure Pane…**, or
by right-clicking a pane and choosing **Configure Pane…**. It sets:

| Field | Meaning |
|---|---|
| Directory | Where the pane starts. Supports a leading `~`. |
| Command | The program to run. |
| Args | Arguments to the program. |
| Run in shell | See below. |

The app can also be started directly at this dialog with the
`--configure-pane` command-line flag (see
[Command Line](/docs/reference/command-line)).

## Run in Shell vs. Direct Exec

**Run in shell = on (default):** the pane's shell starts in `Directory`,
then `Command` plus `Args` are typed into it as a line. `Command` is
inserted as-is, so pipes, `&&`, and `$VARS` all work; each argument is
quoted appropriately for the detected shell. When the program exits, the
pane is back at a normal shell prompt.

**Run in shell = off:** `Command` is executed directly as the pane's own
process, with `Args` as its argv — no shell involved. When it exits, the
pane stays open showing a notice instead of silently closing.

## Restarting a Pane

`Ctrl+Shift+R` restarts the focused pane from its saved startup
configuration: it tears down the current process and starts over at
`Directory` with `Command`/`Args`/`Run in shell` exactly as configured.
This is the normal way to bounce a dev server or long-running process
without rebuilding the whole layout.

## The Startup JSON

Startup configuration is stored inline on the pane inside the layout file,
as a `startup` object on each leaf pane:

```json
"rootPane": {
  "id": "pane-1",
  "workingDirectory": "/home/ryan/code/app",
  "startup": {
    "directory": "~/code/app",
    "command": "npm",
    "args": ["run", "dev"],
    "runInShell": true
  }
}
```

- `workingDirectory` is the last-seen current directory (informational,
  tracked automatically via shell integration) — `startup.directory`, when
  set, wins over it on restore.
- `startup.runInShell` defaults to `true` when omitted.
- An empty `startup` (no `directory`, no `command`) just means "start a
  plain shell here."

See [Layout File Format](/docs/reference/layout-file-format) for a full
annotated layout.
