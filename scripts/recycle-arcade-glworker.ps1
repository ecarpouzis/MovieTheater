<#
.SYNOPSIS
    Recycle a Windows-native CloudRetro GL worker SAFELY: graceful stop first, force-kill only as a
    fallback, and — critically — VERIFY the process actually died so an unkillable "zombie" is surfaced
    loudly instead of sitting silent for hours holding its ConfDir's DLL + shader cache locked.

.DESCRIPTION
    Use this INSTEAD of `Stop-Process worker -Force` for every deploy/recycle. Force-killing a GL/NVENC
    worker is what created the failure mode we hit 2026-07-14/15:
      * TerminateProcess mid-GPU-teardown can strand a thread inside the NVIDIA driver -> a process
        that survives even `taskkill /F` (kernel-mode wait), lingering as a zombie with its ConfDir
        locked. PID 7948 sat like this for ~10 h, silently cutting the pool to one room.
    (RETRACTED 2026-07-15: this header originally also blamed force-kills for dumping PCSX2's GS
    shader cache -> cold-cache audio skips. Source-confirmed wrong: the cache appends immediately on
    compile; a kill loses nothing. The zombie above is the sole reason. docs/arcade-stuntman-plan.md)

    Order of operations:
      1. LIVE-ROOM GUARD. If the coordinator says this worker owns a room whose log is still ticking
         (pace-diag every 5 s), REFUSE unless -Force -- killing a live session is exactly what dumped
         the shader cache. (A wedged/stale room is not protected; that is the watchdog's job.)
      2. GRACEFUL. Drop the CLOUD_GAME_STOP_FILE sentinel; the worker (pkg/os ExpectTermination) runs
         w.Stop() -> flushes the shader cache, closes the room, tears down GL/NVENC -> exits cleanly.
         Wait up to -GraceSec. (Needs a worker built with the stop-file watch; older binaries ignore
         it and we fall through to force -- still safe, just not graceful.)
      3. FORCE. Only if it did not exit: Stop-Process -Force.
      4. VERIFY. Re-check. If it is STILL alive it is a kernel-stuck zombie that no user-mode kill can
         clear -- log a LOUD alert, drop a WEDGED-worker<N>.flag, and tell the operator a reboot is
         required. Never pretend the kill worked.

    The runner (run-arcade-glworker.ps1) respawns the worker in ~4 s and clears a stale sentinel on
    start, so a successful graceful/force stop just recycles it.

.PARAMETER WorkerId   1-based worker id (port 8445+Id; ConfDir worker-gl / worker-gl-<Id>; log
                      glworker.log / glworker-<Id>.log) -- the register-arcade-glworker-task.ps1 convention.
.PARAMETER GraceSec   How long to wait for a graceful exit before forcing (default 60).
.PARAMETER Force      Recycle even if the worker owns a LIVE (ticking) room. Kicks that player.
#>
param(
    [Parameter(Mandatory)][int] $WorkerId,
    # 60s, not seconds: must outwait the worker's own internal wedge bounds (room close 30s, media
    # destroy 10s, whole-shutdown deadman 45s — each ends in a self-TerminateProcess) so a wedged
    # worker dies by its own hand. An external force-kill mid-GPU-teardown is the proven trigger for
    # the UNKILLABLE zombie (docs/arcade-worker-unkillable-wedge.md).
    [int]    $GraceSec        = 60,
    [switch] $Force,
    [int]    $CoordinatorPort = 8000,
    [int]    $WedgeStaleSec   = 150,
    [string] $LogDir          = "D:\ArcadeStorage\logs"
)
$ProgressPreference = 'SilentlyContinue'

$port    = 8445 + $WorkerId
$confDir = if ($WorkerId -le 1) { "D:\ArcadeStorage\worker-gl" } else { "D:\ArcadeStorage\worker-gl-$WorkerId" }
$logPath = if ($WorkerId -le 1) { Join-Path $LogDir "glworker.log" } else { Join-Path $LogDir "glworker-$WorkerId.log" }
$stopFile = Join-Path $confDir ".stop"

function Say([string]$m) { Write-Host ("[recycle w{0}] {1}" -f $WorkerId, $m) }

# -- resolve the worker PID by its mux UDP port ------------------------------------------------------
$wpid = (Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
    Where-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).Name -eq 'worker' } |
    Select-Object -First 1).OwningProcess
if (-not $wpid) { Say "no worker.exe bound to UDP $port -- nothing running to recycle (runner will spawn one)."; return }
$wpid = [int]$wpid
Say "worker PID $wpid on port $port (ConfDir $confDir)"

# -- 1) live-room guard ------------------------------------------------------------------------------
$ansiRx = "$([char]27)\[[0-9;]*m"
function LastRoomInLog([string]$p) {
    foreach ($f in @($p, "$p.1")) {
        if (-not (Test-Path $f)) { continue }
        $m = Get-Content $f -Tail 20000 -ErrorAction SilentlyContinue | ForEach-Object { $_ -replace $ansiRx, '' } |
            Select-String -Pattern 'New room.*room=(?:"([^"]+)"|(\S+))' | Select-Object -Last 1
        if ($m) { $g = $m.Matches[0].Groups; if ($g[1].Success) { return $g[1].Value } else { return $g[2].Value } }
    }
    return $null
}
if (-not $Force) {
    $status = $null
    try { $status = Invoke-RestMethod -Uri "http://localhost:$CoordinatorPort/status" -TimeoutSec 3 } catch { }
    $myRoom = LastRoomInLog $logPath
    $busyHere = $status | Where-Object { $_.room -and $_.room -eq $myRoom }
    if ($busyHere) {
        $staleSec = ((Get-Date) - (Get-Item $logPath).LastWriteTime).TotalSeconds
        if ($staleSec -lt $WedgeStaleSec) {
            Say "REFUSING: worker owns a LIVE room '$myRoom' (log ticking ${staleSec}s ago). Re-run with -Force to kick it."
            return
        }
        Say "worker's room '$myRoom' looks WEDGED (log silent ${staleSec}s) -- proceeding to recycle."
    }
}

# -- 2) graceful stop via the sentinel ---------------------------------------------------------------
Say "requesting graceful shutdown (sentinel $stopFile); waiting up to ${GraceSec}s ..."
Set-Content -Path $stopFile -Value (Get-Date -Format o) -Encoding ASCII
$deadline = (Get-Date).AddSeconds($GraceSec)
while ((Get-Date) -lt $deadline -and (Get-Process -Id $wpid -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
Remove-Item $stopFile -Force -ErrorAction SilentlyContinue

if (-not (Get-Process -Id $wpid -ErrorAction SilentlyContinue)) {
    Say "exited gracefully (shader cache flushed). Runner will respawn it in ~4s."
    return
}

# -- 3) force fallback -------------------------------------------------------------------------------
Say "did not exit gracefully (old binary without stop-file watch, or a hung teardown) -- forcing."
Stop-Process -Id $wpid -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# -- 4) verify + zombie alert ------------------------------------------------------------------------
if (-not (Get-Process -Id $wpid -ErrorAction SilentlyContinue)) {
    Say "force-killed. Runner will respawn it in ~4s."
    return
}
$flag = Join-Path $LogDir ("WEDGED-worker{0}.flag" -f $WorkerId)
$msg = "WEDGED: worker PID $wpid (w$WorkerId, port $port) SURVIVED force-kill -- kernel-stuck GPU-teardown thread, " +
       "UNKILLABLE from user mode. It holds $confDir's DLL + shader cache locked and the coordinator slot is dead. " +
       "A BOX REBOOT is required to clear it. (This is the PID-7948 class.)"
Say $msg
Set-Content -Path $flag -Value ("{0}  {1}" -f (Get-Date -Format o), $msg) -Encoding UTF8
[System.IO.File]::AppendAllText((Join-Path $LogDir "glworker-watchdog.log"),
    ("{0}  {1}`r`n" -f (Get-Date -Format o), $msg), [System.Text.Encoding]::UTF8)
