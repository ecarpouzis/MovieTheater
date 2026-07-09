<#
.SYNOPSIS
    Registers the logon Scheduled Task that keeps the native CloudRetro coordinator running.
    Sibling of register-arcade-glworker-task.ps1.

.DESCRIPTION
    Replaces the WSL/docker `arcade-coordinator-1` container. See run-arcade-coordinator.ps1 for WHY
    (WSL idle-out + the stale-image bug that silently dropped per-room video_bitrate).

    Run ONCE as the user who logs in on Ziggy (no admin needed for an interactive-logon task).
    Re-run to update. Prereqs: D:\ArcadeStorage\coordinator holds coordinator.exe, config.yaml, web\.

    ⚠ Stop the docker coordinator first, or port 8000 is taken:
        wsl -d Ubuntu-24.04 -u root -- docker stop arcade-coordinator-1
      and remove the `coordinator` service from docker-compose.gpu.yml so `up -d` cannot resurrect it.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-coordinator-task.ps1
#>
param(
    [string]$RunScript = (Join-Path $PSScriptRoot "run-arcade-coordinator.ps1"),
    [string]$ConfDir   = "D:\ArcadeStorage\coordinator",
    [string]$LogFile   = "D:\ArcadeStorage\logs\coordinator.log"
)

$TaskName = "MovieTheater - Arcade Coordinator"

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

# conhost --headless keeps it windowless, same trick as the worker + WSL keepalive tasks.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
               "-File `"$RunScript`" -ConfDir `"$ConfDir`" -LogFile `"$LogFile`"")

$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit  (New-TimeSpan -Seconds 0) `
    -StartWhenAvailable `
    -MultipleInstances   IgnoreNew `
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
    -Description "Runs the CloudRetro coordinator natively on Windows (signaling/relay only, no GPU), replacing the WSL docker container. Managed by register-arcade-coordinator-task.ps1." | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (log $LogFile)."
Write-Host "Confirm workers register:  Get-Content '$LogFile' -Tail 20"
