<#
.SYNOPSIS
  wraithcairn installer for Windows (x64 and arm64).

.DESCRIPTION
    irm https://ryandanthony.github.io/wraithcairn/install.ps1 | iex

  Downloads the self-contained release for this CPU from GitHub Releases,
  verifies it against the release's SHA256SUMS, installs it to
  %LOCALAPPDATA%\Programs\wraithcairn, adds that folder to your user PATH
  and creates a Start menu shortcut. No admin rights needed.

  Options: set environment variables before piping into iex,
    $env:WRAITHCAIRN_VERSION = 'v0.1.17'      # a specific release
    $env:WRAITHCAIRN_INSTALL_DIR = 'D:\bin\wraithcairn'
    $env:WRAITHCAIRN_NO_SHORTCUT = '1'        # skip the Start menu entry
    $env:WRAITHCAIRN_NO_PATH = '1'            # leave PATH alone
  or download the script and pass parameters:
    & .\install.ps1 -Version v0.1.17 -Uninstall

  Source: https://github.com/ryandanthony/wraithcairn/blob/main/website/static/install.ps1
#>
param(
    [string]$Version = $env:WRAITHCAIRN_VERSION,
    [string]$InstallDir = $env:WRAITHCAIRN_INSTALL_DIR,
    [switch]$NoShortcut = ($env:WRAITHCAIRN_NO_SHORTCUT -eq '1'),
    [switch]$NoPath = ($env:WRAITHCAIRN_NO_PATH -eq '1'),
    [switch]$Uninstall = ($env:WRAITHCAIRN_UNINSTALL -eq '1')
)

# Everything is in a function invoked on the last line, so a truncated
# download never runs half a script. Under `irm | iex` there is no script
# scope to exit from, so failures `throw` instead of calling `exit`.
function Install-Wraithcairn {
    [CmdletBinding()]
    param([string]$Version, [string]$InstallDir, [bool]$NoShortcut, [bool]$NoPath, [bool]$Uninstall)

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is 10x slower with the progress bar
    $repo = 'ryandanthony/wraithcairn'
    $docs = 'https://ryandanthony.github.io/wraithcairn'

    function Info([string]$m) { Write-Host '==> ' -ForegroundColor Blue -NoNewline; Write-Host $m }
    function Ok([string]$m) { Write-Host '==> ' -ForegroundColor Green -NoNewline; Write-Host $m }
    function Warn([string]$m) { Write-Host "warning: $m" -ForegroundColor Yellow }

    if ([Environment]::OSVersion.Platform -ne 'Win32NT') {
        throw "This installer is for Windows. On Linux/macOS run: curl -fsSL $docs/install | bash"
    }

    # Windows PowerShell 5.1 defaults to TLS 1.0, which GitHub refuses.
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { Write-Verbose 'TLS 1.2 already on (PowerShell 7+)' }

    # The OS architecture, not this PowerShell's: an x64 PowerShell under
    # emulation on arm64 still reports AMD64 in PROCESSOR_ARCHITECTURE.
    $arch = $env:PROCESSOR_ARCHITEW6432
    if (-not $arch) { $arch = $env:PROCESSOR_ARCHITECTURE }
    try {
        $os = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        if ($os) { $arch = $os }
    } catch { Write-Verbose 'RuntimeInformation unavailable; using PROCESSOR_ARCHITECTURE' }
    switch -Regex ($arch) {
        '^(ARM64|Arm64)$' { $rid = 'win-arm64' }
        '^(AMD64|X64|x64)$' { $rid = 'win-x64' }
        default { throw "Unsupported Windows CPU: $arch (x64 and arm64 are available)" }
    }

    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\wraithcairn' }
    $exe = Join-Path $InstallDir 'wraithcairn.exe'
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'wraithcairn.lnk'

    function Get-UserPath { [Environment]::GetEnvironmentVariable('Path', 'User') }
    function Test-OnPath([string]$p, [string]$dir) {
        @($p -split ';' | Where-Object { $_ }) | ForEach-Object { $_.TrimEnd('\') } | Where-Object { $_ -ieq $dir.TrimEnd('\') }
    }

    if ($Uninstall) {
        $removed = $false
        foreach ($f in @($exe, "$exe.old", $startMenu)) {
            if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force; Ok "Removed $f"; $removed = $true }
        }
        if ((Test-Path -LiteralPath $InstallDir) -and -not (Get-ChildItem -LiteralPath $InstallDir -Force)) {
            Remove-Item -LiteralPath $InstallDir -Force
        }
        $userPath = Get-UserPath
        if ($userPath -and (Test-OnPath $userPath $InstallDir)) {
            $kept = @($userPath -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ine $InstallDir.TrimEnd('\')) }) -join ';'
            [Environment]::SetEnvironmentVariable('Path', $kept, 'User')
            Ok "Removed $InstallDir from your user PATH"
        }
        if (-not $removed) { Info "Nothing to remove; wraithcairn is not installed in $InstallDir" }
        Write-Host 'Your settings in %APPDATA%\wraithcairn were left alone.'
        return
    }

    # SHA256SUMS lists every asset with its version in the name, so
    # releases/latest/download needs no GitHub API call (no rate limit).
    if ($Version) {
        if (-not $Version.StartsWith('v')) { $Version = "v$Version" }
        $sumsUrl = "https://github.com/$repo/releases/download/$Version/SHA256SUMS"
    } else {
        $sumsUrl = "https://github.com/$repo/releases/latest/download/SHA256SUMS"
    }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("wraithcairn-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        try {
            $sums = (Invoke-WebRequest -Uri $sumsUrl -UseBasicParsing).Content
            if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
        } catch {
            throw "Could not download $sumsUrl$(if ($Version) { " (does release $Version exist?)" }): $($_.Exception.Message)"
        }

        $line = @($sums -split "`n" | Where-Object { $_ -match "^\s*[0-9a-fA-F]{64}\s+\*?wraithcairn-[0-9]\S*-$rid\.zip\s*$" })[0]
        if (-not $line) { throw "Release $(if ($Version) { $Version } else { 'latest' }) has no $rid build" }
        $expected, $asset = ($line.Trim() -split '\s+', 2)
        $expected = $expected.ToLowerInvariant()
        $asset = $asset.TrimStart('*')
        $ver = $asset -replace '^wraithcairn-', '' -replace "-$rid\.zip$", ''

        $before = $null
        if (Test-Path -LiteralPath $exe) {
            try { $before = ((& $exe --version 2>$null | Select-Object -First 1) -split ' ')[1] } catch { Write-Verbose 'existing install did not report a version' }
        }
        if (-not $before) { Info "Installing wraithcairn $ver ($rid)" }
        elseif ($before -eq $ver) { Info "wraithcairn $ver is already installed; reinstalling" }
        else { Info "Updating wraithcairn $before -> $ver ($rid)" }

        $zip = Join-Path $tmp $asset
        $url = "https://github.com/$repo/releases/download/v$ver/$asset"
        Info "Downloading $asset"
        try { Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing }
        catch { throw "Download failed: $url ($($_.Exception.Message))" }

        $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "SHA-256 mismatch for $asset (expected $expected, got $actual)" }
        Info 'Verified SHA-256'

        $unpacked = Join-Path $tmp 'unpacked'
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked -Force
        $newExe = Join-Path $unpacked 'wraithcairn\wraithcairn.exe'
        if (-not (Test-Path -LiteralPath $newExe)) { throw 'Archive did not contain wraithcairn\wraithcairn.exe' }

        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        # A running exe can't be overwritten but can be renamed out of the way.
        Remove-Item -LiteralPath "$exe.old" -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $exe) { Move-Item -LiteralPath $exe -Destination "$exe.old" -Force }
        Copy-Item -LiteralPath $newExe -Destination $exe -Force
        Remove-Item -LiteralPath "$exe.old" -Force -ErrorAction SilentlyContinue

        # wraithcairn is a GUI-subsystem exe; capturing stdout makes
        # PowerShell wait for it and collect the output.
        $got = & $exe --version 2>$null | Select-Object -First 1
        if ($got -notmatch '^wraithcairn ') { throw "Installed binary did not run: $exe --version" }

        if (-not $NoShortcut) {
            try {
                $shell = New-Object -ComObject WScript.Shell
                $lnk = $shell.CreateShortcut($startMenu)
                $lnk.TargetPath = $exe
                $lnk.WorkingDirectory = $env:USERPROFILE
                $lnk.IconLocation = "$exe,0"
                $lnk.Description = 'wraithcairn terminal'
                $lnk.Save()
                Info 'Added a Start menu shortcut'
            } catch {
                Warn "Could not create the Start menu shortcut: $($_.Exception.Message)"
            }
        }

        $pathNote = $null
        if (-not $NoPath) {
            $userPath = Get-UserPath
            if (-not ($userPath -and (Test-OnPath $userPath $InstallDir))) {
                $newPath = if ($userPath) { "$($userPath.TrimEnd(';'));$InstallDir" } else { $InstallDir }
                [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
                $pathNote = "Added $InstallDir to your user PATH. Open a new terminal to use 'wraithcairn'."
            }
            if (-not (Test-OnPath $env:Path $InstallDir)) { $env:Path = "$env:Path;$InstallDir" }
        }

        Write-Host ''
        Ok "Installed $got"
        Write-Host "    command: $exe"
        if ($pathNote) { Write-Host "    $pathNote" }
        Write-Host ''
        Write-Host 'Start wraithcairn from the Start menu, or run: wraithcairn'
        Write-Host "Docs: $docs"
    } finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Install-Wraithcairn -Version $Version -InstallDir $InstallDir -NoShortcut $NoShortcut.IsPresent -NoPath $NoPath.IsPresent -Uninstall $Uninstall.IsPresent
