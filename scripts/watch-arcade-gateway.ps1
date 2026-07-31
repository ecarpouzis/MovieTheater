<#
.SYNOPSIS
    Gateway-liveness watchdog for the arcade. Companion to watch-arcade-coordinator.ps1 (guards the
    COORDINATOR) and watch-arcade-glworkers.ps1 (guards the WORKERS).

    THE GAP THIS FILLS: run-arcade-gateway.ps1 already restarts the gateway when the process EXITS,
    so a crash is covered. It cannot see a gateway that is still ALIVE but no longer serving --
    the process stays up, the runner stays happy, and every room dies at "Connecting..." because
    signaling never gets proxied. The gateway is a hard single point of failure (it validates every
    capability token, JIT-stages every ROM, and seeds/harvests every save), so an unnoticed wedge
    takes the whole arcade down while looking healthy.

    Its only lever is the gateway scheduled task. It NEVER touches a worker or the coordinator, and
    it deliberately does NOT restart on a slow response -- a JIT extraction can hold the box busy for
    a minute, and cutting the gateway mid-extract would abort a stage the next player needs anyway.

    Registered via register-arcade-gateway-watchdog-task.ps1. Runs in the user's INTERACTIVE session
    like the other arcade tasks. The task action reloads this .ps1 BY PATH at runtime, so editing the
    script changes behaviour on the next tick without re-registering.

    NOTE (PS 5.1): the scheduled-task action runs Windows PowerShell 5.1. Keep this file UTF-8 WITH
    BOM and ASCII-only content, or 5.1 mis-parses it.
#>
[CmdletBinding()]
param(
    [int]    $GatewayPort = 2303,
    [string] $TaskName    = "MovieTheater - Arcade Gateway",
    [int]    $IntervalSec = 20,
    [int]    $FailsToAct  = 4,     # ~80s unreachable before restarting (rides out a restart/GC blip)
    [int]    $TimeoutSec  = 8,     # generous: the gateway shares the box with a live extraction
    [string] $LogFile     = "D:\ArcadeStorage\logs\gateway-watchdog.log"
)

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'  # headless conhost: no progress rendering

function Write-Log([string]$msg) {
    $line = ("{0}  {1}" -f (Get-Date -Format o), $msg)
    try { [System.IO.File]::AppendAllText($LogFile, $line + "`r`n", [System.Text.Encoding]::UTF8) } catch {}
}

# One watchdog only. A prior instance (task re-run) must yield so strikes don't split across processes.
Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*watch-arcade-gateway*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

function Test-Gateway {
    # Alive == /healthz answers HTTP 200 within the timeout. A wedged gateway fails to accept or
    # answer, which is exactly what we act on.
    try {
        $r = Invoke-WebRequest -Uri ("http://localhost:{0}/healthz" -f $GatewayPort) `
                -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
        return ($r.StatusCode -eq 200)
    } catch { return $false }
}

function Restart-Gateway {
    Write-Log "gateway unreachable x$FailsToAct -- restarting task '$TaskName'"
    # End the task (kills its runner wrapper), then force-kill any orphaned gateway still holding
    # :2303 (schtasks /End does not reap a child the wrapper spawned outside its job), then re-run.
    & schtasks.exe /End /TN $TaskName 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    Get-CimInstance Win32_Process -Filter "Name='MovieTheater.ArcadeGateway.exe'" |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    & schtasks.exe /Run /TN $TaskName 2>&1 | Out-Null
    Write-Log "gateway task restart issued; waiting for it to come back"
    # Give it up to ~40s to answer again before we resume counting (avoids an immediate re-strike);
    # a cold start reloads the whole JIT manifest, which is not instant.
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 2
        if (Test-Gateway) { Write-Log "gateway answered /healthz again -- recovered"; return $true }
    }
    Write-Log "gateway STILL not answering after restart -- likely needs manual attention"
    return $false
}

Write-Log "gateway watchdog started (poll /healthz every ${IntervalSec}s, act after $FailsToAct misses)"
$fails = 0
while ($true) {
    if (Test-Gateway) {
        if ($fails -gt 0) { Write-Log "gateway recovered after $fails misses" }
        $fails = 0
    } else {
        $fails++
        Write-Log "gateway /healthz miss $fails/$FailsToAct"
        if ($fails -ge $FailsToAct) {
            Restart-Gateway | Out-Null
            $fails = 0
        }
    }
    Start-Sleep -Seconds $IntervalSec
}
