#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time setup: registers a Windows Scheduled Task that keeps the IIS
    HTTPS certificate in sync with the Let's Encrypt cert in MicroK8s.

.DESCRIPTION
    Copies update-iis-cert-windows.ps1 to a stable installation directory
    and creates a Scheduled Task that runs it every week as SYSTEM.

    cert-manager automatically renews the Let's Encrypt certificate ~30 days
    before it expires (cert lifetime is 90 days).  Running the renewal check
    weekly means IIS will pick up the new certificate within a few days of
    the cluster renewal, with no human action required.

    Run this script ONCE on the Windows server after first deployment.
    Re-run it any time you want to update the installed copy of the
    update script (e.g. after pulling a new version from the repo).

.PARAMETER InstallDir
    Directory where update-iis-cert-windows.ps1 will be permanently
    installed.  Defaults to C:\Program Files\MovieTheater\scripts

.PARAMETER RunNow
    Triggers the task immediately after registration so you can verify
    everything works before the first scheduled run.

.EXAMPLE
    # Register the task (run from the repo scripts\ directory):
    powershell.exe -ExecutionPolicy Bypass -File register-cert-renewal-task.ps1

    # Register and run immediately to test:
    powershell.exe -ExecutionPolicy Bypass -File register-cert-renewal-task.ps1 -RunNow
#>
param(
    [string]$InstallDir = "C:\Program Files\MovieTheater\scripts",
    [switch]$RunNow
)

$TaskName   = "MovieTheater - Renew IIS Certificate"
$ScriptName = "update-iis-cert-windows.ps1"
$LogDir     = "C:\ProgramData\MovieTheater\logs"
$LogFile    = "$LogDir\cert-renewal.log"

# ── Locate source script ──────────────────────────────────────────────────────

$SourceScript = Join-Path $PSScriptRoot $ScriptName
if (-not (Test-Path $SourceScript)) {
    Write-Error "Cannot find '$ScriptName' next to this registration script ($PSScriptRoot).`nRe-run from the repo's scripts\ directory."
    exit 1
}

# ── Install to stable location ───────────────────────────────────────────────

Write-Host ""
Write-Host "Installing renewal script..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogDir     | Out-Null

$InstalledScript = Join-Path $InstallDir $ScriptName
Copy-Item $SourceScript $InstalledScript -Force
Write-Host "  Installed : $InstalledScript"
Write-Host "  Log file  : $LogFile"

# ── Build Scheduled Task components ──────────────────────────────────────────

# Use the PowerShell executable that is currently running this script so
# the same PS version (5.1 or 7+) is used for the task.
$psExe = (Get-Process -Id $PID).Path

$taskArgs = "-NonInteractive -ExecutionPolicy Bypass -File `"$InstalledScript`" " +
            "-LogFile `"$LogFile`""

$action = New-ScheduledTaskAction -Execute $psExe -Argument $taskArgs

# Run every Monday at 03:00; if the machine was off, run as soon as it starts.
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Monday -At "03:00"

$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit   (New-TimeSpan -Hours 1) `
    -StartWhenAvailable `
    -MultipleInstances    IgnoreNew `
    -RunOnlyIfNetworkAvailable

# Run as SYSTEM so no user needs to be logged in.
$principal = New-ScheduledTaskPrincipal `
    -UserId    "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel  Highest

# ── Register (replace if already present) ────────────────────────────────────

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Host "Removing existing task..."
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName   $TaskName `
    -Action     $action `
    -Trigger    $trigger `
    -Settings   $settings `
    -Principal  $principal `
    -Description "Pulls the latest Let's Encrypt TLS certificate from MicroK8s and updates the IIS HTTPS binding for MovieTheater. Managed by register-cert-renewal-task.ps1." | Out-Null

Write-Host ""
Write-Host "======================================"
Write-Host "Scheduled Task registered successfully"
Write-Host "======================================"
Write-Host "  Task name : $TaskName"
Write-Host "  Schedule  : Every Monday at 03:00 (runs on next startup if missed)"
Write-Host "  Runs as   : SYSTEM"
Write-Host "  Script    : $InstalledScript"
Write-Host "  Log       : $LogFile"
Write-Host ""
Write-Host "cert-manager renews the Let's Encrypt cert ~30 days before expiry."
Write-Host "This task will import the renewed cert to IIS within a week of renewal."
Write-Host ""

# ── Optional: run immediately to validate ────────────────────────────────────

if ($RunNow) {
    Write-Host "Running task now to verify setup..."
    Start-ScheduledTask -TaskName $TaskName

    # Poll until the task finishes (or times out after 120 s)
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 3
        $info   = Get-ScheduledTaskInfo -TaskName $TaskName
        $status = (Get-ScheduledTask   -TaskName $TaskName).State
    } while ($status -eq "Running" -and (Get-Date) -lt $deadline)

    $result = $info.LastTaskResult
    $icon   = if ($result -eq 0) { "✅" } else { "❌" }
    Write-Host "$icon Task finished with result code: $result (0 = success)"
    Write-Host ""

    if (Test-Path $LogFile) {
        Write-Host "=== Last 30 log lines ==="
        Get-Content $LogFile -Tail 30
    } else {
        Write-Host "(No log file written yet - the task may still be starting)"
    }
}
