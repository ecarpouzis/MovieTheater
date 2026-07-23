<#
.SYNOPSIS
    Deploy the authoritative GL-worker config (docker/arcade/config.worker-gl.yaml) into every GL
    worker's ConfDir AND recycle the workers so the change takes effect IMMEDIATELY — not "eventually,
    when someone remembers", and not only once the watchdog's stale-config check happens to catch it.

.DESCRIPTION
    Workers read config.yaml ONLY at startup, so a plain `cp` is inert on every running worker until it
    is recycled. This was a live footgun (2026-07-23): both GL workers ran a 5-min-stale config whose
    default n64 core was the OLD core, so a no-override room booted the WRONG core and its render-profile
    options reconciled DEAD. The watchdog now self-heals this within a cycle or two (watch-arcade-glworkers.ps1
    check E), but a deploy should not have to WAIT for that — this script closes the window to ~0.

    Steps, per GL worker (ConfDir worker-gl / worker-gl-<Id>):
      1. DIFF the repo config against the live copy (the "always diff before deploy" rule — the repo copy
         once drifted from the live one and a blind cp would have reverted live tuning). Shown via git.
      2. COPY the repo file over the live one with Copy-Item — a BYTE-EXACT copy. Never Get-Content|Set-Content
         here: that re-encodes and once wrote 27 mojibake sequences into the live config (config header warns
         of this). Copy-Item preserves the plain-UTF-8/no-BOM bytes verbatim.
      3. RECYCLE the worker IF it is running a now-stale config (its process started before the live config's
         mtime) — a GRACEFUL recycle via recycle-arcade-glworker.ps1 (stop-file sentinel; the runner respawns
         in ~4s reading the fresh config). That script's own LIVE-ROOM GUARD refuses to kick a worker hosting a
         real player unless -Force; those are left for watchdog check E to pick up once they go free. Recycles
         are sequential (one worker fully back before the next) so the pool is never fully drained.

    game-overrides.json is NOT handled here — it is exported per-ConfDir by `arcade-gameconfig-export -o
    <ConfDir>\game-overrides.json`, a separate pipeline (config header). This script is config.yaml only.
    The capture worker uses a different file (config.worker-capture.yaml) and its own deploy; not covered here.

.PARAMETER RepoConfig  Source config (default: docker/arcade/config.worker-gl.yaml next to this script's repo).
.PARAMETER WorkerIds   GL worker ids to deploy to (default 1,2). Port = 8445+Id; ConfDir worker-gl / worker-gl-<Id>.
.PARAMETER DryRun      Show the diff for each worker and STOP — no copy, no recycle.
.PARAMETER SkipRecycle Copy the file(s) but do not recycle (let watchdog check E pick it up). Rarely wanted.
.PARAMETER Force       Pass through to the recycle script: recycle even a worker hosting a LIVE room (kicks it).
.PARAMETER GraceSec    Graceful-stop wait handed to the recycle script (default 60 — outwaits the worker's own
                       wedge bounds so a wedged worker dies by its own hand rather than a risky force-kill).
#>
param(
    [string] $RepoConfig = (Join-Path $PSScriptRoot "..\docker\arcade\config.worker-gl.yaml"),
    [int[]]  $WorkerIds  = @(1, 2),
    [switch] $DryRun,
    [switch] $SkipRecycle,
    [switch] $Force,
    [int]    $GraceSec   = 60
)
$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Stop'

function Say([string]$m) { Write-Host "[deploy-glconfig] $m" }

$RepoConfig = (Resolve-Path $RepoConfig).Path
if (-not (Test-Path $RepoConfig)) { throw "repo config not found: $RepoConfig" }
$recycleScript = Join-Path $PSScriptRoot "recycle-arcade-glworker.ps1"
if (-not (Test-Path $recycleScript) -and -not $SkipRecycle) { throw "recycle script not found: $recycleScript" }

$gitExe = (Get-Command git -ErrorAction SilentlyContinue).Source

foreach ($id in $WorkerIds) {
    $confDir  = if ($id -le 1) { "D:\ArcadeStorage\worker-gl" } else { "D:\ArcadeStorage\worker-gl-$id" }
    $livePath = Join-Path $confDir "config.yaml"
    Say "──────── worker $id  ($confDir) ────────"
    if (-not (Test-Path $confDir)) { Say "ConfDir missing — skipping."; continue }

    # 1) DIFF (repo vs live). Present the diff so the operator can eyeball it before it lands.
    $changed = $true
    if (Test-Path $livePath) {
        if ($gitExe) {
            # --no-index diffs two arbitrary files; exit 1 == they differ (not an error here). Show the
            # FULL unified diff, not --stat: the "diff before deploy" rule exists to catch DRIFT (the repo
            # copy once lost live-only core blocks), and a line count can't show what actually changed.
            $diff = & $gitExe --no-pager diff --no-index -- "$livePath" "$RepoConfig" 2>$null
            if ($LASTEXITCODE -eq 0) { $changed = $false } else { $diff | ForEach-Object { Write-Host "    $_" } }
        } else {
            $changed = [bool](Compare-Object (Get-Content $livePath) (Get-Content $RepoConfig))
        }
        if (-not $changed) { Say "live config already matches repo (no copy needed)." }
    } else {
        Say "no live config yet — first deploy."
    }

    if ($DryRun) { Say "DryRun: not copying or recycling."; continue }

    # 2) COPY — byte-exact (never re-encode; preserves plain UTF-8 / no BOM).
    if ($changed) {
        Copy-Item -LiteralPath $RepoConfig -Destination $livePath -Force
        Say "copied repo config → $livePath"
    }

    if ($SkipRecycle) { Say "SkipRecycle: leaving the running worker to watchdog check E."; continue }

    # 3) RECYCLE only if the running worker is on a now-stale config (started before the live file's mtime).
    #    A fresh copy always makes it stale; if unchanged, a worker that predates the file is stale too.
    $port      = 8445 + $id
    $wpid      = (Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
                    Where-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).Name -eq 'worker' } |
                    Select-Object -First 1).OwningProcess
    $startedAt = if ($wpid) { (Get-Process -Id ([int]$wpid) -ErrorAction SilentlyContinue).StartTime } else { $null }
    $cfgTime   = (Get-Item $livePath).LastWriteTime
    if (-not $wpid) {
        Say "no worker.exe on UDP $port — runner will spawn one on the current config; nothing to recycle."
        continue
    }
    if ($startedAt -and $cfgTime -le $startedAt) {
        Say "worker PID $wpid already started after the config ($startedAt) — already current, not recycling."
        continue
    }
    Say "worker PID $wpid is on a stale config (started $startedAt < config $cfgTime) — graceful recycle ..."
    $recycleArgs = @{ WorkerId = $id; GraceSec = $GraceSec }
    if ($Force) { $recycleArgs.Force = $true }
    & $recycleScript @recycleArgs

    # Let the runner respawn before touching the next worker, so the pool is never fully down.
    $respawnBy = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $respawnBy) {
        $newPid = (Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
                    Where-Object { (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).Name -eq 'worker' } |
                    Select-Object -First 1).OwningProcess
        if ($newPid -and [int]$newPid -ne [int]$wpid) { Say "respawned as PID $newPid on port $port."; break }
        Start-Sleep -Milliseconds 500
    }
}
Say "done."
exit 0   # a native `git diff` above sets $LASTEXITCODE=1 on "files differ"; don't leak that as failure.
