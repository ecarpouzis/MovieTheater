<#
.SYNOPSIS
    Registers the logon Scheduled Task that runs the arcade GATEWAY-liveness watchdog
    (scripts/watch-arcade-gateway.ps1). Sibling of register-arcade-coordinator-watchdog-task.ps1
    and register-arcade-glworker-watchdog-task.ps1.

.DESCRIPTION
    run-arcade-gateway.ps1 already restarts the gateway when its PROCESS EXITS, so a crash is covered.
    Nothing covered a gateway that stays ALIVE but stops serving: the runner sees a healthy child, and
    every room dies at "Connecting..." because signaling is never proxied. Since the gateway validates
    every capability token, JIT-stages every ROM and seeds/harvests every save, that wedge takes the
    whole arcade down while looking healthy. This watchdog polls /healthz and restarts the gateway task
    after several consecutive misses. Like the other arcade tasks it runs in the user's INTERACTIVE
    logon session, so it uses an AtLogon trigger and survives logoff/reboot.

    Run ONCE as the user who logs in on Ziggy (no admin needed). Re-run to update -- idempotent
    (unregisters any existing task of the same name first). The task loads the .ps1 BY PATH at runtime,
    so editing the script alone changes behaviour on the next tick; this registrar exists so the TASK
    is reproducible on a box rebuild.
#>
param(
    [string]$Script   = (Join-Path $PSScriptRoot "watch-arcade-gateway.ps1"),
    [string]$TaskName = "MovieTheater - Arcade Gateway Watchdog"
)

if (-not (Test-Path $Script)) { throw "watchdog script not found: $Script" }

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

# conhost --headless keeps it windowless, same trick as the other arcade watchdog tasks.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
               "-File `"$Script`"")

$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# ExecutionTimeLimit 0 = never time-limit the long-running loop. IgnoreNew = the script's OWN singleton
# guard is authoritative (a stacked instance would double-count strikes and restart a healthy gateway).
# Restart 999 x 1min brings the watchdog back if its process ever dies. Battery flags keep it alive
# through a UPS/power blip.
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
    -Description ("Runs the arcade gateway-liveness watchdog (watch-arcade-gateway.ps1) in the " +
                 "interactive session so it survives logoff/reboot. Restarts the gateway task when " +
                 "its /healthz is unreachable. Managed by register-arcade-gateway-watchdog-task.ps1.") | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (script: $Script)."
Write-Host "Confirm:  Get-Content 'D:\ArcadeStorage\logs\gateway-watchdog.log' -Tail 20"
