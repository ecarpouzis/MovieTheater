<#
.SYNOPSIS
    Registers the logon Scheduled Task that keeps the ArcadeGateway running (sibling of
    register-arcade-glworker-task.ps1 / register-arcade-wsl-task.ps1).

.DESCRIPTION
    The gateway had no keep-alive — a manual start that didn't survive reboot, which now also breaks the
    durable save vault (seed/harvest live here). This task launches scripts/run-arcade-gateway.ps1 in the
    interactive logon session (consistent with the other arcade tasks; it reads the gateway's on-disk
    appsettings.Production.json from the repo, which holds the token secret + RomCache/SaveStore config).

    Run ONCE as the user who logs in on Ziggy (no admin needed). Re-run to update.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-gateway-task.ps1
#>
param(
    [string]$RunScript = (Join-Path $PSScriptRoot "run-arcade-gateway.ps1")
)

$TaskName  = "MovieTheater - Arcade Gateway"
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$RunScript`""

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
    -Description "Keeps the ArcadeGateway (signaling proxy + JIT ROM cache + durable save seed/harvest) running so it survives logoff/reboot. Managed by register-arcade-gateway-task.ps1." | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started. Tail D:\ArcadeStorage\logs\gateway.log to confirm."
