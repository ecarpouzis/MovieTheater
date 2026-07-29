# Nightly Jellyfin keyframe-repository backfill (docs/transcode-restart-freeze-plan.md).
#
# Runs the extract-jellyfin-keyframes CLI in bounded chunks so the stream-copyable library gradually
# carries MediaFile.JfKeyframesUtc -- the stamp that lets the patched Jellyfin cut a COPIED HLS session
# on real keyframes, and so lets StreamController stop force-encoding long-GOP titles. Keeps running
# forever so newly-synced files are picked up (a run with nothing to do exits in seconds).
#
# Unlike probe-keyframes this needs no local media mount: the packet walk happens on the Jellyfin host.
# It does need JellyfinBaseUrl/JellyfinApiKey config, and it is MUCH slower per row -- tens of seconds
# to several minutes each -- hence the small nightly cap. This is the Ziggy scheduled-task action.
#
# Registered 2026-07-29 as scheduled task "MovieTheater Extract JF Keyframes" (daily, 04:30, user
# Atoramos, non-elevated — same pattern as the retired "MovieTheater Probe Keyframes" task, which is
# left DISABLED for rollback). Task-host PowerShell is 5.1: this file must stay UTF-8 WITH BOM.

$repo = "F:\Work\MovieTheater"
$log = Join-Path $repo "data\extract-jf-keyframes.log"
$stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
Add-Content $log "[$stamp] nightly run starting"

# 20 chunks x 10 items, hard-stopped at 07:00 (~200/night ceiling, typically 3-4 hours of Jellyfin-side
# ffprobe). Sized up from the original 40/night when the sampled probe pipeline was retired (2026-07-29):
# this backfill is now the ONLY thing closing the unmeasured files' safety hole, so throughput matters —
# but each item is still a full packet walk over SMB and the NAS also serves playback, hence the dawn
# cutoff. Chunking (not one big run) keeps each invocation short and resumable per the global bulk-job
# rule; sequential runs re-read the queue, so successes never repeat and failures retry at most once
# per night.
for ($i = 0; $i -lt 20; $i++) {
    if ((Get-Date).Hour -ge 7) { Add-Content $log "07:00 cutoff reached"; break }
    # Out-String -Width keeps the CLI's one-line summary from wrapping (a wrapped line loses its tail
    # fields to the filter below, which would gut the log's progress reporting).
    #
    # Both per-item lines are kept: at ~40/night they are cheap, and unlike probe-keyframes the success
    # line carries no measurement that needs persisting -- the result IS the stamp in the DB, and the
    # only thing the log adds is how long Jellyfin took, which is how we spot the NAS degrading.
    $out = (& dotnet run --project "$repo\src\MovieTheater\MovieTheater.csproj" -c Release -- extract-jellyfin-keyframes --limit 10 2>&1 |
        Out-String -Width 500) -split "\r?\n" |
        Where-Object { $_ -match '^\s\s[!\+]|^\{ processed|Nothing to extract' }
    $out | Add-Content $log
    # An empty batch means every copyable file is stamped -- nothing left for the later chunks either.
    if ($out -match 'Nothing to extract') { break }
}

Add-Content $log "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] nightly run done"
