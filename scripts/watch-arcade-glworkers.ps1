<#
.SYNOPSIS
    Watchdog for the Windows-native CloudRetro GL workers: kills a worker that lost its
    coordinator connection so its restart loop can respawn it fresh.

.DESCRIPTION
    Failure mode this guards (first seen 2026-07-07, dolphin/GameCube teardown): a worker
    PARTIALLY DEADLOCKS at session teardown -- its HTTP /echo keeps answering (so an /echo
    probe is blind, verified live), but its coordinator WebSocket stops pumping; the
    coordinator drops it ("read tcp ... i/o timeout" then "no free workers") and the worker
    NEVER reconnects. The process doesn't exit, so run-arcade-glworker.ps1's restart loop
    never fires, and the slot is gone until someone kills worker.exe by hand. With both
    workers wedged, every room create gets t=112 ("The arcade is full -- no free machines").

    Detection: a HEALTHY worker always holds one ESTABLISHED TCP connection to the
    coordinator (remote port 8000, via WSL mirrored networking on localhost). A worker.exe
    process with NO established :8000 connection is either wedged or silently disconnected --
    both states are useless and safe to recycle (any room it hosted is already dead).
    Two consecutive strikes 30s apart -> kill that PID (the runner respawns it in ~4s and it
    re-registers with the coordinator).

    Registered as scheduled task "MovieTheater - Arcade GL Worker Watchdog" (logon trigger,
    same pattern as the worker tasks). Safe to run interactively too.
#>
param(
    [int]   $CoordinatorPort = 8000,
    [int]   $IntervalSec = 30,
    [int]   $GraceSec = 60,
    [string]$LogFile = "D:\ArcadeStorage\logs\glworker-watchdog.log"
)

New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
function Log([string]$msg) {
    [System.IO.File]::AppendAllText($LogFile, ("{0}  {1}`r`n" -f (Get-Date -Format o), $msg), [System.Text.Encoding]::UTF8)
}

Log "watchdog started (coordinator port: $CoordinatorPort, interval: ${IntervalSec}s)"
$strikes = @{}

while ($true) {
    try {
        $workers = @(Get-CimInstance Win32_Process -Filter "Name='worker.exe'" -ErrorAction SilentlyContinue)
        $connected = @{}
        Get-NetTCPConnection -State Established -RemotePort $CoordinatorPort -ErrorAction SilentlyContinue |
            ForEach-Object { $connected[[int]$_.OwningProcess] = $true }

        foreach ($w in $workers) {
            $wpid = [int]$w.ProcessId
            # Grace period: a freshly spawned worker needs a moment to connect.
            $ageSec = ((Get-Date) - $w.CreationDate).TotalSeconds
            if ($ageSec -lt $GraceSec) { $strikes[$wpid] = 0; continue }

            if ($connected[$wpid]) { $strikes[$wpid] = 0; continue }

            $strikes[$wpid] = [int]$strikes[$wpid] + 1
            Log ("worker PID {0} has NO coordinator connection (strike {1})" -f $wpid, $strikes[$wpid])
            if ($strikes[$wpid] -ge 2) {
                Log ("KILLING disconnected/wedged worker PID {0} -- restart loop will respawn it" -f $wpid)
                Stop-Process -Id $wpid -Force -ErrorAction SilentlyContinue
                $strikes.Remove($wpid) | Out-Null
            }
        }

        # Tidy strike entries for PIDs that no longer exist.
        $live = @{}; foreach ($w in $workers) { $live[[int]$w.ProcessId] = $true }
        foreach ($k in @($strikes.Keys)) { if (-not $live[$k]) { $strikes.Remove($k) | Out-Null } }
    } catch {
        Log ("watchdog cycle error: {0}" -f $_.Exception.Message)
    }
    Start-Sleep -Seconds $IntervalSec
}
