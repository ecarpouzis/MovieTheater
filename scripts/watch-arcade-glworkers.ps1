<#
.SYNOPSIS
    Watchdog for the Windows-native CloudRetro GL workers: recycles a worker that is
    disconnected, port-drifted, or wedged holding a dead room, so its restart loop can
    respawn it fresh.

.DESCRIPTION
    Guards three observed failure modes (a kill is always safe: run-arcade-glworker.ps1's
    restart loop respawns the worker in ~4 s and it re-registers with the coordinator):

    A) DISCONNECTED (first seen 2026-07-07, dolphin/GameCube teardown): the worker partially
       deadlocks at session teardown -- its HTTP /echo keeps answering, but its coordinator
       WebSocket stops pumping; the coordinator drops it and (in this variant) it never
       reconnects. Detection: a worker.exe process with NO established TCP connection to the
       coordinator port. Two consecutive strikes 30 s apart -> kill.

    B) PORT DRIFT (first seen 2026-07-10): a worker's coordinator socket drops while the
       process lives (e.g. the coordinator's i/o timeout severs it mid-wedge). The worker DOES
       reconnect after ~10 s -- but its reconnect re-runs single-port setup, finds its OWN old
       socket still bound to the configured port, and port-probes upward (8446 -> 8449). It
       then advertises a port the router doesn't forward and Defender doesn't allow: every
       future room on it is silently media-dead. Detection: a worker.exe owning any UDP port
       in the mux range that is NOT in -WorkerPorts. Kill immediately (nothing on it is
       usable; grace period skips fresh spawns).

    C) ROOM-CLOSE WEDGE (first seen 2026-07-10, twice: snes9x and pcsx2 teardown): the room's
       last player leaves, core teardown hangs (e.g. PCSX2 stops after "Releasing host
       memory..."), the room never closes, and the coordinator considers the worker busy
       FOREVER -- rooms then hang while a worker slot is silently gone. The coordinator's own
       read-timeout may reap it ~19 min later; that is far too slow.
       Detection: the coordinator's /status endpoint (patch 0033) lists each worker's Room;
       a LIVE room always writes pace-diag to its worker's log every 5 s, so
       "coordinator says busy" + "that worker's log silent > WedgeStaleSec" = wedged.
       The busy Room id is mapped to its worker via the "New room" line in the worker logs
       (log name -> WorkerId -> port -> owning PID). Two strikes -> kill.
       /status unavailable (pre-0033 coordinator) -> check C is skipped silently.

    Registered as scheduled task "MovieTheater - Arcade GL Worker Watchdog" (logon trigger,
    same pattern as the worker tasks). Safe to run interactively too.
#>
param(
    [int]    $CoordinatorPort = 8000,
    [int]    $IntervalSec = 30,
    [int]    $GraceSec = 60,
    [int]    $WedgeStaleSec = 150,
    [int[]]  $WorkerPorts = @(8446, 8447, 8448),   # 8448 = capture worker (H5). Extend when adding workers.
    #  ⚠ The running watchdog TASK must be re-registered to pick up a new port — otherwise it reaps the
    #    new worker as "port drift" (bound a mux port not in this list). See the watchdog task registration.
    [string] $LogDir = "D:\ArcadeStorage\logs",
    [string] $LogFile = "D:\ArcadeStorage\logs\glworker-watchdog.log"
)

# The task runs under a headless conhost: any cmdlet progress rendering (Invoke-RestMethod
# especially) throws "No process is on the other end of the pipe" at EndProcessing and aborts
# the whole cycle. Suppress progress globally.
$ProgressPreference = 'SilentlyContinue'

# SINGLETON: Stop-ScheduledTask kills only the conhost wrapper and orphans the powershell
# child, so task restarts stack watchdog instances (4 observed 2026-07-10) -- duplicate strike
# counting then kills workers after a single bad cycle. Newest instance wins: kill any older
# sibling running this script.
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match 'watch-arcade-glworkers' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
function Log([string]$msg) {
    [System.IO.File]::AppendAllText($LogFile, ("{0}  {1}`r`n" -f (Get-Date -Format o), $msg), [System.Text.Encoding]::UTF8)
}

# Worker N (port 8445+N) logs to glworker.log (N=1) / glworker-N.log (N>1) -- the
# register-arcade-glworker-task.ps1 convention.
function WorkerLogPath([int]$port) {
    $id = $port - 8445
    if ($id -le 1) { return (Join-Path $LogDir "glworker.log") }
    return (Join-Path $LogDir ("glworker-{0}.log" -f $id))
}

# The LAST "New room" id in a worker's log (checks the rotated .1 file as fallback).
# Captures the id from BOTH shapes the logger emits:
#   room="sv-1-24-0-nes___Super Mario Bros. 3"      <- quoted, because the id contains spaces
#   room=sv-1-61329-0-capture___switch-kirby-...    <- UNQUOTED: zerolog only quotes when it must
# The quoted-only regex was a silent hole: CAPTURE room ids never contain spaces, so they never
# matched, every cycle logged "busy room not found in any worker log (skip)", and check C could not
# see the capture worker at all. It sat wedged holding its one room for a DAY (2026-07-11 -> 07-12)
# while the watchdog "ran" — and a wedged capture worker looks exactly like "the arcade is full".
#
# ⚠ The worker log is COLORIZED: zerolog writes an ANSI escape between `room=` and the value, so a
# pattern anchored on the literal `room="` matches NOTHING — check C was dead for EVERY worker, not
# just capture. Strip the escapes first, then accept either shape.
$roomRx = 'New room.*room=(?:"([^"]+)"|(\S+))'
$ansiRx = "$([char]27)\[[0-9;]*m"
function LastRoomInLog([string]$path) {
    foreach ($f in @($path, "$path.1")) {
        if (-not (Test-Path $f)) { continue }
        $m = Get-Content $f -Tail 20000 -ErrorAction SilentlyContinue |
            ForEach-Object { $_ -replace $ansiRx, '' } |
            Select-String -Pattern $roomRx | Select-Object -Last 1
        if ($m) {
            $g = $m.Matches[0].Groups
            if ($g[1].Success) { return $g[1].Value }
            return $g[2].Value
        }
    }
    return $null
}

function KillWorker([int]$wpid, [string]$why) {
    Log ("KILLING worker PID {0} -- {1} -- restart loop will respawn it" -f $wpid, $why)
    Stop-Process -Id $wpid -Force -ErrorAction SilentlyContinue
}

Log "watchdog v2 started (coordinator port: $CoordinatorPort, interval: ${IntervalSec}s, worker ports: $($WorkerPorts -join ','), wedge stale: ${WedgeStaleSec}s)"
$strikes = @{}       # PID -> consecutive no-coordinator-connection strikes (check A)
$wedgeStrikes = @{}  # PID -> consecutive busy-but-silent strikes (check C)

while ($true) {
    try {
        $workers = @(Get-CimInstance Win32_Process -Filter "Name='worker.exe'" -ErrorAction SilentlyContinue)
        $age = @{}
        foreach ($w in $workers) { $age[[int]$w.ProcessId] = ((Get-Date) - $w.CreationDate).TotalSeconds }
        $livePids = @{}; foreach ($w in $workers) { $livePids[[int]$w.ProcessId] = $true }

        # -- A) coordinator connection check ------------------------------------------------
        $connected = @{}
        Get-NetTCPConnection -State Established -RemotePort $CoordinatorPort -ErrorAction SilentlyContinue |
            ForEach-Object { $connected[[int]$_.OwningProcess] = $true }

        foreach ($w in $workers) {
            $wpid = [int]$w.ProcessId
            if ($age[$wpid] -lt $GraceSec) { $strikes[$wpid] = 0; continue }
            if ($connected[$wpid])         { $strikes[$wpid] = 0; continue }
            $strikes[$wpid] = [int]$strikes[$wpid] + 1
            Log ("worker PID {0} has NO coordinator connection (strike {1})" -f $wpid, $strikes[$wpid])
            if ($strikes[$wpid] -ge 2) {
                KillWorker $wpid "disconnected from coordinator"
                $strikes.Remove($wpid) | Out-Null
            }
        }

        # -- B) port-drift check -------------------------------------------------------------
        # Any worker-owned UDP port in the mux neighbourhood that is not a configured port
        # means the worker rebound after an in-process reconnect: recycle on sight.
        $muxLo = ($WorkerPorts | Measure-Object -Minimum).Minimum
        $muxHi = $muxLo + 20
        Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
            Where-Object { $_.LocalPort -ge $muxLo -and $_.LocalPort -le $muxHi -and $livePids[[int]$_.OwningProcess] } |
            ForEach-Object {
                $wpid = [int]$_.OwningProcess
                if (($WorkerPorts -notcontains [int]$_.LocalPort) -and $age[$wpid] -ge $GraceSec) {
                    KillWorker $wpid ("port drift: bound UDP {0}, expected one of {1}" -f $_.LocalPort, ($WorkerPorts -join ','))
                }
            }

        # -- C) room-close wedge check (needs coordinator /status, patch 0033) ---------------
        $status = $null
        try {
            $status = Invoke-RestMethod -Uri "http://localhost:$CoordinatorPort/status" -TimeoutSec 3
        } catch { }  # old coordinator: no endpoint; skip silently

        $wedgedPids = @{}
        if ($status) {
            foreach ($entry in @($status | Where-Object { $_.room })) {
                # Map the busy room to a worker log by its "New room" line.
                $port = $WorkerPorts | Where-Object { (LastRoomInLog (WorkerLogPath $_)) -eq $entry.room } | Select-Object -First 1
                if (-not $port) { Log ("busy room not found in any worker log (skip): {0}" -f $entry.room); continue }
                $logPath = WorkerLogPath $port
                $staleSec = ((Get-Date) - (Get-Item $logPath).LastWriteTime).TotalSeconds
                if ($staleSec -lt $WedgeStaleSec) { continue }   # room is ticking (pace-diag every 5 s)
                $wpid = (Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1).OwningProcess
                if (-not $wpid -or $age[[int]$wpid] -lt $GraceSec) { continue }
                $wpid = [int]$wpid
                $wedgedPids[$wpid] = $true
                $wedgeStrikes[$wpid] = [int]$wedgeStrikes[$wpid] + 1
                Log ("worker PID {0} (port {1}) BUSY with '{2}' but log silent {3:n0}s (wedge strike {4})" -f `
                    $wpid, $port, $entry.room, $staleSec, $wedgeStrikes[$wpid])
                if ($wedgeStrikes[$wpid] -ge 2) {
                    KillWorker $wpid "room-close wedge (busy + silent log)"
                    $wedgeStrikes.Remove($wpid) | Out-Null
                }
            }
        }
        foreach ($k in @($wedgeStrikes.Keys)) { if (-not $wedgedPids[$k]) { $wedgeStrikes.Remove($k) | Out-Null } }

        # Tidy strike entries for PIDs that no longer exist.
        foreach ($k in @($strikes.Keys)) { if (-not $livePids[$k]) { $strikes.Remove($k) | Out-Null } }
    } catch {
        Log ("watchdog cycle error: {0}" -f $_.Exception.Message)
    }
    Start-Sleep -Seconds $IntervalSec
}
