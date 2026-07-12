<#
.SYNOPSIS
    Registers the DAILY Scheduled Task that snapshots the emulators' virtual memory cards
    (sibling of register-arcade-gateway-task.ps1 / register-arcade-glworker-task.ps1).

.DESCRIPTION
    The memory cards (GameCube .gci dir, PCSX2 Mcd001.ps2) are the one class of save that the per-user
    vault does not cover: one global file, one copy, no backup — and for some games (Gauntlet Dark
    Legacy) that file IS the progress, since the named characters live on the card rather than in a
    save-state. This runs scripts/backup-arcade-memcards.ps1 daily until per-user card vaulting lands.

    Runs whether or not anyone is logged in; misses are made up at next boot (-StartWhenAvailable).
    Run ONCE as the user who logs in on Ziggy. Re-run to update.

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File register-arcade-memcard-backup-task.ps1
#>
param(
    [string]$RunScript = (Join-Path $PSScriptRoot "backup-arcade-memcards.ps1"),
    [string]$At        = "5:00AM"
)

$TaskName  = "MovieTheater - Arcade Memcard Backup"
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$RunScript`""

$trigger  = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit  (New-TimeSpan -Minutes 30) `
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
    -Description "Daily copy-only snapshot of the arcade's virtual memory cards (GC .gci, PS2 Mcd001) to D:\ArcadeStorage\backup\memcards." | Out-Null

Write-Host "Registered '$TaskName' (daily at $At)."
Write-Host "Run it now with: Start-ScheduledTask -TaskName '$TaskName'"
