<#
  Runs `wraithcairn update` while another process holds wraithcairn.exe open
  without FILE_SHARE_DELETE, like Defender or the search indexer briefly do.
  That makes the updater's rename fail with "being used by another process"
  (the flake seen on win-arm64).

  The lock is taken before the update starts and released HoldSeconds after
  the updater has finished extracting the new exe (its very next step is to
  rename the old one), so the rename is guaranteed to run into it, however
  long the download and unzip take. A trace of lock/extract/rename/release
  times is printed.

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
$trace = Join-Path $tmp "lock-$id.trace"
$lockFile = Join-Path $tmp "lock-$id.ps1"
$lockErr = Join-Path $tmp "lock-$id.err"
$installDir = Split-Path $Exe
# Updater.InstallAsync works in %TEMP%\wraithcairn-update-*: the zip, then unpacked\.
$tempRoot = [IO.Path]::GetTempPath()
$before = @(Get-ChildItem $tempRoot -Directory -Filter 'wraithcairn-update-*' -ErrorAction SilentlyContinue | ForEach-Object FullName)

# The locker runs in its own process (a lock held by this one would be
# released when the update blocks it). Values are baked in as literals.
$lockScript = @"
`$ErrorActionPreference = 'Continue'
function T(`$m) { Add-Content '$trace' "`$((Get-Date).ToString('HH:mm:ss.fff')) `$m" }
function OldSeen { [bool](Get-ChildItem '$installDir' -Filter '*.old' -ErrorAction SilentlyContinue) }
`$fs = [IO.File]::Open('$Exe', 'Open', 'Read', 'Read')
T 'locked'
New-Item '$ready' | Out-Null
`$before = @('$($before -join "','")')
`$deadline = (Get-Date).AddSeconds(180)
`$expected = `$null
`$extracted = `$false
`$oldSeen = `$false
while (-not `$extracted -and (Get-Date) -lt `$deadline) {
  if (-not `$oldSeen -and (OldSeen)) { `$oldSeen = `$true; T 'old exe renamed before extraction finished?!' }
  foreach (`$d in Get-ChildItem '$tempRoot' -Directory -Filter 'wraithcairn-update-*' -ErrorAction SilentlyContinue) {
    if (`$before -contains `$d.FullName) { continue }
    if (-not `$expected) {
      `$zip = Get-ChildItem `$d.FullName -Filter '*.zip' -ErrorAction SilentlyContinue | Select-Object -First 1
      if (`$zip -and (Test-Path (Join-Path `$d.FullName 'unpacked'))) {
        # The zip is complete once extraction starts; read the exe's size.
        try {
          Add-Type -AssemblyName System.IO.Compression.FileSystem
          `$za = [IO.Compression.ZipFile]::OpenRead(`$zip.FullName)
          `$expected = (`$za.Entries | Where-Object FullName -eq 'wraithcairn/wraithcairn.exe').Length
          `$za.Dispose()
          T "extracting, exe will be `$expected bytes"
        } catch { }
      }
    }
    `$fresh = Join-Path `$d.FullName 'unpacked\wraithcairn\wraithcairn.exe'
    if (`$expected -and (Test-Path `$fresh) -and (Get-Item `$fresh).Length -eq `$expected) {
      `$extracted = `$true
      T 'new exe extracted'
    }
  }
  Start-Sleep -Milliseconds 5
}
`$until = (Get-Date).AddMilliseconds($([int]($HoldSeconds * 1000)))
while ((Get-Date) -lt `$until) {
  if (-not `$oldSeen -and (OldSeen)) { `$oldSeen = `$true; T 'old exe renamed WHILE LOCKED?!' }
  Start-Sleep -Milliseconds 5
}
`$fs.Close()
T 'released'
New-Item '$released' | Out-Null
`$after = (Get-Date).AddSeconds(15)
while (-not `$oldSeen -and (Get-Date) -lt `$after) {
  if (OldSeen) { `$oldSeen = `$true; T 'old exe renamed (after release)' }
  Start-Sleep -Milliseconds 5
}
"@
Set-Content -Path $lockFile -Value $lockScript
$locker = Start-Process pwsh -PassThru -NoNewWindow -RedirectStandardError $lockErr `
    -ArgumentList '-NoProfile', '-File', "`"$lockFile`""
while (-not (Test-Path $ready)) {
    if ($locker.HasExited) { Get-Content $lockErr; throw 'locker exited early' }
    Start-Sleep -Milliseconds 25
}

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
'lock trace:'
Get-Content $trace -ErrorAction SilentlyContinue | ForEach-Object { "  $_" }
Get-Content $lockErr -ErrorAction SilentlyContinue | ForEach-Object { "  locker stderr: $_" }
"  $($finished.ToString('HH:mm:ss.fff')) update exited ($($p.ExitCode)); installed now: $(& $Exe --version | Select-Object -First 1)"

$traceText = Get-Content -Raw $trace -ErrorAction SilentlyContinue
$heldThroughExtract = $traceText -match 'new exe extracted' -and (Test-Path $released)
$renamedEarly = $traceText -match '\?!'
$outlastedLock = $heldThroughExtract -and (Get-Item $released).CreationTime -lt $finished
"update exit=$($p.ExitCode) after $([math]::Round($elapsed, 1))s; held ${HoldSeconds}s past extraction: $heldThroughExtract; update outlasted the lock: $outlastedLock"
if (-not $heldThroughExtract) { throw 'the lock was not held through extraction' }
if ($renamedEarly) { throw 'the old exe was renamed while it should have been locked: lock not effective' }

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
