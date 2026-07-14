<#
.SYNOPSIS
    Stages PPSSPP's VFPU lookup tables into each GL worker's PPSSPP system directory.

.DESCRIPTION
    PPSSPP ships a set of .dat lookup tables (assets/vfpu/) that it uses for accurate VFPU
    trigonometry. Ours were NEVER STAGED — the vfpu/ directory did not exist on either worker — so the
    core logged, on every single PSP boot:

        [CPU] Error loading 'vfpu/vfpu_sin_lut8192.dat' (size=0, expected: 4100)
        [CPU] Error loading 'vfpu/vfpu_asin_lut65536.dat' (size=0, expected: 1536)

    and fell back to computing those functions at runtime. It went unnoticed for a year because the
    PPSSPP system assets were placed BY HAND (unlike Dolphin's Sys, which is sparse-cloned), so there
    was nothing to notice an omission. Hence this script: the staging is now reproducible and the gap
    cannot silently return.

    Idempotent — re-run any time (after a worker rebuild, or a PPSSPP core update). Verifies every
    file's size against what upstream reports and refuses to write a short/corrupt download, because a
    truncated LUT is worse than an absent one: PPSSPP would read garbage rather than fall back.

.PARAMETER ConfDirs  The worker ConfDirs to stage into.
.PARAMETER Ref       PPSSPP git ref to pull assets from.
#>
param(
    [string[]]$ConfDirs = @("D:\ArcadeStorage\worker-gl", "D:\ArcadeStorage\worker-gl-2"),
    [string]  $Ref      = "master"
)

$ErrorActionPreference = "Stop"
$api = "https://api.github.com/repos/hrydgard/ppsspp/contents/assets/vfpu?ref=$Ref"

Write-Host "listing PPSSPP vfpu assets ($Ref)..."
$files = Invoke-RestMethod -Uri $api -Headers @{ "User-Agent" = "movietheater-arcade" }
if (-not $files) { throw "no vfpu assets returned from GitHub" }

# Download once into memory, size-verified, then fan out to every worker.
$blobs = @{}
foreach ($f in $files) {
    $bytes = (Invoke-WebRequest -Uri $f.download_url -UseBasicParsing).Content
    if ($bytes.Length -ne $f.size) {
        throw "size mismatch for $($f.name): got $($bytes.Length), upstream says $($f.size)"
    }
    $blobs[$f.name] = $bytes
}
Write-Host ("fetched {0} files, all sizes verified" -f $blobs.Count)

foreach ($conf in $ConfDirs) {
    $dest = Join-Path $conf "libretro\system\PPSSPP\vfpu"
    if (-not (Test-Path (Join-Path $conf "libretro\system\PPSSPP"))) {
        Write-Warning "no PPSSPP system dir under $conf - skipping"
        continue
    }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($name in $blobs.Keys) {
        [System.IO.File]::WriteAllBytes((Join-Path $dest $name), $blobs[$name])
    }
    Write-Host ("staged {0} vfpu tables -> {1}" -f $blobs.Count, $dest)
}

Write-Host ""
Write-Host "Done. Verify on the next PSP boot: the '[CPU] Error loading vfpu/...' lines must be GONE."
