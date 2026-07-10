<#
.SYNOPSIS
    Sweeps ORPHANED Dolphin shader caches — files whose graphics-config hash was superseded.

.DESCRIPTION
    Dolphin keys its persistent shader caches by <GameID>-<ConfigHash> (e.g.
    OpenGL-specialized-pipeline-GFZE01-246FFF41.cache). Every graphics tuning change mints a new
    hash and starts a fresh cache; the old one is never touched again but keeps its gigabytes
    (F-Zero GX built a 1.3 GB cache in ONE evening). Since we tune constantly, orphans compound.

    Rule: group cache files by (prefix, GameID); KEEP the most-recently-written file of each group
    (that's the active config's cache — Dolphin touches it every session); any OTHER file in the
    group is deletable once it hasn't been written for -GraceDays (default 7). The grace window
    exists because reverting a tuning experiment re-activates the old hash — deleting it too eagerly
    would throw away a warm cache you're about to want back.

    Files that don't match the <anything>-<GAMEID6>-<HEX8>.cache shape are SKIPPED, never deleted
    (when unsure, don't clobber). Dry-run by default; -Apply performs the deletes and reports
    {kept, deleted, freedBytes}.

.NOTES
    Registered as a weekly task by register-shader-cache-clean-task.ps1 (runs with -Apply).
#>
param(
    [string]$CacheDir  = "D:\ArcadeStorage\worker-gl\libretro\legacy_save\User\Cache\Shaders",
    [int]$GraceDays    = 7,
    [switch]$Apply
)

if (-not (Test-Path $CacheDir)) { Write-Output "cache dir not found: $CacheDir"; exit 0 }

$cutoff = (Get-Date).AddDays(-$GraceDays)
$files  = Get-ChildItem -Path $CacheDir -Filter *.cache -File
# NB: build the list explicitly — a Write-Output inside a captured foreach would leak the skip
# messages into the collection as strings and create a phantom null group (learned the hard way).
$parsed = New-Object System.Collections.Generic.List[object]
foreach ($f in $files) {
    if ($f.Name -match '^(?<prefix>.+)-(?<game>[A-Z0-9]{6})-(?<hash>[0-9A-Fa-f]{8})\.cache$') {
        $parsed.Add([pscustomobject]@{ File = $f; Group = "$($Matches.prefix)|$($Matches.game)" })
    } elseif ($f.Name -match '^(?<prefix>.+)-(?<hash>[0-9A-Fa-f]{8})\.cache$') {
        # Game-agnostic caches (e.g. OpenGL-uber-pipeline-<hash>.cache) orphan by config hash the
        # same way — group them by prefix alone, newest hash wins.
        $parsed.Add([pscustomobject]@{ File = $f; Group = $Matches.prefix })
    } else {
        Write-Output "SKIP (unrecognized name): $($f.Name)"
    }
}

$kept = 0; $deleted = 0; $freed = 0L
foreach ($group in ($parsed | Group-Object Group)) {
    $sorted = $group.Group | Sort-Object { $_.File.LastWriteTime } -Descending
    $kept++  # the newest is always kept, silently
    foreach ($entry in ($sorted | Select-Object -Skip 1)) {
        $f = $entry.File
        if ($f.LastWriteTime -gt $cutoff) {
            Write-Output ("KEEP (inside {0}d grace): {1}  {2:N0} bytes  last {3}" -f $GraceDays, $f.Name, $f.Length, $f.LastWriteTime)
            $kept++
            continue
        }
        if ($Apply) {
            try {
                Remove-Item -LiteralPath $f.FullName -Force -Confirm:$false -ErrorAction Stop
                Write-Output ("DELETED orphan: {0}  {1:N0} bytes  last {2}" -f $f.Name, $f.Length, $f.LastWriteTime)
                $deleted++; $freed += $f.Length
            } catch { Write-Output "FAILED to delete $($f.Name): $_" }
        } else {
            Write-Output ("WOULD DELETE orphan: {0}  {1:N0} bytes  last {2}" -f $f.Name, $f.Length, $f.LastWriteTime)
            $deleted++; $freed += $f.Length
        }
    }
}

$mode = if ($Apply) { "applied" } else { "dry-run" }
Write-Output ("{0}: kept={1} orphans={2} freed={3:N0} bytes" -f $mode, $kept, $deleted, $freed)
