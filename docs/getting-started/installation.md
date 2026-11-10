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
| macOS Apple Silicon | `wraithcairn-{version}-osx-arm64.tar.gz` |
| macOS Intel | `wraithcairn-{version}-osx-x64.tar.gz` |

Each release also includes a `SHA256SUMS` file for verifying the
downloads. Releases are published automatically on every push to `main`:
GitVersion bumps the patch version by default, or the minor/major version
if a commit message contains `+semver: minor` / `+semver: major`.
Builds are self-contained (no .NET runtime install required) and
single-file. Each archive extracts to a `wraithcairn/` folder holding
one executable, `wraithcairn` (`wraithcairn.exe` on Windows). Copy it
anywhere, for example `~/.local/bin`.

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
