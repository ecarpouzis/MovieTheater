# On-demand keyframe-spacing backfill -- the "I'm not watching anything, burn through the queue" run.
#
# Same work as scripts\probe-keyframes-nightly.ps1 (docs/transcode-restart-freeze-plan.md Part 1), but
# it keeps going instead of stopping after 5 chunks, and it reports progress to the console so you can
# watch it advance. Every chunk is a separate bounded CLI call that re-reads the queue, so killing this
# at any moment (Ctrl-C, closing the window, a reboot) loses at most the chunk in flight -- the next run
# picks up exactly where it stopped. Nothing here is destructive: it only fills
# MediaFile.KeyframeIntervalSeconds on rows where it is null.
#
# Must run on a machine with the library drives mapped (prod is a Linux pod with no media mount).
#
# Usage:
#   pwsh -File scripts\probe-keyframes-marathon.ps1                 # run until the queue is empty
#   pwsh -File scripts\probe-keyframes-marathon.ps1 -Minutes 120    # ...or until 2 hours are up
#   pwsh -File scripts\probe-keyframes-marathon.ps1 -Limit 100      # smaller chunks = finer stop points
#
# To stop it from another window (or when you sit down to watch something), drop the stop file:
#   New-Item F:\Work\MovieTheater\data\probe-keyframes.stop
# It finishes the chunk in flight, clears the file, and exits cleanly.

param(
    # 0 = run until the queue is empty. Otherwise stop once this many minutes have elapsed; the deadline
    # is checked between chunks, so the last chunk may overrun it by a few minutes.
    [int]$Minutes = 0,
    # Files per CLI call. Bigger = slightly less per-call overhead, smaller = stops/resumes sooner.
    [int]$Limit = 200,
    # Skip the one-time build (only when you know bin\Release is current -- a stale DLL silently probes
    # with old code).
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repo     = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repo "src\MovieTheater\MovieTheater.csproj"
$appDir   = Join-Path $repo "src\MovieTheater\bin\Release\net10.0"
$dll      = Join-Path $appDir "MovieTheater.dll"
$log      = Join-Path $repo "data\probe-keyframes.log"
$stopFile = Join-Path $repo "data\probe-keyframes.stop"

function Stamp { Get-Date -Format "yyyy-MM-dd HH:mm:ss" }

# A leftover stop file from a previous run would kill this one after a single chunk.
if (Test-Path $stopFile) { Remove-Item $stopFile -Force }

# Build ONCE, up front, then invoke the DLL directly for every chunk. The nightly uses `dotnet run`,
# which re-checks the build on every single call -- fine for 5 chunks, real overhead across a hundred.
if (-not $NoBuild) {
    Write-Host "Building (once)..." -ForegroundColor Cyan
    & dotnet build $project -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed -- not starting the run." }
}
if (-not (Test-Path $dll)) { throw "No build at $dll -- re-run without -NoBuild." }

# `dotnet run` sets this from Properties\launchSettings.json; invoking the DLL directly does not, and
# without it Program.BuildConfiguration never layers appsettings.Development.json -- so the CLI starts
# with no DbConnectionString and dies in the service-provider constructor.
if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = "Development" }

$deadline = if ($Minutes -gt 0) { (Get-Date).AddMinutes($Minutes) } else { [datetime]::MaxValue }
$startedAt = Get-Date
$header = if ($Minutes -gt 0) { "marathon run starting (until $($deadline.ToString('HH:mm')))" } else { "marathon run starting (until the queue is empty)" }
Add-Content $log "[$(Stamp)] $header"
Write-Host $header -ForegroundColor Cyan

# Rows that fail to probe (dead path, unreadable file) stay null and would be handed back forever by the
# next chunk. --skip pages past the ones we have already tried, which is what makes "run until empty"
# terminate instead of spinning on a bad block at the head of the queue.
$skip = 0
$totalProcessed = 0
$totalFailed = 0
$chunks = 0
$reason = "queue empty"

while ($true) {
    if ((Get-Date) -ge $deadline) { $reason = "time limit reached"; break }
    if (Test-Path $stopFile) { Remove-Item $stopFile -Force; $reason = "stop file"; break }

    # -Width keeps the CLI's one-line summary from wrapping; a wrapped line loses its tail fields to the
    # parse below (the bug fixed in 3a389e6 for the nightly log).
    Push-Location $appDir   # the CLI resolves appsettings + data paths relative to the working directory
    try {
        $out = (& dotnet $dll probe-keyframes --limit $Limit --skip $skip 2>&1 | Out-String -Width 500) -split "\r?\n"
    }
    finally { Pop-Location }

    # Per-file failures ("  ! <id> ...") and the summary are what the nightly log keeps; do the same here.
    # The per-file SUCCESS lines are dropped on purpose: their measurement is persisted to the DB
    # (MediaFile.KeyframeSampleDetail / KeyframeMinSeconds). Before those columns existed this same filter
    # silently destroyed the only copy of that data. Never let a log filter be the thing standing between
    # an expensive measurement and durable storage.
    ($out | Where-Object { $_ -match '^\s\s!' }) | Add-Content $log

    if ($out -match 'Nothing to probe') {
        ($out | Where-Object { $_ -match 'Nothing to probe' }) | Add-Content $log
        $reason = if ($skip -gt 0) { "queue empty past $skip unprobeable row(s)" } else { "queue empty" }
        break
    }

    $summary = $out | Where-Object { $_ -match '^\{ processed' } | Select-Object -First 1
    if (-not $summary) {
        # No summary means the CLI itself failed (bad config, DB down) rather than a file failing. Retrying
        # would just loop on the same error, so stop and leave the output for diagnosis.
        Add-Content $log "[$(Stamp)] marathon run ABORTED -- chunk produced no summary:"
        ($out | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 15) | Add-Content $log
        Write-Host "Chunk produced no summary -- aborting. Last output:" -ForegroundColor Red
        $out | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" }
        $reason = "CLI error"
        break
    }

    $summary | Add-Content $log
    $chunks++

    if ($summary -match 'processed:\s*(\d+).*remaining:\s*(\d+).*skippedMissingFile:\s*(\d+).*probeFailed:\s*(\d+)') {
        $processed = [int]$Matches[1]
        $remaining = [int]$Matches[2]
        $failed    = [int]$Matches[3] + [int]$Matches[4]

        $totalProcessed += $processed
        $totalFailed += $failed
        $skip += $failed   # page past this chunk's failures; everything probed leaves the queue on its own

        $elapsed = (Get-Date) - $startedAt
        $rate = if ($elapsed.TotalMinutes -gt 0) { $totalProcessed / $elapsed.TotalMinutes } else { 0 }
        $eta = if ($rate -gt 0) { "{0:N1}h" -f ($remaining / $rate / 60) } else { "?" }
        $failNote = if ($totalFailed -gt 0) { ", $totalFailed unprobeable" } else { "" }
        Write-Host ("[{0}] chunk {1}: +{2}  remaining {3}  ({4:N0}/min, ~{5} left{6})" -f (Get-Date -Format 'HH:mm:ss'), $chunks, $processed, $remaining, $rate, $eta, $failNote)

        # Belt-and-braces: a chunk that probed nothing AND failed nothing means the queue is not draining
        # and --skip is not advancing, so looping again would be an infinite retry.
        if ($processed -eq 0 -and $failed -eq 0) { $reason = "no progress"; break }
    }
    else {
        Write-Host "  (unparsed summary: $summary)" -ForegroundColor Yellow
    }
}

$elapsed = (Get-Date) - $startedAt
$footer = "marathon run done -- $reason; $totalProcessed probed in $chunks chunk(s) over $([int]$elapsed.TotalMinutes)m"
Add-Content $log "[$(Stamp)] $footer"
Write-Host $footer -ForegroundColor Cyan
Write-Host "Log: $log"
