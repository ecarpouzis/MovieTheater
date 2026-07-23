<#
.SYNOPSIS
    Registers the logon Scheduled Task that runs the arcade GL-worker WATCHDOG
    (scripts/watch-arcade-glworkers.ps1). Sibling of register-arcade-glworker-task.ps1.

.DESCRIPTION
    The watchdog recycles a GL worker that is disconnected, port-drifted, room-wedged, idle-wedged,
    or running a STALE CONFIG (check E — config.yaml newer than the worker's start; see
    watch-arcade-glworkers.ps1). Like the workers it guards, it must run in the user's INTERACTIVE
    logon session (it reads the worker logs under the user profile and talks to the localhost
    coordinator), so it uses an AtLogon trigger and thus survives logoff/reboot.

    Run ONCE as the user who logs in on Ziggy (no admin needed for an interactive-logon task). Re-run
    to update — idempotent: it unregisters any existing task of the same name first. NOTE the task
    loads the .ps1 BY PATH at runtime, so editing the script alone already changes behaviour on the
    next run/reboot; this registrar exists so the TASK ITSELF is reproducible on a box rebuild (every
    other arcade task has a register-*.ps1 — this one did not, until now).

    Worker ports the watchdog guards live in watch-arcade-glworkers.ps1 ($WorkerPorts default
    8446,8447,8448). Add a worker → update that default (or pass -WorkerPorts there) and re-run this
    so the running watchdog picks up the new port (else it reaps the new worker as "port drift").
#>
param(
    [string]$Script   = (Join-Path $PSScriptRoot "watch-arcade-glworkers.ps1"),
    [string]$TaskName = "MovieTheater - Arcade GL Worker Watchdog"
)

if (-not (Test-Path $Script)) { throw "watchdog script not found: $Script" }

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

# conhost --headless keeps it windowless, same trick as the worker + WSL keepalive tasks.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
               "-File `"$Script`"")

$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# ExecutionTimeLimit 0 = never time-limit the long-running loop. IgnoreNew = the script's OWN singleton
# guard is authoritative (a task re-trigger must not stack a second instance — stacked watchdogs
# double-count strikes and kill healthy workers). Restart 999 x 1min = if the watchdog process ever
# dies, bring it back (the script is a while($true) loop, so this only fires on a crash). Battery flags
# keep it alive through a UPS/laptop power blip.
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
    -Description ("Runs the arcade GL-worker watchdog (watch-arcade-glworkers.ps1) in the interactive " +
                 "session so it survives logoff/reboot. Recycles disconnected/port-drifted/wedged/" +
                 "stale-config GL workers. Managed by register-arcade-glworker-watchdog-task.ps1.") | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (script: $Script)."
Write-Host "Confirm:  Get-Content 'D:\ArcadeStorage\logs\glworker-watchdog.log' -Tail 20"
