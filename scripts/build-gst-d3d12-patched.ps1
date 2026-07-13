<#
.SYNOPSIS
    Rebuilds GStreamer's d3d12 plugin from source with our cursor fix and installs it over MSYS2's.

.DESCRIPTION
    ⚠ RUN THIS AFTER ANY `pacman -Syu` THAT TOUCHES GSTREAMER. A package upgrade overwrites
    libgstd3d12.dll with the stock build, which silently reintroduces a hard crash: the capture
    worker abort()s (0xC0000409) within seconds of ANY fullscreen game, and it presents to players
    as "the arcade is full" once the wedged worker stops taking rooms.

    THE BUG (docker/arcade/patches/gst/0001-d3d12-dxgicapture-monochrome-cursor-oob.patch):
    PtrInfo::buildMonochrom() in sys/d3d12/gstd3d12dxgicapture.cpp reads the XOR half of a
    monochrome cursor at `shape_buffer[src_pos + size]`, where `size` is the DESTINATION RGBA size
    (height * width * 4) rather than the source offset (height * Pitch). For a 32x32 cursor the
    buffer is 256 bytes and `size` is 4096 — a read ~16x past the end, on EVERY monochrome cursor,
    which is what Windows hands a fullscreen game. It is fatal (rather than a silent overread)
    because MSYS2 builds libstdc++ with _GLIBCXX_ASSERTIONS, so operator[] bounds-checks and aborts.

    Still present upstream in 1.28.4 and 1.28.5. Drop this script (and the patch) the day a GStreamer
    release carries the fix — verify by grepping the released source for `src_pos + size`.

    Why bother instead of just using d3d11screencapturesrc (which does not crash): d3d12 is the only
    screen source with HDR tonemapping (`tonemap=reinhard`), so it is the one that stays
    colour-correct if the game PC's desktop is ever switched into HDR mode (plan §11 Tier 1).

.PARAMETER Version   gst-plugins-bad version to build. MUST match the installed GStreamer
                     (`gst-inspect-1.0 --version`) — the plugin links against those libs.
.PARAMETER Ucrt64    MSYS2 UCRT64 prefix.
.PARAMETER WorkDir   Scratch dir for the source tree.
#>
param(
    [string]$Version = "",
    [string]$Ucrt64  = "D:\msys64\ucrt64",
    [string]$WorkDir = "D:\Arcade\build\gst"
)

$ErrorActionPreference = "Stop"
$env:Path = "$Ucrt64\bin;$env:Path"

$repo    = Split-Path $PSScriptRoot -Parent
$patch   = Join-Path $repo "docker\arcade\patches\gst\0001-d3d12-dxgicapture-monochrome-cursor-oob.patch"
$target  = Join-Path $Ucrt64 "lib\gstreamer-1.0\libgstd3d12.dll"
if (-not (Test-Path $patch)) { throw "patch not found: $patch" }

# The plugin must be built against the GStreamer that will load it.
if (-not $Version) {
    $v = (& "$Ucrt64\bin\gst-inspect-1.0.exe" --version | Select-String -Pattern 'gst-inspect-1.0 version ([\d.]+)').Matches[0].Groups[1].Value
    if (-not $v) { throw "could not determine installed GStreamer version" }
    $Version = $v
}
Write-Host "Building gst-plugins-bad $Version (d3d12 only) against the installed GStreamer."

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$src  = Join-Path $WorkDir "gst-plugins-bad-$Version"
$tar  = Join-Path $WorkDir "gst-plugins-bad-$Version.tar.xz"

if (-not (Test-Path $src)) {
    if (-not (Test-Path $tar)) {
        $url = "https://gstreamer.freedesktop.org/src/gst-plugins-bad/gst-plugins-bad-$Version.tar.xz"
        Write-Host "downloading $url"
        Invoke-WebRequest -Uri $url -OutFile $tar -UseBasicParsing
    }
    & "$Ucrt64\bin\bsdtar.exe" -xf $tar -C $WorkDir
    if (-not (Test-Path $src)) { & tar -xf $tar -C $WorkDir }

    Push-Location $src
    # git apply works in a non-repo tree with --unsafe-paths; patch(1) is the portable fallback.
    & "$Ucrt64\bin\patch.exe" -p1 -i $patch
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "failed to apply $patch" }
    Pop-Location
    Write-Host "applied the monochrome-cursor fix"
}

$build = Join-Path $src "build"
if (-not (Test-Path $build)) {
    & meson setup $build --buildtype=release -Dauto_features=disabled -Dd3d12=enabled `
        -Dtests=disabled -Dexamples=disabled -Dintrospection=disabled -Ddoc=disabled --wipe-if-needed 2>$null
    if ($LASTEXITCODE -ne 0) {
        & meson setup $build --buildtype=release -Dauto_features=disabled -Dd3d12=enabled `
            -Dtests=disabled -Dexamples=disabled -Dintrospection=disabled -Ddoc=disabled
    }
}
& ninja -C $build
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$dll = Join-Path $build "sys\d3d12\libgstd3d12.dll"
if (-not (Test-Path $dll)) { throw "built plugin not found: $dll" }

# Never clobber the stock DLL without keeping a copy to roll back to.
$backup = "D:\ArcadeStorage\backup\libgstd3d12.dll.msys2-stock-$Version"
if (-not (Test-Path $backup)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null
    Copy-Item $target $backup -Force
    Write-Host "stock plugin backed up -> $backup"
}

# The capture worker holds the DLL open while it runs.
$running = @(Get-Process worker -ErrorAction SilentlyContinue | Where-Object { $_.Path -like 'D:\ArcadeStorage\worker-capture\*' })
if ($running) {
    Write-Host "stopping the capture worker (it has the plugin loaded)"
    Stop-ScheduledTask -TaskName 'MovieTheater - Arcade GL Worker 3' -ErrorAction SilentlyContinue
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Copy-Item $dll $target -Force
Write-Host "installed patched libgstd3d12.dll -> $target"

# Stamp the patched DLL's hash where the capture worker can check it at boot (capture.d3d12PluginSha256).
# This is what makes the pacman trap DETECTABLE instead of silent: if an upgrade swaps the stock plugin
# back in, the hash no longer matches, and the worker falls back to d3d11 with a loud error rather than
# aborting seconds into every heavy session (which players only ever see as "the arcade is full").
$stamp = "$target.sha256"
(Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLower() | Set-Content -Path $stamp -Encoding ascii -NoNewline
Write-Host "stamped expected hash -> $stamp"

if ($running) {
    Start-ScheduledTask -TaskName 'MovieTheater - Arcade GL Worker 3'
    Write-Host "capture worker restarted"
}
Write-Host "`nVerify: launch a heavy title and confirm it survives >60s (it used to die in 10-30s)."
Write-Host "The capture worker logs 'libgstd3d12 is the patched build' at boot when the stamp matches."
