# Keeper for the Jellyfin keyframe backfill -- keeps extract-jf-keyframes-marathon.ps1 running until
# the queue is EMPTY, then swaps the system back to the nightly trickle and disables itself.
#
# Why it exists: a marathon run stops at its -Minutes deadline, a stop file, a transient CLI/DB/
# Jellyfin error, or a Ziggy reboot (GPU TDR bugchecks happen) -- and every one of those means the
# backfill sits idle until someone notices. This wraps the marathon in a supervised loop, registered
# as scheduled task "MovieTheater Extract JF Keyframes Keeper" (see
# register-extract-jf-keyframes-keeper-task.ps1): AtLogon trigger + crash-restart, so it survives
# reboots. While the keeper is active the nightly task stays DISABLED (the registrar disables it);
# the keeper re-enables it on every exit path so the trickle safety net always comes back.
#
# Controls (both under data\):
#   extract-jf-keyframes.stop   -> marathon finishes the chunk in flight and exits; the keeper then
#                                  waits $StopGraceMinutes (default 2h) before relaunching.
#                                  "I want to watch something now."
#   extract-jf-keyframes.pause  -> keeper stops LAUNCHING until you delete the file. Does not stop a
#                                  run already in flight -- drop BOTH files to stop now and stay
#                                  stopped.
#
# End states (all logged to data\extract-jf-keyframes.log as "keeper:" lines):
#   queue empty            -> re-enable nightly, disable the keeper task, exit. New files synced
#                             later are handled by the nightly, as before.
#   $MaxConsecutiveFailures failed launches in a row -> re-enable nightly and exit (deterministic
#                             stop, no infinite retry). Fix whatever the log shows, then resume with:
#                             Start-ScheduledTask 'MovieTheater Extract JF Keyframes Keeper'
#
# Every marathon launch builds the CLI (no -NoBuild) so a keeper-driven run can never execute a
# stale DLL. Task-host PowerShell is 5.1: this file must stay UTF-8 WITH BOM.

param(
    [string]$TaskNameSelf = "MovieTheater Extract JF Keyframes Keeper",
    [string]$TaskNameNightly = "MovieTheater Extract JF Keyframes",
    [int]$IdlePollSeconds = 300,
    [int]$FailBackoffMinutes = 30,
    [int]$MaxConsecutiveFailures = 10,
    [int]$StopGraceMinutes = 120
)

# The keeper must outlive transient cmdlet errors (CIM hiccup, log locked for a read, ...); the
# scheduled task only auto-restarts it on a hard crash.
$ErrorActionPreference = "Continue"

$repo      = Split-Path -Parent $PSScriptRoot
$marathon  = Join-Path $PSScriptRoot "extract-jf-keyframes-marathon.ps1"
$log       = Join-Path $repo "data\extract-jf-keyframes.log"
$pauseFile = Join-Path $repo "data\extract-jf-keyframes.pause"

function Stamp { Get-Date -Format "yyyy-MM-dd HH:mm:ss" }
function KLog([string]$msg) { Add-Content $log "[$(Stamp)] keeper: $msg" }

# The last "marathon run done -- <reason>; ..." footer in the log, with its timestamp. This is how
# the keeper knows WHY the previous run ended -- including runs it did not launch itself.
function Get-LastFooter {
    if (-not (Test-Path $log)) { return $null }
    $footers = @(Get-Content $log -Tail 800 |
        Where-Object { $_ -match '^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] marathon run done -- ' })
    if ($footers.Count -eq 0) { return $null }
    if ($footers[$footers.Count - 1] -match '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] marathon run done -- (.+?);') {
        return [pscustomobject]@{
            Time   = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', [System.Globalization.CultureInfo]::InvariantCulture)
            Reason = $Matches[2]
        }
    }
    return $null
}

# True if a marathon (this keeper's or a hand-started one) or a bare extract-jellyfin-keyframes CLI
# is already walking files -- launching a second would double-extract the head of the queue.
function Test-ExtractionRunning {
    $procs = @(Get-CimInstance Win32_Process `
        -Filter "Name = 'pwsh.exe' OR Name = 'powershell.exe' OR Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'extract-jf-keyframes-marathon\.ps1|extract-jellyfin-keyframes' })
    return ($procs.Count -gt 0)
}

# Both exit paths restore the nightly trickle so a stopped keeper never leaves the library with NO
# backfill driver at all.
function Restore-Nightly {
    try { Enable-ScheduledTask -TaskName $TaskNameNightly -ErrorAction Stop | Out-Null; KLog "nightly task re-enabled" }
    catch { KLog "could not re-enable nightly task: $_" }
}

if (-not (Test-Path $marathon)) { KLog "FATAL: marathon script not found at $marathon"; exit 0 }

KLog "starting (poll ${IdlePollSeconds}s, backoff ${FailBackoffMinutes}m, stop grace ${StopGraceMinutes}m)"
$consecFail = 0

while ($true) {
    if (Test-Path $pauseFile) { Start-Sleep -Seconds $IdlePollSeconds; continue }
    if (Test-ExtractionRunning) { Start-Sleep -Seconds $IdlePollSeconds; continue }

    # A run someone stopped by hand gets a grace window before the keeper starts the next one.
    $footer = Get-LastFooter
    if ($footer -and $footer.Reason -like 'stop file*' -and
        ((Get-Date) - $footer.Time).TotalMinutes -lt $StopGraceMinutes) {
        Start-Sleep -Seconds $IdlePollSeconds; continue
    }

    $launchedAt = Get-Date
    KLog "launching marathon"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $marathon *> $null

    # Judge the run by the footer it wrote, not by exit code -- the marathon's own log line carries
    # the reason. A missing/stale footer means it died before its loop (build or config failure).
    $footer = Get-LastFooter
    if (-not $footer -or $footer.Time -lt $launchedAt) {
        $consecFail++
        KLog "marathon exited WITHOUT a footer (build/config failure?) -- failure $consecFail/$MaxConsecutiveFailures"
    }
    elseif ($footer.Reason -like 'queue empty*') {
        KLog "backfill queue drained ($($footer.Reason)) -- swapping back to the nightly trickle"
        Restore-Nightly
        try { Disable-ScheduledTask -TaskName $TaskNameSelf -ErrorAction Stop | Out-Null; KLog "keeper task disabled -- done" }
        catch { KLog "could not disable keeper task ($_) -- disable it by hand" }
        exit 0
    }
    elseif ($footer.Reason -match 'CLI error|no progress') {
        $consecFail++
        KLog "marathon ended with '$($footer.Reason)' -- failure $consecFail/$MaxConsecutiveFailures, retrying in ${FailBackoffMinutes}m"
    }
    else {
        # "stop file" (grace handled at the top of the loop) or "time limit reached" (a hand-started
        # bounded run) -- healthy ends; relaunch on the next pass.
        $consecFail = 0
        continue
    }

    if ($consecFail -ge $MaxConsecutiveFailures) {
        KLog "giving up after $consecFail consecutive failures -- fix the error above, then: Start-ScheduledTask '$TaskNameSelf'"
        Restore-Nightly
        exit 0
    }
    Start-Sleep -Seconds ($FailBackoffMinutes * 60)
}
