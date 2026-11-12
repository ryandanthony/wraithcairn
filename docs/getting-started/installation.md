---
sidebar_position: 1
title: Installation
---

# Installation

Wraithcairn is distributed as self-contained builds for six platforms:
Linux, Windows and macOS, each on x64 and arm64. No .NET install is
needed. Use the install script, download a release by hand, or build from
source.

## Install Script (recommended)

**Linux and macOS:**

```bash
curl -fsSL https://ryandanthony.github.io/wraithcairn/install | bash
```

**Windows (PowerShell):**

```powershell
irm https://ryandanthony.github.io/wraithcairn/install.ps1 | iex
```

To update later, run `wraithcairn update` or use **View → Check for
Updates…** (see [Updating](#updating)); running the same command again
works too. Both scripts pick the build for your
OS and CPU, download it from
[GitHub Releases](https://github.com/ryandanthony/wraithcairn/releases),
check it against the release's `SHA256SUMS`, and install it for your user
only (no `sudo` or admin rights):

| Platform | What the script installs |
|----------|--------------------------|
| Linux x64 / arm64 | `~/.local/bin/wraithcairn`, plus an application launcher entry and icons in `~/.local/share` |
| macOS Apple Silicon / Intel | `wraithcairn.app` in `/Applications` (or `~/Applications` if that isn't writable), plus a `wraithcairn` command in `~/.local/bin` |
| Windows x64 / arm64 | `%LOCALAPPDATA%\Programs\wraithcairn\wraithcairn.exe`, a Start menu shortcut, and that folder added to your user `PATH` |

On macOS the script also clears the download quarantine flag, so the first
launch doesn't need right-click → **Open**. If `~/.local/bin` isn't on your
`PATH`, the script prints the line to add to your shell profile.

### Options

Pass options to the shell script after `bash -s --`:

```bash
curl -fsSL https://ryandanthony.github.io/wraithcairn/install | bash -s -- --version v0.1.18
```

| Option | Environment variable | Effect |
|--------|----------------------|--------|
| `--version vX.Y.Z` | `WRAITHCAIRN_VERSION` | Install a specific release instead of the latest |
| `--dir DIR` | `WRAITHCAIRN_INSTALL_DIR` | Put the `wraithcairn` command in `DIR` (default `~/.local/bin`) |
| `--app-dir DIR` | `WRAITHCAIRN_APP_DIR` | macOS: where `wraithcairn.app` goes |
| `--no-desktop` | `WRAITHCAIRN_NO_DESKTOP=1` | Linux: skip the launcher entry and icons |
| `--uninstall` | | Remove what the script installed |

On Windows, set environment variables before running the one-liner:

```powershell
$env:WRAITHCAIRN_VERSION = 'v0.1.18'          # a specific release
$env:WRAITHCAIRN_INSTALL_DIR = 'D:\Tools\wraithcairn'
$env:WRAITHCAIRN_NO_SHORTCUT = '1'            # no Start menu shortcut
$env:WRAITHCAIRN_NO_PATH = '1'                # leave PATH alone
irm https://ryandanthony.github.io/wraithcairn/install.ps1 | iex
```

### Uninstall

```bash
curl -fsSL https://ryandanthony.github.io/wraithcairn/install | bash -s -- --uninstall
```

```powershell
& ([scriptblock]::Create((irm https://ryandanthony.github.io/wraithcairn/install.ps1))) -Uninstall
```

The same form takes the other options as parameters, for example
`-Version v0.1.18`, `-InstallDir D:\Tools\wraithcairn`, `-NoShortcut` or
`-NoPath`.

Uninstalling leaves your settings and saved layouts alone
(`~/.config/wraithcairn`, or `%APPDATA%\wraithcairn` on Windows).

### Read before you run

Piping a script into a shell runs it as you. Both scripts are short and
readable. Look first if you like:
[`install`](https://ryandanthony.github.io/wraithcairn/install) and
[`install.ps1`](https://ryandanthony.github.io/wraithcairn/install.ps1)
(source in
[`website/static/`](https://github.com/ryandanthony/wraithcairn/tree/main/website/static)).

## Download a Release

To install by hand, grab the latest release for your platform from
[GitHub Releases](https://github.com/ryandanthony/wraithcairn/releases):

| Platform | Artifact |
|----------|----------|
| Linux x64 | `wraithcairn-{version}-linux-x64.tar.gz` |
| Linux arm64 | `wraithcairn-{version}-linux-arm64.tar.gz` |
| Windows x64 | `wraithcairn-{version}-win-x64.zip` |
| Windows arm64 | `wraithcairn-{version}-win-arm64.zip` |
| macOS Apple Silicon | `wraithcairn-{version}-osx-arm64.zip` (contains `wraithcairn.app`) |
| macOS Intel | `wraithcairn-{version}-osx-x64.zip` (contains `wraithcairn.app`) |

Each release also includes a `SHA256SUMS` file for verifying the
downloads. Releases are published automatically on every push to `main`:
GitVersion bumps the patch version by default, or the minor/major version
if a commit message contains `+semver: minor` / `+semver: major`.
Builds are self-contained (no .NET runtime install required) and
single-file. The Linux and Windows archives extract to a `wraithcairn/`
folder holding one executable, `wraithcairn` (`wraithcairn.exe` on
Windows). Copy it anywhere, for example `~/.local/bin`.

### macOS

Unzip and drag `wraithcairn.app` into `/Applications`. It then has its own
Dock icon, name and Cmd+Tab entry. The app is ad-hoc signed, not notarized,
so the first launch needs right-click → **Open** (or
`xattr -dr com.apple.quarantine /Applications/wraithcairn.app`).

### Linux taskbar and launcher

On first launch wraithcairn writes `~/.local/share/applications/wraithcairn.desktop`
and its icons to `~/.local/share/icons/hicolor/`, pointing at the binary you
ran. That gives it its own task manager entry with the right icon and name
(otherwise KDE files it under whatever terminal you started it from) and
puts it in the application launcher. If you move the binary, launch it once
from the new place and the entry follows. To do it by hand:

```bash
wraithcairn --install-desktop     # write the entry and icons
wraithcairn --uninstall-desktop   # remove them
```

A `wraithcairn.desktop` you wrote yourself is never overwritten.

### Native libraries
On first run the bundled native libraries (libghostty-vt, Skia,
HarfBuzz) are extracted to the .NET bundle cache (`~/.net/wraithcairn/`
on Linux and macOS, `%TEMP%\.net\wraithcairn\` on Windows). Set
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` to put them somewhere else.

## Build From Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Clone and Run

```bash
git clone https://github.com/ryandanthony/wraithcairn.git
cd wraithcairn
dotnet run
```

### Local Self-Contained Build (Linux)

The repo includes a helper script that produces a self-contained Linux
single-file executable under `~/temp/wraithcairn-single`:

```bash
./scripts/publish-local.sh            # single file
./scripts/publish-local.sh --folder   # folder build in ~/temp/wraithcairn
```

## Next Steps

Continue with the [Quick Start](/docs/getting-started/quick-start) to take
your first look at layouts and panes.

## Updating

```bash
wraithcairn update           # install the latest release if it's newer
wraithcairn update --check   # just report; exit code 10 = update available
wraithcairn update --force   # reinstall the latest release
```

Or open **View → Check for Updates…**: it shows your version and the
latest one, links the release notes, and **Update Now** installs it.
**Restart Now** then saves your layouts and reopens wraithcairn on the new
version.

Updates come from the same place as the install script: the latest
[GitHub Release](https://github.com/ryandanthony/wraithcairn/releases), the
build for your OS and CPU, checked against the release's `SHA256SUMS`
before anything is replaced.

| Platform | What gets replaced |
|---|---|
| Linux | the `wraithcairn` binary, atomically (running windows keep the old one until restarted) |
| macOS | the whole `wraithcairn.app` bundle |
| Windows | `wraithcairn.exe` (the running one is renamed to `.old` and removed on the next start) |

On Windows, `wraithcairn.exe` is a windowed app, so cmd and PowerShell
return to the prompt before its output finishes. To wait for it, pipe it:
`wraithcairn update | Out-Host` (PowerShell) or
`start /wait wraithcairn update` (cmd).

Updating in place needs write access to where wraithcairn is installed.
If it can't (for example a system-wide install, or a build you made from
source), it says so and shows the install command to use instead.
