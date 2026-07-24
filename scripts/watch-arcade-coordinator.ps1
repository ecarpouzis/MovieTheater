<#
.SYNOPSIS
    Coordinator-liveness watchdog for the arcade CloudRetro stack. Defense-in-depth companion to
    watch-arcade-glworkers.ps1 (which guards the WORKERS) and to the coordinator's own in-process
    worker health monitor (health.go, which deregisters dead workers).

    THE GAP THIS FILLS: the worker health monitor lives INSIDE the coordinator, so it cannot save the
    arcade if the COORDINATOR ITSELF wedges or dies. A dead coordinator = the whole arcade is down
    (no rooms can be created, no worker can register). This watchdog polls the coordinator's /status
    HTTP endpoint and, if it is unreachable for several consecutive checks, restarts the coordinator
    scheduled task -- the exact recovery that resolved the 2026-07-24 incident by hand. A fresh
    coordinator also drops every stale worker registration on start, and a dead/wedged worker cannot
    reconnect to it, so this doubles as the belt-and-suspenders zombie-registration cleaner.

    IT NEVER kills a zombie worker process (proven futile -- a 1-thread wedged process survives every
    force-kill and needs a reboot) and NEVER touches a worker. Its only lever is the coordinator task.

    Registered via register-arcade-coordinator-watchdog-task.ps1. Runs in the user's INTERACTIVE
    session like the other arcade tasks. The task action reloads this .ps1 BY PATH at runtime, so
    editing the script changes behaviour on the next tick without re-registering.

    NOTE (PS 5.1): the scheduled-task action runs Windows PowerShell 5.1. Keep this file UTF-8 WITH
    BOM and ASCII-only content, or 5.1 mis-parses it.
#>
[CmdletBinding()]
param(
    [int]    $CoordinatorPort = 8000,
    [string] $TaskName        = "MovieTheater - Arcade Coordinator",
    [int]    $IntervalSec     = 20,
    [int]    $FailsToAct      = 3,     # ~60s unreachable before restarting (rides out a restart/GC blip)
    [int]    $TimeoutSec      = 5,
    [string] $LogFile         = "D:\ArcadeStorage\logs\coordinator-watchdog.log"
)

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'  # headless conhost: no progress rendering

function Write-Log([string]$msg) {
    $line = ("{0}  {1}" -f (Get-Date -Format o), $msg)
    try { [System.IO.File]::AppendAllText($LogFile, $line + "`r`n", [System.Text.Encoding]::UTF8) } catch {}
}

# One watchdog only. A prior instance (task re-run) must yield so strikes don't split across processes.
Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*watch-arcade-coordinator*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

function Test-Coordinator {
    # Alive == /status answers HTTP 200 within the timeout. A wedged or dead coordinator fails to
    # accept/answer, which is exactly what we act on.
    try {
        $r = Invoke-WebRequest -Uri ("http://localhost:{0}/status" -f $CoordinatorPort) `
                -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
        return ($r.StatusCode -eq 200)
    } catch { return $false }
}

function Restart-Coordinator {
    Write-Log "coordinator unreachable x$FailsToAct -- restarting task '$TaskName'"
    # End the task (kills its wrapper), then force-kill any orphaned coordinator.exe still holding
    # :8000 (schtasks /End does not reap a child the wrapper spawned outside its job), then re-run.
    & schtasks.exe /End /TN $TaskName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    Get-CimInstance Win32_Process -Filter "Name='coordinator.exe'" |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    & schtasks.exe /Run /TN $TaskName 2>&1 | Out-Null
    Write-Log "coordinator task restart issued; waiting for it to come back"
    # Give it up to ~30s to answer again before we resume counting (avoids an immediate re-strike).
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 2
        if (Test-Coordinator) { Write-Log "coordinator answered /status again -- recovered"; return $true }
    }
    Write-Log "coordinator STILL not answering after restart -- likely needs manual attention / reboot"
    return $false
}

Write-Log "coordinator watchdog started (poll /status every ${IntervalSec}s, act after $FailsToAct misses)"
$fails = 0
while ($true) {
    if (Test-Coordinator) {
        if ($fails -gt 0) { Write-Log "coordinator recovered after $fails misses" }
        $fails = 0
    } else {
        $fails++
        Write-Log "coordinator /status miss $fails/$FailsToAct"
        if ($fails -ge $FailsToAct) {
            Restart-Coordinator | Out-Null
            $fails = 0
        }
    }
    Start-Sleep -Seconds $IntervalSec
}
