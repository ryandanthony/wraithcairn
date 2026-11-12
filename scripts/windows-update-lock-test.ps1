<#
  Runs `wraithcairn update` while another process holds wraithcairn.exe open
  without FILE_SHARE_DELETE, like Defender or the search indexer briefly do.
  That makes the updater's rename fail with "being used by another process"
  (the flake seen on win-arm64). The lock is taken before the update starts
  and released HoldSeconds after the updater prints "Installing", so it is
  held exactly while the exe is being swapped, however long the download is.

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

$lockScript = @"
`$fs = [IO.File]::Open('$Exe', 'Open', 'Read', 'Read')
New-Item '$ready' | Out-Null
`$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt `$deadline) {
  if (Test-Path '$log') {
    `$r = [IO.File]::Open('$log', 'Open', 'Read', 'ReadWrite,Delete')
    `$text = (New-Object IO.StreamReader(`$r)).ReadToEnd(); `$r.Close()
    if (`$text -match 'Installing') { break }
  }
  Start-Sleep -Milliseconds 25
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
"update exit=$($p.ExitCode) after $([math]::Round($elapsed, 1))s; lock held until after 'Installing': $lockedDuringInstall; update still running when it was released: $outlastedLock"
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
