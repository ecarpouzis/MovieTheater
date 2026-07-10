<#
.SYNOPSIS
    Registers a weekly scheduled task that sweeps orphaned Dolphin shader caches.

.DESCRIPTION
    Sibling of the other MovieTheater arcade task registrars. Runs clean-shader-caches.ps1 -Apply
    weekly (Sunday 05:00) so constant graphics tuning can't accumulate gigabytes of superseded
    per-config shader caches (the grace window inside the cleaner protects recent A/B experiments).
    Run this once, elevated. Re-running replaces the task.
#>
param(
    [string]$TaskName = "MovieTheater - Shader Cache Clean",
    [string]$Script   = "F:\Work\MovieTheater\scripts\clean-shader-caches.ps1"
)

$action  = New-ScheduledTaskAction -Execute "powershell.exe" `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$Script`" -Apply"
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At 5:00AM
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force
Write-Output "Registered '$TaskName' (weekly, Sunday 05:00, dry-run OFF: applies deletes)."
