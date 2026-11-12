<#
  Runs `wraithcairn update` while another process holds wraithcairn.exe open
  without FILE_SHARE_DELETE, like Defender or the search indexer briefly do.
  That makes the updater's rename fail with "being used by another process"
  (the flake seen on win-arm64). The lock is taken before the update starts
  and released HoldSeconds after the updater has finished unpacking the new
  exe (the very next thing it does is rename the old one), so the rename is
  guaranteed to run into it, however long the download and unzip take.

    -ExpectFail   the updater has no retry (v0.1.32 and older): it must fail
                  with the sharing-violation message
    (default)     the updater must ride out the lock and succeed
#>
param(
    [Parameter(Mandatory)] [string]$Exe,
    [double]$HoldSeconds = 2,
    [switch]$ExpectFail
)
$ErrorActionPreference = 'Stop'

$tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$id = [guid]::NewGuid().ToString('N')
$log = Join-Path $tmp "update-$id.log"
$err = Join-Path $tmp "update-$id.err"
$ready = Join-Path $tmp "lock-$id.ready"
$released = Join-Path $tmp "lock-$id.released"
# Updater.InstallAsync unpacks into %TEMP%\wraithcairn-update-*\unpacked.
$tempRoot = [IO.Path]::GetTempPath()
$before = @(Get-ChildItem $tempRoot -Directory -Filter 'wraithcairn-update-*' -ErrorAction SilentlyContinue | ForEach-Object FullName)

$lockScript = @"
`$fs = [IO.File]::Open('$Exe', 'Open', 'Read', 'Read')
New-Item '$ready' | Out-Null
`$before = @('$($before -join "','")')
`$deadline = (Get-Date).AddSeconds(180)
`$unpacked = `$false
while (-not `$unpacked -and (Get-Date) -lt `$deadline) {
  foreach (`$d in Get-ChildItem '$tempRoot' -Directory -Filter 'wraithcairn-update-*' -ErrorAction SilentlyContinue) {
    if (`$before -contains `$d.FullName) { continue }
    `$fresh = Join-Path `$d.FullName 'unpacked\wraithcairn\wraithcairn.exe'
    if (Test-Path `$fresh) {
      # Still being extracted while the zip code holds it open exclusively.
      try { `$probe = [IO.File]::Open(`$fresh, 'Open', 'Read', 'ReadWrite'); `$probe.Close(); `$unpacked = `$true } catch { }
    }
  }
  Start-Sleep -Milliseconds 10
}
Start-Sleep -Milliseconds $([int]($HoldSeconds * 1000))
`$fs.Close()
New-Item '$released' | Out-Null
"@
$locker = Start-Process pwsh -PassThru -NoNewWindow -ArgumentList '-NoProfile', '-Command', $lockScript
while (-not (Test-Path $ready)) { Start-Sleep -Milliseconds 25 }

# Prove the lock reproduces the flake: a plain rename must fail right now.
try {
    [IO.File]::Move($Exe, "$Exe.probe")
    [IO.File]::Move("$Exe.probe", $Exe)
    throw 'rename worked under the lock, so it does not reproduce the flake'
} catch [IO.IOException] {
    "while locked, plain rename: $($_.Exception.Message)"
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $Exe -ArgumentList 'update' -PassThru -NoNewWindow -Wait `
    -RedirectStandardOutput $log -RedirectStandardError $err
$finished = Get-Date
$elapsed = $sw.Elapsed.TotalSeconds
$locker.WaitForExit()
$out = (Get-Content -Raw $log) + (Get-Content -Raw $err)
$out
$lockedDuringInstall = Test-Path $released
# The update must still have been running when the lock went away, i.e. it
# really ran into the lock rather than finishing first.
$outlastedLock = $lockedDuringInstall -and (Get-Item $released).CreationTime -lt $finished
"update exit=$($p.ExitCode) after $([math]::Round($elapsed, 1))s; lock held until ${HoldSeconds}s after unpack: $lockedDuringInstall; update still running when it was released: $outlastedLock"
if (-not $lockedDuringInstall) { throw 'the lock was not held through the install step' }

if ($ExpectFail) {
    if ($p.ExitCode -eq 0 -or $out -notmatch 'being used by another process') {
        throw "expected the update to fail with a sharing violation (exit $($p.ExitCode))"
    }
    'OK: updater without retry fails under the lock (reproduces the flake)'
} else {
    if ($p.ExitCode -ne 0) { throw "update failed under the lock (exit $($p.ExitCode))" }
    if (-not $outlastedLock) { throw 'update finished before the lock was released: lock not exercised' }
    'OK: updater waited out the lock and installed'
}
