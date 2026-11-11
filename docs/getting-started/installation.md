---
sidebar_position: 1
title: Installation
---

# Installation

Wraithcairn is distributed as self-contained release archives, one per
platform. You can download a pre-built release or build from source.

## Download a Release

Grab the latest release for your platform from
[GitHub Releases](https://github.com/ryandanthony/wraithcairn/releases):

| Platform | Artifact |
|----------|----------|
| Linux x64 | `wraithcairn-{version}-linux-x64.tar.gz` |
| Windows x64 | `wraithcairn-{version}-win-x64.zip` |
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
