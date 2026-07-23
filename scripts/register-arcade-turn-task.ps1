<#
.SYNOPSIS
    Registers the logon Scheduled Task that keeps the native arcade TURN relay running.
    Sibling of register-arcade-coordinator-task.ps1.

.DESCRIPTION
    Run ONCE as the user who logs in on Ziggy (no admin needed for an interactive-logon task).
    Re-run to update. Prereqs (see docs/arcade/turn-relay.md):
      - D:\ArcadeStorage\turn holds arcade-turn.exe, secret.txt, turn.crt, turn.key
      - a Deco WAN port-forward: TCP 5349 -> 192.168.68.69
      - the site's ArcadeTurnUrls/ArcadeTurnSecret populated (secret byte-identical to secret.txt)

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-turn-task.ps1
#>
param(
    [string]$RunScript = (Join-Path $PSScriptRoot "run-arcade-turn.ps1"),
    [string]$ConfDir   = "D:\ArcadeStorage\turn",
    [string]$LogFile   = "D:\ArcadeStorage\logs\turn.log"
)

$TaskName = "MovieTheater - Arcade TURN"

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

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
    -Description "Runs the native pion/turn relay (last-resort ICE path for guest/remote arcade clients). Managed by register-arcade-turn-task.ps1." | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (log $LogFile)."
Write-Host "Confirm it's listening:  Get-Content '$LogFile' -Tail 10"
