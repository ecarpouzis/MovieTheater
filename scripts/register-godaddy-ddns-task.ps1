<#
.SYNOPSIS
    Registers the Scheduled Task that runs update-godaddy-aaaa.ps1 every 15 minutes.
    Sibling of register-arcade-turn-task.ps1 (same conventions).

.DESCRIPTION
    Run ONCE as the user who logs in on Ziggy (no admin needed). Re-run to update.
    Prereqs (see docs/site-ipv4-door.md):
      - D:\ArcadeStorage\ddns\godaddy.json holds the GoDaddy API { key, secret }
      - one clean run of update-godaddy-aaaa.ps1 -WhatIf first, so the first live
        write is a decision and not a surprise

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File register-godaddy-ddns-task.ps1
#>
param(
    [string]$RunScript = (Join-Path $PSScriptRoot 'update-godaddy-aaaa.ps1'),
    [string]$LogFile   = 'D:\ArcadeStorage\logs\ddns.log'
)

$TaskName = 'MovieTheater - GoDaddy AAAA DDNS'

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
               "-File `"$RunScript`" -LogFile `"$LogFile`"")

# Every 15 minutes, forever. A prefix re-delegation therefore costs dual-stack visitors their
# direct path for at most 15 minutes + the record TTL.
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
    -RepetitionInterval (New-TimeSpan -Minutes 15) -RepetitionDuration ([TimeSpan]::MaxValue)

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit  (New-TimeSpan -Minutes 5) `
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
    -Description "Keeps the books/turn AAAA records pointed at Ziggy's stable SLAAC address (the durable fix for ISP prefix re-delegation). Managed by register-godaddy-ddns-task.ps1." | Out-Null

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (log $LogFile)."
Write-Host "Watch a cycle:  Get-Content '$LogFile' -Tail 10"
