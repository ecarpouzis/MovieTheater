<#
.SYNOPSIS
    Registers the logon Scheduled Task that runs the arcade COORDINATOR-liveness watchdog
    (scripts/watch-arcade-coordinator.ps1). Sibling of register-arcade-glworker-watchdog-task.ps1.

.DESCRIPTION
    The coordinator's own in-process worker health monitor (health.go) deregisters DEAD WORKERS, and
    watch-arcade-glworkers.ps1 recycles wedged WORKERS -- but neither can save the arcade if the
    COORDINATOR ITSELF wedges or dies, which takes the whole arcade down. This watchdog polls the
    coordinator /status endpoint and restarts the coordinator task when it is unreachable for several
    consecutive checks (the recovery that resolved the 2026-07-24 zombie incident by hand). Like the
    other arcade tasks it runs in the user's INTERACTIVE logon session (localhost coordinator, user
    profile logs), so it uses an AtLogon trigger and survives logoff/reboot.

    Run ONCE as the user who logs in on Ziggy (no admin needed). Re-run to update -- idempotent
    (unregisters any existing task of the same name first). The task loads the .ps1 BY PATH at runtime,
    so editing the script alone changes behaviour on the next tick; this registrar exists so the TASK
    is reproducible on a box rebuild.
#>
param(
    [string]$Script   = (Join-Path $PSScriptRoot "watch-arcade-coordinator.ps1"),
    [string]$TaskName = "MovieTheater - Arcade Coordinator Watchdog"
)

if (-not (Test-Path $Script)) { throw "watchdog script not found: $Script" }

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

# conhost --headless keeps it windowless, same trick as the worker + GL-worker watchdog tasks.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
               "-File `"$Script`"")

$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# ExecutionTimeLimit 0 = never time-limit the long-running loop. IgnoreNew = the script's OWN singleton
# guard is authoritative (a stacked instance would double-count strikes and restart a healthy
# coordinator). Restart 999 x 1min brings the watchdog back if its process ever dies. Battery flags
# keep it alive through a UPS/power blip.
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit  (New-TimeSpan -Seconds 0) `
    -StartWhenAvailable `
    -MultipleInstances   IgnoreNew `
    -RestartCount        999 `
    -RestartInterval     (New-TimeSpan -Minutes 1) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName    $TaskName `
    -Action      $action `
    -Trigger     $trigger `
    -Settings    $settings `
    -Principal   $principal `
    -Description ("Runs the arcade coordinator-liveness watchdog (watch-arcade-coordinator.ps1) in the " +
                 "interactive session so it survives logoff/reboot. Restarts the coordinator task when " +
                 "its /status is unreachable. Managed by register-arcade-coordinator-watchdog-task.ps1.") | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (script: $Script)."
Write-Host "Confirm:  Get-Content 'D:\ArcadeStorage\logs\coordinator-watchdog.log' -Tail 20"
