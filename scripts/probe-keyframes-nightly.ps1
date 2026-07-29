# Nightly keyframe-spacing backfill (docs/transcode-restart-freeze-plan.md Part 1).
#
# Runs the probe-keyframes CLI in bounded chunks until the whole library carries
# MediaFile.KeyframeIntervalSeconds, then keeps running so newly-synced files are probed within a
# day (a run with nothing to do exits in seconds). Must run on a machine with the library drives
# mapped — prod cannot probe (Linux pod, no media mount); this is the Ziggy scheduled-task action.
#
# Registered via:
#   Register-ScheduledTask "MovieTheater Probe Keyframes" (daily, 03:30) — see scripts/README or
#   the task's own definition. Task-host PowerShell is 5.1: this file must stay UTF-8 WITH BOM.

$repo = "F:\Work\MovieTheater"
$log = Join-Path $repo "data\probe-keyframes.log"
$stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
Add-Content $log "[$stamp] nightly run starting"

# 5 chunks x 200 files ~= 1000 files/night. Chunking (not one big run) keeps each invocation short
# and resumable per the global bulk-job rule; a dead NAS makes every chunk fail fast and tomorrow
# retries. Sequential runs re-read the queue, so successes never repeat and failures are retried at
# most once per night.
for ($i = 0; $i -lt 5; $i++) {
    # Out-String -Width keeps the CLI's one-line summary from wrapping (a wrapped line loses its
    # tail fields to the filter below, which gutted the log's progress reporting).
    $out = (& dotnet run --project "$repo\src\MovieTheater\MovieTheater.csproj" -c Release -- probe-keyframes --limit 200 2>&1 |
        Out-String -Width 500) -split "\r?\n" |
        Where-Object { $_ -match '^\s\s[!\+]|^\{ processed|Nothing to probe' }
    $out | Add-Content $log
    # An empty batch means the library is fully probed — nothing left for the later chunks either.
    if ($out -match 'Nothing to probe') { break }
}

Add-Content $log "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] nightly run done"
