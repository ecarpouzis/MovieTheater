<#
.SYNOPSIS
    Registers the Scheduled Task that runs the Jellyfin keyframe-backfill KEEPER
    (scripts/extract-jf-keyframes-keeper.ps1) and disables the nightly trickle while it works.

.DESCRIPTION
    The keeper relaunches extract-jf-keyframes-marathon.ps1 until MediaFile.JfKeyframesUtc is
    stamped on every copyable file (the exact-HLS-copy backfill, .claude/skills/hls-copy-freeze),
    then re-enables the nightly task and disables itself. Same task pattern as the arcade GL-worker
    watchdog: interactive AtLogon trigger (survives Ziggy's TDR-bugcheck reboots via autologon),
    crash-restart, no execution time limit, singleton.

    Run ONCE as the user who logs in on Ziggy (no admin needed). Re-run to update -- idempotent.
    The nightly task "MovieTheater Extract JF Keyframes" is DISABLED here so 04:30 doesn't
    double-walk the head of the queue; the keeper re-enables it on every exit path.
#>
param(
    [string]$Script   = (Join-Path $PSScriptRoot "extract-jf-keyframes-keeper.ps1"),
    [string]$TaskName = "MovieTheater Extract JF Keyframes Keeper",
    [string]$NightlyTaskName = "MovieTheater Extract JF Keyframes"
)

if (-not (Test-Path $Script)) { throw "keeper script not found: $Script" }

$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive

# conhost --headless keeps it windowless, same trick as the arcade tasks.
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" `
    -Argument ("--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
               "-File `"$Script`"")

$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

# ExecutionTimeLimit 0 = the loop may legitimately run for days (the backfill is ~25 TB of ffprobe
# walks). IgnoreNew = never stack a second keeper. Restart 999 x 1min = revive on a crash only --
# the keeper's deliberate exits (queue drained, give-up) return 0 and stay exited.
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
    -Description ("Keeps the Jellyfin keyframe backfill (extract-jf-keyframes-marathon.ps1) running " +
                 "until the queue is empty, then re-enables the nightly task and disables itself. " +
                 "Managed by register-extract-jf-keyframes-keeper-task.ps1.") | Out-Null

# The keeper owns the queue while it lives; the nightly comes back when it exits.
if (Get-ScheduledTask -TaskName $NightlyTaskName -ErrorAction SilentlyContinue) {
    Disable-ScheduledTask -TaskName $NightlyTaskName | Out-Null
    Write-Host "Nightly task '$NightlyTaskName' disabled (keeper re-enables it when done)."
}

Start-ScheduledTask -TaskName $TaskName
Write-Host "Task '$TaskName' registered and started (script: $Script)."
Write-Host "Watch:  Get-Content 'F:\Work\MovieTheater\data\extract-jf-keyframes.log' -Tail 20 -Wait"
